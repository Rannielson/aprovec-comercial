namespace Recorrencia.Domain.Commissions;

public static class CommissionEngine
{
    public static IReadOnlyList<CommissionEntry> Calculate(
        CommissionPlan plan,
        IEnumerable<PaidBoleto> boletos,
        IEnumerable<HierarchyPath> paths,
        IEnumerable<GroupMembership> memberships)
    {
        foreach (var rule in plan.Rules)
            Validate(rule);

        var ancestorAt = BuildAncestorIndex(paths);
        var membersByGroup = memberships
            .GroupBy(m => m.GroupId)
            .ToDictionary(g => g.Key, g => g.Select(m => m.UserId).Distinct().ToList());

        var entries = new List<CommissionEntry>();
        foreach (var boleto in boletos)
        {
            if (boleto.Amount <= 0)
                throw new ArgumentException($"O boleto {boleto.Id} tem valor inválido.", nameof(boletos));

            foreach (var rule in plan.Rules)
            {
                switch (rule.Type)
                {
                    case RuleType.Own:
                        entries.Add(Entry(boleto.OwnerId, boleto, rule));
                        break;
                    case RuleType.Upline:
                        if (ancestorAt.TryGetValue((boleto.OwnerId, rule.Level!.Value), out var ancestor))
                            entries.Add(Entry(ancestor, boleto, rule));
                        break;
                    case RuleType.Global:
                        if (membersByGroup.TryGetValue(rule.GroupId!.Value, out var members))
                            entries.AddRange(members.Select(member => Entry(member, boleto, rule)));
                        break;
                }
            }
        }
        return entries;
    }

    private static void Validate(CommissionRule rule)
    {
        if (rule.Rate <= 0 || rule.Rate > 1)
            throw new ArgumentException($"A regra {rule.Id} tem taxa fora do intervalo (0, 1].");
        if (rule.Type == RuleType.Upline && rule.Level is not >= 1)
            throw new ArgumentException($"A regra {rule.Id} do tipo upline precisa de nível >= 1.");
        if (rule.Type == RuleType.Global && rule.GroupId is null)
            throw new ArgumentException($"A regra {rule.Id} do tipo global precisa de grupo.");
    }

    private static Dictionary<(Guid Descendant, int Depth), Guid> BuildAncestorIndex(IEnumerable<HierarchyPath> paths)
    {
        var index = new Dictionary<(Guid, int), Guid>();
        foreach (var path in paths.Where(p => p.Depth > 0))
        {
            var key = (path.DescendantId, path.Depth);
            if (index.TryGetValue(key, out var existing) && existing != path.AncestorId)
                throw new InvalidOperationException($"Hierarquia inconsistente para {path.DescendantId} na profundidade {path.Depth}.");
            index[key] = path.AncestorId;
        }
        return index;
    }

    private static CommissionEntry Entry(Guid beneficiary, PaidBoleto boleto, CommissionRule rule) => new(
        beneficiary,
        boleto.Id,
        boleto.OwnerId,
        rule.Id,
        rule.Type,
        rule.Level,
        rule.GroupId,
        rule.Rate,
        boleto.Amount,
        Math.Round(boleto.Amount * rule.Rate, 2, MidpointRounding.ToEven));
}
