using Microsoft.Extensions.Options;
using Recorrencia.Api.Authorization;
using Recorrencia.Api.Email;
using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Integracoes;
using Recorrencia.Api.Security;
using Recorrencia.Api.Tenancy;
using Recorrencia.Api.Users;
using static Recorrencia.Api.Audit.Audit;

namespace Recorrencia.Api.Convites;

public static class SolicitacaoCadastroEndpoints
{
    public sealed record SolicitarRequest(string? Nome, string? Cpf, string? Celular, string? Email,
        string? Cep, string? Logradouro, string? Numero, string? Complemento, string? Bairro, string? Cidade, string? Estado);

    public sealed record SolicitacaoResponse(Guid Id, string Nome, string Cpf, string Celular, string Email, string Cep,
        string Logradouro, string Numero, string? Complemento, string Bairro, string Cidade, string Estado,
        Guid IndicadorUserId, string IndicadorNome, DateTimeOffset CriadoEm);

    private sealed class IndicadorRow
    {
        public Guid UserId { get; set; }
        public string Nome { get; set; } = "";
    }

    private sealed class SolicitacaoRow
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public Guid IndicadorUserId { get; set; }
        // Populated only by the queries that join users (ListarAsync, and AprovarAsync's phase-1
        // read), aliasing users.name as indicador_nome; the plain "select * from
        // solicitacoes_cadastro ... for update" in AprovarAsync's final transaction has no such
        // column, so it stays "" there -- harmless, since that code path doesn't read it.
        public string IndicadorNome { get; set; } = "";
        public string Nome { get; set; } = "";
        public string Cpf { get; set; } = "";
        public string Celular { get; set; } = "";
        public string Email { get; set; } = "";
        public string Cep { get; set; } = "";
        public string Logradouro { get; set; } = "";
        public string Numero { get; set; } = "";
        public string? Complemento { get; set; }
        public string Bairro { get; set; } = "";
        public string Cidade { get; set; } = "";
        public string Estado { get; set; } = "";
        public string Status { get; set; } = "";
        public DateTimeOffset CriadoEm { get; set; }
    }

    public static void MapSolicitacaoCadastroEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/convite-links/{token}/solicitacoes", SolicitarAsync);
        app.MapGet("/solicitacoes-cadastro", ListarAsync).RequirePermission("usuarios.convidar").RequirePermission("integracoes.gerenciar");
        app.MapPost("/solicitacoes-cadastro/{id:guid}/aprovar", AprovarAsync).RequirePermission("usuarios.convidar").RequirePermission("integracoes.gerenciar");
        app.MapPost("/solicitacoes-cadastro/{id:guid}/rejeitar", RejeitarAsync).RequirePermission("usuarios.convidar").RequirePermission("integracoes.gerenciar");
    }

    private static async Task<IResult> SolicitarAsync(string token, SolicitarRequest body, RequestContext request, Database db,
        LoginThrottle throttle, CancellationToken ct)
    {
        var tenant = request.RequireTenant();

        var ipKey = request.ClientIp is { } ip ? $"convite-submit-ip:{ip}" : null;
        var keys = ipKey is not null ? new[] { ipKey } : Array.Empty<string>();
        if (!throttle.TryReserve(keys))
            throw new ApiProblem(StatusCodes.Status429TooManyRequests, "auth.too_many_attempts");

        try
        {
            // CPF só com dígitos: "111.222.333-44" e "11122233344" são o mesmo CPF, e é por dígitos
            // que a Hinova compara (BuscarVoluntarioAsync no AprovarAsync lê este mesmo valor salvo).
            // Um CPF vazio (ou sem nenhum dígito) vira "" e cai no request.invalid abaixo, junto com
            // os outros campos obrigatórios; só um CPF preenchido com tamanho errado é cpf_invalido.
            var cpf = new string((body.Cpf ?? "").Where(char.IsDigit).ToArray());
            var nome = (body.Nome ?? "").Trim();
            var celular = (body.Celular ?? "").Trim();
            var email = (body.Email ?? "").Trim();
            var cep = (body.Cep ?? "").Trim();
            var logradouro = (body.Logradouro ?? "").Trim();
            var numero = (body.Numero ?? "").Trim();
            var complemento = body.Complemento?.Trim();
            var bairro = (body.Bairro ?? "").Trim();
            var cidade = (body.Cidade ?? "").Trim();
            var estado = (body.Estado ?? "").Trim();
            if (nome.Length == 0 || cpf.Length == 0 || celular.Length == 0 || email.Length == 0 || cep.Length == 0
                || logradouro.Length == 0 || numero.Length == 0 || bairro.Length == 0 || cidade.Length == 0 || estado.Length == 0)
                throw new ApiProblem(StatusCodes.Status400BadRequest, "request.invalid");
            if (cpf.Length != 11)
                throw new ApiProblem(StatusCodes.Status400BadRequest, "solicitacao.cpf_invalido");

            // app.resolve_convite_link (Task 4, Step 0b) -- não um join direto com `users`: uma
            // sessão anônima não vê nenhuma linha de `users` via RLS, então um join aqui devolveria
            // zero linhas sempre, token válido ou não. Mesma função que ConviteLinkEndpoints.PublicoAsync usa.
            var indicador = await db.InTenantAsync(tenant, null, tx => tx.QuerySingleOrDefaultAsync<IndicadorRow>(
                "select * from app.resolve_convite_link(@tenant, @token)",
                new { tenant, token }), ct) ?? throw new ApiProblem(StatusCodes.Status404NotFound, "convite.link_invalido");

            // O id é gerado aqui, não via "returning" -- sob RLS, RETURNING é filtrado pelas
            // policies de SELECT da tabela, e essa sessão é anônima (db.InTenantAsync(tenant,
            // null, ...)): sem uma policy de select anônimo (que não existe, e não deveria
            // existir -- exporia CPF/endereço/e-mail de todo mundo no tenant), um insert com
            // "returning id" simplesmente devolveria zero linhas e QuerySingleAsync lançaria.
            // Gerar o id em C# (mesmo padrão de UserEndpoints.InviteAsync) evita o problema
            // completamente -- não é preciso ler a linha de volta pra saber o id que ela tem.
            var id = Guid.CreateVersion7();
            await db.InTenantAsync(tenant, null, tx => tx.ExecuteAsync(
                """
                insert into solicitacoes_cadastro
                  (id, tenant_id, indicador_user_id, nome, cpf, celular, email, cep, logradouro, numero, complemento, bairro, cidade, estado)
                values (@id, @tenant, @indicadorId, @nome, @cpf, @celular, @email, @cep, @logradouro, @numero, @complemento, @bairro, @cidade, @estado)
                """,
                new { id, tenant, indicadorId = indicador.UserId, nome, cpf, celular, email, cep, logradouro, numero, complemento, bairro, cidade, estado }), ct);

            throttle.Complete(keys, false);
            return Results.Created($"/solicitacoes-cadastro/{id}", new { id });
        }
        catch
        {
            throttle.Complete(keys, true);
            throw;
        }
    }

    private static async Task<IResult> ListarAsync(RequestContext request, Database db, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var user = request.RequireUser();
        var rows = await db.InTenantAsync(tenant, user, tx => tx.QueryAsync<SolicitacaoRow>(
            """
            select s.id, s.tenant_id, s.indicador_user_id, u.name as indicador_nome, s.nome, s.cpf, s.celular, s.email,
                   s.cep, s.logradouro, s.numero, s.complemento, s.bairro, s.cidade, s.estado, s.status, s.criado_em
              from solicitacoes_cadastro s join users u on u.id = s.indicador_user_id
             where s.status = 'pendente'
             order by s.criado_em
            """), ct);
        return Results.Ok(rows.Select(r => new SolicitacaoResponse(r.Id, r.Nome, r.Cpf, r.Celular, r.Email, r.Cep,
            r.Logradouro, r.Numero, r.Complemento, r.Bairro, r.Cidade, r.Estado, r.IndicadorUserId, r.IndicadorNome, r.CriadoEm)).ToList());
    }

    private static async Task<IResult> AprovarAsync(Guid id, RequestContext request, Database db, CurrentPermissions permissions,
        IHinovaClient hinova, AesGcmCipher cipher, IEmailSender email, LinkBuilder links, IOptions<AuthOptions> options, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        var mine = await permissions.GetAsync(ct);

        // Fase 1: leituras sem lock, só pra decidir se vale a pena tentar a Hinova. Pode ficar
        // desatualizado entre aqui e a fase 3 (outro admin resolvendo a mesma solicitação); a fase 3
        // reconfirma tudo com "for update" antes de escrever.
        var pre = await db.InTenantAsync(tenant, actor, tx => tx.QuerySingleOrDefaultAsync<SolicitacaoRow>(
            """
            select s.*, u.name as indicador_nome
              from solicitacoes_cadastro s join users u on u.id = s.indicador_user_id
             where s.tenant_id = @tenant and s.id = @id
            """, new { tenant, id }), ct) ?? throw new ApiProblem(StatusCodes.Status404NotFound, "solicitacao.not_found");
        if (pre.Status != "pendente")
            throw new ApiProblem(StatusCodes.Status409Conflict, "solicitacao.ja_resolvida");

        var indicadorCodigo = await db.InTenantAsync(tenant, actor, tx => tx.QuerySingleOrDefaultAsync<string>(
            "select codigo_voluntario from hinova_voluntario_mapping where tenant_id = @tenant and user_id = @indicadorId",
            new { tenant, indicadorId = pre.IndicadorUserId }), ct)
            ?? throw new ApiProblem(StatusCodes.Status409Conflict, "solicitacao.indicador_sem_hinova");

        // "::citext": users.email é citext, mas o parâmetro chega como text -- sem o cast o Postgres
        // escolhe text = text (sensível a caixa) e "Fulano@x" passaria por aqui, só batendo na
        // unique (tenant_id, email) depois de já ter escrito na Hinova.
        if (await db.InTenantAsync(tenant, actor, tx => tx.ExecuteScalarAsync<bool>(
            "select exists(select 1 from users where tenant_id = @tenant and email = @email::citext)", new { tenant, email = pre.Email }), ct))
            throw new ApiProblem(StatusCodes.Status409Conflict, "users.email_taken");

        var roleIds = (await db.InTenantAsync(tenant, actor, tx => tx.QueryAsync<Guid>(
            "select id from roles where tenant_id = @tenant and source_template_key = 'consultor'", new { tenant }), ct)).ToArray();
        await db.InTenantAsync(tenant, actor, async tx => { await RoleGuards.EnsureCanGrantRolesAsync(tx, mine, roleIds); return 0; }, ct);

        // Fase 2: chamadas de LEITURA à Hinova -- ainda seguro cancelar com o ct do request.
        var tokenUsuario = await HinovaAuth.GetTokenUsuarioAsync(db, tenant, actor, cipher, hinova, ct);

        var existente = await hinova.BuscarVoluntarioAsync(tokenUsuario, pre.Cpf, ct);
        if (existente is not null)
            throw new ApiProblem(StatusCodes.Status409Conflict, "solicitacao.cpf_ja_cadastrado");

        var indicador = await hinova.BuscarVoluntarioAsync(tokenUsuario, indicadorCodigo, ct)
            ?? throw new ApiProblem(StatusCodes.Status409Conflict, "solicitacao.indicador_sem_hinova");

        // Fase 3: daqui em diante é ESCRITA real (Hinova + a transação final) -- CancellationToken.None
        // pra não deixar um timeout comum do BFF cancelar a meio caminho e deixar a Hinova e o APROVEC
        // fora de sincronia.
        var obs = $"Cadastrado via indicação de {pre.IndicadorNome} em {DateOnly.FromDateTime(DateTime.UtcNow):dd/MM/yyyy}.";
        var codigoVoluntario = await hinova.CadastrarVoluntarioAsync(tokenUsuario, new CadastrarVoluntarioRequest(
            pre.Nome, pre.Cpf, pre.Celular, pre.Email, pre.Logradouro, pre.Numero, pre.Complemento, pre.Bairro,
            pre.Cidade, pre.Estado, pre.Cep, indicador.CooperativaCodigos, indicadorCodigo, obs), CancellationToken.None);

        var (newUserId, enderecoEmail, inviteToken) = await db.InTenantAsync(tenant, actor, async tx =>
        {
            var solicitacao = await tx.QuerySingleAsync<SolicitacaoRow>(
                "select * from solicitacoes_cadastro where tenant_id = @tenant and id = @id for update", new { tenant, id });
            if (solicitacao.Status != "pendente")
                throw new ApiProblem(StatusCodes.Status409Conflict, "solicitacao.ja_resolvida");

            var userId = Guid.CreateVersion7();
            await UserEndpoints.CreateUserWithRolesAsync(tx, tenant, userId, solicitacao.Nome, solicitacao.Email, solicitacao.IndicadorUserId, roleIds);
            await UserEndpoints.LinkHinovaAsync(tx, tenant, userId, actor, codigoVoluntario, solicitacao.Nome, solicitacao.Cpf);
            var inviteToken = await UserEndpoints.CreateInviteTokenAsync(tx, tenant, actor, userId, solicitacao.Nome, solicitacao.Email,
                solicitacao.IndicadorUserId, roleIds, options.Value.InviteTtlSeconds);

            await tx.ExecuteAsync(
                "update solicitacoes_cadastro set status = 'aprovado', resolvido_em = now(), resolvido_por = @actor, user_id_resultante = @userId where id = @id",
                new { actor, userId, id });
            await WriteAsync(tx, tenant, actor, "solicitacao.aprovar", "solicitacoes_cadastro", id, null, new { userId, codigoVoluntario });

            return (userId, solicitacao.Email, inviteToken);
        }, CancellationToken.None);

        await email.SendAsync(enderecoEmail, "Convite de acesso",
            EmailTemplates.AccessLink(
                "Convite de acesso",
                $"Você foi convidado para acessar a plataforma de {request.TenantName}.",
                "Definir minha senha",
                links.SetPassword(request.TenantSlug!, inviteToken),
                "O link vale por 72 horas.",
                links.Logo(request.TenantSlug!)), ct);

        return Results.Ok(new { userId = newUserId });
    }

    private static async Task<IResult> RejeitarAsync(Guid id, RequestContext request, Database db, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        await db.InTenantAsync(tenant, actor, async tx =>
        {
            var status = await tx.QuerySingleOrDefaultAsync<string>(
                "select status from solicitacoes_cadastro where tenant_id = @tenant and id = @id for update", new { tenant, id })
                ?? throw new ApiProblem(StatusCodes.Status404NotFound, "solicitacao.not_found");
            if (status != "pendente")
                throw new ApiProblem(StatusCodes.Status409Conflict, "solicitacao.ja_resolvida");

            await tx.ExecuteAsync(
                "update solicitacoes_cadastro set status = 'rejeitado', resolvido_em = now(), resolvido_por = @actor where id = @id",
                new { actor, id });
            await WriteAsync(tx, tenant, actor, "solicitacao.rejeitar", "solicitacoes_cadastro", id, null, null);
            return 0;
        }, ct);
        return Results.NoContent();
    }
}
