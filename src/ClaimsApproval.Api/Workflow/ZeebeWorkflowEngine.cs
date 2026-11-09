using System.Text.Json;
using ClaimsApproval.Api.Domain;
using Grpc.Core;
using Zeebe.Client;

namespace ClaimsApproval.Api.Workflow;

/// <summary>Camunda 8 (Zeebe) implementation of <see cref="IWorkflowEngine"/>.</summary>
public class ZeebeWorkflowEngine(IZeebeClient zeebe) : IWorkflowEngine
{
    // A decision is only accepted while the claim is waiting for it, so a short TTL is enough
    // to cover the small gap between "status updated" and "process subscribed to the message".
    private static readonly TimeSpan MessageTtl = TimeSpan.FromMinutes(1);

    public async Task<long> StartApprovalAsync(Guid claimId, decimal amount, CancellationToken ct)
    {
        var variables = JsonSerializer.Serialize(new { claimId = claimId.ToString(), amount });
        var instance = await zeebe.NewCreateProcessInstanceCommand()
            .BpmnProcessId(Bpmn.ProcessId)
            .LatestVersion()
            .Variables(variables)
            .Send(ct);
        return instance.ProcessInstanceKey;
    }

    public async Task PublishDecisionAsync(Guid claimId, ApprovalStage stage, bool approved, string approverId,
        string? comment, CancellationToken ct)
    {
        var (message, flag) = stage == ApprovalStage.Manager
            ? (Bpmn.Messages.ManagerDecision, "managerApproved")
            : (Bpmn.Messages.FinanceDecision, "financeApproved");

        var variables = new Dictionary<string, object?>
        {
            [flag] = approved,
            ["lastApprover"] = approverId,
        };
        if (!approved)
        {
            variables["rejectionReason"] = $"Rejected by {stage.ToString().ToLowerInvariant()} ({approverId})"
                                           + (string.IsNullOrWhiteSpace(comment) ? "" : $": {comment}");
        }

        await PublishOnce(zeebe.NewPublishMessageCommand()
            .MessageName(message)
            .CorrelationKey(claimId.ToString())
            .MessageId($"{message}-{claimId}") // a second identical message is rejected by Zeebe, not delivered twice
            .TimeToLive(MessageTtl)
            .Variables(JsonSerializer.Serialize(variables))
            .Send(ct));
    }

    public async Task PublishCancellationAsync(Guid claimId, CancellationToken ct)
    {
        await PublishOnce(zeebe.NewPublishMessageCommand()
            .MessageName(Bpmn.Messages.CancelClaim)
            .CorrelationKey(claimId.ToString())
            .MessageId($"{Bpmn.Messages.CancelClaim}-{claimId}")
            .TimeToLive(MessageTtl)
            .Send(ct));
    }

    /// <summary>A duplicate message id means the same message is already on its way: treat as success.</summary>
    private static async Task PublishOnce(Task publish)
    {
        try
        {
            await publish;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.AlreadyExists)
        {
        }
    }
}
