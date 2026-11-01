using System.Net;
using System.Net.Http.Json;
using AeroCompliance.Domain.Enums;
using AeroCompliance.IntegrationTests.Infrastructure;

namespace AeroCompliance.IntegrationTests.Violations;

[Collection(ApiTestCollection.Name)]
public class ViolationDecisionConcurrencyTests(ApiTestFixture fixture)
{
    [Fact]
    public async Task Concurrent_reviewers_only_commit_one_decision()
    {
        var reviewer = await fixture.CreateUserAsync(RoleName.Reviewer);
        var droneOperator = await fixture.CreateUserAsync(RoleName.DroneOperator);
        var violationId = await fixture.CreateReviewViolationAsync(droneOperator.Id);
        using var loginClient = fixture.CreateClient();
        var tokens = await loginClient.LoginAsync(reviewer);
        using var confirmClient = fixture.CreateClient();
        using var dismissClient = fixture.CreateClient();
        confirmClient.UseBearer(tokens.AccessToken);
        dismissClient.UseBearer(tokens.AccessToken);

        var responses = await Task.WhenAll(
            confirmClient.PostAsJsonAsync($"/api/violations/{violationId}/confirm", new { notes = "confirmed" }),
            dismissClient.PostAsJsonAsync($"/api/violations/{violationId}/dismiss", new { notes = "dismissed" }));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(1, await fixture.CountReviewDecisionsAsync(violationId));
    }

    [Fact]
    public async Task Reviewer_from_another_municipality_cannot_read_or_decide_violation()
    {
        var otherMunicipalityId = await fixture.CreateMunicipalityAsync();
        var reviewer = await fixture.CreateUserAsync(RoleName.Reviewer, municipalityId: otherMunicipalityId);
        var droneOperator = await fixture.CreateUserAsync(RoleName.DroneOperator);
        var violationId = await fixture.CreateReviewViolationAsync(droneOperator.Id);
        using var client = fixture.CreateClient();
        var tokens = await client.LoginAsync(reviewer);
        client.UseBearer(tokens.AccessToken);

        var read = await client.GetAsync($"/api/violations/{violationId}");
        var decision = await client.PostAsJsonAsync(
            $"/api/violations/{violationId}/confirm", new { notes = "not my municipality" });

        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, decision.StatusCode);
        Assert.Equal(0, await fixture.CountReviewDecisionsAsync(violationId));
    }
}
