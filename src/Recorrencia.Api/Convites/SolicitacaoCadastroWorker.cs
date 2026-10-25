using Microsoft.Extensions.Options;
using Recorrencia.Api.Email;
using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Integracoes;
using Recorrencia.Api.Security;
using Recorrencia.Api.Users;
using static Recorrencia.Api.Audit.Audit;

namespace Recorrencia.Api.Convites;

/// <summary>Processes public registrations outside the browser request and keeps failures visible for review.</summary>
public sealed class SolicitacaoCadastroWorker(IServiceScopeFactory scopes, ILogger<SolicitacaoCadastroWorker> logger)
    : BackgroundService
{
    private sealed class Job
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public Guid IndicadorUserId { get; set; }
        public Guid? ActorId { get; set; }
        public string TenantSlug { get; set; } = "";
        public string TenantName { get; set; } = "";
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
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<Database>();
                var job = await db.AsPlatformAsync(null, async tx =>
                {
                    var candidate = await tx.QuerySingleOrDefaultAsync<Job>(
                        """
                        select s.*, t.slug as tenant_slug, t.name as tenant_name,
                               u.name as indicador_nome,
                               (select admin.id
                                  from users admin
                                  join user_roles ur on ur.user_id = admin.id and ur.tenant_id = admin.tenant_id
                                  join roles r on r.id = ur.role_id and r.tenant_id = admin.tenant_id
                                 where admin.tenant_id = s.tenant_id and admin.status = 'ativo'
                                   and r.source_template_key = 'administrador'
                                 order by admin.created_at limit 1) as actor_id
                          from solicitacoes_cadastro s
                          join tenants t on t.id = s.tenant_id
                          join users u on u.id = s.indicador_user_id and u.tenant_id = s.tenant_id
                         where s.status = 'pendente' and s.processar_automaticamente
                           and s.erro_codigo is null
                           and (s.processando_em is null or s.processando_em < now() - interval '15 minutes')
                         order by s.criado_em
                         for update of s skip locked limit 1
                        """);
                    if (candidate is not null)
                        await tx.ExecuteAsync(
                            "update solicitacoes_cadastro set processando_em = now() where id = @id",
                            new { id = candidate.Id });
                    return candidate;
                }, stoppingToken);

                if (job is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
                    continue;
                }

                try
                {
                    await ProcessarAsync(job, db, scope.ServiceProvider, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // A later worker pass recovers the row if shutdown interrupted read-only work.
                    throw;
                }
                catch (Exception ex)
                {
                    var code = ex is ApiProblem problem ? problem.Code : "internal.error";
                    if (ex is ApiProblem)
                        logger.LogInformation("Cadastro automático {SolicitacaoId} requer revisão: {Code}", job.Id, code);
                    else
                        logger.LogError(ex, "Falha no cadastro automático {SolicitacaoId}", job.Id);
                    await db.AsPlatformAsync(job.TenantId, async tx =>
                    {
                        await tx.ExecuteAsync(
                            """
                            update solicitacoes_cadastro
                               set erro_codigo = @code, processando_em = null
                             where id = @id and status = 'pendente'
                            """, new { job.Id, code });
                        return 0;
                    }, CancellationToken.None);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Erro na fila de cadastros automáticos");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task ProcessarAsync(Job job, Database db, IServiceProvider services, CancellationToken ct)
    {
        if (job.ActorId is not { } actor)
            throw new ApiProblem(StatusCodes.Status400BadRequest, "hinova.nao_configurado");

        var pre = await db.InTenantAsync(job.TenantId, actor, async tx =>
        {
            var emailOcupado = await tx.ExecuteScalarAsync<bool>(
                "select exists(select 1 from users where tenant_id = @tenant and email = @email::citext)",
                new { tenant = job.TenantId, job.Email });
            var indicadorCodigo = await tx.QuerySingleOrDefaultAsync<string>(
                "select codigo_voluntario from hinova_voluntario_mapping where tenant_id = @tenant and user_id = @id",
                new { tenant = job.TenantId, id = job.IndicadorUserId });
            var roles = (await tx.QueryAsync<Guid>(
                "select id from roles where tenant_id = @tenant and source_template_key = 'consultor'",
                new { tenant = job.TenantId })).ToArray();
            return (emailOcupado, indicadorCodigo, roles);
        }, ct);
        if (pre.emailOcupado)
            throw new ApiProblem(StatusCodes.Status409Conflict, "users.email_taken");
        if (pre.indicadorCodigo is null)
            throw new ApiProblem(StatusCodes.Status409Conflict, "solicitacao.indicador_sem_hinova");
        if (pre.roles.Length == 0)
            throw new ApiProblem(StatusCodes.Status409Conflict, "auth.forbidden");

        var hinova = services.GetRequiredService<IHinovaClient>();
        var cipher = services.GetRequiredService<AesGcmCipher>();
        var token = await HinovaAuth.GetTokenUsuarioAsync(db, job.TenantId, actor, cipher, hinova, ct);
        var existente = await hinova.BuscarVoluntarioAsync(token, job.Cpf, ct);
        if (existente is not null && new string(existente.Cpf.Where(char.IsDigit).ToArray()) == job.Cpf)
            throw new ApiProblem(StatusCodes.Status409Conflict, "solicitacao.cpf_ja_cadastrado");
        var indicador = await hinova.BuscarVoluntarioAsync(token, pre.indicadorCodigo, ct)
            ?? throw new ApiProblem(StatusCodes.Status409Conflict, "solicitacao.indicador_sem_hinova");

        // From this external write onward, complete the local record even if the app is stopping.
        var obs = $"Cadastrado via indicação de {job.IndicadorNome} em {DateOnly.FromDateTime(DateTime.UtcNow):dd/MM/yyyy}.";
        var codigo = await hinova.CadastrarVoluntarioAsync(token, new CadastrarVoluntarioRequest(
            job.Nome, job.Cpf, job.Celular, job.Email, job.Logradouro, job.Numero, job.Complemento,
            job.Bairro, job.Cidade, job.Estado, job.Cep, indicador.CooperativaCodigos,
            pre.indicadorCodigo, obs), CancellationToken.None);

        var options = services.GetRequiredService<IOptions<AuthOptions>>().Value;
        var convite = await db.InTenantAsync(job.TenantId, actor, async tx =>
        {
            var status = await tx.QuerySingleAsync<string>(
                "select status from solicitacoes_cadastro where tenant_id = @tenant and id = @id for update",
                new { tenant = job.TenantId, job.Id });
            if (status != "pendente")
                throw new ApiProblem(StatusCodes.Status409Conflict, "solicitacao.ja_resolvida");
            var userId = Guid.CreateVersion7();
            await UserEndpoints.CreateUserWithRolesAsync(tx, job.TenantId, userId, job.Nome,
                job.Email, job.IndicadorUserId, pre.roles);
            await UserEndpoints.LinkHinovaAsync(tx, job.TenantId, userId, actor, codigo, job.Nome, job.Cpf);
            var inviteToken = await UserEndpoints.CreateInviteTokenAsync(tx, job.TenantId, actor,
                userId, job.Nome, job.Email, job.IndicadorUserId, pre.roles, options.InviteTtlSeconds);
            await tx.ExecuteAsync(
                """
                update solicitacoes_cadastro
                   set status = 'aprovado', resolvido_em = now(), resolvido_por = @actor,
                       user_id_resultante = @userId, processando_em = null, erro_codigo = null
                 where id = @id
                """, new { actor, userId, job.Id });
            await WriteAsync(tx, job.TenantId, actor, "solicitacao.aprovar_automaticamente",
                "solicitacoes_cadastro", job.Id, null, new { userId, codigoVoluntario = codigo });
            return inviteToken;
        }, CancellationToken.None);

        var email = services.GetRequiredService<IEmailSender>();
        var links = services.GetRequiredService<LinkBuilder>();
        try
        {
            await email.SendAsync(job.Email, "Convite de acesso",
                EmailTemplates.AccessLink("Convite de acesso",
                    $"Você foi convidado para acessar a plataforma de {job.TenantName}.",
                    "Definir minha senha", links.SetPassword(job.TenantSlug, convite),
                    "O link vale por 72 horas.", links.Logo(job.TenantSlug)), CancellationToken.None);
            await db.AsPlatformAsync(job.TenantId, async tx =>
            {
                await tx.ExecuteAsync(
                    "update solicitacoes_cadastro set convite_enviado_em = now(), convite_erro = null where id = @id",
                    new { job.Id });
                return 0;
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Cadastro concluído, mas o convite não foi enviado para a solicitação {SolicitacaoId}", job.Id);
            await db.AsPlatformAsync(job.TenantId, async tx =>
            {
                await tx.ExecuteAsync(
                    "update solicitacoes_cadastro set convite_erro = 'internal.error' where id = @id",
                    new { job.Id });
                return 0;
            }, CancellationToken.None);
        }
    }
}
