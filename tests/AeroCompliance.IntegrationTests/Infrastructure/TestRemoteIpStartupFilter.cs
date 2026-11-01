using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace AeroCompliance.IntegrationTests.Infrastructure;

/// <summary>Sets the transport peer before production forwarded-header middleware runs.</summary>
public sealed class TestRemoteIpStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        => app =>
        {
            app.Use((context, continuation) =>
            {
                if (context.Request.Headers.TryGetValue("X-Test-Remote-IP", out var value))
                    context.Connection.RemoteIpAddress = IPAddress.Parse(value.ToString());
                return continuation();
            });
            next(app);
        };
}
