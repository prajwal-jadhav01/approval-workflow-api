namespace ClaimsApproval.Api.Domain;

public enum ClaimStatus
{
    Draft,
    Submitted,
    PendingManager,
    PendingFinance,
    Approved,
    Rejected,
    Cancelled
}

public enum ApprovalStage
{
    Manager,
    Finance
}

/// <summary>
/// An expense claim and the rules for how it may move between states.
/// The BPMN process decides the order of steps; this class makes sure no step
/// can push a claim into a state it isn't allowed to reach, whoever calls it.
/// </summary>
public class ExpenseClaim
{
    /// <summary>Claims above this amount also need finance approval.</summary>
    public const decimal FinanceApprovalThreshold = 1000m;

    public Guid Id { get; private set; }
    public string EmployeeId { get; private set; } = default!;
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = "INR";
    public string Category { get; private set; } = default!;
    public string Description { get; private set; } = default!;
    public ClaimStatus Status { get; private set; }
    public long? ProcessInstanceKey { get; private set; }
    public string? DecisionReason { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    /// <summary>Optimistic concurrency token, so two approvers can't overwrite each other.</summary>
    public Guid Version { get; private set; }

    private readonly List<ClaimEvent> _history = new();
    public IReadOnlyList<ClaimEvent> History => _history;

    private ExpenseClaim()
    {
    }

    public static ExpenseClaim CreateDraft(string employeeId, decimal amount, string currency, string category,
        string description, DateTime now)
    {
        if (amount <= 0) throw new DomainException("Amount must be greater than zero.");
        var claim = new ExpenseClaim
        {
            Id = Guid.NewGuid(),
            EmployeeId = employeeId,
            Amount = amount,
            Currency = currency,
            Category = category,
            Description = description,
            Status = ClaimStatus.Draft,
            CreatedAt = now,
        };
        claim.Record("Created", employeeId, null, now);
        return claim;
    }

    public bool NeedsFinanceApproval => Amount > FinanceApprovalThreshold;

    public bool IsFinal => Status is ClaimStatus.Approved or ClaimStatus.Rejected or ClaimStatus.Cancelled;

    public void Submit(string by, DateTime now)
    {
        Require(Status == ClaimStatus.Draft, "Only draft claims can be submitted.");
        Status = ClaimStatus.Submitted;
        Record("Submitted", by, null, now);
    }

    public void AwaitApproval(ApprovalStage stage, DateTime now)
    {
        var expectedFrom = stage == ApprovalStage.Manager ? ClaimStatus.Submitted : ClaimStatus.PendingManager;
        // Idempotent: a retried workflow job must not fail because the first attempt already did the work.
        var target = stage == ApprovalStage.Manager ? ClaimStatus.PendingManager : ClaimStatus.PendingFinance;
        if (Status == target) return;
        Require(Status == expectedFrom, $"Cannot wait for {stage} approval while the claim is {Status}.");
        Status = target;
        Record($"Awaiting{stage}Approval", "workflow", null, now);
    }

    /// <summary>Checks that a person's decision is allowed right now. The workflow applies the outcome.</summary>
    public void EnsureCanDecide(ApprovalStage stage, string approverId)
    {
        var expected = stage == ApprovalStage.Manager ? ClaimStatus.PendingManager : ClaimStatus.PendingFinance;
        Require(Status == expected, $"Claim is {Status}, not waiting for {stage} approval.");
        Require(approverId != EmployeeId, "You cannot approve your own claim.");
        Require(!_history.Any(h => h.Action == $"{stage}Approved" || h.Action == $"{stage}Rejected"),
            $"The {stage.ToString().ToLowerInvariant()} decision has already been recorded.");
    }

    public void RecordDecision(ApprovalStage stage, bool approved, string by, string? comment, DateTime now)
    {
        Record($"{stage}{(approved ? "Approved" : "Rejected")}", by, comment, now);
    }

    public void Approve(DateTime now)
    {
        if (Status == ClaimStatus.Approved) return;
        Require(Status is ClaimStatus.PendingManager or ClaimStatus.PendingFinance,
            $"Cannot approve a claim that is {Status}.");
        Status = ClaimStatus.Approved;
        Record("Approved", "workflow", null, now);
    }

    public void Reject(string reason, DateTime now)
    {
        if (Status == ClaimStatus.Rejected) return;
        Require(!IsFinal, $"Cannot reject a claim that is {Status}.");
        Status = ClaimStatus.Rejected;
        DecisionReason = reason;
        Record("Rejected", "workflow", reason, now);
    }

    public void EnsureCanCancel(string by)
    {
        Require(by == EmployeeId, "Only the employee who raised the claim can cancel it.");
        Require(Status is ClaimStatus.Submitted or ClaimStatus.PendingManager or ClaimStatus.PendingFinance,
            $"A claim that is {Status} cannot be cancelled.");
    }

    public void Cancel(DateTime now)
    {
        if (Status == ClaimStatus.Cancelled) return;
        Require(!IsFinal, $"A claim that is {Status} cannot be cancelled.");
        Status = ClaimStatus.Cancelled;
        Record("Cancelled", EmployeeId, null, now);
    }

    private void Record(string action, string by, string? comment, DateTime now)
    {
        _history.Add(new ClaimEvent(Id, action, by, comment, now));
        UpdatedAt = now;
        Version = Guid.NewGuid();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new DomainException(message);
    }
}

public class ClaimEvent
{
    public long Id { get; private set; }
    public Guid ClaimId { get; private set; }
    public string Action { get; private set; } = default!;
    public string By { get; private set; } = default!;
    public string? Comment { get; private set; }
    public DateTime At { get; private set; }

    private ClaimEvent()
    {
    }

    public ClaimEvent(Guid claimId, string action, string by, string? comment, DateTime at)
    {
        ClaimId = claimId;
        Action = action;
        By = by;
        Comment = comment;
        At = at;
    }
}

/// <summary>A business rule was broken. Mapped to HTTP 409 Conflict.</summary>
public class DomainException(string message) : Exception(message);
