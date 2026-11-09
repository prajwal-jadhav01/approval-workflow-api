using ClaimsApproval.Api.Domain;
using Xunit;

namespace ClaimsApproval.Tests;

public class ExpenseClaimTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

    private static ExpenseClaim Submitted(decimal amount = 500m)
    {
        var claim = ExpenseClaim.CreateDraft("asha", amount, "INR", "travel", "Cab to client site", Now);
        claim.Submit("asha", Now);
        return claim;
    }

    [Fact]
    public void Draft_must_have_positive_amount()
    {
        Assert.Throws<DomainException>(() =>
            ExpenseClaim.CreateDraft("asha", 0m, "INR", "travel", "Nothing", Now));
    }

    [Fact]
    public void Full_happy_path_records_history()
    {
        var claim = Submitted(1500m);
        claim.AwaitApproval(ApprovalStage.Manager, Now);
        claim.EnsureCanDecide(ApprovalStage.Manager, "ravi");
        claim.RecordDecision(ApprovalStage.Manager, true, "ravi", "ok", Now);
        claim.AwaitApproval(ApprovalStage.Finance, Now);
        claim.Approve(Now);

        Assert.Equal(ClaimStatus.Approved, claim.Status);
        Assert.True(claim.NeedsFinanceApproval);
        Assert.Equal(
            ["Created", "Submitted", "AwaitingManagerApproval", "ManagerApproved", "AwaitingFinanceApproval", "Approved"],
            claim.History.Select(h => h.Action));
    }

    [Fact]
    public void Cannot_approve_own_claim()
    {
        var claim = Submitted();
        claim.AwaitApproval(ApprovalStage.Manager, Now);
        var ex = Assert.Throws<DomainException>(() => claim.EnsureCanDecide(ApprovalStage.Manager, "asha"));
        Assert.Contains("own claim", ex.Message);
    }

    [Fact]
    public void Cannot_decide_twice_on_the_same_stage()
    {
        var claim = Submitted();
        claim.AwaitApproval(ApprovalStage.Manager, Now);
        claim.RecordDecision(ApprovalStage.Manager, true, "ravi", null, Now);
        Assert.Throws<DomainException>(() => claim.EnsureCanDecide(ApprovalStage.Manager, "meera"));
    }

    [Fact]
    public void Finance_decision_not_allowed_while_waiting_for_manager()
    {
        var claim = Submitted(5000m);
        claim.AwaitApproval(ApprovalStage.Manager, Now);
        Assert.Throws<DomainException>(() => claim.EnsureCanDecide(ApprovalStage.Finance, "fin"));
    }

    [Fact]
    public void Workflow_steps_are_idempotent()
    {
        var claim = Submitted();
        claim.AwaitApproval(ApprovalStage.Manager, Now);
        claim.AwaitApproval(ApprovalStage.Manager, Now); // job delivered twice
        claim.Approve(Now);
        claim.Approve(Now);
        Assert.Equal(ClaimStatus.Approved, claim.Status);
        Assert.Single(claim.History, h => h.Action == "Approved");
    }

    [Fact]
    public void Final_claims_cannot_be_cancelled()
    {
        var claim = Submitted();
        claim.Reject("duplicate", Now);
        Assert.Throws<DomainException>(() => claim.EnsureCanCancel("asha"));
    }

    [Fact]
    public void Only_the_owner_can_cancel()
    {
        var claim = Submitted();
        Assert.Throws<DomainException>(() => claim.EnsureCanCancel("someone-else"));
    }
}
