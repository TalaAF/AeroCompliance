using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AeroCompliance.IntegrationTests.Infrastructure;

public sealed class AeroComplianceApiFactory(string databaseConnectionString)
    : WebApplicationFactory<Program>
{
    public const string TestSigningKey = "integration-test-signing-key-with-at-least-32-bytes";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder
            .UseEnvironment("Testing")
            .UseSetting("ConnectionStrings:aerocompliancedb", databaseConnectionString)
            .UseSetting("ConnectionStrings:messaging", "amqp://guest:guest@127.0.0.1:5672")
            .UseSetting("Jwt:Issuer", "AeroCompliance.IntegrationTests")
            .UseSetting("Jwt:Audience", "AeroCompliance.IntegrationTests.Client")
            .UseSetting("Jwt:SigningKey", TestSigningKey)
            .UseSetting("Jwt:AccessTokenMinutes", "15")
            .UseSetting("Jwt:RefreshTokenDays", "7")
            .UseSetting("Auth:LoginRateLimit:PermitLimit", "1000");
    }
}
