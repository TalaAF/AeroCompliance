using System.Text;
using AeroCompliance.Contracts.Messaging;
using AeroCompliance.Infrastructure.Persistence;
using RabbitMQ.Client;

namespace AeroCompliance.ApiService.Services;

public sealed class ProcessingOutboxDispatcher(IServiceScopeFactory scopes, IConnection connection, ILogger<ProcessingOutboxDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await DispatchOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Outbox dispatch cycle failed; leases will expire."); }
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }

    public async Task DispatchOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroComplianceDbContext>();
        var claims = new OutboxClaimStore(db);
        var (token, pending) = await claims.ClaimAsync(20, ct);
        if (pending.Count == 0) return;
        try
        {
            var options = new CreateChannelOptions(true, true);
            await using var channel = await connection.CreateChannelAsync(options, ct);
            await channel.ExchangeDeclareAsync(MessagingConstants.ProcessingExchange, ExchangeType.Topic, true, false, cancellationToken: ct);
            await channel.QueueDeclareAsync(MessagingConstants.AiProcessingQueue, true, false, false,
                new Dictionary<string, object?> { ["x-dead-letter-exchange"] = MessagingConstants.ProcessingExchange,
                    ["x-dead-letter-routing-key"] = "image.processing.dead.v1" }, cancellationToken: ct);
            await channel.QueueDeclareAsync(MessagingConstants.AiProcessingDeadLetterQueue, true, false, false, cancellationToken: ct);
            await channel.QueueBindAsync(MessagingConstants.AiProcessingQueue, MessagingConstants.ProcessingExchange,
                MessagingConstants.ProcessingRequestRoutingKey, cancellationToken: ct);
            await channel.QueueBindAsync(MessagingConstants.AiProcessingDeadLetterQueue, MessagingConstants.ProcessingExchange,
                "image.processing.dead.v1", cancellationToken: ct);
            foreach (var message in pending)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var props = new BasicProperties { ContentType = "application/json", DeliveryMode = DeliveryModes.Persistent,
                        MessageId = message.EventId.ToString(), Type = message.Type };
                    await channel.BasicPublishAsync(MessagingConstants.ProcessingExchange, message.RoutingKey, false,
                        props, Encoding.UTF8.GetBytes(message.Payload), ct);
                    if (await claims.PublishedAsync(message.EventId, token, ct) != 1)
                        logger.LogWarning("Outbox claim for {EventId} changed after publish; duplicate delivery is possible.", message.EventId);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    await claims.FailedAsync(message, token, Safe(ex), ct);
                    logger.LogWarning(ex, "Outbox publish failed for {EventId} (attempt {Attempt}).", message.EventId, message.AttemptCount);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            foreach (var message in pending)
                await claims.FailedAsync(message, token, Safe(ex), ct);
            logger.LogWarning(ex, "Outbox channel failed; claimed messages were scheduled for retry.");
        }
    }

    private static string Safe(Exception ex) => $"{ex.GetType().Name}: publish failed";
}
