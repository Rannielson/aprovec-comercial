namespace Recorrencia.Domain.Tests;

internal static class Builders
{
    public static readonly Guid Joao = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    public static readonly Guid Maria = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    public static readonly Guid Pedro = Guid.Parse("00000000-0000-0000-0000-00000000000c");
    public static readonly Guid Coordenador = Guid.Parse("00000000-0000-0000-0000-00000000000d");
    public static readonly Guid Coordenacao = Guid.Parse("00000000-0000-0000-0000-0000000000f1");

    public static CommissionPlan AprovecPlan() => new(
        Guid.NewGuid(),
        new DateOnly(2026, 8, 1),
        [
            new CommissionRule(Guid.NewGuid(), RuleType.Own, 0.07m),
            new CommissionRule(Guid.NewGuid(), RuleType.Upline, 0.02m, Level: 1),
            new CommissionRule(Guid.NewGuid(), RuleType.Global, 0.01m, GroupId: Coordenacao),
        ]);

    public static IEnumerable<PaidBoleto> Boletos(Guid owner, int count, decimal amount) =>
        Enumerable.Range(0, count).Select(_ => new PaidBoleto(Guid.NewGuid(), owner, amount));

    public static List<HierarchyPath> Chain(params Guid[] topToBottom)
    {
        var paths = new List<HierarchyPath>();
        for (var i = 0; i < topToBottom.Length; i++)
            for (var j = i; j < topToBottom.Length; j++)
                paths.Add(new HierarchyPath(topToBottom[i], topToBottom[j], j - i));
        return paths;
    }

    public static Dictionary<Guid, decimal> TotalsByBeneficiary(IEnumerable<CommissionEntry> entries) =>
        entries.GroupBy(e => e.BeneficiaryId).ToDictionary(g => g.Key, g => g.Sum(e => e.Amount));
}
