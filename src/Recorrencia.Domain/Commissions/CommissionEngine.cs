namespace Recorrencia.Domain.Commissions;

public static class CommissionEngine
{
    /// <summary>
    /// Computes commission entries strictly from the data supplied in <paramref name="boletos"/>,
    /// <paramref name="paths"/> and <paramref name="memberships"/> — it never looks beyond them.
    /// </summary>
    /// <remarks>
    /// If a caller restricts these inputs to a subset of people (e.g. a database-backed caller that
    /// scopes its queries to specific beneficiaries), that restriction is invisible to the engine, and
    /// the caller MUST also filter the returned entries down to its intended beneficiary set. Concretely:
    /// <c>own</c> entries are produced for every boleto owner present in <paramref name="boletos"/>, even
    /// one outside the caller's intended set, since <c>own</c> needs no hierarchy data; and <c>upline</c>
    /// entries for a person whose full ancestor chain was not supplied in <paramref name="paths"/> will be
    /// silently incomplete (missing levels), not an error.
    /// </remarks>
    public static IReadOnlyList<CommissionEntry> Calculate(
        CommissionPlan plan,
        IEnumerable<PaidBoleto> boletos,
        IEnumerable<HierarchyPath> paths,
        IEnumerable<GroupMembership> memberships)
    {
        foreach (var rule in plan.Rules)
            Validate(rule);
        ValidateRuleSet(plan.Rules);

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
                    default:
                        throw new ArgumentOutOfRangeException(nameof(rule.Type), rule.Type, $"Tipo de regra desconhecido: {rule.Type}.");
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

    private static void ValidateRuleSet(IReadOnlyList<CommissionRule> rules)
    {
        if (rules.Count(r => r.Type == RuleType.Own) > 1)
            throw new ArgumentException("O plano não pode ter mais de uma regra do tipo own.");

        var duplicateLevel = rules
            .Where(r => r.Type == RuleType.Upline)
            .GroupBy(r => r.Level)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicateLevel is not null)
            throw new ArgumentException($"O plano não pode ter mais de uma regra upline no nível {duplicateLevel.Key}.");

        var duplicateGroup = rules
            .Where(r => r.Type == RuleType.Global)
            .GroupBy(r => r.GroupId)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicateGroup is not null)
            throw new ArgumentException($"O plano não pode ter mais de uma regra global para o grupo {duplicateGroup.Key}.");
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
