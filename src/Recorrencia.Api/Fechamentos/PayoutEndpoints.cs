using System.Text.Json;
using Recorrencia.Api.Authorization;
using Recorrencia.Api.Commissions;
using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Integracoes;
using Recorrencia.Api.PlanosCarreira;
using Recorrencia.Api.Security;
using Recorrencia.Api.Tenancy;
using Recorrencia.Domain.Commissions;

namespace Recorrencia.Api.Fechamentos;

public static class PayoutEndpoints
{
    public sealed record BonusPlacasResponse(string CompetenciaProducao, string PlanoNome,
        DateOnly DataLimitePagamento, int Quantidade, decimal? ValorUnitario, decimal Valor,
        IReadOnlyList<BonusPlacasCalculator.PlacaPaga> Placas);
    public sealed record BonusExtraResponse(string PlanoNome, decimal Valor, DateTimeOffset AprovadoEm);
    public sealed record RegraArvoreResponse(RuleType RuleType, int? Level, Guid? GroupId,
        string? GroupName, decimal Rate, decimal Base, decimal Amount, decimal GrossBase = 0, decimal BankFee = 0);
    public sealed record BeneficiarioResponse(Guid UserId, string Name, decimal Arvore, decimal Placas,
        decimal Extra, decimal Total, IReadOnlyList<RegraArvoreResponse> RegrasArvore,
        BonusPlacasResponse? BonusPlacas, BonusExtraResponse? BonusExtra,
        decimal? ExtraDisponivel);
    public sealed record PagamentoResponse(string Competencia, string Status, bool Previa,
        decimal Arvore, decimal Placas, decimal Extra, decimal Total,
        IReadOnlyList<BeneficiarioResponse> Beneficiarios);

    private sealed class FechamentoRow
    {
        public Guid Id { get; set; }
        public string Status { get; set; } = "";
    }
    private sealed class RemuneracaoRow
    {
        public Guid BeneficiarioId { get; set; }
        public string Tipo { get; set; } = "";
        public string PlanoNome { get; set; } = "";
        public DateOnly CompetenciaProducao { get; set; }
        public DateOnly? DataLimitePagamento { get; set; }
        public int Quantidade { get; set; }
        public decimal? ValorUnitario { get; set; }
        public decimal Valor { get; set; }
        public string Detalhes { get; set; } = "[]";
        public DateTimeOffset? AprovadoEm { get; set; }
    }
    private sealed class UserRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public decimal? ExtraDisponivel { get; set; }
    }
    private sealed class GroupRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
    }

    public static void MapPayoutEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/payouts/{competencia}", SummaryAsync).RequirePermission("fechamento.visualizar");
        app.MapGet("/payouts/{competencia}/preview", PreviewAsync)
            .RequirePermission("fechamento.confirmar")
            .RequirePermission("integracoes.gerenciar");
    }

    private static Task<IResult> SummaryAsync(string competencia, RequestContext request, Database db,
        CancellationToken ct) => BuildAsync(competencia, false, request, db, null, null, null, ct);

    private static Task<IResult> PreviewAsync(string competencia, RequestContext request, Database db,
        IHinovaClient hinova, AesGcmCipher cipher, TimeProvider time, CancellationToken ct) =>
        BuildAsync(competencia, true, request, db, hinova, cipher, time, ct);

    private static async Task<IResult> BuildAsync(string competencia, bool preview, RequestContext request,
        Database db, IHinovaClient? hinova, AesGcmCipher? cipher, TimeProvider? time, CancellationToken ct)
    {
        var month = CommissionService.ParseCompetencia(competencia);
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        var data = await db.InTenantAsync(tenant, actor, async tx =>
        {
            var ids = await tx.ExecuteScalarAsync<Guid[]>("select app.commission_beneficiaries()") ?? [];
            if (preview && await tx.ExecuteScalarAsync<string?>("select app.scope_for('comissoes.visualizar')") != "tenant")
                throw new ApiProblem(StatusCodes.Status403Forbidden, "fechamento.requires_full_visibility");
            var loaded = await CommissionService.LoadAsync(tx, month, ids);
            var fechamento = await tx.QuerySingleOrDefaultAsync<FechamentoRow>(
                "select id, status from fechamentos where competencia = @month", new { month });
            var remun = fechamento is null ? [] : (await tx.QueryAsync<RemuneracaoRow>(
                """
                select beneficiario_id, tipo, plano_nome, competencia_producao, data_limite_pagamento,
                       quantidade, valor_unitario, valor, detalhes::text as detalhes, aprovado_em
                  from fechamento_remuneracao
                 where fechamento_id = @id and beneficiario_id = any(@ids)
                """, new { fechamento.Id, ids })).ToList();
            var users = (await tx.QueryAsync<UserRow>(
                """
                select u.id, u.name,
                       case when u.status = 'ativo' and p.status = 'ativo' then p.bonus_extra_valor end as extra_disponivel
                  from users u left join planos_carreira p on p.id = u.plano_carreira_id
                 where u.id = any(@ids)
                """, new { ids })).ToList();
            var groups = (await tx.QueryAsync<GroupRow>("select id, name from commission_groups")).ToList();
            return (ids, loaded, fechamento, remun, users, groups);
        }, ct);

        var finalizado = data.fechamento?.Status is "confirmado" or "provisionado";
        IReadOnlyList<BonusPayoutService.BonusPayout> calculados = [];
        if (preview && !finalizado)
            calculados = await BonusPayoutService.CalculateAsync(month, tenant, actor, db,
                hinova!, cipher!, time!, ct);

        var tree = CommissionSummary.ByBeneficiary(data.loaded.Entries).ToDictionary(b => b.BeneficiaryId);
        var names = data.users.ToDictionary(u => u.Id);
        var groupNames = data.groups.ToDictionary(g => g.Id, g => g.Name);
        var bonusRows = data.remun.Where(r => r.Tipo == "placas").ToDictionary(r => r.BeneficiarioId);
        var extraRows = data.remun.Where(r => r.Tipo == "extra").ToDictionary(r => r.BeneficiarioId);
        var previewRows = calculados.ToDictionary(r => r.BeneficiarioId);
        var beneficiaries = new List<BeneficiarioResponse>();
        foreach (var id in data.ids)
        {
            tree.TryGetValue(id, out var treeEntry);
            names.TryGetValue(id, out var user);
            bonusRows.TryGetValue(id, out var savedBonus);
            extraRows.TryGetValue(id, out var savedExtra);
            previewRows.TryGetValue(id, out var calculated);
            BonusPlacasResponse? bonus = null;
            if (finalizado && savedBonus is not null)
                bonus = new BonusPlacasResponse(savedBonus.CompetenciaProducao.ToString("yyyy-MM"),
                    savedBonus.PlanoNome, savedBonus.DataLimitePagamento!.Value,
                    savedBonus.Quantidade, savedBonus.ValorUnitario, savedBonus.Valor,
                    JsonSerializer.Deserialize<List<BonusPlacasCalculator.PlacaPaga>>(savedBonus.Detalhes,
                        new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? []);
            else if (preview && calculated is not null)
                bonus = new BonusPlacasResponse(calculated.CompetenciaProducao.ToString("yyyy-MM"),
                    calculated.PlanoNome, calculated.DataLimitePagamento, calculated.Quantidade,
                    calculated.ValorUnitario, calculated.Valor, calculated.Placas);
            var extra = savedExtra is null ? null : new BonusExtraResponse(savedExtra.PlanoNome,
                savedExtra.Valor, savedExtra.AprovadoEm!.Value);
            var arvore = treeEntry?.Total ?? 0m;
            var placas = bonus?.Valor ?? 0m;
            var valorExtra = extra?.Valor ?? 0m;
            beneficiaries.Add(new BeneficiarioResponse(id, user?.Name ?? "Participante", arvore,
                placas, valorExtra, arvore + placas + valorExtra,
                treeEntry?.ByRule.Select(r => new RegraArvoreResponse(r.RuleType, r.Level, r.GroupId,
                    r.GroupId is { } groupId ? groupNames.GetValueOrDefault(groupId) : null,
                    r.Rate, r.Base, r.Amount, r.GrossBase, r.BankFee)).ToList() ?? [], bonus,
                extra, preview && !finalizado ? user?.ExtraDisponivel : null));
        }
        beneficiaries = beneficiaries.OrderByDescending(b => b.Total).ThenBy(b => b.Name).ToList();
        return Results.Ok(new PagamentoResponse(competencia, data.loaded.Status,
            preview && !finalizado, beneficiaries.Sum(b => b.Arvore),
            beneficiaries.Sum(b => b.Placas), beneficiaries.Sum(b => b.Extra),
            beneficiaries.Sum(b => b.Total), beneficiaries));
    }
}
