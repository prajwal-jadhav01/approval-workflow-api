using System.Net;
using System.Net.Http.Json;
using ClaimsApproval.Api.Contracts;
using Xunit;

namespace ClaimsApproval.Tests;

public class ClaimsApiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public ClaimsApiTests(ApiFactory factory)
    {
        _factory = factory;
        _factory.EnsureDatabase();
    }

    private static object NewClaim(decimal amount, string category = "travel") =>
        new { amount, currency = "inr", category, description = $"Test claim {Guid.NewGuid():N}" };

    private static async Task<ClaimResponse> CreateAndSubmit(HttpClient employee, decimal amount, string category)
    {
        var created = await employee.PostAsJsonAsync("/api/claims", NewClaim(amount, category));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var claim = (await created.Content.ReadFromJsonAsync<ClaimResponse>())!;
        Assert.Equal("Draft", claim.Status);
        Assert.Equal("INR", claim.Currency);

        var submitted = await employee.PostAsync($"/api/claims/{claim.Id}/submit", null);
        Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);
        return claim;
    }

    private static async Task<string> StatusOf(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<ClaimResponse>($"/api/claims/{id}"))!.Status;

    [Fact]
    public async Task Small_claim_needs_only_manager_approval()
    {
        var employee = await _factory.ClientFor("emp-small", "employee");
        var manager = await _factory.ClientFor("mgr-1", "manager");

        var claim = await CreateAndSubmit(employee, 800m, "meals");
        Assert.Equal("PendingManager", await StatusOf(employee, claim.Id));

        var decision = await manager.PostAsJsonAsync($"/api/claims/{claim.Id}/manager-decision",
            new { approved = true, comment = "Fine" });
        Assert.Equal(HttpStatusCode.Accepted, decision.StatusCode);
        Assert.Equal("Approved", await StatusOf(employee, claim.Id));
    }

    [Fact]
    public async Task Large_claim_also_needs_finance_approval()
    {
        var employee = await _factory.ClientFor("emp-large", "employee");
        var manager = await _factory.ClientFor("mgr-1", "manager");
        var finance = await _factory.ClientFor("fin-1", "finance");

        var claim = await CreateAndSubmit(employee, 4500m, "hardware");
        await manager.PostAsJsonAsync($"/api/claims/{claim.Id}/manager-decision", new { approved = true });
        Assert.Equal("PendingFinance", await StatusOf(employee, claim.Id));

        // A manager cannot take the finance step.
        var wrongRole = await manager.PostAsJsonAsync($"/api/claims/{claim.Id}/finance-decision", new { approved = true });
        Assert.Equal(HttpStatusCode.Forbidden, wrongRole.StatusCode);

        await finance.PostAsJsonAsync($"/api/claims/{claim.Id}/finance-decision", new { approved = true });
        Assert.Equal("Approved", await StatusOf(employee, claim.Id));

        var history = await employee.GetFromJsonAsync<List<ClaimEventResponse>>($"/api/claims/{claim.Id}/history");
        Assert.Contains(history!, h => h.Action == "FinanceApproved" && h.By == "fin-1");
    }

    [Fact]
    public async Task Rejection_records_reason()
    {
        var employee = await _factory.ClientFor("emp-reject", "employee");
        var manager = await _factory.ClientFor("mgr-1", "manager");

        var claim = await CreateAndSubmit(employee, 300m, "books");
        await manager.PostAsJsonAsync($"/api/claims/{claim.Id}/manager-decision",
            new { approved = false, comment = "Not a business expense" });

        var final = await employee.GetFromJsonAsync<ClaimResponse>($"/api/claims/{claim.Id}");
        Assert.Equal("Rejected", final!.Status);
        Assert.NotNull(final.DecisionReason);
    }

    [Fact]
    public async Task Second_decision_on_same_stage_is_a_conflict()
    {
        var employee = await _factory.ClientFor("emp-twice", "employee");
        var manager = await _factory.ClientFor("mgr-1", "manager");

        var claim = await CreateAndSubmit(employee, 2500m, "conference");
        await manager.PostAsJsonAsync($"/api/claims/{claim.Id}/manager-decision", new { approved = true });
        var again = await manager.PostAsJsonAsync($"/api/claims/{claim.Id}/manager-decision", new { approved = false });

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task Employee_can_cancel_while_in_approval()
    {
        var employee = await _factory.ClientFor("emp-cancel", "employee");

        var claim = await CreateAndSubmit(employee, 700m, "taxi");
        var cancel = await employee.PostAsync($"/api/claims/{claim.Id}/cancel", null);

        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
        Assert.Equal("Cancelled", await StatusOf(employee, claim.Id));
    }

    [Fact]
    public async Task Duplicate_claim_is_rejected_by_validation()
    {
        var employee = await _factory.ClientFor("emp-dup", "employee");

        await CreateAndSubmit(employee, 1234m, "training");
        var second = await CreateAndSubmit(employee, 1234m, "training");

        Assert.Equal("Rejected", await StatusOf(employee, second.Id));
    }

    [Fact]
    public async Task Employees_cannot_see_each_others_claims()
    {
        var owner = await _factory.ClientFor("emp-owner", "employee");
        var other = await _factory.ClientFor("emp-other", "employee");

        var created = await owner.PostAsJsonAsync("/api/claims", NewClaim(100m));
        var claim = (await created.Content.ReadFromJsonAsync<ClaimResponse>())!;

        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/claims/{claim.Id}")).StatusCode);
    }

    [Fact]
    public async Task Requests_without_a_token_are_rejected()
    {
        var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/claims")).StatusCode);
    }

    [Fact]
    public async Task Invalid_input_returns_validation_problem()
    {
        var employee = await _factory.ClientFor("emp-invalid", "employee");
        var response = await employee.PostAsJsonAsync("/api/claims",
            new { amount = -5, currency = "INR", category = "travel", description = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Managers_can_list_what_is_waiting_for_them()
    {
        var employee = await _factory.ClientFor("emp-list", "employee");
        var manager = await _factory.ClientFor("mgr-2", "manager");
        var claim = await CreateAndSubmit(employee, 450m, "stationery");

        var page = await manager.GetFromJsonAsync<PagedResponse<ClaimResponse>>(
            "/api/claims?status=PendingManager&pageSize=100");

        Assert.Contains(page!.Items, c => c.Id == claim.Id);
        Assert.All(page.Items, c => Assert.Equal("PendingManager", c.Status));
    }
}
