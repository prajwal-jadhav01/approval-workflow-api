using System.Text.Json;
using ClaimsApproval.Api.Domain;
using Zeebe.Client;
using Zeebe.Client.Api.Responses;
using Zeebe.Client.Api.Worker;

namespace ClaimsApproval.Api.Workflow;

/// <summary>
/// Deploys the BPMN model on startup and opens one Zeebe job worker per service task type.
/// Each job runs in its own DI scope (its own DbContext), like an HTTP request would.
/// </summary>
public class ZeebeJobWorkers(
    IZeebeClient zeebe,
    IServiceScopeFactory scopes,
    ILogger<ZeebeJobWorkers> log) : BackgroundService
{
    private readonly List<IJobWorker> _workers = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await DeployProcessWithRetry(stoppingToken);

        Open(Bpmn.Jobs.Validate, async (h, vars, ct) =>
        {
            var result = await h.ValidateAsync(vars.ClaimId, ct);
            return new { valid = result.Valid, rejectionReason = result.RejectionReason };
        });
        Open(Bpmn.Jobs.RequestManagerApproval,
            async (h, vars, ct) => { await h.RequestApprovalAsync(vars.ClaimId, ApprovalStage.Manager, ct); return null; });
        Open(Bpmn.Jobs.RequestFinanceApproval,
            async (h, vars, ct) => { await h.RequestApprovalAsync(vars.ClaimId, ApprovalStage.Finance, ct); return null; });
        Open(Bpmn.Jobs.Approve, async (h, vars, ct) => { await h.ApproveAsync(vars.ClaimId, ct); return null; });
        Open(Bpmn.Jobs.Reject, async (h, vars, ct) =>
        {
            await h.RejectAsync(vars.ClaimId, vars.RejectionReason ?? "Rejected", ct);
            return null;
        });
        Open(Bpmn.Jobs.Cancel, async (h, vars, ct) => { await h.CancelAsync(vars.ClaimId, ct); return null; });

        log.LogInformation("Opened {Count} Zeebe job workers", _workers.Count);
        await Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    private record JobVariables(Guid ClaimId, string? RejectionReason);

    private void Open(string jobType, Func<ClaimWorkflowHandlers, JobVariables, CancellationToken, Task<object?>> handle)
    {
        var worker = zeebe.NewWorker()
            .JobType(jobType)
            .Handler(async (jobClient, job) =>
            {
                try
                {
                    var vars = Parse(job);
                    using var scope = scopes.CreateScope();
                    var handlers = scope.ServiceProvider.GetRequiredService<ClaimWorkflowHandlers>();
                    var output = await handle(handlers, vars, CancellationToken.None);

                    var complete = jobClient.NewCompleteJobCommand(job.Key);
                    if (output is not null) complete.Variables(JsonSerializer.Serialize(output));
                    await complete.Send();
                }
                catch (Exception ex)
                {
                    // Hand the job back with one retry fewer. At zero retries Zeebe raises an incident
                    // that shows up in Operate, instead of the claim silently getting stuck.
                    log.LogWarning(ex, "Job {JobType} {JobKey} failed ({Retries} retries left)",
                        jobType, job.Key, job.Retries - 1);
                    await jobClient.NewFailCommand(job.Key)
                        .Retries(Math.Max(job.Retries - 1, 0))
                        .ErrorMessage(ex.Message)
                        .RetryBackOff(TimeSpan.FromSeconds(5))
                        .Send();
                }
            })
            .MaxJobsActive(10)
            .Name($"claims-api-{Environment.MachineName}")
            .Timeout(TimeSpan.FromSeconds(30))
            .PollInterval(TimeSpan.FromMilliseconds(250))
            .Open();
        _workers.Add(worker);
    }

    private static JobVariables Parse(IJob job)
    {
        using var doc = JsonDocument.Parse(job.Variables);
        var root = doc.RootElement;
        var claimId = Guid.Parse(root.GetProperty("claimId").GetString()!);
        string? reason = root.TryGetProperty("rejectionReason", out var r) && r.ValueKind == JsonValueKind.String
            ? r.GetString()
            : null;
        return new JobVariables(claimId, reason);
    }

    private async Task DeployProcessWithRetry(CancellationToken ct)
    {
        var file = Path.Combine(AppContext.BaseDirectory, "bpmn", "expense-claim-approval.bpmn");
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var result = await zeebe.NewDeployCommand().AddResourceFile(file).Send(ct);
                var process = result.Processes.Single();
                log.LogInformation("Deployed {ProcessId} version {Version}", process.BpmnProcessId, process.Version);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && attempt < 30)
            {
                log.LogWarning("Zeebe not ready yet ({Message}); retrying in 2s", ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var w in _workers) w.Dispose();
        await base.StopAsync(cancellationToken);
    }
}
