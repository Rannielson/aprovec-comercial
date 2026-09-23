using static Recorrencia.Domain.Tests.Builders;

namespace Recorrencia.Domain.Tests;

public class CommissionEngineTests
{
    private static readonly GroupMembership[] CoordenadorNaCoordenacao = [new(Coordenacao, Coordenador)];

    [Fact]
    public void Reproduces_the_consolidated_example_from_the_pdf()
    {
        var boletos = Boletos(Joao, 100, 200m)
            .Concat(Boletos(Maria, 50, 200m))
            .Concat(Boletos(Pedro, 25, 200m))
            .ToList();

        var entries = CommissionEngine.Calculate(AprovecPlan(), boletos, Chain(Joao, Maria, Pedro), CoordenadorNaCoordenacao);
        var totals = TotalsByBeneficiary(entries);

        Assert.Equal(1600.00m, totals[Joao]);
        Assert.Equal(800.00m, totals[Maria]);
        Assert.Equal(350.00m, totals[Pedro]);
        Assert.Equal(350.00m, totals[Coordenador]);
        Assert.Equal(3100.00m, entries.Sum(e => e.Amount));
    }

    [Fact]
    public void Reproduces_the_august_history()
    {
        var boletos = Boletos(Joao, 90, 200m)
            .Concat(Boletos(Maria, 45, 200m))
            .Concat(Boletos(Pedro, 20, 200m))
            .ToList();

        var totals = TotalsByBeneficiary(
            CommissionEngine.Calculate(AprovecPlan(), boletos, Chain(Joao, Maria, Pedro), CoordenadorNaCoordenacao));

        Assert.Equal(1440.00m, totals[Joao]);
        Assert.Equal(710.00m, totals[Maria]);
        Assert.Equal(280.00m, totals[Pedro]);
        Assert.Equal(310.00m, totals[Coordenador]);
    }

    [Fact]
    public void Upline_commission_is_limited_to_one_level_in_the_aprovec_plan()
    {
        var entries = CommissionEngine.Calculate(
            AprovecPlan(), Boletos(Pedro, 1, 1000m).ToList(), Chain(Joao, Maria, Pedro), []);

        Assert.DoesNotContain(entries, e => e.BeneficiaryId == Joao);
        var maria = Assert.Single(entries, e => e.BeneficiaryId == Maria);
        Assert.Equal((RuleType.Upline, 1, 20.00m), (maria.RuleType, maria.Level!.Value, maria.Amount));
    }

    [Fact]
    public void Supports_plans_with_more_levels()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var d = Guid.NewGuid();
        var plan = new CommissionPlan(Guid.NewGuid(), new DateOnly(2026, 8, 1),
        [
            new CommissionRule(Guid.NewGuid(), RuleType.Own, 0.05m),
            new CommissionRule(Guid.NewGuid(), RuleType.Upline, 0.03m, Level: 1),
            new CommissionRule(Guid.NewGuid(), RuleType.Upline, 0.02m, Level: 2),
            new CommissionRule(Guid.NewGuid(), RuleType.Upline, 0.01m, Level: 3),
        ]);

        var totals = TotalsByBeneficiary(CommissionEngine.Calculate(plan, Boletos(d, 1, 1000m).ToList(), Chain(a, b, c, d), []));

        Assert.Equal(50.00m, totals[d]);
        Assert.Equal(30.00m, totals[c]);
        Assert.Equal(20.00m, totals[b]);
        Assert.Equal(10.00m, totals[a]);
    }

    [Fact]
    public void Missing_level_generates_nothing_and_does_not_roll_up()
    {
        var plan = new CommissionPlan(Guid.NewGuid(), new DateOnly(2026, 8, 1),
        [
            new CommissionRule(Guid.NewGuid(), RuleType.Own, 0.05m),
            new CommissionRule(Guid.NewGuid(), RuleType.Upline, 0.03m, Level: 1),
            new CommissionRule(Guid.NewGuid(), RuleType.Upline, 0.02m, Level: 2),
            new CommissionRule(Guid.NewGuid(), RuleType.Upline, 0.01m, Level: 3),
        ]);

        var entries = CommissionEngine.Calculate(plan, Boletos(Maria, 1, 1000m).ToList(), Chain(Joao, Maria), []);

        Assert.Equal(2, entries.Count);
        Assert.Equal(80.00m, entries.Sum(e => e.Amount));
    }

    [Fact]
    public void Every_member_of_a_global_group_receives_the_full_rate()
    {
        var other = Guid.NewGuid();
        var entries = CommissionEngine.Calculate(
            AprovecPlan(),
            Boletos(Joao, 1, 1000m).ToList(),
            Chain(Joao),
            [new GroupMembership(Coordenacao, Coordenador), new GroupMembership(Coordenacao, other)]);

        var totals = TotalsByBeneficiary(entries);
        Assert.Equal(10.00m, totals[Coordenador]);
        Assert.Equal(10.00m, totals[other]);
    }

    [Fact]
    public void Plan_without_own_rule_pays_only_the_upline()
    {
        var plan = new CommissionPlan(Guid.NewGuid(), new DateOnly(2026, 8, 1),
            [new CommissionRule(Guid.NewGuid(), RuleType.Upline, 0.02m, Level: 1)]);

        var entry = Assert.Single(CommissionEngine.Calculate(plan, Boletos(Maria, 1, 500m).ToList(), Chain(Joao, Maria), []));
        Assert.Equal((Joao, 10.00m), (entry.BeneficiaryId, entry.Amount));
    }

    [Fact]
    public void Entry_carries_the_rule_the_base_and_the_origin()
    {
        var plan = AprovecPlan();
        var boleto = new PaidBoleto(Guid.NewGuid(), Maria, 300m);

        var entries = CommissionEngine.Calculate(plan, [boleto], Chain(Joao, Maria), []);
        var upline = Assert.Single(entries, e => e.RuleType == RuleType.Upline);

        Assert.Equal(
            new CommissionEntry(Joao, boleto.Id, Maria, plan.Rules[1].Id, RuleType.Upline, 1, null, 0.02m, 300m, 6.00m),
            upline);
    }

    [Theory]
    [InlineData("2.50", "0.05", "0.12")]
    [InlineData("12.50", "0.07", "0.88")]
    [InlineData("0.30", "0.05", "0.02")]
    [InlineData("0.10", "0.05", "0.00")]
    public void Each_entry_is_rounded_half_to_even(string amount, string rate, string expected)
    {
        var plan = new CommissionPlan(Guid.NewGuid(), new DateOnly(2026, 8, 1),
            [new CommissionRule(Guid.NewGuid(), RuleType.Own, decimal.Parse(rate, System.Globalization.CultureInfo.InvariantCulture))]);
        var boleto = new PaidBoleto(Guid.NewGuid(), Joao, decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture));

        var entry = Assert.Single(CommissionEngine.Calculate(plan, [boleto], [], []));
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), entry.Amount);
    }

    [Fact]
    public void Invalid_rules_are_rejected()
    {
        var boletos = Boletos(Joao, 1, 100m).ToList();
        CommissionPlan With(CommissionRule rule) => new(Guid.NewGuid(), new DateOnly(2026, 8, 1), [rule]);

        Assert.Throws<ArgumentException>(() => CommissionEngine.Calculate(With(new(Guid.NewGuid(), RuleType.Upline, 0.02m)), boletos, [], []));
        Assert.Throws<ArgumentException>(() => CommissionEngine.Calculate(With(new(Guid.NewGuid(), RuleType.Global, 0.01m)), boletos, [], []));
        Assert.Throws<ArgumentException>(() => CommissionEngine.Calculate(With(new(Guid.NewGuid(), RuleType.Own, 0m)), boletos, [], []));
        Assert.Throws<ArgumentException>(() => CommissionEngine.Calculate(With(new(Guid.NewGuid(), RuleType.Own, 1.5m)), boletos, [], []));
    }

    [Fact]
    public void Non_positive_boleto_is_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            CommissionEngine.Calculate(AprovecPlan(), [new PaidBoleto(Guid.NewGuid(), Joao, 0m)], [], []));
    }

    [Fact]
    public void Inconsistent_hierarchy_is_rejected()
    {
        var paths = new[] { new HierarchyPath(Joao, Pedro, 1), new HierarchyPath(Maria, Pedro, 1) };
        Assert.Throws<InvalidOperationException>(() =>
            CommissionEngine.Calculate(AprovecPlan(), Boletos(Pedro, 1, 100m).ToList(), paths, []));
    }
}
