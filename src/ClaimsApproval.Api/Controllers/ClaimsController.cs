using ClaimsApproval.Api.Auth;
using ClaimsApproval.Api.Contracts;
using ClaimsApproval.Api.Data;
using ClaimsApproval.Api.Domain;
using ClaimsApproval.Api.Workflow;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClaimsApproval.Api.Controllers;

[ApiController]
[Route("api/claims")]
[Authorize]
[Produces("application/json")]
public class ClaimsController(
    ClaimsDbContext db,
    IWorkflowEngine workflow,
    ClaimMetrics metrics,
    TimeProvider clock) : ControllerBase
{
    private string UserId => User.Identity!.Name!;
    private bool IsApprover => User.IsInRole(Roles.Manager) || User.IsInRole(Roles.Finance);

    /// <summary>Create a draft claim. Nothing is sent for approval until it is submitted.</summary>
    [HttpPost]
    [Authorize(Policy = Roles.Employee)]
    [ProducesResponseType<ClaimResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(CreateClaimRequest request, CancellationToken ct)
    {
        var claim = ExpenseClaim.CreateDraft(UserId, request.Amount, request.Currency.ToUpperInvariant(),
            request.Category, request.Description, clock.GetUtcNow().UtcDateTime);
        db.Claims.Add(claim);
        await db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = claim.Id }, ClaimResponse.From(claim));
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<ClaimResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var claim = await FindVisible(id, ct);
        return claim is null ? NotFound() : Ok(ClaimResponse.From(claim));
    }

    /// <summary>
    /// List claims, newest first. Employees see their own claims; managers and finance see all,
    /// which is how they find what is waiting for them (e.g. ?status=PendingManager).
    /// </summary>
    [HttpGet]
    [ProducesResponseType<PagedResponse<ClaimResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] ClaimStatus? status, [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.Claims.AsNoTracking();
        if (!IsApprover) query = query.Where(c => c.EmployeeId == UserId);
        if (status is not null) query = query.Where(c => c.Status == status);

        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(c => c.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct);
        return Ok(new PagedResponse<ClaimResponse>(items.Select(ClaimResponse.From).ToList(), page, pageSize, total));
    }

    [HttpGet("{id:guid}/history")]
    [ProducesResponseType<IEnumerable<ClaimEventResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> History(Guid id, CancellationToken ct)
    {
        var claim = await FindVisible(id, ct);
        if (claim is null) return NotFound();
        return Ok(claim.History.OrderBy(h => h.At).ThenBy(h => h.Id)
            .Select(h => new ClaimEventResponse(h.Action, h.By, h.Comment, h.At)));
    }

    /// <summary>Submit a draft: starts the BPMN approval process in Camunda.</summary>
    [HttpPost("{id:guid}/submit")]
    [Authorize(Policy = Roles.Employee)]
    [ProducesResponseType<ClaimResponse>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> Submit(Guid id, CancellationToken ct)
    {
        var claim = await db.Claims.Include(c => c.History).SingleOrDefaultAsync(c => c.Id == id, ct);
        if (claim is null || claim.EmployeeId != UserId) return NotFound();

        claim.Submit(UserId, clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct);

        // Saved first, then the process is started: the workers that run next must be able to find the claim.
        var key = await workflow.StartApprovalAsync(claim.Id, claim.Amount, ct);
        // The process may already have moved the claim on by now (a worker saved a newer version),
        // so the key is written with a targeted update instead of re-saving this stale copy.
        await db.Claims.Where(c => c.Id == claim.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ProcessInstanceKey, key), ct);
        metrics.SubmittedOne();

        var current = await db.Claims.AsNoTracking().SingleAsync(c => c.Id == claim.Id, ct);
        return Accepted(ClaimResponse.From(current));
    }

    [HttpPost("{id:guid}/manager-decision")]
    [Authorize(Policy = Roles.Manager)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> ManagerDecision(Guid id, DecisionRequest request, CancellationToken ct) =>
        Decide(id, ApprovalStage.Manager, request, ct);

    [HttpPost("{id:guid}/finance-decision")]
    [Authorize(Policy = Roles.Finance)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> FinanceDecision(Guid id, DecisionRequest request, CancellationToken ct) =>
        Decide(id, ApprovalStage.Finance, request, ct);

    /// <summary>Withdraw a claim that is still in approval. Interrupts the running process.</summary>
    [HttpPost("{id:guid}/cancel")]
    [Authorize(Policy = Roles.Employee)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        var claim = await db.Claims.SingleOrDefaultAsync(c => c.Id == id, ct);
        if (claim is null || claim.EmployeeId != UserId) return NotFound();

        claim.EnsureCanCancel(UserId);
        await workflow.PublishCancellationAsync(claim.Id, ct);
        return Accepted();
    }

    private async Task<IActionResult> Decide(Guid id, ApprovalStage stage, DecisionRequest request,
        CancellationToken ct)
    {
        var claim = await db.Claims.Include(c => c.History).SingleOrDefaultAsync(c => c.Id == id, ct);
        if (claim is null) return NotFound();

        claim.EnsureCanDecide(stage, UserId);
        claim.RecordDecision(stage, request.Approved, UserId, request.Comment, clock.GetUtcNow().UtcDateTime);
        // The concurrency token makes a second approver acting at the same moment fail with 409.
        await db.SaveChangesAsync(ct);

        await workflow.PublishDecisionAsync(claim.Id, stage, request.Approved, UserId, request.Comment, ct);
        return Accepted();
    }

    private async Task<ExpenseClaim?> FindVisible(Guid id, CancellationToken ct)
    {
        var claim = await db.Claims.AsNoTracking().Include(c => c.History).SingleOrDefaultAsync(c => c.Id == id, ct);
        if (claim is null) return null;
        return IsApprover || claim.EmployeeId == UserId ? claim : null;
    }
}
