using System.Net;
using System.Net.Http.Json;
using AeroCompliance.Contracts.Dtos.Auth;
using AeroCompliance.Domain.Enums;
using AeroCompliance.IntegrationTests.Infrastructure;

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
