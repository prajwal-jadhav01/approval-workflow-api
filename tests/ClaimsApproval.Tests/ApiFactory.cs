using System.Net.Http.Headers;
using System.Net.Http.Json;
using ClaimsApproval.Api.Data;
using ClaimsApproval.Api.Domain;
using ClaimsApproval.Api.Workflow;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ClaimsApproval.Tests;

/// <summary>
/// Runs the real API in memory with SQLite instead of Postgres, and an in-process stand-in for
/// Camunda that follows the same path as the BPMN model. The real engine is covered by
/// scripts/smoke-test.sh against docker compose.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Workflow:Enabled", "false");
        builder.UseSetting("Database:MigrateOnStartup", "false");
        builder.UseSetting("Jwt:SigningKey", "test-signing-key-that-is-long-enough-0123456789");
        builder.UseSetting("Jwt:EnableDevTokens", "true");

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<ClaimsDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<ClaimsDbContext>>();
            _connection.Open();
            services.AddDbContext<ClaimsDbContext>(o => o.UseSqlite(_connection));
            services.AddSingleton<IWorkflowEngine, InProcessWorkflow>();
        });
    }

    public void EnsureDatabase()
    {
        using var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ClaimsDbContext>().Database.EnsureCreated();
    }

    public async Task<HttpClient> ClientFor(string userId, params string[] roles)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/dev/token", new { userId, roles });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<TokenBody>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body!.AccessToken);
        return client;
    }

    private record TokenBody(string AccessToken);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _connection.Dispose();
    }
}

/// <summary>Mirrors bpmn/expense-claim-approval.bpmn by calling the same handlers the job workers call.</summary>
public class InProcessWorkflow(IServiceScopeFactory scopes) : IWorkflowEngine
{
    private long _nextKey = 1000;

    public async Task<long> StartApprovalAsync(Guid claimId, decimal amount, CancellationToken ct)
    {
        await Run(async h =>
        {
            var result = await h.ValidateAsync(claimId, ct);
            if (result.Valid) await h.RequestApprovalAsync(claimId, ApprovalStage.Manager, ct);
            else await h.RejectAsync(claimId, result.RejectionReason!, ct);
        });
        return Interlocked.Increment(ref _nextKey);
    }

    public Task PublishDecisionAsync(Guid claimId, ApprovalStage stage, bool approved, string approverId,
        string? comment, CancellationToken ct) => Run(async h =>
    {
        if (!approved)
        {
            await h.RejectAsync(claimId, $"Rejected by {stage.ToString().ToLowerInvariant()}", ct);
            return;
        }

        using var scope = scopes.CreateScope();
        var amount = (await scope.ServiceProvider.GetRequiredService<ClaimsDbContext>().Claims
            .SingleAsync(c => c.Id == claimId, ct)).Amount;
        if (stage == ApprovalStage.Manager && amount > ExpenseClaim.FinanceApprovalThreshold)
            await h.RequestApprovalAsync(claimId, ApprovalStage.Finance, ct);
        else
            await h.ApproveAsync(claimId, ct);
    });

    public Task PublishCancellationAsync(Guid claimId, CancellationToken ct) =>
        Run(h => h.CancelAsync(claimId, ct));

    private async Task Run(Func<ClaimWorkflowHandlers, Task> step)
    {
        using var scope = scopes.CreateScope();
        await step(scope.ServiceProvider.GetRequiredService<ClaimWorkflowHandlers>());
    }
}
