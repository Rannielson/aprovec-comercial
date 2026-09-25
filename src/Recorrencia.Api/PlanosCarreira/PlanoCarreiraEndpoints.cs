using Npgsql;
using Recorrencia.Api.Authorization;
using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Tenancy;
using static Recorrencia.Api.Audit.Audit;

namespace Recorrencia.Api.PlanosCarreira;

public static class PlanoCarreiraEndpoints
{
    public sealed record FaixaBonusInput(int QuantidadeMin, int? QuantidadeMax, decimal ValorPorPlaca);
    public sealed record RegraRecorrenciaInput(string? Tipo, int? Nivel, decimal Taxa);
    public sealed record PlanoCarreiraRequest(string? Name, string? Status, string? Classificacao, int JanelaApuracaoDias,
        int? MetaMinimaContratos, string? MetaMinimaFonteData, decimal? BonusExtraValor, bool RecorrenciaAtiva,
        FaixaBonusInput[]? Faixas, RegraRecorrenciaInput[]? RegrasRecorrencia);

    public sealed record FaixaBonusResponse(Guid Id, int QuantidadeMin, int? QuantidadeMax, decimal ValorPorPlaca);
    public sealed record RegraRecorrenciaResponse(Guid Id, string Tipo, int? Nivel, decimal Taxa);
    public sealed record PlanoCarreiraResponse(Guid Id, string Name, string Status, string Classificacao,
        int JanelaApuracaoDias, int? MetaMinimaContratos, string? MetaMinimaFonteData, decimal? BonusExtraValor,
        bool RecorrenciaAtiva, IReadOnlyList<FaixaBonusResponse> Faixas, IReadOnlyList<RegraRecorrenciaResponse> RegrasRecorrencia);

    private sealed class PlanoRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string Status { get; set; } = "";
        public string Classificacao { get; set; } = "";
        public int JanelaApuracaoDias { get; set; }
        public int? MetaMinimaContratos { get; set; }
        public string? MetaMinimaFonteData { get; set; }
        public decimal? BonusExtraValor { get; set; }
        public bool RecorrenciaAtiva { get; set; }
    }

    private static readonly string[] Classificacoes = ["clt_interno", "clt_externo", "so_externo"];
    private static readonly string[] MetaMinimaFontes = ["contrato", "cadastro"];
    private static readonly string[] StatusValidos = ["ativo", "inativo"];
    private static readonly string[] TiposRecorrencia = ["propria", "upline"];

    public static void MapPlanoCarreiraEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/planos-carreira", ListarAsync).RequirePermission("regras_comissao.visualizar");
        app.MapPost("/planos-carreira", CriarAsync).RequirePermission("regras_comissao.editar");
        app.MapPut("/planos-carreira/{id:guid}", AtualizarAsync).RequirePermission("regras_comissao.editar");
        app.MapDelete("/planos-carreira/{id:guid}", RemoverAsync).RequirePermission("regras_comissao.editar");
    }

    private static async Task<PlanoCarreiraResponse> LoadPlanoAsync(Tx tx, Guid id)
    {
        var plano = await tx.QuerySingleAsync<PlanoRow>(
            """
            select id, name, status, classificacao, janela_apuracao_dias, meta_minima_contratos,
                   meta_minima_fonte_data, bonus_extra_valor, recorrencia_ativa
              from planos_carreira where id = @id
            """,
            new { id });
        var faixas = (await tx.QueryAsync<FaixaBonusResponse>(
            "select id, quantidade_min, quantidade_max, valor_por_placa from planos_carreira_faixas_bonus where plano_carreira_id = @id order by quantidade_min",
            new { id })).ToList();
        var regras = (await tx.QueryAsync<RegraRecorrenciaResponse>(
            "select id, tipo, nivel, taxa from planos_carreira_regras_recorrencia where plano_carreira_id = @id order by tipo, nivel",
            new { id })).ToList();
        return new PlanoCarreiraResponse(plano.Id, plano.Name, plano.Status, plano.Classificacao, plano.JanelaApuracaoDias,
            plano.MetaMinimaContratos, plano.MetaMinimaFonteData, plano.BonusExtraValor, plano.RecorrenciaAtiva, faixas, regras);
    }

    private static async Task<IResult> ListarAsync(RequestContext request, Database db, CancellationToken ct)
    {
        var planos = await db.InTenantAsync(request.RequireTenant(), request.RequireUser(), async tx =>
        {
            var ids = await tx.QueryAsync<Guid>("select id from planos_carreira order by name");
            var list = new List<PlanoCarreiraResponse>();
            foreach (var id in ids)
                list.Add(await LoadPlanoAsync(tx, id));
            return list;
        }, ct);
        return Results.Ok(planos);
    }

    private static (string Name, string Classificacao, FaixaBonusInput[] Faixas, RegraRecorrenciaInput[] Regras) Validate(PlanoCarreiraRequest body)
    {
        var name = (body.Name ?? "").Trim();
        if (name.Length == 0)
            throw new ApiProblem(StatusCodes.Status400BadRequest, "plano_carreira.name_required");

        if (body.Classificacao is null || !Classificacoes.Contains(body.Classificacao))
            throw new ApiProblem(StatusCodes.Status400BadRequest, "plano_carreira.dados_invalidos");
        if (body.JanelaApuracaoDias <= 0)
            throw new ApiProblem(StatusCodes.Status400BadRequest, "plano_carreira.dados_invalidos");
        if (body.BonusExtraValor is <= 0)
            throw new ApiProblem(StatusCodes.Status400BadRequest, "plano_carreira.dados_invalidos");
        if (body.Status is not null && !StatusValidos.Contains(body.Status))
            throw new ApiProblem(StatusCodes.Status400BadRequest, "plano_carreira.dados_invalidos");

        if ((body.MetaMinimaContratos is null) != (body.MetaMinimaFonteData is null))
            throw new ApiProblem(StatusCodes.Status400BadRequest, "plano_carreira.meta_minima_incompleta");
        if (body.MetaMinimaContratos is <= 0)
            throw new ApiProblem(StatusCodes.Status400BadRequest, "plano_carreira.meta_minima_incompleta");
        if (body.MetaMinimaFonteData is not null && !MetaMinimaFontes.Contains(body.MetaMinimaFonteData))
            throw new ApiProblem(StatusCodes.Status400BadRequest, "plano_carreira.meta_minima_incompleta");

        var faixas = body.Faixas ?? [];
        if (faixas.Any(f => f.QuantidadeMin <= 0 || (f.QuantidadeMax is not null && f.QuantidadeMax < f.QuantidadeMin) || f.ValorPorPlaca <= 0))
            throw new ApiProblem(StatusCodes.Status400BadRequest, "plano_carreira.dados_invalidos");

        var regras = body.RegrasRecorrencia ?? [];
        if (body.RecorrenciaAtiva != (regras.Length > 0))
            throw new ApiProblem(StatusCodes.Status400BadRequest, "plano_carreira.recorrencia_inconsistente");
        if (regras.Any(r => r.Tipo is null || !TiposRecorrencia.Contains(r.Tipo)
                || (r.Tipo == "upline") != (r.Nivel is >= 1)
                || r.Taxa <= 0 || r.Taxa > 1))
            throw new ApiProblem(StatusCodes.Status400BadRequest, "plano_carreira.dados_invalidos");

        return (name, body.Classificacao, faixas, regras);
    }

    private static async Task InsertFaixasAsync(Tx tx, Guid tenant, Guid planoId, FaixaBonusInput[] faixas)
    {
        foreach (var faixa in faixas)
        {
            await tx.ExecuteAsync(
                """
                insert into planos_carreira_faixas_bonus (id, tenant_id, plano_carreira_id, quantidade_min, quantidade_max, valor_por_placa)
                values (@id, @tenant, @planoId, @quantidadeMin, @quantidadeMax, @valorPorPlaca)
                """,
                new { id = Guid.CreateVersion7(), tenant, planoId, quantidadeMin = faixa.QuantidadeMin, quantidadeMax = faixa.QuantidadeMax, valorPorPlaca = faixa.ValorPorPlaca });
        }
    }

    private static async Task InsertRegrasAsync(Tx tx, Guid tenant, Guid planoId, RegraRecorrenciaInput[] regras)
    {
        try
        {
            foreach (var regra in regras)
            {
                await tx.ExecuteAsync(
                    """
                    insert into planos_carreira_regras_recorrencia (id, tenant_id, plano_carreira_id, tipo, nivel, taxa)
                    values (@id, @tenant, @planoId, @tipo, @nivel, @taxa)
                    """,
                    new { id = Guid.CreateVersion7(), tenant, planoId, tipo = regra.Tipo, nivel = regra.Nivel, taxa = regra.Taxa });
            }
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new ApiProblem(StatusCodes.Status409Conflict, "plano_carreira.regra_duplicada");
        }
    }

    private static async Task<IResult> CriarAsync(PlanoCarreiraRequest body, RequestContext request, Database db, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        var (name, classificacao, faixas, regras) = Validate(body);

        var id = Guid.CreateVersion7();
        var plano = await db.InTenantAsync(tenant, actor, async tx =>
        {
            await tx.ExecuteAsync(
                """
                insert into planos_carreira (id, tenant_id, name, classificacao, janela_apuracao_dias, meta_minima_contratos,
                       meta_minima_fonte_data, bonus_extra_valor, recorrencia_ativa)
                values (@id, @tenant, @name, @classificacao, @janelaApuracaoDias, @metaMinimaContratos,
                       @metaMinimaFonteData, @bonusExtraValor, @recorrenciaAtiva)
                """,
                new { id, tenant, name, classificacao, janelaApuracaoDias = body.JanelaApuracaoDias,
                    metaMinimaContratos = body.MetaMinimaContratos, metaMinimaFonteData = body.MetaMinimaFonteData,
                    bonusExtraValor = body.BonusExtraValor, recorrenciaAtiva = body.RecorrenciaAtiva });
            await InsertFaixasAsync(tx, tenant, id, faixas);
            await InsertRegrasAsync(tx, tenant, id, regras);
            await WriteAsync(tx, tenant, actor, "planos_carreira.criar", "planos_carreira", id, null, body);
            return await LoadPlanoAsync(tx, id);
        }, ct);
        return Results.Created($"/planos-carreira/{id}", plano);
    }

    private static async Task<IResult> AtualizarAsync(Guid id, PlanoCarreiraRequest body, RequestContext request, Database db, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        var (name, classificacao, faixas, regras) = Validate(body);
        var status = body.Status ?? "ativo";

        var plano = await db.InTenantAsync(tenant, actor, async tx =>
        {
            var affected = await tx.ExecuteAsync(
                """
                update planos_carreira set name = @name, status = @status, classificacao = @classificacao,
                       janela_apuracao_dias = @janelaApuracaoDias, meta_minima_contratos = @metaMinimaContratos,
                       meta_minima_fonte_data = @metaMinimaFonteData, bonus_extra_valor = @bonusExtraValor,
                       recorrencia_ativa = @recorrenciaAtiva
                 where id = @id
                """,
                new { id, name, status, classificacao, janelaApuracaoDias = body.JanelaApuracaoDias,
                    metaMinimaContratos = body.MetaMinimaContratos, metaMinimaFonteData = body.MetaMinimaFonteData,
                    bonusExtraValor = body.BonusExtraValor, recorrenciaAtiva = body.RecorrenciaAtiva });
            if (affected == 0)
                throw new ApiProblem(StatusCodes.Status404NotFound, "plano_carreira.not_found");
            await tx.ExecuteAsync("delete from planos_carreira_faixas_bonus where plano_carreira_id = @id", new { id });
            await tx.ExecuteAsync("delete from planos_carreira_regras_recorrencia where plano_carreira_id = @id", new { id });
            await InsertFaixasAsync(tx, tenant, id, faixas);
            await InsertRegrasAsync(tx, tenant, id, regras);
            await WriteAsync(tx, tenant, actor, "planos_carreira.atualizar", "planos_carreira", id, null, body);
            return await LoadPlanoAsync(tx, id);
        }, ct);
        return Results.Ok(plano);
    }

    private static async Task<IResult> RemoverAsync(Guid id, RequestContext request, Database db, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        await db.InTenantAsync(tenant, actor, async tx =>
        {
            var emUso = await tx.ExecuteScalarAsync<bool>("select exists(select 1 from users where plano_carreira_id = @id)", new { id });
            if (emUso)
                throw new ApiProblem(StatusCodes.Status409Conflict, "plano_carreira.em_uso");
            var affected = await tx.ExecuteAsync("delete from planos_carreira where id = @id", new { id });
            if (affected == 0)
                throw new ApiProblem(StatusCodes.Status404NotFound, "plano_carreira.not_found");
            await WriteAsync(tx, tenant, actor, "planos_carreira.remover", "planos_carreira", id, null, null);
            return 0;
        }, ct);
        return Results.NoContent();
    }
}
