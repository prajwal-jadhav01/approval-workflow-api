using Prometheus;

namespace ClaimsApproval.Api.Workflow;

/// <summary>Business metrics exposed on /metrics next to the standard HTTP metrics.</summary>
public class ClaimMetrics
{
    private static readonly Counter Submitted = Metrics.CreateCounter(
        "claims_submitted_total", "Expense claims submitted for approval.");

    private static readonly Counter Outcomes = Metrics.CreateCounter(
        "claims_completed_total", "Expense claims that reached a final state.",
        new CounterConfiguration { LabelNames = ["outcome"] });

    public void SubmittedOne() => Submitted.Inc();

    public void Decided(string outcome) => Outcomes.WithLabels(outcome).Inc();
}
