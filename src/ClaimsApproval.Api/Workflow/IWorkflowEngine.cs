using ClaimsApproval.Api.Domain;

namespace ClaimsApproval.Api.Workflow;

/// <summary>
/// What the API needs from the workflow engine. Kept small and engine-agnostic so the
/// controllers can be tested without a running Camunda cluster.
/// </summary>
public interface IWorkflowEngine
{
    /// <summary>Starts the approval process for a claim and returns the process instance key.</summary>
    Task<long> StartApprovalAsync(Guid claimId, decimal amount, CancellationToken ct);

    /// <summary>Delivers an approver's decision to the waiting process instance.</summary>
    Task PublishDecisionAsync(Guid claimId, ApprovalStage stage, bool approved, string approverId, string? comment,
        CancellationToken ct);

    /// <summary>Interrupts the process wherever it is and cancels the claim.</summary>
    Task PublishCancellationAsync(Guid claimId, CancellationToken ct);
}

/// <summary>Names shared by the BPMN file and the code. Change one, change both.</summary>
public static class Bpmn
{
    public const string ProcessId = "expense-claim-approval";

    public static class Jobs
    {
        public const string Validate = "claim.validate";
        public const string RequestManagerApproval = "claim.request-manager-approval";
        public const string RequestFinanceApproval = "claim.request-finance-approval";
        public const string Approve = "claim.approve";
        public const string Reject = "claim.reject";
        public const string Cancel = "claim.cancel";
    }

    public static class Messages
    {
        public const string ManagerDecision = "manager-decision";
        public const string FinanceDecision = "finance-decision";
        public const string CancelClaim = "cancel-claim";
    }
}
