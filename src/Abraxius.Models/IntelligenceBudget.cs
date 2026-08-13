using System.Collections.Concurrent;
using Abraxius.Protocol;

namespace Abraxius.Models;

/// <summary>Immutable usage totals for one execution's model requests.</summary>
public sealed record IntelligenceUsageSnapshot(
    ExecutionId ExecutionId,
    int ModelCalls,
    decimal EstimatedCost,
    int PremiumTokens,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Admission ledger for execution-scoped intelligence budgets.
/// Reservations are deliberately conservative: a request consumes its estimated
/// cost before submission so a burst cannot race past a mission ceiling.
/// </summary>
public sealed class IntelligenceBudgetLedger
{
    private readonly ConcurrentDictionary<ExecutionId, Usage> _usage = new();

    /// <summary>Reserves one model request if all supplied limits remain available.</summary>
    public bool TryReserve(
        ExecutionId executionId,
        int? maximumCalls,
        decimal? maximumCost,
        int? maximumPremiumTokens,
        decimal estimatedCost,
        int estimatedPremiumTokens,
        out RuntimeError? error)
    {
        if (maximumCalls is null && maximumCost is null && maximumPremiumTokens is null)
        {
            error = null;
            return true;
        }

        var usage = _usage.GetOrAdd(executionId, static _ => new Usage());
        lock (usage)
        {
            if (maximumCalls is { } calls && usage.ModelCalls >= calls)
            {
                error = new RuntimeError(
                    ErrorCategory.Policy,
                    "model_call_budget_exhausted",
                    $"Execution model-call budget exhausted ({calls}).");
                return false;
            }

            if (maximumCost is { } cost && usage.EstimatedCost + Math.Max(0, estimatedCost) > cost)
            {
                error = new RuntimeError(
                    ErrorCategory.Policy,
                    "model_cost_budget_exhausted",
                    $"Execution model-cost budget would be exceeded ({cost:0.####}).");
                return false;
            }

            if (maximumPremiumTokens is { } premiumTokens &&
                usage.PremiumTokens + Math.Max(0, estimatedPremiumTokens) > premiumTokens)
            {
                error = new RuntimeError(
                    ErrorCategory.Policy,
                    "premium_token_budget_exhausted",
                    $"Execution premium-token budget would be exceeded ({premiumTokens}).");
                return false;
            }

            usage.ModelCalls++;
            usage.EstimatedCost += Math.Max(0, estimatedCost);
            usage.PremiumTokens += Math.Max(0, estimatedPremiumTokens);
            usage.UpdatedAt = DateTimeOffset.UtcNow;
            error = null;
            return true;
        }
    }

    /// <summary>Returns a point-in-time usage snapshot, or an empty snapshot when unused.</summary>
    public IntelligenceUsageSnapshot Get(ExecutionId executionId)
    {
        if (!_usage.TryGetValue(executionId, out var usage))
        {
            return new IntelligenceUsageSnapshot(executionId, 0, 0, 0, DateTimeOffset.UtcNow);
        }

        lock (usage)
        {
            return new IntelligenceUsageSnapshot(
                executionId,
                usage.ModelCalls,
                usage.EstimatedCost,
                usage.PremiumTokens,
                usage.UpdatedAt);
        }
    }

    private sealed class Usage
    {
        public int ModelCalls;
        public decimal EstimatedCost;
        public int PremiumTokens;
        public DateTimeOffset UpdatedAt = DateTimeOffset.UtcNow;
    }
}
