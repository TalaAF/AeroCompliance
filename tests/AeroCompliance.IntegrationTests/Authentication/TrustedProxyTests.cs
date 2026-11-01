using System.Net;
using System.Net.Http.Json;
using AeroCompliance.Contracts.Dtos.Auth;
using AeroCompliance.Domain.Enums;
using AeroCompliance.IntegrationTests.Infrastructure;

namespace AeroCompliance.IntegrationTests.Authentication;

[Collection(ApiTestCollection.Name)]
public class TrustedProxyTests(ApiTestFixture fixture)
{
    private const string ComposeProxy = "172.30.250.254";

    [Fact]
    public async Task Trusted_compose_proxy_uses_client_ip_for_audit_and_separate_login_limits()
    {
        using var factory = fixture.CreateProxyTestFactory(2, ComposeProxy);
        using var firstClient = ProxyClient(factory.CreateClient(), ComposeProxy,
            "203.0.113.99, 198.51.100.10");
        using var secondClient = ProxyClient(factory.CreateClient(), ComposeProxy, "198.51.100.11");
        var reviewer = await fixture.CreateUserAsync(RoleName.Reviewer);
        var droneOperator = await fixture.CreateUserAsync(RoleName.DroneOperator);
        var violationId = await fixture.CreateReviewViolationAsync(droneOperator.Id);

        var tokens = await firstClient.LoginAsync(reviewer);
        firstClient.UseBearer(tokens.AccessToken);
        var decision = await firstClient.PostAsJsonAsync(
            $"/api/violations/{violationId}/confirm", new { notes = "proxy audit" });
        Assert.Equal(HttpStatusCode.OK, decision.StatusCode);
        Assert.Equal(IPAddress.Parse("198.51.100.10"),
            await fixture.FindAuditIpAsync(violationId, "violation.confirm"));

        Assert.Equal(HttpStatusCode.Unauthorized, await InvalidLogin(firstClient));
        Assert.Equal(HttpStatusCode.TooManyRequests, await InvalidLogin(firstClient));
        Assert.Equal(HttpStatusCode.Unauthorized, await InvalidLogin(secondClient));
        Assert.Equal(HttpStatusCode.Unauthorized, await InvalidLogin(secondClient));
        Assert.Equal(HttpStatusCode.TooManyRequests, await InvalidLogin(secondClient));
    }

    [Fact]
    public async Task Untrusted_peer_cannot_spoof_audit_ip_or_evade_login_limit()
    {
        using var factory = fixture.CreateProxyTestFactory(2, ComposeProxy);
        using var client = ProxyClient(factory.CreateClient(), "172.30.250.253", "198.51.100.20");
        var reviewer = await fixture.CreateUserAsync(RoleName.Reviewer);
        var droneOperator = await fixture.CreateUserAsync(RoleName.DroneOperator);
        var violationId = await fixture.CreateReviewViolationAsync(droneOperator.Id);

        var tokens = await client.LoginAsync(reviewer);
        client.UseBearer(tokens.AccessToken);
        var decision = await client.PostAsJsonAsync(
            $"/api/violations/{violationId}/confirm", new { notes = "untrusted audit" });
        Assert.Equal(HttpStatusCode.OK, decision.StatusCode);
        Assert.Equal(IPAddress.Parse("172.30.250.253"),
            await fixture.FindAuditIpAsync(violationId, "violation.confirm"));

        Assert.Equal(HttpStatusCode.Unauthorized, await InvalidLogin(client));
        client.DefaultRequestHeaders.Remove("X-Forwarded-For");
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.21");
        Assert.Equal(HttpStatusCode.TooManyRequests, await InvalidLogin(client));
    }

    [Fact]
    public async Task Single_container_loopback_proxy_remains_trusted()
    {
        using var factory = fixture.CreateProxyTestFactory(2, ComposeProxy);
        using var client = ProxyClient(factory.CreateClient(), "127.0.0.1", "198.51.100.30");
        var reviewer = await fixture.CreateUserAsync(RoleName.Reviewer);
        var droneOperator = await fixture.CreateUserAsync(RoleName.DroneOperator);
        var violationId = await fixture.CreateReviewViolationAsync(droneOperator.Id);

        var tokens = await client.LoginAsync(reviewer);
        client.UseBearer(tokens.AccessToken);
        var decision = await client.PostAsJsonAsync(
            $"/api/violations/{violationId}/confirm", new { notes = "loopback audit" });

        Assert.Equal(HttpStatusCode.OK, decision.StatusCode);
        Assert.Equal(IPAddress.Parse("198.51.100.30"),
            await fixture.FindAuditIpAsync(violationId, "violation.confirm"));
    }

    private static HttpClient ProxyClient(HttpClient client, string peer, string forwardedFor)
    {
        client.DefaultRequestHeaders.Add("X-Test-Remote-IP", peer);
        client.DefaultRequestHeaders.Add("X-Forwarded-For", forwardedFor);
        return client;
    }

    private static async Task<HttpStatusCode> InvalidLogin(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest($"missing-{Guid.NewGuid():N}@example.test", "incorrect"));
        return response.StatusCode;
    }
}
