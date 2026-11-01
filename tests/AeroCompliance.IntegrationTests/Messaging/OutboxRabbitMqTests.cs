using AeroCompliance.ApiService.Services;
using AeroCompliance.Contracts.Messaging;
using AeroCompliance.Contracts.Messaging.Messages;
using AeroCompliance.Domain.Entities;
using AeroCompliance.Infrastructure.Persistence;
using AeroCompliance.IntegrationTests.Infrastructure;
using DotNet.Testcontainers.Builders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;

namespace AeroCompliance.IntegrationTests.Messaging;

[Collection(ApiTestCollection.Name)]
public class OutboxRabbitMqTests(ApiTestFixture fixture)
{
    [Fact]
    public async Task Two_dispatchers_publish_one_claimed_message_to_real_rabbitmq()
    {
        await using var rabbit = new ContainerBuilder("rabbitmq:4.3.6-management")
            .WithEnvironment("RABBITMQ_DEFAULT_USER", "integration")
            .WithEnvironment("RABBITMQ_DEFAULT_PASS", "isolated-tests-only-password")
            .WithPortBinding(5672, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(5672))
            .Build();
        await rabbit.StartAsync();

        var factory = new ConnectionFactory
        {
            HostName = rabbit.Hostname,
            Port = rabbit.GetMappedPublicPort(5672),
            UserName = "integration",
            Password = "isolated-tests-only-password"
        };
        await using var connection = await factory.CreateConnectionAsync();
        var eventId = Guid.NewGuid();
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroComplianceDbContext>();
            db.OutboxMessages.Add(new OutboxMessage
            {
                EventId = eventId, Type = nameof(ImageProcessingRequested),
                RoutingKey = MessagingConstants.ProcessingRequestRoutingKey,
                SchemaVersion = 1, Payload = "{}", CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var scopes = fixture.Services.GetRequiredService<IServiceScopeFactory>();
        var first = new ProcessingOutboxDispatcher(scopes, connection, NullLogger<ProcessingOutboxDispatcher>.Instance);
        var second = new ProcessingOutboxDispatcher(scopes, connection, NullLogger<ProcessingOutboxDispatcher>.Instance);
        await Task.WhenAll(first.DispatchOnceAsync(default), second.DispatchOnceAsync(default));

        await using var channel = await connection.CreateChannelAsync();
        var delivered = await channel.BasicGetAsync(MessagingConstants.AiProcessingQueue, true);
        var duplicate = await channel.BasicGetAsync(MessagingConstants.AiProcessingQueue, true);
        Assert.NotNull(delivered);
        Assert.Equal(eventId.ToString(), delivered.BasicProperties.MessageId);
        Assert.Null(duplicate);
    }
}
