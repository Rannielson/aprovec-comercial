namespace Recorrencia.Api.Tests;

[Collection(ApiCollection.Name)]
public class CarteiraWhatsappTests(ApiFixture api)
{
    private sealed record ContactResponse(string Telefone);

    [Fact]
    public async Task Consultant_can_open_own_associate_contact_but_not_another_wallet()
    {
        var tenant = await api.SeedAsync();
        var boleto = Guid.NewGuid();
        var associado = "contato-" + Guid.NewGuid().ToString("N");
        await api.SqlAsync(
            """
            insert into boletos (id, tenant_id, participante_id, associado_ref, associado_nome, valor, status, vencimento, pago_em)
            values (@boleto, @tenant, @joao, @associado, 'Associado Teste', 100, 'recebido', '2026-09-10', '2026-09-10')
            """, new { boleto, tenant = tenant.TenantId, joao = tenant.Joao, associado });
        api.Hinova.ContatosAssociados[associado] = "5531998765432";

        var admin = api.Client(tenant.Slug);
        await admin.LoginAsync($"admin@{tenant.Slug}.local", ApiFixture.Password);
        await ApiClient.ExpectAsync(await admin.PutAsync("/integracoes/hinova/credenciais",
            new { usuario = "usuario", senha = "senha", tokenSga = "token" }), HttpStatusCode.NoContent);

        var joao = api.Client(tenant.Slug);
        await joao.LoginAsync($"joao@{tenant.Slug}.local", ApiFixture.Password);
        var contact = await joao.GetJsonAsync<ContactResponse>($"/carteira/{boleto}/whatsapp");
        Assert.Equal("5531998765432", contact.Telefone);

        var maria = api.Client(tenant.Slug);
        await maria.LoginAsync($"maria@{tenant.Slug}.local", ApiFixture.Password);
        var forbidden = await maria.GetAsync($"/carteira/{boleto}/whatsapp");
        Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);
    }
}
