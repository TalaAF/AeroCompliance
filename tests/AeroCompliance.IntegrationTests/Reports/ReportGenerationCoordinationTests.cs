using System.Net;
using System.Net.Http.Json;
using AeroCompliance.ApiService.Services;
using AeroCompliance.Domain.Enums;
using AeroCompliance.Infrastructure.Persistence;
using AeroCompliance.Infrastructure.Storage;
using AeroCompliance.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroCompliance.IntegrationTests.Reports;

[Collection(ApiTestCollection.Name)]
public class ReportGenerationCoordinationTests(ApiTestFixture fixture)
{
    [Fact]
    public async Task Two_api_hosts_cannot_generate_the_same_report_concurrently()
    {
        var (reviewer, violationId) = await ConfirmViolationAsync();
        var storage = new ControlledStorage { HoldUploads = true };
        using var firstHost = fixture.CreateReportFactory(storage);
        using var secondHost = fixture.CreateReportFactory(storage);
        using var first = firstHost.CreateClient();
        using var second = secondHost.CreateClient();
        var token = await LoginAsync(reviewer);
        first.UseBearer(token);
        second.UseBearer(token);

        var firstRequest = first.PostAsync($"/api/violations/{violationId}/reports", null);
        await storage.UploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var concurrent = await second.PostAsync($"/api/violations/{violationId}/reports", null);
        storage.ReleaseUploads();
        var completed = await firstRequest;

        Assert.Equal(HttpStatusCode.Conflict, concurrent.StatusCode);
        Assert.Equal(HttpStatusCode.Created, completed.StatusCode);
        Assert.Equal(1, await fixture.CountReportsAsync(violationId));
        var repeat = await second.PostAsync($"/api/violations/{violationId}/reports", null);
        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        Assert.Equal(1, await fixture.CountReportsAsync(violationId));
        var deliberateRegeneration = await second.PostAsync($"/api/violations/{violationId}/reports?regenerate=true", null);
        Assert.Equal(HttpStatusCode.Created, deliberateRegeneration.StatusCode);
        Assert.Equal(2, await fixture.CountReportsAsync(violationId));
    }

    [Fact]
    public async Task Failed_upload_creates_no_report_and_a_later_request_can_retry()
    {
        var (reviewer, violationId) = await ConfirmViolationAsync();
        var storage = new ControlledStorage();
        storage.FailOneUpload();
        using var host = fixture.CreateReportFactory(storage);
        using var client = host.CreateClient();
        client.UseBearer(await LoginAsync(reviewer));

        var failed = await client.PostAsync($"/api/violations/{violationId}/reports", null);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal(0, await fixture.CountReportsAsync(violationId));

        var retried = await client.PostAsync($"/api/violations/{violationId}/reports", null);
        Assert.Equal(HttpStatusCode.Created, retried.StatusCode);
        Assert.Equal(1, await fixture.CountReportsAsync(violationId));
    }

    [Fact]
    public async Task Failed_transaction_releases_the_database_gate_for_retry()
    {
        var id = Guid.NewGuid();
        using var firstScope = fixture.Services.CreateScope();
        using var secondScope = fixture.Services.CreateScope();
        var firstDb = firstScope.ServiceProvider.GetRequiredService<AeroComplianceDbContext>();
        var secondDb = secondScope.ServiceProvider.GetRequiredService<AeroComplianceDbContext>();
        var firstStrategy = firstDb.Database.CreateExecutionStrategy();
        var secondStrategy = secondDb.Database.CreateExecutionStrategy();

        await firstStrategy.ExecuteAsync(async () =>
        {
            await using var firstTransaction = await firstDb.Database.BeginTransactionAsync();
            Assert.True(await ReportGenerationGate.TryAcquireAsync(firstDb, id, default));
            await secondStrategy.ExecuteAsync(async () =>
            {
                await using var secondTransaction = await secondDb.Database.BeginTransactionAsync();
                Assert.False(await ReportGenerationGate.TryAcquireAsync(secondDb, id, default));
                return true;
            });
            // Disposing without commit simulates a failed generator or dead connection.
            return true;
        });
        await secondStrategy.ExecuteAsync(async () =>
        {
            await using var retryTransaction = await secondDb.Database.BeginTransactionAsync();
            Assert.True(await ReportGenerationGate.TryAcquireAsync(secondDb, id, default));
            return true;
        });
    }

    private async Task<(AeroCompliance.Domain.Entities.User Reviewer, Guid ViolationId)> ConfirmViolationAsync()
    {
        var reviewer = await fixture.CreateUserAsync(RoleName.Reviewer);
        var operatorUser = await fixture.CreateUserAsync(RoleName.DroneOperator);
        var violationId = await fixture.CreateReviewViolationAsync(operatorUser.Id);
        using var client = fixture.CreateClient();
        var token = await client.LoginAsync(reviewer);
        client.UseBearer(token.AccessToken);
        var response = await client.PostAsJsonAsync($"/api/violations/{violationId}/confirm", new { notes = "confirmed" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (reviewer, violationId);
    }

    private async Task<string> LoginAsync(AeroCompliance.Domain.Entities.User reviewer)
    {
        using var client = fixture.CreateClient();
        return (await client.LoginAsync(reviewer)).AccessToken;
    }

    private sealed class ControlledStorage : IObjectStorage
    {
        private readonly TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int failNextUpload;
        public TaskCompletionSource<bool> UploadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HoldUploads { get; init; }
        public string Bucket => "integration-tests";
        public Task EnsureBucketAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task UploadFileAsync(string objectKey, string filePath, string contentType, CancellationToken ct = default)
            => throw new NotSupportedException();
        public async Task UploadBytesAsync(string objectKey, byte[] content, string contentType, CancellationToken ct = default)
        {
            UploadStarted.TrySetResult(true);
            if (Interlocked.Exchange(ref failNextUpload, 0) == 1) throw new IOException("Simulated storage failure");
            if (HoldUploads) await release.Task.WaitAsync(ct);
        }
        public Task<Stream> OpenReadAsync(string objectKey, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<bool> ExistsAsync(string objectKey, CancellationToken ct = default) => Task.FromResult(true);
        public Task DeleteAsync(string objectKey, CancellationToken ct = default) => Task.CompletedTask;
        public void ReleaseUploads() => release.TrySetResult(true);
        public void FailOneUpload() => Interlocked.Exchange(ref failNextUpload, 1);
    }
}
