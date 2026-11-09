using System.ComponentModel.DataAnnotations;
using ClaimsApproval.Api.Domain;

namespace ClaimsApproval.Api.Contracts;

public record CreateClaimRequest(
    [Range(0.01, 10_000_000)] decimal Amount,
    [Required, StringLength(3, MinimumLength = 3)] string Currency,
    [Required, StringLength(50)] string Category,
    [Required, StringLength(500, MinimumLength = 5)] string Description);

public record DecisionRequest(bool Approved, [StringLength(500)] string? Comment);

public record DevTokenRequest([Required] string UserId, [Required, MinLength(1)] string[] Roles);

public record ClaimResponse(
    Guid Id,
    string EmployeeId,
    decimal Amount,
    string Currency,
    string Category,
    string Description,
    string Status,
    bool NeedsFinanceApproval,
    string? DecisionReason,
    long? ProcessInstanceKey,
    DateTime CreatedAt,
    DateTime UpdatedAt)
{
    public static ClaimResponse From(ExpenseClaim c) => new(c.Id, c.EmployeeId, c.Amount, c.Currency, c.Category,
        c.Description, c.Status.ToString(), c.NeedsFinanceApproval, c.DecisionReason, c.ProcessInstanceKey,
        c.CreatedAt, c.UpdatedAt);
}

public record ClaimEventResponse(string Action, string By, string? Comment, DateTime At);

public record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);
