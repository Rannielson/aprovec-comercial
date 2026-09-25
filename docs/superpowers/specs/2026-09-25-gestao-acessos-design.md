# Gestão de Acessos — Cadastro Direto, Senha em Tela e Edição — Design

## Contexto

Consultores que já existem como voluntários na Hinova (SGA) precisam entrar na plataforma
agora, num processo conduzido pelo admin, não pelo link de indicação (`docs/superpowers/specs/2026-09-25-convites-indicacao-design.md`),
que é o caminho para quem ainda não tem conta e é indicado por um consultor existente.

Hoje a tela "Participantes" (`web/src/app/administracao/participantes/page.tsx`) já busca
voluntários na Hinova e cria um usuário vinculado a um código de voluntário
(`POST /users`, `Recorrencia.Api/Users/UserEndpoints.cs:58`), mas sempre por e-mail: cria o
usuário com `status = 'convidado'`, gera um token de convite e manda um e-mail com link
"Definir minha senha", válido por 72h. Não existe:

- uma forma de o admin definir a senha diretamente, sem depender do e-mail chegar;
- uma forma de editar nome, e-mail ou o vínculo Hinova de alguém já cadastrado;
- uma forma de trocar a senha de alguém já ativo sem passar pelo fluxo de redefinição por e-mail.

Este spec cobre exatamente essas três lacunas. Não cobre importação em lote (múltiplos
cadastros de uma vez) — ficou combinado que isso vem depois, como uma ação dentro desta mesma
área, uma vez que ela exista.

## Fora de escopo

- Importação em lote de voluntários Hinova.
- Qualquer mudança no fluxo de indicação (`convite_links`, `solicitacoes_cadastro`) — esse
  fluxo continua exclusivamente por e-mail, sem alteração.
- Novos campos cadastrais (celular, endereço) — não são persistidos hoje em `users` e não
  entram neste spec.

## Decisões

- **Permissão**: definir/trocar senha de outra pessoa usa a mesma permissão que já existe para
  convidar/cadastrar (`usuarios.convidar`) — quem já é confiável para dar acesso a alguém
  também é confiável para definir a senha inicial dessa pessoa. Nenhuma permissão nova, nenhuma
  migration de catálogo.
- **`POST /users` ganha um campo opcional, não dois fluxos separados**: em vez de um endpoint
  novo, `InviteUserRequest` ganha `Password` opcional. Presente → cadastro direto (sem e-mail,
  sem token, `status = 'ativo'` desde o insert). Ausente → comportamento de hoje, sem nenhuma
  mudança observável nos testes e chamadores existentes.
- **Um único endpoint de senha serve cadastro e troca posterior**: `PUT /users/{id}/password`
  é o mesmo código chamado depois da criação (quando `Password` vem em `POST /users`) e como
  ação independente sobre um usuário já existente — não há dois lugares com a mesma lógica de
  hash/validação.

## Arquitetura

### Backend — `src/Recorrencia.Api/Users/UserEndpoints.cs`

**`POST /users` (modificado):** `InviteUserRequest` ganha `string? Password`. Em `InviteAsync`,
depois da validação de nome/e-mail e antes do bloco Hinova, quando `body.Password is not null`:

```csharp
string? passwordHash = null;
if (body.Password is not null)
{
    PasswordPolicy.Validate(body.Password);
    passwordHash = hasher.Hash(body.Password);
}
```

(`PasswordHasher hasher` entra como parâmetro do handler, igual já é feito em
`PasswordEndpoints.SetPasswordAsync`.) `CreateUserWithRolesAsync` ganha um parâmetro opcional
`string? passwordHash = null`; quando não nulo, o insert inclui `password_hash` e
`status = 'ativo'` explicitamente (em vez de deixar o default `'convidado'` do schema — ver
`0003_users_hierarchy.sql:7`, que já permite isso: `check (status <> 'ativo' or password_hash is
not null)` é satisfeito porque os dois chegam juntos no mesmo insert). Depois do insert, quando
`passwordHash is not null`: pula `CreateInviteTokenAsync` e o `email.SendAsync` inteiros, grava
auditoria `users.cadastrar_com_senha` (em vez de `users.invite`) e responde `201 Created` com
`{ id }` — igual hoje, só sem o efeito colateral de e-mail. Quando `Password` vem preenchido mas
não passa `PasswordPolicy.Validate`, o erro é `auth.weak_password` (400) — o mesmo código que o
próprio usuário já vê ao definir a própria senha, então a mensagem de erro no
`web/src/lib/errors.ts` já existe e não precisa de entrada nova.

**`PUT /users/{id}/password` (novo):**

```csharp
public sealed record SetUserPasswordRequest(string? Password);

app.MapPut("/users/{id:guid}/password", SetPasswordAsync).RequirePermission("usuarios.convidar");

private static async Task<IResult> SetPasswordAsync(Guid id, SetUserPasswordRequest body, RequestContext request,
    Database db, PasswordHasher hasher, CancellationToken ct)
{
    var tenant = request.RequireTenant();
    var actor = request.RequireUser();
    PasswordPolicy.Validate(body.Password);
    var hash = hasher.Hash(body.Password!);
    await db.InTenantAsync(tenant, actor, async tx =>
    {
        var status = await tx.QuerySingleOrDefaultAsync<string>("select status from users where id = @id for update", new { id })
            ?? throw new ApiProblem(StatusCodes.Status404NotFound, "users.not_found");
        if (status == "desligado")
            throw new ApiProblem(StatusCodes.Status409Conflict, "users.desligado");
        await tx.ExecuteAsync(
            "update users set password_hash = @hash, status = 'ativo' where id = @id",
            new { hash, id });
        await tx.ExecuteAsync("select app.revoke_user_sessions(@id)", new { id });
        await WriteAsync(tx, tenant, actor, "users.set_password_admin", "users", id, null, null);
        return 0;
    }, ct);
    return Results.NoContent();
}
```

Bloqueado para um usuário `desligado` (`users.desligado`, 409) pelo mesmo motivo que
`DeactivateAsync` revoga sessões ao desligar: reativar acesso de alguém desligado não é uma
mera troca de senha, é uma decisão de reengajamento que não faz parte deste spec. `for update`
e a revogação de sessões seguem exatamente o padrão já usado em `DeactivateAsync`
(`UserEndpoints.cs:194`).

**`PUT /users/{id}` (novo) — editar nome/e-mail:**

```csharp
public sealed record UpdateUserRequest(string? Name, string? Email);

app.MapPut("/users/{id:guid}", UpdateAsync).RequirePermission("usuarios.convidar");

private static async Task<IResult> UpdateAsync(Guid id, UpdateUserRequest body, RequestContext request, Database db, CancellationToken ct)
{
    var tenant = request.RequireTenant();
    var actor = request.RequireUser();
    var name = (body.Name ?? "").Trim();
    var address = (body.Email ?? "").Trim().ToLowerInvariant();
    if (name.Length == 0)
        throw new ApiProblem(StatusCodes.Status400BadRequest, "users.name_required");
    if (!MailAddress.TryCreate(address, out var parsedAddress) || parsedAddress.Address != address)
        throw new ApiProblem(StatusCodes.Status400BadRequest, "users.invalid_email");

    await db.InTenantAsync(tenant, actor, async tx =>
    {
        var current = await tx.QuerySingleOrDefaultAsync<UserRow>("select * from users where id = @id for update", new { id })
            ?? throw new ApiProblem(StatusCodes.Status404NotFound, "users.not_found");
        try
        {
            await tx.ExecuteAsync("update users set name = @name, email = @address where id = @id", new { name, address, id });
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new ApiProblem(StatusCodes.Status409Conflict, "users.email_taken");
        }
        await WriteAsync(tx, tenant, actor, "users.update", "users", id,
            new { current.Name, current.Email }, new { name, address });
        return 0;
    }, ct);
    return Results.NoContent();
}
```

Reaproveita exatamente as mesmas validações e códigos de erro de `InviteAsync`
(`users.name_required`, `users.invalid_email`, `users.email_taken`), então nenhuma entrada nova
em `errors.ts`.

### Backend — `src/Recorrencia.Api/Integracoes/HinovaEndpoints.cs`

**`PUT /integracoes/hinova/mapeamentos/{userId}` (novo):**

```csharp
app.MapPut("/integracoes/hinova/mapeamentos/{userId:guid}", AtualizarMapeamentoAsync).RequirePermission("integracoes.gerenciar");

public sealed record AtualizarMapeamentoRequest(string? CodigoVoluntario, string? NomeHinova, string? CpfHinova);

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
        // Reaproveita a MapeamentoRow privada que já existe neste arquivo (ListarMapeamentosAsync) --
        // mesmas colunas que este update precisa para o "antes" da auditoria.
        var current = await tx.QuerySingleOrDefaultAsync<MapeamentoRow>(
            "select * from hinova_voluntario_mapping where tenant_id = @tenant and user_id = @userId for update",
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
```

`request.invalid` e `hinova.vinculo_duplicado` já têm entrada em `errors.ts`. `hinova.vinculo_not_found`
já é lançado hoje por `RemoverMapeamentoAsync` (`HinovaEndpoints.cs:187`), mas não tem entrada em
`errors.ts` ainda — esta é a primeira tela que precisa mostrá-lo, então a entrada
(`'hinova.vinculo_not_found': 'Este usuário não tem vínculo com a Hinova.'`) entra em `errors.ts`
como parte desta tarefa.

### Frontend

**`web/src/app/administracao/participante-search.tsx`:** o formulário de confirmação (depois
de escolher um voluntário) ganha um campo `password` obrigatório, com o mesmo texto de ajuda de
tamanho mínimo que a tela de definir senha já usa. `criarParticipante` (`actions.ts`) passa
`password` no corpo de `POST /users`.

**`web/src/app/administracao/participantes/page.tsx`:** a tabela ganha uma coluna "Ações" com
um link "Editar" por linha, visível só quando `has('usuarios.convidar')` — mesmo padrão de
`canAdd` que a página já usa.

**Nova rota `web/src/app/administracao/participantes/[id]/editar/page.tsx`:** busca o usuário
(via `GET /users`, filtrando pelo id — não existe `GET /users/{id}` e não é necessário criar um
só para isto) e, só quando o próprio ator tem `integracoes.gerenciar` (mesmo `has(...)` já usado
em `participantes/page.tsx` para `canSeeRoles`/`canAdd` — quem não tem essa permissão nem chama
`GET /integracoes/hinova/mapeamentos`, que devolveria 403), o vínculo Hinova. Formulário com:
nome, e-mail, código/nome/CPF de voluntário (só quando há vínculo E o ator pode ver/editar
vínculos — sem uma das duas condições, esses três campos não aparecem; editar vínculo Hinova não
é uma forma de criar um vínculo novo neste spec) e "Nova senha" (opcional, em branco por padrão,
com a nota "deixe em branco para não alterar"). Ao submeter: sempre chama `PUT /users/{id}`; chama
`PUT /integracoes/hinova/mapeamentos/{userId}` só se os campos Hinova estavam visíveis; chama
`PUT /users/{id}/password` só se o campo de senha foi preenchido. As três chamadas são
sequenciais (não há necessidade de atomicidade entre elas — cada uma já é atômica no banco, e um
erro parcial deixa o restante para o admin corrigir e tentar de novo, igual qualquer formulário
multi-campo desta tela).

## Testes

- `POST /users` com `password` preenchido: usuário nasce com `status = 'ativo'`, login
  funciona imediatamente com a senha enviada, nenhum e-mail é registrado em
  `tmp/emails`/`IEmailSender` fake.
- `POST /users` com `password` fraca (< 10 caracteres): `400 auth.weak_password`, nenhum
  usuário é criado.
- `POST /users` sem `password`: nenhuma mudança de comportamento — os testes existentes
  (`UserEndpointsTests` ou equivalente) continuam passando sem alteração.
- `PUT /users/{id}/password`: troca a senha de um usuário `ativo`, login com a senha antiga
  passa a falhar, login com a nova funciona; sobre um usuário `convidado`, o status vira
  `ativo`; sobre um usuário `desligado`, `409 users.desligado`.
- `PUT /users/{id}`: nome/e-mail atualizados; e-mail duplicado retorna `409 users.email_taken`
  sem alterar a linha.
- `PUT /integracoes/hinova/mapeamentos/{userId}`: atualiza os três campos; `404` quando o
  usuário não tem vínculo; `409 hinova.vinculo_duplicado` quando o novo código já pertence a
  outro usuário.
- Permissão: todos os três novos endpoints retornam `403` para um ator sem `usuarios.convidar`
  (os dois primeiros) / `integracoes.gerenciar` (o terceiro).
