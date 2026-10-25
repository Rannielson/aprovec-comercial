using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Authorization;
using Recorrencia.Api.Tenancy;
using static Recorrencia.Api.Audit.Audit;

namespace Recorrencia.Api.Commissions;

public static class CommissionCutoffEndpoints
{
    public sealed record CutoffResponse(Guid UserId, DateOnly? CutoffDate);
    public sealed record SaveCutoffRequest(DateOnly? CutoffDate);

    public static void MapCommissionCutoffEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/commission-contract-cutoffs/{userId:guid}", GetAsync)
            .RequirePermission("regras_comissao.visualizar");
        app.MapPut("/commission-contract-cutoffs/{userId:guid}", PutAsync)
            .RequirePermission("regras_comissao.editar");
    }

    private static async Task<IResult> GetAsync(Guid userId, RequestContext request, Database db, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        var cutoff = await db.InTenantAsync(tenant, actor, async tx =>
        {
            var exists = await tx.ExecuteScalarAsync<bool>(
                "select exists(select 1 from users where tenant_id = @tenant and id = @userId)",
                new { tenant, userId });
            if (!exists) throw new ApiProblem(StatusCodes.Status404NotFound, "users.not_found");
            return await tx.ExecuteScalarAsync<DateOnly?>(
                "select cutoff_date from commission_contract_cutoffs where tenant_id = @tenant and user_id = @userId",
                new { tenant, userId });
        }, ct);
        return Results.Ok(new CutoffResponse(userId, cutoff));
    }

    private static async Task<IResult> PutAsync(Guid userId, SaveCutoffRequest body, RequestContext request, Database db,
        CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        await db.InTenantAsync(tenant, actor, async tx =>
        {
            var exists = await tx.ExecuteScalarAsync<bool>(
                "select exists(select 1 from users where tenant_id = @tenant and id = @userId)",
                new { tenant, userId });
            if (!exists) throw new ApiProblem(StatusCodes.Status404NotFound, "users.not_found");

            var before = await tx.ExecuteScalarAsync<DateOnly?>(
                "select cutoff_date from commission_contract_cutoffs where tenant_id = @tenant and user_id = @userId",
                new { tenant, userId });
            if (body.CutoffDate is null)
            {
                await tx.ExecuteAsync(
                    "delete from commission_contract_cutoffs where tenant_id = @tenant and user_id = @userId",
                    new { tenant, userId });
            }
            else
            {
                await tx.ExecuteAsync(
                    """
                    insert into commission_contract_cutoffs (tenant_id, user_id, cutoff_date)
                    values (@tenant, @userId, @cutoffDate)
                    on conflict (tenant_id, user_id) do update set cutoff_date = excluded.cutoff_date
                    """,
                    new { tenant, userId, cutoffDate = body.CutoffDate });
            }
            await WriteAsync(tx, tenant, actor, "comissao.corte_contrato.salvar", "commission_contract_cutoffs", userId,
                new { cutoffDate = before }, new { body.CutoffDate });
            return 0;
        }, ct);
        return Results.Ok(new CutoffResponse(userId, body.CutoffDate));
    }
}
