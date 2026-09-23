namespace Recorrencia.Domain.Commissions;

public static class PlanSelector
{
    public static CommissionPlan? ForCompetencia(IEnumerable<CommissionPlan> activePlans, DateOnly competencia)
    {
        if (competencia.Day != 1)
            throw new ArgumentException("A competência deve ser o primeiro dia do mês.", nameof(competencia));

        return activePlans
            .Where(plan => plan.EffectiveFrom <= competencia)
            .MaxBy(plan => plan.EffectiveFrom);
    }
}
