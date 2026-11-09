using ClaimsApproval.Api.Data;
using ClaimsApproval.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace ClaimsApproval.Api.Workflow;

/// <summary>
/// What each BPMN service task actually does. Plain methods over the database, so they can be
/// unit tested; <see cref="ZeebeJobWorkers"/> only translates jobs into these calls.
/// Every handler is idempotent: Zeebe may deliver the same job twice after a timeout or crash.
/// </summary>
public class ClaimWorkflowHandlers(ClaimsDbContext db, TimeProvider clock, ClaimMetrics metrics)
{
    public const decimal MaxClaimAmount = 200_000m;

    public record ValidationResult(bool Valid, string? RejectionReason);

    public async Task<ValidationResult> ValidateAsync(Guid claimId, CancellationToken ct)
    {
        var claim = await Load(claimId, ct);
        if (claim.Amount > MaxClaimAmount)
        {
            return new(false, $"Amount exceeds the {MaxClaimAmount:N0} limit for a single claim.");
        }

        var duplicates = await db.Claims.CountAsync(c =>
            c.Id != claim.Id && c.EmployeeId == claim.EmployeeId && c.Amount == claim.Amount
            && c.Category == claim.Category && c.Status != ClaimStatus.Rejected && c.Status != ClaimStatus.Cancelled
            && c.Status != ClaimStatus.Draft, ct);
        if (duplicates > 0)
        {
            return new(false, "A claim with the same amount and category is already in progress or approved.");
        }

        return new(true, null);
    }

    public async Task RequestApprovalAsync(Guid claimId, ApprovalStage stage, CancellationToken ct)
    {
        var claim = await Load(claimId, ct);
        claim.AwaitApproval(stage, clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct);
    }

    public async Task ApproveAsync(Guid claimId, CancellationToken ct)
    {
        var claim = await Load(claimId, ct);
        var wasApproved = claim.Status == ClaimStatus.Approved;
        claim.Approve(clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct);
        if (!wasApproved) metrics.Decided("approved");
    }

    public async Task RejectAsync(Guid claimId, string reason, CancellationToken ct)
    {
        var claim = await Load(claimId, ct);
        var wasRejected = claim.Status == ClaimStatus.Rejected;
        claim.Reject(reason, clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct);
        if (!wasRejected) metrics.Decided("rejected");
    }

    public async Task CancelAsync(Guid claimId, CancellationToken ct)
    {
        var claim = await Load(claimId, ct);
        var wasCancelled = claim.Status == ClaimStatus.Cancelled;
        claim.Cancel(clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct);
        if (!wasCancelled) metrics.Decided("cancelled");
    }

    private async Task<ExpenseClaim> Load(Guid id, CancellationToken ct) =>
        await db.Claims.Include(c => c.History).SingleOrDefaultAsync(c => c.Id == id, ct)
        ?? throw new DomainException($"Claim {id} not found.");
}
