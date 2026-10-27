using Npgsql;
using static Recorrencia.Api.Audit.Audit;
using Recorrencia.Api.Authorization;
using Recorrencia.Api.Commissions;
using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Security;
using Recorrencia.Api.Tenancy;

namespace Recorrencia.Api.Integracoes;

public static class HinovaEndpoints
{
    public sealed record CredenciaisStatusResponse(bool Configurado, DateTimeOffset? AtualizadoEm, string? AtualizadoPor);
    public sealed record SalvarCredenciaisRequest(string Usuario, string Senha, string TokenSga);
    public sealed record VoluntarioResponse(string Codigo, string Nome, string Cpf, string? Telefone, IReadOnlyList<string> Cooperativas, bool JaVinculado, string? VinculadoA);
    public sealed record MapeamentoResponse(Guid UserId, string UserName, string CodigoVoluntario, string NomeHinova, string CpfHinova, DateTimeOffset MappedAt);
    public sealed record CriarMapeamentoRequest(Guid UserId, string CodigoVoluntario, string NomeHinova, string CpfHinova);
    public sealed record AtualizarMapeamentoRequest(string? CodigoVoluntario, string? NomeHinova, string? CpfHinova);

    private sealed class StatusRow
    {
        public DateTimeOffset UpdatedAt { get; set; }
        public string UpdatedByName { get; set; } = "";
    }

    private sealed class MappingRow
    {
        public string CodigoVoluntario { get; set; } = "";
        public string VinculadoA { get; set; } = "";
    }

    private sealed class MapeamentoRow
    {
        public Guid UserId { get; set; }
        public string UserName { get; set; } = "";
        public string CodigoVoluntario { get; set; } = "";
        public string NomeHinova { get; set; } = "";
        public string CpfHinova { get; set; } = "";
        public DateTimeOffset MappedAt { get; set; }
    }

    public static void MapHinovaEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/integracoes/hinova/credenciais", ObterCredenciaisStatusAsync).RequirePermission("integracoes.gerenciar");
        app.MapPut("/integracoes/hinova/credenciais", SalvarCredenciaisAsync).RequirePermission("integracoes.gerenciar");
        app.MapGet("/integracoes/hinova/voluntarios", ListarVoluntariosAsync).RequirePermission("integracoes.gerenciar");
        app.MapGet("/integracoes/hinova/mapeamentos", ListarMapeamentosAsync).RequirePermission("integracoes.gerenciar");
        app.MapPost("/integracoes/hinova/mapeamentos", CriarMapeamentoAsync).RequirePermission("integracoes.gerenciar");
        app.MapPut("/integracoes/hinova/mapeamentos/{userId:guid}", AtualizarMapeamentoAsync).RequirePermission("integracoes.gerenciar");
        app.MapDelete("/integracoes/hinova/mapeamentos/{userId:guid}", RemoverMapeamentoAsync).RequirePermission("integracoes.gerenciar");
        app.MapPost("/integracoes/hinova/boletos/importar", ImportarBoletosAsync).RequirePermission("integracoes.gerenciar");
    }

    private static async Task<IResult> ObterCredenciaisStatusAsync(RequestContext request, Database db, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var user = request.RequireUser();
        var row = await db.InTenantAsync(tenant, user, tx => tx.QuerySingleOrDefaultAsync<StatusRow>(
            """
            select hc.updated_at, u.name as updated_by_name
              from hinova_credenciais hc join users u on u.id = hc.updated_by
             where hc.tenant_id = @tenant
            """,
            new { tenant }), ct);

        return Results.Ok(row is null
            ? new CredenciaisStatusResponse(false, null, null)
            : new CredenciaisStatusResponse(true, row.UpdatedAt, row.UpdatedByName));
    }

    private static async Task<IResult> SalvarCredenciaisAsync(SalvarCredenciaisRequest body, RequestContext request, Database db,
        IHinovaClient hinova, AesGcmCipher cipher, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        if (string.IsNullOrWhiteSpace(body.Usuario) || string.IsNullOrWhiteSpace(body.Senha) || string.IsNullOrWhiteSpace(body.TokenSga))
            throw new ApiProblem(StatusCodes.Status400BadRequest, "request.invalid");

        try
        {
            await hinova.AutenticarAsync(body.Usuario, body.Senha, body.TokenSga, ct);
        }
        catch (HinovaAuthException ex)
        {
            throw HinovaAuth.ToProblem(ex);
        }

        await db.InTenantAsync(tenant, actor, async tx =>
        {
            await tx.ExecuteAsync(
                """
                insert into hinova_credenciais (tenant_id, usuario_enc, senha_enc, token_sga_enc, updated_by)
                values (@tenant, @usuario, @senha, @tokenSga, @actor)
                on conflict (tenant_id) do update set
                  usuario_enc = excluded.usuario_enc, senha_enc = excluded.senha_enc,
                  token_sga_enc = excluded.token_sga_enc, updated_at = now(), updated_by = excluded.updated_by
                """,
                new
                {
                    tenant, actor,
                    usuario = cipher.Encrypt(body.Usuario),
                    senha = cipher.Encrypt(body.Senha),
                    tokenSga = cipher.Encrypt(body.TokenSga),
                });
            await WriteAsync(tx, tenant, actor, "hinova.credenciais.salvar", "hinova_credenciais", null, null, new { body.Usuario });
            return 0;
        }, ct);

        return Results.NoContent();
    }

    private static async Task<IResult> ListarVoluntariosAsync(string? query, RequestContext request, Database db, IHinovaClient hinova,
        AesGcmCipher cipher, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var user = request.RequireUser();

        var tokenUsuario = await HinovaAuth.GetTokenUsuarioAsync(db, tenant, user, cipher, hinova, ct);
        var voluntarios = await hinova.ListarVoluntariosAsync(tokenUsuario, ct);

        var filtered = string.IsNullOrWhiteSpace(query)
            ? voluntarios
            : voluntarios.Where(v => v.Nome.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        var mapped = (await db.InTenantAsync(tenant, user, tx => tx.QueryAsync<MappingRow>(
            """
            select m.codigo_voluntario, u.name as vinculado_a
              from hinova_voluntario_mapping m join users u on u.id = m.user_id
             where m.tenant_id = @tenant
            """,
            new { tenant }), ct)).ToDictionary(m => m.CodigoVoluntario, m => m.VinculadoA);

        return Results.Ok(filtered.Select(v => new VoluntarioResponse(
            v.Codigo, v.Nome, v.Cpf, v.Telefone, v.Cooperativas, mapped.ContainsKey(v.Codigo), mapped.GetValueOrDefault(v.Codigo))).ToList());
    }

    private static async Task<IResult> ListarMapeamentosAsync(RequestContext request, Database db, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var user = request.RequireUser();
        var rows = await db.InTenantAsync(tenant, user, tx => tx.QueryAsync<MapeamentoRow>(
            """
            select m.user_id, u.name as user_name, m.codigo_voluntario, m.nome_hinova, m.cpf_hinova, m.mapped_at
              from hinova_voluntario_mapping m join users u on u.id = m.user_id
             where m.tenant_id = @tenant
             order by u.name
            """,
            new { tenant }), ct);

        return Results.Ok(rows.Select(r => new MapeamentoResponse(
            r.UserId, r.UserName, r.CodigoVoluntario, r.NomeHinova, r.CpfHinova, r.MappedAt)).ToList());
    }

    private static async Task<IResult> CriarMapeamentoAsync(CriarMapeamentoRequest body, RequestContext request, Database db, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();

        await db.InTenantAsync(tenant, actor, async tx =>
        {
            try
            {
                await tx.ExecuteAsync(
                    """
                    insert into hinova_voluntario_mapping (tenant_id, user_id, codigo_voluntario, nome_hinova, cpf_hinova, mapped_by)
                    values (@tenant, @userId, @codigo, @nome, @cpf, @actor)
                    """,
                    new { tenant, actor, userId = body.UserId, codigo = body.CodigoVoluntario, nome = body.NomeHinova, cpf = body.CpfHinova });
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw new ApiProblem(StatusCodes.Status409Conflict, "hinova.vinculo_duplicado");
            }
            await WriteAsync(tx, tenant, actor, "hinova.vincular", "hinova_voluntario_mapping", body.UserId, null, body);
            return 0;
        }, ct);

        return Results.Created($"/integracoes/hinova/mapeamentos/{body.UserId}", body);
    }

    private static async Task<IResult> AtualizarMapeamentoAsync(Guid userId, AtualizarMapeamentoRequest body, RequestContext request, Database db, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        var codigo = (body.CodigoVoluntario ?? "").Trim();
        var nome = (body.NomeHinova ?? "").Trim();
        var cpf = (body.CpfHinova ?? "").Trim();
        if (codigo.Length == 0 || nome.Length == 0 || cpf.Length == 0)
            throw new ApiProblem(StatusCodes.Status400BadRequest, "request.invalid");

        await db.InTenantAsync(tenant, actor, async tx =>
        {
            var current = await tx.QuerySingleOrDefaultAsync<MapeamentoRow>(
                """
                select user_id, codigo_voluntario, nome_hinova, cpf_hinova
                  from hinova_voluntario_mapping
                 where tenant_id = @tenant and user_id = @userId
                 for update
                """,
                new { tenant, userId }) ?? throw new ApiProblem(StatusCodes.Status404NotFound, "hinova.vinculo_not_found");
            try
            {
                await tx.ExecuteAsync(
                    "update hinova_voluntario_mapping set codigo_voluntario = @codigo, nome_hinova = @nome, cpf_hinova = @cpf where tenant_id = @tenant and user_id = @userId",
                    new { tenant, userId, codigo, nome, cpf });
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw new ApiProblem(StatusCodes.Status409Conflict, "hinova.vinculo_duplicado");
            }
            await WriteAsync(tx, tenant, actor, "hinova.atualizar_vinculo", "hinova_voluntario_mapping", userId,
                new { current.CodigoVoluntario, current.NomeHinova, current.CpfHinova }, new { codigo, nome, cpf });
            return 0;
        }, ct);

        return Results.NoContent();
    }

    private static async Task<IResult> RemoverMapeamentoAsync(Guid userId, RequestContext request, Database db, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();

        await db.InTenantAsync(tenant, actor, async tx =>
        {
            var deleted = await tx.ExecuteAsync(
                "delete from hinova_voluntario_mapping where tenant_id = @tenant and user_id = @userId", new { tenant, userId });
            if (deleted == 0)
                throw new ApiProblem(StatusCodes.Status404NotFound, "hinova.vinculo_not_found");
            await WriteAsync(tx, tenant, actor, "hinova.desvincular", "hinova_voluntario_mapping", userId, null, null);
            return 0;
        }, ct);

        return Results.NoContent();
    }

    public sealed record ImportarBoletosRequest(string? Competencia);
    public sealed record ImportarBoletosResponse(int TotalBoletos, int Importados, int SemVinculo, int ConflitoMultiploVendedor, int NaoPago,
        int VencimentoRetornados = 0, int VencimentoVinculados = 0);
    public sealed record ImportarBoletosProgress(string Fase, int TotalBoletos, int PagamentosConsultados,
        int PagamentosProcessados, int Importados, int SemVinculo, int ConflitoMultiploVendedor, int NaoPago,
        int VencimentoRetornados, int VencimentosConsultados, int VencimentosProcessados, int VencimentoVinculados);

    private sealed class MapeamentoCodigoRow
    {
        public Guid UserId { get; set; }
        public string CodigoVoluntario { get; set; } = "";
    }

    private static async Task<IResult> ImportarBoletosAsync(
        ImportarBoletosRequest body, RequestContext request, Database db, IHinovaClient hinova, AesGcmCipher cipher, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        if (string.IsNullOrWhiteSpace(body.Competencia))
            throw new ApiProblem(StatusCodes.Status400BadRequest, "commission.invalid_competencia");
        var mesInicio = CommissionService.ParseCompetencia(body.Competencia);
        return Results.Ok(await ImportarBoletosCoreAsync(mesInicio, tenant, actor, db, hinova, cipher, ct));
    }

    internal static async Task<ImportarBoletosResponse> ImportarBoletosCoreAsync(
        DateOnly mesInicio, Guid tenant, Guid actor, Database db, IHinovaClient hinova, AesGcmCipher cipher, CancellationToken ct,
        Func<ImportarBoletosProgress, CancellationToken, Task>? reportProgress = null,
        bool incluirVencimentos = true, DateOnly? pagamentoInicio = null, DateOnly? pagamentoFim = null,
        bool ignorarImportados = false, bool somenteVencimentos = false)
    {
        var mesFim = mesInicio.AddMonths(1).AddDays(-1);
        var inicioPagamento = pagamentoInicio ?? mesInicio;
        var fimPagamento = pagamentoFim ?? mesFim;

        var tokenUsuario = await HinovaAuth.GetTokenUsuarioAsync(db, tenant, actor, cipher, hinova, ct);

        var mapeamentos = await db.InTenantAsync(tenant, actor, tx => tx.QueryAsync<MapeamentoCodigoRow>(
            "select user_id, codigo_voluntario from hinova_voluntario_mapping where tenant_id = @tenant", new { tenant }), ct);
        var userIdPorCodigo = mapeamentos.ToDictionary(m => m.CodigoVoluntario, m => m.UserId);
        if (userIdPorCodigo.Count == 0 && !await db.InTenantAsync(tenant, actor, tx => tx.ExecuteScalarAsync<bool>(
            "select exists(select 1 from boletos where tenant_id = @tenant and hinova_nosso_numero is not null)",
            new { tenant }), ct))
            return new ImportarBoletosResponse(0, 0, 0, 0, 0);


        var importados = 0;
        var semVinculo = 0;
        var conflito = 0;
        var naoPago = 0;
        var vencimentoVinculados = 0;
        var pagamentosTotal = 0;
        var pagamentosConsultados = 0;
        var pagamentosProcessados = 0;
        var vencimentosTotal = 0;
        var vencimentosConsultados = 0;
        var vencimentosProcessados = 0;
        async Task ReportAsync(string fase) {
            if (reportProgress is not null)
                await reportProgress(new ImportarBoletosProgress(fase, pagamentosTotal, pagamentosConsultados,
                    pagamentosProcessados, importados, semVinculo, conflito, naoPago,
                    vencimentosTotal, vencimentosConsultados, vencimentosProcessados, vencimentoVinculados), ct);
        }

        // General by date only -- codigo_voluntario is matched client-side below, against each
        // returned boleto's own veiculos[] (see HinovaBoletoPeriodoFiltro for why).
        var todosBoletos = somenteVencimentos ? new List<HinovaBoleto>() : await ListarPeriodoAsync(hinova, tokenUsuario, inicioPagamento, fimPagamento, false, ct,
            async (consultados, total) => {
                pagamentosConsultados = consultados;
                pagamentosTotal = total;
                await ReportAsync("buscando_pagamentos");
            });
        pagamentosTotal = todosBoletos.Count;

        var hoje = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, "America/Sao_Paulo"));
        foreach (var lote in todosBoletos.Chunk(100))
        {
            await db.InTenantAsync(tenant, actor, async tx =>
            {
            foreach (var boleto in lote)
            {
                if (pagamentoInicio is not null && (boleto.DataPagamento is null ||
                    boleto.DataPagamento < inicioPagamento || boleto.DataPagamento > fimPagamento))
                    continue;
                if (ignorarImportados && await tx.ExecuteScalarAsync<bool>(
                    "select exists(select 1 from boletos where tenant_id = @tenant and hinova_nosso_numero = @numero and status = 'recebido' and pago_em is not null)",
                    new { tenant, numero = boleto.NossoNumero }))
                    continue;

                // Every vehicle on a boleto belongs to exactly one vendedor, but a boleto can carry
                // several vehicles -- when they're all the same vendedor's, the whole payment is
                // theirs (counted once, not once per vehicle); when they resolve to more than one
                // vendedor, there's no agreed rule for splitting a single payment (explicitly
                // deferred), so that boleto is skipped and counted instead of guessed at.
                var vendorIds = boleto.Veiculos
                    .Select(v => v.CodigoVoluntario)
                    .Where(c => c is not null && userIdPorCodigo.ContainsKey(c))
                    .Select(c => userIdPorCodigo[c!])
                    .Distinct()
                    .ToList();

                if (vendorIds.Count != 1)
                    await ReconcileExistingBoletoAsync(tx, tenant, boleto);
                if (vendorIds.Count == 0)
                {
                    semVinculo++;
                    // An existing payment may have been attributed manually before a
                    // volunteer mapping was created. A missing current mapping alone
                    // does not invalidate that historical attribution.
                    continue;
                }
                if (vendorIds.Count > 1)
                {
                    conflito++;
                    await HoldPreviouslyImportedBoletoAsync(tx, tenant, boleto.NossoNumero, "multiple_mapped_volunteers");
                    continue;
                }
                // The date filter is by data_pagamento, but confirmed against the real API: it
                // still returns boletos due in that window whose payment hasn't actually landed
                // yet (valor_pagamento 0 or absent, data_pagamento null) -- only real confirmed
                // payments should ever generate commission, so these are counted, not imported.
                if (boleto.ValorPagamento <= 0 || boleto.DataPagamento is null)
                {
                    naoPago++;
                    await HoldPreviouslyImportedBoletoAsync(tx, tenant, boleto.NossoNumero, "payment_not_confirmed");
                    continue;
                }

                var placa = boleto.Veiculos.FirstOrDefault(v => v.Placa is not null)?.Placa;
                var boletoId = await tx.ExecuteScalarAsync<Guid?>(
                    """
                    insert into boletos (tenant_id, participante_id, associado_ref, associado_nome, placa, valor, status, vencimento, pago_em, hinova_nosso_numero)
                    values (@tenant, @participante, @associadoRef, @associadoNome, @placa, @valor, 'recebido', @vencimento, @pagoEm, @nossoNumero)
                    on conflict (tenant_id, hinova_nosso_numero) where hinova_nosso_numero is not null
                    do update set participante_id = excluded.participante_id, valor = excluded.valor,
                                  status = 'recebido', vencimento = excluded.vencimento,
                                  pago_em = excluded.pago_em, placa = excluded.placa,
                                  commission_hold_reason = null, updated_at = now()
                    where not @ignorarImportados or boletos.status <> 'recebido' or boletos.pago_em is null
                    returning id
                    """,
                    new
                    {
                        tenant,
                        ignorarImportados,
                        participante = vendorIds[0],
                        associadoRef = boleto.CodigoAssociado,
                        associadoNome = boleto.NomeAssociado,
                        placa,
                        valor = boleto.ValorPagamento,
                        // A handful of real boletos come back with no data_vencimento at all;
                        // the payment date is a reasonable stand-in since every imported row is
                        // already paid (status is always 'recebido' here, never 'a_vencer').
                        vencimento = boleto.Vencimento ?? boleto.DataPagamento!.Value,
                        pagoEm = boleto.DataPagamento,
                        nossoNumero = boleto.NossoNumero,
                    });
                if (boletoId is not { } idImportado) continue;
                await SaveBoletoVeiculosAsync(tx, tenant, idImportado, boleto.Veiculos);
                importados++;
            }
            return 0;
            }, ct);
            pagamentosProcessados += lote.Length;
            await ReportAsync("importando_pagamentos");
        }
        // Finish persisting payments before querying the outstanding wallets.
        var mesesVencimento = new HashSet<DateOnly>();
        var vinculadosPorMes = new Dictionary<DateOnly, int>();
        var boletosPorVencimento = new List<HinovaBoleto>();
        if (incluirVencimentos)
        {
            mesesVencimento.Add(mesInicio);
            {
                var anteriores = await db.InTenantAsync(tenant, actor, tx => tx.QueryAsync<DateOnly>(
                    """
                    select distinct date_trunc('month', vencimento)::date
                      from boletos
                     where tenant_id = @tenant and status in ('atraso', 'a_vencer')
                       and hinova_nosso_numero is not null
                       and vencimento < @mesInicio
                    """, new { tenant, mesInicio }), ct);
                mesesVencimento.UnionWith(anteriores);
            }
            foreach (var mes in mesesVencimento.Order())
            {
                var anteriores = boletosPorVencimento.Count;
                var rows = await ListarPeriodoAsync(hinova, tokenUsuario, mes, mes.AddMonths(1).AddDays(-1), true, ct,
                    async (consultados, total) =>
                    {
                        vencimentosConsultados = anteriores + consultados;
                        vencimentosTotal = anteriores + total;
                        await ReportAsync("buscando_vencimentos");
                    });
                boletosPorVencimento.AddRange(rows);
            }
            vencimentosTotal = boletosPorVencimento.Count;
        }
        foreach (var lote in boletosPorVencimento.Chunk(100))
        {
            await db.InTenantAsync(tenant, actor, async tx =>
            {
            foreach (var boleto in lote)
            {
                // Settlement/closure of an existing debt must not depend on today's
                // volunteer mapping, vehicle value or commission attribution.
                await ReconcileExistingBoletoAsync(tx, tenant, boleto);
                var vendorIds = boleto.Veiculos
                    .Select(v => v.CodigoVoluntario)
                    .Where(c => c is not null && userIdPorCodigo.ContainsKey(c))
                    .Select(c => userIdPorCodigo[c!])
                    .Distinct()
                    .ToList();
                if (vendorIds.Count != 1 || boleto.Vencimento is null || !mesesVencimento.Contains(new DateOnly(boleto.Vencimento.Value.Year, boleto.Vencimento.Value.Month, 1))
                    || boleto.ValorBoleto <= 0 || string.IsNullOrWhiteSpace(boleto.NossoNumero))
                    continue;

                // The real API can echo valor_boleto in valor_pagamento on an unpaid row.
                // Only a genuine payment date makes it received.
                var isPaid = boleto.DataPagamento is not null && boleto.ValorPagamento > 0;
                var status = isPaid ? "recebido"
                    : HinovaBoletoStatus.IsClosed(boleto) ? "cancelado"
                    : boleto.Vencimento < hoje ? "atraso" : "a_vencer";
                var placa = boleto.Veiculos.FirstOrDefault(v => v.Placa is not null)?.Placa;
                var boletoId = await tx.ExecuteScalarAsync<Guid>(
                    """
                    insert into boletos (tenant_id, participante_id, associado_ref, associado_nome, placa, valor, status, vencimento, pago_em, hinova_nosso_numero)
                    values (@tenant, @participante, @associadoRef, @associadoNome, @placa, @valor, @status, @vencimento, @pagoEm, @nossoNumero)
                    on conflict (tenant_id, hinova_nosso_numero) where hinova_nosso_numero is not null
                    do update set participante_id = excluded.participante_id,
                                  valor = case when boletos.status = 'recebido' and excluded.status <> 'recebido' then boletos.valor else excluded.valor end,
                                  status = case when boletos.status = 'recebido' and excluded.status <> 'recebido' then boletos.status else excluded.status end,
                                  pago_em = case when boletos.status = 'recebido' and excluded.status <> 'recebido' then boletos.pago_em else excluded.pago_em end,
                                  vencimento = excluded.vencimento, placa = excluded.placa, updated_at = now()
                    returning id
                    """,
                    new
                    {
                        tenant,
                        participante = vendorIds[0],
                        associadoRef = boleto.CodigoAssociado,
                        associadoNome = boleto.NomeAssociado,
                        placa,
                        valor = isPaid ? boleto.ValorPagamento : boleto.ValorBoleto,
                        status,
                        vencimento = boleto.Vencimento,
                        pagoEm = isPaid ? boleto.DataPagamento : null,
                        nossoNumero = boleto.NossoNumero,
                    });
                await SaveBoletoVeiculosAsync(tx, tenant, boletoId, boleto.Veiculos);
                vencimentoVinculados++;
                var mesVencimento = new DateOnly(boleto.Vencimento.Value.Year, boleto.Vencimento.Value.Month, 1);
                vinculadosPorMes[mesVencimento] = vinculadosPorMes.GetValueOrDefault(mesVencimento) + 1;
            }
            return 0;
            }, ct);
            vencimentosProcessados += lote.Length;
            await ReportAsync("importando_vencimentos");
        }

        await db.InTenantAsync(tenant, actor, async tx =>
        {

            foreach (var mes in mesesVencimento) await tx.ExecuteAsync(
                """
                insert into boleto_vencimento_importacoes (tenant_id, competencia, total_retornado, total_vinculado)
                values (@tenant, @mesInicio, @totalRetornado, @totalVinculado)
                on conflict (tenant_id, competencia)
                do update set importado_em = now(), total_retornado = excluded.total_retornado, total_vinculado = excluded.total_vinculado
                """,
                new { tenant, mesInicio = mes,
                    totalRetornado = boletosPorVencimento.Count(b => b.Vencimento is { } d && d.Year == mes.Year && d.Month == mes.Month),
                    totalVinculado = vinculadosPorMes.GetValueOrDefault(mes) });
            await WriteAsync(tx, tenant, actor, "hinova.boletos.importar", "boletos", null, null,
                new { competencia = mesInicio.ToString("yyyy-MM"), total = todosBoletos.Count, importados, semVinculo, conflito, naoPago,
                    vencimentoRetornados = boletosPorVencimento.Count, vencimentoVinculados });
            return 0;
        }, ct);

        return new ImportarBoletosResponse(todosBoletos.Count, importados, semVinculo, conflito, naoPago,
            boletosPorVencimento.Count, vencimentoVinculados);
    }

    private static async Task ReconcileExistingBoletoAsync(Tx tx, Guid tenant, HinovaBoleto boleto)
    {
        var paid = HinovaBoletoStatus.IsPaid(boleto);
        if (string.IsNullOrWhiteSpace(boleto.NossoNumero) || (!paid && !HinovaBoletoStatus.IsClosed(boleto))) return;
        await tx.ExecuteAsync("""
            update boletos set status = @status,
                valor = case when @paid then @valor else valor end,
                pago_em = case when @paid then @pagamento else pago_em end
             where tenant_id = @tenant and hinova_nosso_numero = @numero
               and status <> 'recebido'
            """, new { tenant, numero = boleto.NossoNumero, paid,
                status = paid ? "recebido" : "cancelado", valor = boleto.ValorPagamento,
                pagamento = boleto.DataPagamento });
    }

    private static async Task HoldPreviouslyImportedBoletoAsync(Tx tx, Guid tenant, string nossoNumero, string reason)
    {
        if (string.IsNullOrWhiteSpace(nossoNumero)) return;
        await tx.ExecuteAsync(
            """
            update boletos
               set commission_hold_reason = @reason
             where tenant_id = @tenant and hinova_nosso_numero = @nossoNumero
               and commission_hold_reason is distinct from @reason
            """, new { tenant, nossoNumero, reason });
    }

    private static async Task SaveBoletoVeiculosAsync(Tx tx, Guid tenant, Guid boletoId,
        IReadOnlyList<HinovaVeiculoBoleto> veiculos)
    {
        for (var ordem = 0; ordem < veiculos.Count; ordem++)
        {
            var veiculo = veiculos[ordem];
            await tx.ExecuteAsync(
                """
                insert into boleto_veiculos (tenant_id, boleto_id, ordem, codigo_veiculo, placa, data_inicio_contrato)
                values (@tenant, @boletoId, @ordem, @codigo, @placa, @dataContrato)
                on conflict (tenant_id, boleto_id, ordem) do update set
                  codigo_veiculo = excluded.codigo_veiculo,
                  placa = excluded.placa,
                  data_inicio_contrato = case
                    when boleto_veiculos.codigo_veiculo is not distinct from excluded.codigo_veiculo
                     and boleto_veiculos.placa is not distinct from excluded.placa
                    then coalesce(excluded.data_inicio_contrato, boleto_veiculos.data_inicio_contrato)
                    else excluded.data_inicio_contrato
                  end
                """,
                new { tenant, boletoId, ordem, codigo = veiculo.CodigoVeiculo,
                    veiculo.Placa, dataContrato = veiculo.DataInicioContratoVeiculo });
        }
        await tx.ExecuteAsync(
            "delete from boleto_veiculos where tenant_id = @tenant and boleto_id = @boletoId and ordem >= @count",
            new { tenant, boletoId, count = veiculos.Count });
    }

    private static async Task<List<HinovaBoleto>> ListarPeriodoAsync(IHinovaClient hinova, string tokenUsuario,
        DateOnly mesInicio, DateOnly mesFim, bool porVencimento, CancellationToken ct,
        Func<int, int, Task>? reportProgress = null)
    {
        var result = new List<HinovaBoleto>();
        var pagina = 0;
        var pageSize = 1000;
        while (true)
        {
            HinovaBoletoPagina page;
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    page = await hinova.ListarBoletosPeriodoAsync(tokenUsuario,
                        new HinovaBoletoPeriodoFiltro(mesInicio, mesFim, pageSize, pagina, FiltrarPorVencimento: porVencimento), ct);
                    break;
                }
                catch (HttpRequestException ex) when (pageSize > 100 && !ct.IsCancellationRequested &&
                    ex.StatusCode == System.Net.HttpStatusCode.BadGateway)
                {
                    // Some large pages exceed Hinova's processing limit. Convert the page
                    // index to the same absolute offset before reducing the page size.
                    pagina = result.Count / 100;
                    pageSize = 100;
                    attempt = -1;
                }
                catch (OperationCanceledException) when (pageSize > 100 && !ct.IsCancellationRequested)
                {
                    pagina = result.Count / 100;
                    pageSize = 100;
                    attempt = -1;
                }
                catch (HttpRequestException ex) when (attempt < 2 && !ct.IsCancellationRequested &&
                    (ex.StatusCode is null || (int)ex.StatusCode == 429 || (int)ex.StatusCode >= 500))
                {
                    await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)), ct);
                }
                catch (OperationCanceledException) when (attempt < 2 && !ct.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)), ct);
                }
            }
            result.AddRange(page.Boletos);
            if (reportProgress is not null)
                await reportProgress(result.Count, Math.Max(page.TotalRegistros, result.Count));
            pagina++;
            if (page.Boletos.Count == 0 || page.NumeroPaginas == 0 || pagina >= page.NumeroPaginas)
                break;
        }
        return result;
    }
}
