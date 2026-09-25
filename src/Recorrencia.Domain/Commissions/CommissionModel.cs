namespace Recorrencia.Domain.Commissions;

public enum RuleType
{
    Own,
    Upline,
    Global,
}

public sealed record CommissionRule(Guid Id, RuleType Type, decimal Rate, int? Level = null, Guid? GroupId = null);

public sealed record CommissionPlan(Guid Id, DateOnly EffectiveFrom, IReadOnlyList<CommissionRule> Rules);

public sealed record PaidBoleto(Guid Id, Guid OwnerId, decimal Amount);

public sealed record HierarchyPath(Guid AncestorId, Guid DescendantId, int Depth);

public sealed record GroupMembership(Guid GroupId, Guid UserId);

public sealed record CommissionEntry(
    Guid BeneficiaryId,
    Guid BoletoId,
    Guid OwnerId,
    Guid RuleId,
    RuleType RuleType,
    int? Level,
    Guid? GroupId,
    decimal Rate,
    decimal Base,
    decimal Amount);
