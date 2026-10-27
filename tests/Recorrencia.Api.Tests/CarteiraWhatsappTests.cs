using Recorrencia.Api.Integracoes;

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
            insert into boletos (id, tenant_id, participante_id, associado_ref, associado_nome, valor, status, vencimento, pago_em, hinova_nosso_numero)
            values (@boleto, @tenant, @joao, @associado, 'Associado Teste', 100, 'atraso', '2026-09-10', null, @associado)
            """, new { boleto, tenant = tenant.TenantId, joao = tenant.Joao, associado });
        api.Hinova.BoletosPorNumero[associado] = new HinovaBoleto(associado, 100, 0, associado, "Associado Teste",
            new DateOnly(2026, 9, 10), null, [], SituacaoBoleto: "ABERTO");
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
    [Theory]
    [InlineData("BAIXADO", true, "recebido")]
    [InlineData("EXCLUIDO", false, "cancelado")]
    [InlineData("CANCELADO", false, "cancelado")]
    [InlineData("DESCONHECIDO", false, "atraso")]
    [InlineData("BAIXADO", false, "cancelado")]
    public async Task Live_verification_blocks_collection_and_reconciles_settled_boleto(string situacao, bool paid, string expectedStatus)
    {
        var s = await api.SeedAsync();
        var id = Guid.NewGuid();
        var numero = id.ToString();
        await api.SqlAsync("""
            insert into boletos(id,tenant_id,participante_id,associado_ref,associado_nome,valor,status,vencimento,hinova_nosso_numero)
            values(@id,@tenant,@user,'999','Teste',100,'atraso','2026-09-10',@numero)
            """, new { id, tenant = s.TenantId, user = s.Joao, numero });
        var admin = api.Client(s.Slug);
        await admin.LoginAsync($"admin@{s.Slug}.local", ApiFixture.Password);
        await ApiClient.ExpectAsync(await admin.PutAsync("/integracoes/hinova/credenciais",
            new { usuario = "usuario", senha = "senha", tokenSga = "token" }), HttpStatusCode.NoContent);
        api.Hinova.BoletosPorNumero[numero] = new HinovaBoleto(numero,100,paid ? 100 : 0,"999","Teste",
            new DateOnly(2026,9,10),paid ? new DateOnly(2026,10,27) : null,[],SituacaoBoleto:situacao);
        var user = api.Client(s.Slug);
        await user.LoginAsync($"joao@{s.Slug}.local", ApiFixture.Password);
        var response = await user.GetAsync($"/carteira/{id}/whatsapp");
        Assert.Equal(HttpStatusCode.Conflict,response.StatusCode);
        Assert.Equal(expectedStatus,await api.SqlScalarAsync<string>("select status from boletos where id=@id",new {id}));
        if (paid)
        {
            var wallet = await user.GetJsonAsync<WalletDto>("/carteira?status=atraso");
            Assert.DoesNotContain(wallet.Items, x => x.Id == id);
        }
    }
    private sealed record WalletDto(List<WalletItem> Items);
    private sealed record WalletItem(Guid Id);
}
