namespace Recorrencia.Domain.Tests;

public class PlanSelectorTests
{
    private static CommissionPlan Plan(int year, int month) =>
        new(Guid.NewGuid(), new DateOnly(year, month, 1), [new CommissionRule(Guid.NewGuid(), RuleType.Own, 0.07m)]);

    [Fact]
    public void Picks_the_latest_plan_that_started_on_or_before_the_competencia()
    {
        var august = Plan(2026, 8);
        var october = Plan(2026, 10);
        var plans = new[] { october, august };

        Assert.Same(august, PlanSelector.ForCompetencia(plans, new DateOnly(2026, 8, 1)));
        Assert.Same(august, PlanSelector.ForCompetencia(plans, new DateOnly(2026, 9, 1)));
        Assert.Same(october, PlanSelector.ForCompetencia(plans, new DateOnly(2026, 10, 1)));
        Assert.Same(october, PlanSelector.ForCompetencia(plans, new DateOnly(2027, 1, 1)));
    }

    [Fact]
    public void Returns_null_before_the_first_plan()
    {
        Assert.Null(PlanSelector.ForCompetencia([Plan(2026, 8)], new DateOnly(2026, 7, 1)));
    }

    [Fact]
    public void Rejects_a_competencia_that_is_not_the_first_day()
    {
        Assert.Throws<ArgumentException>(() => PlanSelector.ForCompetencia([Plan(2026, 8)], new DateOnly(2026, 9, 15)));
    }
}
