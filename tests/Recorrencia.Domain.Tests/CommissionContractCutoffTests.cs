using Recorrencia.Domain.Commissions;

namespace Recorrencia.Domain.Tests;

public class CommissionContractCutoffTests
{
    private static readonly Guid Consultant = Guid.NewGuid();
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Boleto = Guid.NewGuid();
    private static readonly Guid Rule = Guid.NewGuid();
    private static readonly DateOnly Cutoff = new(2026, 9, 30);

    private static CommissionEntry Upline(decimal rate = .02m, decimal baseAmount = 100m) =>
        new(Consultant, Boleto, Owner, Rule, RuleType.Upline, 1, null, rate, baseAmount, baseAmount * rate);

    [Fact]
    public void Uses_contract_date_instead_of_payment_month()
    {
        var result = CommissionContractCutoff.Apply([Upline()],
            [new(Boleto, 0, Cutoff)], [new(Consultant, Cutoff)]);
        Assert.Empty(result);
    }

    [Fact]
    public void New_contract_uses_normal_tree_rate()
    {
        var result = CommissionContractCutoff.Apply([Upline()],
            [new(Boleto, 0, Cutoff.AddDays(1))], [new(Consultant, Cutoff)]);
        var entry = Assert.Single(result);
        Assert.Equal((.02m, 100m, 2m, 0), (entry.Rate, entry.Base, entry.Amount, entry.ContractSegment));
    }

    [Fact]
    public void Second_level_starts_at_one_percent_after_the_cutoff()
    {
        var entry = Upline(.01m) with { Level = 2 };
        var result = CommissionContractCutoff.Apply([entry],
            [new(Boleto, 0, Cutoff.AddDays(1))], [new(Consultant, Cutoff)]);
        Assert.Equal((.01m, 1m), (Assert.Single(result).Rate, Assert.Single(result).Amount));
    }

    [Fact]
    public void Mixed_vehicle_boleto_pays_upline_only_for_new_contracts()
    {
        var result = CommissionContractCutoff.Apply([Upline(baseAmount: 100.01m)],
            [new(Boleto, 0, Cutoff), new(Boleto, 1, Cutoff.AddDays(1))],
            [new(Consultant, Cutoff)]);
        var entry = Assert.Single(result);
        Assert.Equal((.02m, 50m, 1m, 0), (entry.Rate, entry.Base, entry.Amount, entry.ContractSegment));
    }

    [Fact]
    public void Unselected_consultant_and_own_sales_keep_the_plan()
    {
        var own = Upline() with { RuleType = RuleType.Own, BeneficiaryId = Owner, Level = null, Rate = .07m, Amount = 7m };
        var entries = new[] { Upline(), own };
        var result = CommissionContractCutoff.Apply(entries, [], [new(Guid.NewGuid(), Cutoff)]);
        Assert.Equal(entries, result);
    }

    [Fact]
    public void Own_seven_percent_remains_even_when_the_consultant_has_a_cutoff()
    {
        var own = Upline() with { RuleType = RuleType.Own, BeneficiaryId = Consultant, Level = null, Rate = .07m, Amount = 7m };
        var result = CommissionContractCutoff.Apply([own], [], [new(Consultant, Cutoff)]);
        Assert.Equal(own, Assert.Single(result));
    }

    [Fact]
    public void Missing_contract_date_cannot_be_silently_treated_as_new()
    {
        Assert.Throws<InvalidOperationException>(() => CommissionContractCutoff.Apply(
            [Upline()], [new(Boleto, 0, null)], [new(Consultant, Cutoff)]));
    }
}
