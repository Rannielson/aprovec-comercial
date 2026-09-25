using Recorrencia.Api.Tenancy;

namespace Recorrencia.Api.Tests;

[Collection(ApiCollection.Name)]
public class TenancyTests(ApiFixture api)
{
    private sealed record TenantDto(string Slug, string Name, bool Platform);

    [Fact]
    public async Task Tenant_endpoint_describes_the_subdomain()
    {
        var s = await api.SeedAsync();
        var tenant = await api.Client(s.Slug).GetJsonAsync<TenantDto>("/tenant");
        Assert.Equal(new TenantDto(s.Slug, $"Empresa {s.Slug}", false), tenant);
    }

    [Fact]
    public async Task Platform_host_is_recognized()
    {
        var tenant = await api.Client("admin").GetJsonAsync<TenantDto>("/tenant");
        Assert.True(tenant.Platform);
    }

    [Theory]
    [InlineData("nao-existe")]
    [InlineData("")]
    public async Task Unknown_host_is_not_found(string host)
    {
        var response = await api.Client(host).GetAsync("/tenant");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("tenant.not_found", await ApiClient.CodeAsync(response));
    }

    // Obviously-invalid subdomain shapes (uppercase, underscores) still resolve to a clean
    // 404, exactly like an unknown-but-validly-shaped slug would.
    [Theory]
    [InlineData("Tem-Maiuscula")]
    [InlineData("tem_underscore")]
    public async Task Invalid_slug_shape_is_not_found(string host)
    {
        var response = await api.Client(host).GetAsync("/tenant");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("tenant.not_found", await ApiClient.CodeAsync(response));
    }

    // Unit-level check that an invalid slug shape is rejected before it ever becomes a cache
    // entry (positive or negative) -- unlike a validly-shaped-but-unknown slug, which IS
    // cached (see Suspended_tenant_is_not_found's use of Invalidate below).
    [Fact]
    public async Task Invalid_slug_shape_creates_no_cache_entry()
    {
        var resolver = api.Service<TenantResolver>();
        var cache = api.Service<Microsoft.Extensions.Caching.Memory.IMemoryCache>();
        const string slug = "Invalid_Slug!";

        var info = await resolver.ResolveActiveAsync(slug, CancellationToken.None);

        Assert.Null(info);
        Assert.False(cache.TryGetValue("tenant:" + slug.ToLowerInvariant(), out _));
    }

    [Fact]
    public async Task Suspended_tenant_is_not_found()
    {
        var s = await api.SeedAsync();
        await api.SqlAsync("update tenants set status = 'suspenso' where id = @id", new { id = s.TenantId });
        api.Service<TenantResolver>().Invalidate(s.Slug);

        var response = await api.Client(s.Slug).GetAsync("/tenant");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Dev_seed_reproduces_the_reference_data()
    {
        var s = await api.SeedAsync();

        Assert.Equal(5, await api.SqlScalarAsync<int>("select count(*)::int from users where tenant_id = @id and status = 'ativo'", new { id = s.TenantId }));
        Assert.Equal(336, await api.SqlScalarAsync<int>("select count(*)::int from boletos where tenant_id = @id", new { id = s.TenantId }));
        Assert.Equal(35000m, await api.SqlScalarAsync<decimal>(
            "select sum(valor) from boletos where tenant_id = @id and status = 'recebido' and pago_em >= date '2026-09-01'", new { id = s.TenantId }));
        Assert.Equal(31000m, await api.SqlScalarAsync<decimal>(
            "select sum(valor) from boletos where tenant_id = @id and status = 'recebido' and pago_em < date '2026-09-01'", new { id = s.TenantId }));
    }
}
