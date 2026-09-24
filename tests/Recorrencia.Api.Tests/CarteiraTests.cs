using Recorrencia.Api.Cli;

namespace Recorrencia.Api.Tests;

[Collection(ApiCollection.Name)]
public class CarteiraTests(ApiFixture api)
{
    public sealed record ItemDto(Guid Id, string AssociadoNome, string? Placa, decimal Valor, string Status,
        DateOnly Vencimento, DateOnly? PagoEm, int? DiasAtraso, decimal Comissao);
    public sealed record CarteiraDto(List<ItemDto> Items, int Page, int PageSize, int TotalCount, int TotalAllCount,
        Dictionary<string, int> StatusCounts, decimal TotalValor, decimal TotalComissao);

    private async Task<ApiClient> LoginAsync(SeededTenant s, string login)
    {
        var client = api.Client(s.Slug);
        await client.LoginAsync($"{login}@{s.Slug}.local", ApiFixture.Password);
        return client;
    }

    [Fact]
    public async Task Consultor_sees_only_their_own_boletos_across_all_statuses()
    {
        var s = await api.SeedAsync();
        var result = await (await LoginAsync(s, "joao")).GetJsonAsync<CarteiraDto>("/carteira");

        // João: 70(J7,recebido)+20(J9,recebido)+90(J8,recebido)=180 recebido, 12 atraso, 4 cancelado, 0 a_vencer.
        Assert.Equal(196, result.TotalAllCount);
        Assert.Equal(180, result.StatusCounts["recebido"]);
        Assert.Equal(12, result.StatusCounts["atraso"]);
        Assert.Equal(4, result.StatusCounts["cancelado"]);
        Assert.Equal(8, result.Items.Count); // primeira página, tamanho padrão 8
    }

    [Fact]
    public async Task Status_filter_narrows_the_totals_but_not_the_tab_counts()
    {
        var s = await api.SeedAsync();
        var result = await (await LoginAsync(s, "joao")).GetJsonAsync<CarteiraDto>("/carteira?status=cancelado");

        Assert.Equal(4, result.TotalCount);
        Assert.All(result.Items, i => Assert.Equal("cancelado", i.Status));
        Assert.All(result.Items, i => Assert.Equal(0, i.Comissao));
        Assert.Equal(196, result.TotalAllCount); // não muda com o filtro de status
    }

    [Fact]
    public async Task Search_matches_by_associado_name()
    {
        var s = await api.SeedAsync();
        var result = await (await LoginAsync(s, "joao")).GetJsonAsync<CarteiraDto>("/carteira?query=Associado J9 5");

        Assert.Equal(1, result.TotalCount);
        Assert.Equal("Associado J9 5", Assert.Single(result.Items).AssociadoNome);
    }

    // Guards against the plain-ILIKE substring search (restored in fix round 1, task 2) ever
    // being silently replaced again by a word-boundary regex, which would break this exact
    // capability: a plate like "TST0001" has no separator between the letter and digit parts,
    // so \y-anchored matching would never find a partial fragment like "0001" inside it.
    [Fact]
    public async Task Search_matches_by_partial_plate()
    {
        var s = await api.SeedAsync();
        var result = await (await LoginAsync(s, "joao")).GetJsonAsync<CarteiraDto>("/carteira?query=0001");

        Assert.True(result.Items.Count > 1);
        Assert.All(result.Items, i => Assert.Contains("0001", i.Placa));
    }

    [Fact]
    public async Task Own_commission_is_calculated_for_a_received_boleto()
    {
        var s = await api.SeedAsync();
        var result = await (await LoginAsync(s, "joao")).GetJsonAsync<CarteiraDto>("/carteira?query=Associado J7 8&status=recebido");

        var item = Assert.Single(result.Items);
        Assert.Equal(200m, item.Valor);
        Assert.Equal(14.00m, item.Comissao); // 200 × 7% (taxa "own" do plano seed)
    }

    // A draft plan (status = 'rascunho') with a later effective_from than the active plan must
    // never leak its rate into the illustrative commission column -- only an activated plan may
    // be used. The draft's effective_from (2026-09-01) sits between the active plan's
    // (2026-08-01) and the target boleto's pago_em month, and its own rate (50%) is wildly
    // different from the active plan's (7%), so any regression here changes the asserted value.
    [Fact]
    public async Task Draft_plan_with_a_later_effective_from_does_not_affect_commission()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsync(s, "admin");
        await ApiClient.ExpectAsync(await admin.PostAsync("/commission-plans", new
        {
            name = "Rascunho futuro",
            effectiveFrom = "2026-09-01",
            rules = new object[] { new { type = "own", rate = 0.50m } },
        }), HttpStatusCode.Created);

        var result = await (await LoginAsync(s, "joao")).GetJsonAsync<CarteiraDto>("/carteira?query=Associado J7 8&status=recebido");

        var item = Assert.Single(result.Items);
        Assert.Equal(200m, item.Valor);
        Assert.Equal(14.00m, item.Comissao); // ainda a taxa do plano ativo (7%), não os 50% do rascunho
    }

    [Fact]
    public async Task Missing_permission_returns_forbidden()
    {
        var s = await api.SeedAsync();
        var roleId = Guid.NewGuid();
        await api.SqlAsync(
            """
            delete from user_roles where tenant_id = @tenant and user_id = @pedro;
            insert into roles (id, tenant_id, name) values (@roleId, @tenant, 'Sem carteira');
            insert into role_permissions (tenant_id, role_id, permission_key, scope) values (@tenant, @roleId, 'usuarios.convidar', null);
            insert into user_roles (tenant_id, user_id, role_id) values (@tenant, @pedro, @roleId);
            """,
            new { roleId, tenant = s.TenantId, pedro = s.Pedro });

        var response = await (await LoginAsync(s, "pedro")).GetAsync("/carteira");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("auth.forbidden", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Coordenador_sees_the_whole_tenant()
    {
        var s = await api.SeedAsync();
        var result = await (await LoginAsync(s, "coordenacao")).GetJsonAsync<CarteiraDto>("/carteira");

        // João(196) + Maria(50+45=95) + Pedro(25+20=45) + Coord(0) = 336
        Assert.Equal(336, result.TotalAllCount);
    }

    [Fact]
    public async Task Pagination_moves_to_the_second_page()
    {
        var s = await api.SeedAsync();
        var page1 = await (await LoginAsync(s, "joao")).GetJsonAsync<CarteiraDto>("/carteira?status=recebido&page=1");
        var page2 = await (await LoginAsync(s, "joao")).GetJsonAsync<CarteiraDto>("/carteira?status=recebido&page=2");

        Assert.Equal(8, page1.Items.Count);
        Assert.Equal(8, page2.Items.Count);
        Assert.Empty(page1.Items.Select(i => i.Id).Intersect(page2.Items.Select(i => i.Id)));
    }
}
