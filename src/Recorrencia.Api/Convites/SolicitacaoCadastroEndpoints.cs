using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Security;
using Recorrencia.Api.Tenancy;

namespace Recorrencia.Api.Convites;

public static class SolicitacaoCadastroEndpoints
{
    public sealed record SolicitarRequest(string? Nome, string? Cpf, string? Celular, string? Email,
        string? Cep, string? Logradouro, string? Numero, string? Complemento, string? Bairro, string? Cidade, string? Estado);

    private sealed class IndicadorRow
    {
        public Guid UserId { get; set; }
        public string Nome { get; set; } = "";
    }

    public static void MapSolicitacaoCadastroEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/convite-links/{token}/solicitacoes", SolicitarAsync);
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
            var nome = (body.Nome ?? "").Trim();
            var cpf = (body.Cpf ?? "").Trim();
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
}
