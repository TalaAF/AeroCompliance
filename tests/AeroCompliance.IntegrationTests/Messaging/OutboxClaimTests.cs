using AeroCompliance.ApiService.Services;
using AeroCompliance.Domain.Entities;
using AeroCompliance.Infrastructure.Persistence;
using AeroCompliance.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroCompliance.IntegrationTests.Messaging;

[Collection(ApiTestCollection.Name)]
public class OutboxClaimTests(ApiTestFixture fixture)
{
    [Fact]
    public async Task Competing_dispatchers_claim_each_message_once()
    {
        var eventId = await AddMessageAsync();
        using var firstScope = fixture.Services.CreateScope();
        using var secondScope = fixture.Services.CreateScope();
        var first = new OutboxClaimStore(firstScope.ServiceProvider.GetRequiredService<AeroComplianceDbContext>());
        var second = new OutboxClaimStore(secondScope.ServiceProvider.GetRequiredService<AeroComplianceDbContext>());

        var claims = await Task.WhenAll(first.ClaimAsync(1, default), second.ClaimAsync(1, default));

        Assert.Single(claims.SelectMany(value => value.Messages), value => value.EventId == eventId);
    }

    [Fact]
    public async Task Expired_claim_can_be_recovered_but_old_owner_cannot_finish_it()
    {
        var eventId = await AddMessageAsync();
        using var firstScope = fixture.Services.CreateScope();
        using var secondScope = fixture.Services.CreateScope();
        var firstDb = firstScope.ServiceProvider.GetRequiredService<AeroComplianceDbContext>();
        var secondDb = secondScope.ServiceProvider.GetRequiredService<AeroComplianceDbContext>();
        var first = new OutboxClaimStore(firstDb);
        var second = new OutboxClaimStore(secondDb);
        var original = await first.ClaimAsync(1, default);
        Assert.Single(original.Messages, value => value.EventId == eventId);
        await firstDb.OutboxMessages.Where(value => value.EventId == eventId)
            .ExecuteUpdateAsync(update => update.SetProperty(value => value.ClaimUntil,
                DateTimeOffset.UtcNow.AddSeconds(-1)));

        var recovered = await second.ClaimAsync(1, default);

        Assert.Single(recovered.Messages, value => value.EventId == eventId);
        Assert.Equal(0, await first.PublishedAsync(eventId, original.Token, default));
        Assert.Equal(1, await second.PublishedAsync(eventId, recovered.Token, default));
    }

    [Fact]
    public async Task Repeated_failures_reach_dead_state_and_manual_reset_makes_message_due()
    {
        var eventId = await AddMessageAsync();
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroComplianceDbContext>();
        var store = new OutboxClaimStore(db);
        for (var attempt = 1; attempt <= OutboxClaimStore.MaxAttempts; attempt++)
        {
            var claim = await store.ClaimAsync(1, default);
            var message = Assert.Single(claim.Messages, value => value.EventId == eventId);
            Assert.Equal(attempt, message.AttemptCount);
            Assert.Equal(1, await store.FailedAsync(message, claim.Token, "test broker failure", default));
            await db.OutboxMessages.Where(value => value.EventId == eventId)
                .ExecuteUpdateAsync(update => update.SetProperty(value => value.NextAttemptAt,
                    DateTimeOffset.UtcNow.AddSeconds(-1)));
        }

        var failed = await db.OutboxMessages.AsNoTracking().SingleAsync(value => value.EventId == eventId);
        Assert.NotNull(failed.FailedAt);
        Assert.Empty((await store.ClaimAsync(1, default)).Messages);

        await db.OutboxMessages.Where(value => value.EventId == eventId)
            .ExecuteUpdateAsync(update => update.SetProperty(value => value.AttemptCount, 0)
                .SetProperty(value => value.FailedAt, (DateTimeOffset?)null)
                .SetProperty(value => value.NextAttemptAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        Assert.Single((await store.ClaimAsync(1, default)).Messages, value => value.EventId == eventId);
    }

    private async Task<Guid> AddMessageAsync()
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroComplianceDbContext>();
        var eventId = Guid.NewGuid();
        db.OutboxMessages.Add(new OutboxMessage
        {
            EventId = eventId, Type = "IntegrationTest", RoutingKey = "image.processing.requested.v1",
            SchemaVersion = 1, Payload = "{}", CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        return eventId;
    }
}
