using System.Net;
using System.Net.Http.Json;
using AeroCompliance.Contracts.Dtos.Auth;
using AeroCompliance.Domain.Enums;
using AeroCompliance.Infrastructure.Persistence;
using AeroCompliance.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroCompliance.IntegrationTests.Authentication;

[Collection(ApiTestCollection.Name)]
public class AuthenticationEndpointTests(ApiTestFixture fixture)
{
    [Theory]
    [InlineData(null, "ValidPassword123!")]
    [InlineData("not-an-email", "ValidPassword123!")]
    [InlineData("person@example.test", null)]
    [InlineData("person@example.test", "   ")]
    public async Task Invalid_login_request_returns_validation_problem(string? email, string? password)
    {
        using var client = fixture.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login", new { email, password });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("/api/auth/refresh")]
    [InlineData("/api/auth/logout")]
    public async Task Empty_refresh_or_logout_token_returns_validation_problem(string path)
    {
        using var client = fixture.CreateClient();

        var response = await client.PostAsJsonAsync(path, new { refreshToken = " " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Active_user_can_login()
    {
        var user = await fixture.CreateUserAsync(RoleName.Inspector);
        using var client = fixture.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(user.Email, ApiTestFixture.TestPassword));
        var tokens = await response.Content.ReadFromJsonAsync<TokenResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(tokens);
        Assert.False(string.IsNullOrWhiteSpace(tokens.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(tokens.RefreshToken));
    }

    [Fact]
    public async Task Login_normalizes_email_case()
    {
        var user = await fixture.CreateUserAsync(RoleName.Inspector);
        using var client = fixture.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(user.Email.ToUpperInvariant(), ApiTestFixture.TestPassword));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Login_returns_429_after_configured_limit()
    {
        using var limitedFactory = fixture.CreateFactoryWithLoginLimit(2);
        using var client = limitedFactory.CreateClient();
        var request = new LoginRequest($"missing-{Guid.NewGuid():N}@example.test", "incorrect");

        var first = await client.PostAsJsonAsync("/api/auth/login", request);
        var second = await client.PostAsJsonAsync("/api/auth/login", request);
        var third = await client.PostAsJsonAsync("/api/auth/login", request);

        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
    }

    [Fact]
    public async Task Invalid_credentials_have_generic_unauthorized_responses_that_do_not_disclose_cause()
    {
        var activeUser = await fixture.CreateUserAsync(RoleName.Inspector);
        var inactiveUser = await fixture.CreateUserAsync(RoleName.Inspector, isActive: false);
        using var client = fixture.CreateClient();
        var attempts = new[]
        {
            new LoginRequest($"unknown-{Guid.NewGuid():N}@example.test", ApiTestFixture.TestPassword),
            new LoginRequest(activeUser.Email, "DefinitelyWrong123!"),
            new LoginRequest(inactiveUser.Email, ApiTestFixture.TestPassword)
        };

        foreach (var attempt in attempts)
        {
            var response = await client.PostAsJsonAsync("/api/auth/login", attempt);
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.DoesNotContain("unknown", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("inactive", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("password", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(attempt.Email, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Valid_refresh_rotates_and_revokes_old_token()
    {
        var user = await fixture.CreateUserAsync(RoleName.Reviewer);
        using var client = fixture.CreateClient();
        var original = await client.LoginAsync(user);

        var response = await client.PostAsJsonAsync(
            "/api/auth/refresh",
            new RefreshTokenRequest(original.RefreshToken));
        var rotated = await response.Content.ReadFromJsonAsync<TokenResponse>();
        var oldStoredToken = await fixture.FindRefreshTokenAsync(original.RefreshToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(rotated);
        Assert.NotEqual(original.RefreshToken, rotated.RefreshToken);
        Assert.NotNull(oldStoredToken?.RevokedAt);
        Assert.NotNull(oldStoredToken?.RotatedAt);
        Assert.Equal(oldStoredToken?.FamilyId, (await fixture.FindRefreshTokenAsync(rotated.RefreshToken))?.FamilyId);
    }

    [Fact]
    public async Task Concurrent_refresh_only_rotates_presented_token_once()
    {
        var user = await fixture.CreateUserAsync(RoleName.Reviewer);
        using var loginClient = fixture.CreateClient();
        var original = await loginClient.LoginAsync(user);
        using var firstClient = fixture.CreateClient();
        using var secondClient = fixture.CreateClient();

        var request = new RefreshTokenRequest(original.RefreshToken);
        var responses = await Task.WhenAll(
            firstClient.PostAsJsonAsync("/api/auth/refresh", request),
            secondClient.PostAsJsonAsync("/api/auth/refresh", request));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Unauthorized);
        var winner = await Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK)
            .Content.ReadFromJsonAsync<TokenResponse>();
        var next = await firstClient.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(winner!.RefreshToken));
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        Assert.Null((await fixture.FindRefreshTokenAsync(original.RefreshToken))?.ReuseDetectedAt);
    }

    [Fact]
    public async Task Reusing_rotated_token_after_grace_revokes_only_its_family()
    {
        var user = await fixture.CreateUserAsync(RoleName.Reviewer);
        using var client = fixture.CreateClient();
        var firstLogin = await client.LoginAsync(user);
        var otherLogin = await client.LoginAsync(user);
        var firstRotation = await RotateAsync(client, firstLogin.RefreshToken);
        var current = await RotateAsync(client, firstRotation.RefreshToken);
        var original = (await fixture.FindRefreshTokenAsync(firstLogin.RefreshToken))!;
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroComplianceDbContext>();
            await db.RefreshTokens.Where(rt => rt.Id == original.Id).ExecuteUpdateAsync(update =>
                update.SetProperty(rt => rt.RotatedAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }

        var replay = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(firstLogin.RefreshToken));
        var descendant = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(current.RefreshToken));
        var unrelated = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(otherLogin.RefreshToken));

        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, descendant.StatusCode);
        Assert.Equal(HttpStatusCode.OK, unrelated.StatusCode);
        Assert.NotNull((await fixture.FindRefreshTokenAsync(firstLogin.RefreshToken))?.ReuseDetectedAt);
        Assert.NotNull((await fixture.FindRefreshTokenAsync(current.RefreshToken))?.RevokedAt);
    }

    [Fact]
    public async Task Logout_with_rotated_ancestor_revokes_its_descendants_but_not_other_login()
    {
        var user = await fixture.CreateUserAsync(RoleName.Inspector);
        using var client = fixture.CreateClient();
        var firstLogin = await client.LoginAsync(user);
        var otherLogin = await client.LoginAsync(user);
        var descendant = await RotateAsync(client, firstLogin.RefreshToken);

        var logout = await client.PostAsJsonAsync("/api/auth/logout", new LogoutRequest(firstLogin.RefreshToken));
        var sameFamily = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(descendant.RefreshToken));
        var otherFamily = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(otherLogin.RefreshToken));

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, sameFamily.StatusCode);
        Assert.Equal(HttpStatusCode.OK, otherFamily.StatusCode);
        Assert.Null((await fixture.FindRefreshTokenAsync(firstLogin.RefreshToken))?.ReuseDetectedAt);
    }

    [Fact]
    public async Task Replay_racing_descendant_rotation_leaves_no_live_token_in_that_family()
    {
        var user = await fixture.CreateUserAsync(RoleName.Reviewer);
        using var loginClient = fixture.CreateClient();
        var original = await loginClient.LoginAsync(user);
        var child = await RotateAsync(loginClient, original.RefreshToken);
        var parent = (await fixture.FindRefreshTokenAsync(original.RefreshToken))!;
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroComplianceDbContext>();
            await db.RefreshTokens.Where(rt => rt.Id == parent.Id).ExecuteUpdateAsync(update =>
                update.SetProperty(rt => rt.RotatedAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }
        using var replayClient = fixture.CreateClient();
        using var rotateClient = fixture.CreateClient();

        var responses = await Task.WhenAll(
            replayClient.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(original.RefreshToken)),
            rotateClient.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(child.RefreshToken)));

        Assert.Equal(HttpStatusCode.Unauthorized, responses[0].StatusCode);
        Assert.Contains(responses[1].StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Unauthorized });
        using var verificationScope = fixture.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<AeroComplianceDbContext>();
        Assert.False(await verificationDb.RefreshTokens.AnyAsync(rt =>
            rt.FamilyId == parent.FamilyId && rt.RevokedAt == null));
    }

    private static async Task<TokenResponse> RotateAsync(HttpClient client, string token)
    {
        var response = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(token));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
    }

    [Fact]
    public async Task Expired_refresh_token_is_rejected()
    {
        var user = await fixture.CreateUserAsync(RoleName.Inspector);
        var token = $"expired-{Guid.NewGuid():N}";
        await fixture.AddRefreshTokenAsync(user, token, DateTimeOffset.UtcNow.AddMinutes(-1), revoked: false);
        using var client = fixture.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(token));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Revoked_refresh_token_is_rejected()
    {
        var user = await fixture.CreateUserAsync(RoleName.Inspector);
        var token = $"revoked-{Guid.NewGuid():N}";
        await fixture.AddRefreshTokenAsync(user, token, DateTimeOffset.UtcNow.AddDays(1), revoked: true);
        using var client = fixture.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(token));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Logout_is_idempotent_and_revokes_token()
    {
        var user = await fixture.CreateUserAsync(RoleName.Inspector);
        using var client = fixture.CreateClient();
        var tokens = await client.LoginAsync(user);
        var request = new LogoutRequest(tokens.RefreshToken);

        var first = await client.PostAsJsonAsync("/api/auth/logout", request);
        var second = await client.PostAsJsonAsync("/api/auth/logout", request);
        var stored = await fixture.FindRefreshTokenAsync(tokens.RefreshToken);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        Assert.NotNull(stored?.RevokedAt);
    }
}
