using static Recorrencia.Domain.Tests.Builders;

namespace Recorrencia.Domain.Tests;

public class CommissionSummaryTests
{
    [Fact]
    public void Groups_the_pdf_example_by_beneficiary_and_rule()
    {
        var boletos = Boletos(Joao, 70, 200m)
            .Concat(Boletos(Joao, 20, 300m))
            .Concat(Boletos(Maria, 50, 200m))
            .Concat(Boletos(Pedro, 25, 200m))
            .ToList();
        var entries = CommissionEngine.Calculate(AprovecPlan(), boletos, Chain(Joao, Maria, Pedro), [new(Coordenacao, Coordenador)]);

        var summary = CommissionSummary.ByBeneficiary(entries);

        Assert.Equal(new[] { Joao, Maria, Pedro, Coordenador }.OrderBy(x => x).ToHashSet(), summary.Select(s => s.BeneficiaryId).ToHashSet());
        var joao = summary.Single(s => s.BeneficiaryId == Joao);
        Assert.Equal(1600.00m, joao.Total);
        Assert.Equal(
            new[]
            {
                new RuleTotal(RuleType.Own, null, null, 0.07m, 20000m, 1400.00m),
                new RuleTotal(RuleType.Upline, 1, null, 0.02m, 10000m, 200.00m),
            },
            joao.ByRule);

        var coordenador = summary.Single(s => s.BeneficiaryId == Coordenador);
        Assert.Equal(new[] { new RuleTotal(RuleType.Global, null, Coordenacao, 0.01m, 35000m, 350.00m) }, coordenador.ByRule);
    }

    [Fact]
    public void Orders_beneficiaries_by_total_descending()
    {
        var entries = CommissionEngine.Calculate(
            AprovecPlan(),
            Boletos(Joao, 100, 200m).Concat(Boletos(Maria, 50, 200m)).Concat(Boletos(Pedro, 25, 200m)).ToList(),
            Chain(Joao, Maria, Pedro),
            []);

        var order = CommissionSummary.ByBeneficiary(entries).Select(s => s.BeneficiaryId).ToList();
        Assert.Equal(new[] { Joao, Maria, Pedro }, order);
    }

    [Fact]
    public void Total_is_the_sum_of_rounded_entries()
    {
        var plan = new CommissionPlan(Guid.NewGuid(), new DateOnly(2026, 8, 1), [new CommissionRule(Guid.NewGuid(), RuleType.Own, 0.05m)]);
        var entries = CommissionEngine.Calculate(plan, Boletos(Joao, 3, 2.50m).ToList(), [], []);

        var joao = Assert.Single(CommissionSummary.ByBeneficiary(entries));
        Assert.Equal(0.36m, joao.Total);
        Assert.Equal(7.50m, joao.ByRule.Single().Base);
    }

    [Fact]
    public void Empty_input_gives_empty_summary()
    {
        Assert.Empty(CommissionSummary.ByBeneficiary([]));
    }
}
