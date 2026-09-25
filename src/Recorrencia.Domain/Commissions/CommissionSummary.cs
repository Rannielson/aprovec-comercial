namespace Recorrencia.Domain.Commissions;

public sealed record RuleTotal(RuleType RuleType, int? Level, Guid? GroupId, decimal Rate, decimal Base, decimal Amount);

public sealed record BeneficiaryTotal(Guid BeneficiaryId, decimal Total, IReadOnlyList<RuleTotal> ByRule);

public static class CommissionSummary
{
    public static IReadOnlyList<BeneficiaryTotal> ByBeneficiary(IEnumerable<CommissionEntry> entries) =>
        entries
            .GroupBy(e => e.BeneficiaryId)
            .Select(beneficiary => new BeneficiaryTotal(
                beneficiary.Key,
                beneficiary.Sum(e => e.Amount),
                beneficiary
                    .GroupBy(e => (e.RuleType, e.Level, e.GroupId, e.Rate))
                    .Select(rule => new RuleTotal(
                        rule.Key.RuleType,
                        rule.Key.Level,
                        rule.Key.GroupId,
                        rule.Key.Rate,
                        rule.Sum(e => e.Base),
                        rule.Sum(e => e.Amount)))
                    .OrderBy(r => r.RuleType)
                    .ThenBy(r => r.Level)
                    .ToList()))
            .OrderByDescending(b => b.Total)
            .ThenBy(b => b.BeneficiaryId)
            .ToList();
}
