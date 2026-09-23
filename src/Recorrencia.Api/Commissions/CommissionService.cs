using System.Globalization;
using Recorrencia.Api.Infrastructure;
using Recorrencia.Domain.Commissions;

namespace Recorrencia.Api.Commissions;

public static class CommissionService
{
    public sealed record LoadedCommissions(string Status, string Source, IReadOnlyList<CommissionEntry> Entries);

    public sealed class FechamentoRow
    {
        public Guid FechamentoId { get; set; }
        public string Status { get; set; } = "";
    }

    public sealed class RuleRow
    {
        public Guid Id { get; set; }
        public string Type { get; set; } = "";
        public decimal Rate { get; set; }
        public int? Level { get; set; }
        public Guid? GroupId { get; set; }
    }

    public sealed class SourceBoletoRow
    {
        public Guid BoletoId { get; set; }
        public Guid ParticipanteId { get; set; }
        public decimal Valor { get; set; }
    }

    public sealed class PathRow
    {
        public Guid AncestorId { get; set; }
        public Guid DescendantId { get; set; }
        public int Depth { get; set; }
    }

    public sealed class MemberRow
    {
        public Guid GroupId { get; set; }
        public Guid UserId { get; set; }
    }

    public sealed class SnapshotRow
    {
        public Guid BeneficiarioId { get; set; }
        public Guid BoletoId { get; set; }
        public Guid OrigemParticipanteId { get; set; }
        public Guid RuleId { get; set; }
        public string RuleType { get; set; } = "";
        public int? Level { get; set; }
        public Guid? GroupId { get; set; }
        public decimal Rate { get; set; }
        public decimal Base { get; set; }
        public decimal Valor { get; set; }
    }

    public static DateOnly ParseCompetencia(string value) =>
        DateOnly.TryParseExact(value + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new ApiProblem(StatusCodes.Status400BadRequest, "commission.invalid_competencia");

    public static async Task<LoadedCommissions> LoadAsync(Tx tx, DateOnly competencia, Guid[] beneficiaries)
    {
        var fechamento = await tx.QuerySingleOrDefaultAsync<FechamentoRow>(
            "select fechamento_id, status from app.fechamento_status(@competencia)", new { competencia });

        if (fechamento is { Status: "confirmado" or "provisionado" })
            return new LoadedCommissions(fechamento.Status, "snapshot", await LoadSnapshotAsync(tx, fechamento.FechamentoId, beneficiaries));

        return new LoadedCommissions(fechamento?.Status ?? "apuracao", "live", await CalculateLiveAsync(tx, competencia, beneficiaries));
    }

    public static async Task<IReadOnlyList<CommissionEntry>> CalculateLiveAsync(Tx tx, DateOnly competencia, Guid[] beneficiaries)
    {
        if (beneficiaries.Length == 0)
            return [];
        var planId = await tx.ExecuteScalarAsync<Guid?>("select app.commission_plan_for(@competencia)", new { competencia });
        if (planId is null)
            return [];

        var rules = await tx.QueryAsync<RuleRow>(
            "select id, type, rate, level, group_id from commission_rules where plan_id = @planId", new { planId });
        var boletos = await tx.QueryAsync<SourceBoletoRow>(
            "select boleto_id, participante_id, valor from app.commission_source_boletos(@competencia, @beneficiaries)",
            new { competencia, beneficiaries });
        var paths = await tx.QueryAsync<PathRow>(
            "select ancestor_id, descendant_id, depth from app.commission_source_paths(@competencia, @beneficiaries)",
            new { competencia, beneficiaries });
        var members = await tx.QueryAsync<MemberRow>("select group_id, user_id from commission_group_members");

        var plan = new CommissionPlan(planId.Value, competencia,
            rules.Select(r => new CommissionRule(r.Id, ParseRuleType(r.Type), r.Rate, r.Level, r.GroupId)).ToList());
        var wanted = beneficiaries.ToHashSet();

        return CommissionEngine.Calculate(
                plan,
                boletos.Select(b => new PaidBoleto(b.BoletoId, b.ParticipanteId, b.Valor)),
                paths.Select(p => new HierarchyPath(p.AncestorId, p.DescendantId, p.Depth)),
                members.Select(m => new GroupMembership(m.GroupId, m.UserId)))
            .Where(e => wanted.Contains(e.BeneficiaryId))
            .ToList();
    }

    private static async Task<IReadOnlyList<CommissionEntry>> LoadSnapshotAsync(Tx tx, Guid fechamentoId, Guid[] beneficiaries) =>
        (await tx.QueryAsync<SnapshotRow>(
            """
            select beneficiario_id, boleto_id, origem_participante_id, rule_id, rule_type, level, group_id, rate, base, valor
              from fechamento_detalhes
             where fechamento_id = @fechamentoId and beneficiario_id = any(@beneficiaries)
            """,
            new { fechamentoId, beneficiaries }))
        .Select(r => new CommissionEntry(r.BeneficiarioId, r.BoletoId, r.OrigemParticipanteId, r.RuleId,
            ParseRuleType(r.RuleType), r.Level, r.GroupId, r.Rate, r.Base, r.Valor))
        .ToList();

    public static RuleType ParseRuleType(string value) => value switch
    {
        "own" => RuleType.Own,
        "upline" => RuleType.Upline,
        "global" => RuleType.Global,
        _ => throw new InvalidOperationException($"Tipo de regra desconhecido: {value}"),
    };

    public static string ToDb(RuleType type) => type switch
    {
        RuleType.Own => "own",
        RuleType.Upline => "upline",
        RuleType.Global => "global",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
}
