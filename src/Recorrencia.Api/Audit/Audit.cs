using System.Text.Json;
using Recorrencia.Api.Infrastructure;

namespace Recorrencia.Api.Audit;

public static class Audit
{
    public static Task WriteAsync(Tx tx, Guid tenantId, Guid userId, string action, string entity, Guid? entityId, object? before, object? after) =>
        tx.ExecuteAsync(
            """
            insert into audit_log (tenant_id, user_id, action, entity, entity_id, before, after)
            values (@tenantId, @userId, @action, @entity, @entityId, @before::jsonb, @after::jsonb)
            """,
            new { tenantId, userId, action, entity, entityId, before = Serialize(before), after = Serialize(after) });

    private static string? Serialize(object? value) =>
        value is null ? null : JsonSerializer.Serialize(value, JsonSerializerOptions.Web);
}
