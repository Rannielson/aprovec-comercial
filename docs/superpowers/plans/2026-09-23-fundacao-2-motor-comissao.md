# Fundação 2 — Motor de comissão: plano de implementação

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implementar o cálculo de comissões como uma biblioteca C# pura (sem banco), com regras `own`, `upline` (N níveis) e `global`, arredondamento bancário por lançamento e agregação por beneficiário.

**Architecture:** Projeto `Recorrencia.Domain` sem dependências externas. Recebe o plano vigente, os boletos pagos, os caminhos da hierarquia e os membros dos grupos, e devolve lançamentos por beneficiário, regra e boleto. A API (plano 3) carrega as entradas pelas funções do banco (plano 1, Task 8) e chama este motor.

**Tech Stack:** .NET 10, xUnit v2.

**Spec:** `docs/superpowers/specs/2026-09-23-fundacao-saas-design.md` (seção 7)

**Ordem dos planos:** plano 2 de 4. Independente do plano 1; os dois podem rodar em paralelo. Se o plano 1 ainda não criou a solução, a Task 1 cria.

## Global Constraints

- Target framework: `net10.0`, com as configurações de `Directory.Build.props` (nullable, warnings como erro). Se o arquivo não existir, crie-o conforme a Task 1 do plano 1.
- Valores e taxas sempre `decimal`. Nunca `double`/`float`.
- Cada lançamento é arredondado para 2 casas com `MidpointRounding.ToEven`. Totais são a soma dos lançamentos já arredondados.
- Um nível sem beneficiário não gera valor, e o valor não passa para outro nível.
- Competência é sempre o dia 1 do mês (`DateOnly`).
- O projeto `Recorrencia.Domain` não referencia pacotes NuGet.
- Commits: mensagens convencionais (`feat(domain): ...`), terminando com a linha de atribuição indicada pelo ambiente.

## Mapa de arquivos

```
src/Recorrencia.Domain/
  Recorrencia.Domain.csproj
  Commissions/CommissionModel.cs     tipos de entrada e saída
  Commissions/PlanSelector.cs        plano vigente por competência
  Commissions/CommissionEngine.cs    cálculo
  Commissions/CommissionSummary.cs   agregação por beneficiário
tests/Recorrencia.Domain.Tests/
  Recorrencia.Domain.Tests.csproj
  Builders.cs                        helpers de montagem de cenários
  PlanSelectorTests.cs
  CommissionEngineTests.cs
  CommissionSummaryTests.cs
```

---

### Task 1: Projeto de domínio, tipos e seleção do plano vigente

**Files:**
- Create: `src/Recorrencia.Domain/Recorrencia.Domain.csproj`, `src/Recorrencia.Domain/Commissions/CommissionModel.cs`, `src/Recorrencia.Domain/Commissions/PlanSelector.cs`
- Create: `tests/Recorrencia.Domain.Tests/Recorrencia.Domain.Tests.csproj`, `tests/Recorrencia.Domain.Tests/PlanSelectorTests.cs`

**Interfaces:**
- Produces (namespace `Recorrencia.Domain.Commissions`):
  - `enum RuleType { Own, Upline, Global }`
  - `sealed record CommissionRule(Guid Id, RuleType Type, decimal Rate, int? Level = null, Guid? GroupId = null)`
  - `sealed record CommissionPlan(Guid Id, DateOnly EffectiveFrom, IReadOnlyList<CommissionRule> Rules)`
  - `sealed record PaidBoleto(Guid Id, Guid OwnerId, decimal Amount)`
  - `sealed record HierarchyPath(Guid AncestorId, Guid DescendantId, int Depth)`
  - `sealed record GroupMembership(Guid GroupId, Guid UserId)`
  - `sealed record CommissionEntry(Guid BeneficiaryId, Guid BoletoId, Guid OwnerId, Guid RuleId, RuleType RuleType, int? Level, Guid? GroupId, decimal Rate, decimal Base, decimal Amount)`
  - `static class PlanSelector { static CommissionPlan? ForCompetencia(IEnumerable<CommissionPlan> activePlans, DateOnly competencia) }` — lança `ArgumentException` se `competencia.Day != 1`.

- [ ] **Step 1: Criar os projetos**

```bash
test -f Recorrencia.slnx || dotnet new sln -n Recorrencia --format slnx
dotnet new classlib -n Recorrencia.Domain -o src/Recorrencia.Domain
dotnet new classlib -n Recorrencia.Domain.Tests -o tests/Recorrencia.Domain.Tests
rm src/Recorrencia.Domain/Class1.cs tests/Recorrencia.Domain.Tests/Class1.cs
dotnet sln Recorrencia.slnx add src/Recorrencia.Domain tests/Recorrencia.Domain.Tests
dotnet add tests/Recorrencia.Domain.Tests package Microsoft.NET.Test.Sdk
dotnet add tests/Recorrencia.Domain.Tests package xunit
dotnet add tests/Recorrencia.Domain.Tests package xunit.runner.visualstudio
dotnet add tests/Recorrencia.Domain.Tests reference src/Recorrencia.Domain
```

Em `tests/Recorrencia.Domain.Tests/Recorrencia.Domain.Tests.csproj`, acrescente:

```xml
  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <Using Include="Xunit" />
    <Using Include="Recorrencia.Domain.Commissions" />
  </ItemGroup>
```

- [ ] **Step 2: Escrever os testes (falhando)**

`tests/Recorrencia.Domain.Tests/PlanSelectorTests.cs`:

```csharp
namespace Recorrencia.Domain.Tests;

public class PlanSelectorTests
{
    private static CommissionPlan Plan(int year, int month) =>
        new(Guid.NewGuid(), new DateOnly(year, month, 1), [new CommissionRule(Guid.NewGuid(), RuleType.Own, 0.07m)]);

    [Fact]
    public void Picks_the_latest_plan_that_started_on_or_before_the_competencia()
    {
        var august = Plan(2026, 8);
        var october = Plan(2026, 10);
        var plans = new[] { october, august };

        Assert.Same(august, PlanSelector.ForCompetencia(plans, new DateOnly(2026, 8, 1)));
        Assert.Same(august, PlanSelector.ForCompetencia(plans, new DateOnly(2026, 9, 1)));
        Assert.Same(october, PlanSelector.ForCompetencia(plans, new DateOnly(2026, 10, 1)));
        Assert.Same(october, PlanSelector.ForCompetencia(plans, new DateOnly(2027, 1, 1)));
    }

    [Fact]
    public void Returns_null_before_the_first_plan()
    {
        Assert.Null(PlanSelector.ForCompetencia([Plan(2026, 8)], new DateOnly(2026, 7, 1)));
    }

    [Fact]
    public void Rejects_a_competencia_that_is_not_the_first_day()
    {
        Assert.Throws<ArgumentException>(() => PlanSelector.ForCompetencia([Plan(2026, 8)], new DateOnly(2026, 9, 15)));
    }
}
```

- [ ] **Step 3: Rodar e confirmar que falha**

Run: `dotnet test tests/Recorrencia.Domain.Tests`
Expected: FAIL de compilação (`CommissionPlan`, `PlanSelector` não existem).

- [ ] **Step 4: Implementar**

`src/Recorrencia.Domain/Commissions/CommissionModel.cs`:

```csharp
namespace Recorrencia.Domain.Commissions;

public enum RuleType
{
    Own,
    Upline,
    Global,
}

public sealed record CommissionRule(Guid Id, RuleType Type, decimal Rate, int? Level = null, Guid? GroupId = null);

public sealed record CommissionPlan(Guid Id, DateOnly EffectiveFrom, IReadOnlyList<CommissionRule> Rules);

public sealed record PaidBoleto(Guid Id, Guid OwnerId, decimal Amount);

public sealed record HierarchyPath(Guid AncestorId, Guid DescendantId, int Depth);

public sealed record GroupMembership(Guid GroupId, Guid UserId);

public sealed record CommissionEntry(
    Guid BeneficiaryId,
    Guid BoletoId,
    Guid OwnerId,
    Guid RuleId,
    RuleType RuleType,
    int? Level,
    Guid? GroupId,
    decimal Rate,
    decimal Base,
    decimal Amount);
```

`src/Recorrencia.Domain/Commissions/PlanSelector.cs`:

```csharp
namespace Recorrencia.Domain.Commissions;

public static class PlanSelector
{
    public static CommissionPlan? ForCompetencia(IEnumerable<CommissionPlan> activePlans, DateOnly competencia)
    {
        if (competencia.Day != 1)
            throw new ArgumentException("A competência deve ser o primeiro dia do mês.", nameof(competencia));

        return activePlans
            .Where(plan => plan.EffectiveFrom <= competencia)
            .MaxBy(plan => plan.EffectiveFrom);
    }
}
```

- [ ] **Step 5: Rodar os testes e confirmar que passam**

Run: `dotnet test tests/Recorrencia.Domain.Tests`
Expected: PASS (3 testes).

- [ ] **Step 6: Commit**

```bash
git add Recorrencia.slnx src/Recorrencia.Domain tests/Recorrencia.Domain.Tests
git commit -m "feat(domain): add commission model types and plan selection"
```

---

### Task 2: Cálculo de comissões

**Files:**
- Create: `src/Recorrencia.Domain/Commissions/CommissionEngine.cs`
- Create: `tests/Recorrencia.Domain.Tests/Builders.cs`, `tests/Recorrencia.Domain.Tests/CommissionEngineTests.cs`

**Interfaces:**
- Consumes: tipos da Task 1.
- Produces: `static class CommissionEngine { static IReadOnlyList<CommissionEntry> Calculate(CommissionPlan plan, IEnumerable<PaidBoleto> boletos, IEnumerable<HierarchyPath> paths, IEnumerable<GroupMembership> memberships) }`.
  - `paths` pode conter linhas com `Depth == 0`; elas são ignoradas.
  - Lança `ArgumentException` se uma regra for inválida (`Upline` sem `Level >= 1`, `Global` sem `GroupId`, taxa fora de `(0, 1]`) ou se um boleto tiver valor `<= 0`.
  - Lança `InvalidOperationException` se `paths` trouxer dois ancestrais diferentes na mesma profundidade para o mesmo descendente.
  - Ordem de saída: para cada boleto, na ordem recebida, as regras na ordem do plano; em `Global`, os membros na ordem recebida.

- [ ] **Step 1: Escrever os helpers e os testes (falhando)**

`tests/Recorrencia.Domain.Tests/Builders.cs`:

```csharp
namespace Recorrencia.Domain.Tests;

internal static class Builders
{
    public static readonly Guid Joao = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    public static readonly Guid Maria = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    public static readonly Guid Pedro = Guid.Parse("00000000-0000-0000-0000-00000000000c");
    public static readonly Guid Coordenador = Guid.Parse("00000000-0000-0000-0000-00000000000d");
    public static readonly Guid Coordenacao = Guid.Parse("00000000-0000-0000-0000-0000000000f1");

    public static CommissionPlan AprovecPlan() => new(
        Guid.NewGuid(),
        new DateOnly(2026, 8, 1),
        [
            new CommissionRule(Guid.NewGuid(), RuleType.Own, 0.07m),
            new CommissionRule(Guid.NewGuid(), RuleType.Upline, 0.02m, Level: 1),
            new CommissionRule(Guid.NewGuid(), RuleType.Global, 0.01m, GroupId: Coordenacao),
        ]);

    public static IEnumerable<PaidBoleto> Boletos(Guid owner, int count, decimal amount) =>
        Enumerable.Range(0, count).Select(_ => new PaidBoleto(Guid.NewGuid(), owner, amount));

    public static List<HierarchyPath> Chain(params Guid[] topToBottom)
    {
        var paths = new List<HierarchyPath>();
        for (var i = 0; i < topToBottom.Length; i++)
            for (var j = i; j < topToBottom.Length; j++)
                paths.Add(new HierarchyPath(topToBottom[i], topToBottom[j], j - i));
        return paths;
    }

    public static Dictionary<Guid, decimal> TotalsByBeneficiary(IEnumerable<CommissionEntry> entries) =>
        entries.GroupBy(e => e.BeneficiaryId).ToDictionary(g => g.Key, g => g.Sum(e => e.Amount));
}
```

`tests/Recorrencia.Domain.Tests/CommissionEngineTests.cs`:

```csharp
using static Recorrencia.Domain.Tests.Builders;

namespace Recorrencia.Domain.Tests;

public class CommissionEngineTests
{
    private static readonly GroupMembership[] CoordenadorNaCoordenacao = [new(Coordenacao, Coordenador)];

    [Fact]
    public void Reproduces_the_consolidated_example_from_the_pdf()
    {
        var boletos = Boletos(Joao, 100, 200m)
            .Concat(Boletos(Maria, 50, 200m))
            .Concat(Boletos(Pedro, 25, 200m))
            .ToList();

        var entries = CommissionEngine.Calculate(AprovecPlan(), boletos, Chain(Joao, Maria, Pedro), CoordenadorNaCoordenacao);
        var totals = TotalsByBeneficiary(entries);

        Assert.Equal(1600.00m, totals[Joao]);
        Assert.Equal(800.00m, totals[Maria]);
        Assert.Equal(350.00m, totals[Pedro]);
        Assert.Equal(350.00m, totals[Coordenador]);
        Assert.Equal(3100.00m, entries.Sum(e => e.Amount));
    }

    [Fact]
    public void Reproduces_the_august_history()
    {
        var boletos = Boletos(Joao, 90, 200m)
            .Concat(Boletos(Maria, 45, 200m))
            .Concat(Boletos(Pedro, 20, 200m))
            .ToList();

        var totals = TotalsByBeneficiary(
            CommissionEngine.Calculate(AprovecPlan(), boletos, Chain(Joao, Maria, Pedro), CoordenadorNaCoordenacao));

        Assert.Equal(1440.00m, totals[Joao]);
        Assert.Equal(710.00m, totals[Maria]);
        Assert.Equal(280.00m, totals[Pedro]);
        Assert.Equal(310.00m, totals[Coordenador]);
    }

    [Fact]
    public void Upline_commission_is_limited_to_one_level_in_the_aprovec_plan()
    {
        var entries = CommissionEngine.Calculate(
            AprovecPlan(), Boletos(Pedro, 1, 1000m).ToList(), Chain(Joao, Maria, Pedro), []);

        Assert.DoesNotContain(entries, e => e.BeneficiaryId == Joao);
        var maria = Assert.Single(entries, e => e.BeneficiaryId == Maria);
        Assert.Equal((RuleType.Upline, 1, 20.00m), (maria.RuleType, maria.Level!.Value, maria.Amount));
    }

    [Fact]
    public void Supports_plans_with_more_levels()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var d = Guid.NewGuid();
        var plan = new CommissionPlan(Guid.NewGuid(), new DateOnly(2026, 8, 1),
        [
            new CommissionRule(Guid.NewGuid(), RuleType.Own, 0.05m),
            new CommissionRule(Guid.NewGuid(), RuleType.Upline, 0.03m, Level: 1),
            new CommissionRule(Guid.NewGuid(), RuleType.Upline, 0.02m, Level: 2),
            new CommissionRule(Guid.NewGuid(), RuleType.Upline, 0.01m, Level: 3),
        ]);

        var totals = TotalsByBeneficiary(CommissionEngine.Calculate(plan, Boletos(d, 1, 1000m).ToList(), Chain(a, b, c, d), []));

        Assert.Equal(50.00m, totals[d]);
        Assert.Equal(30.00m, totals[c]);
        Assert.Equal(20.00m, totals[b]);
        Assert.Equal(10.00m, totals[a]);
    }

    [Fact]
    public void Missing_level_generates_nothing_and_does_not_roll_up()
    {
        var plan = new CommissionPlan(Guid.NewGuid(), new DateOnly(2026, 8, 1),
        [
            new CommissionRule(Guid.NewGuid(), RuleType.Own, 0.05m),
            new CommissionRule(Guid.NewGuid(), RuleType.Upline, 0.03m, Level: 1),
            new CommissionRule(Guid.NewGuid(), RuleType.Upline, 0.02m, Level: 2),
            new CommissionRule(Guid.NewGuid(), RuleType.Upline, 0.01m, Level: 3),
        ]);

        var entries = CommissionEngine.Calculate(plan, Boletos(Maria, 1, 1000m).ToList(), Chain(Joao, Maria), []);

        Assert.Equal(2, entries.Count);
        Assert.Equal(80.00m, entries.Sum(e => e.Amount));
    }

    [Fact]
    public void Every_member_of_a_global_group_receives_the_full_rate()
    {
        var other = Guid.NewGuid();
        var entries = CommissionEngine.Calculate(
            AprovecPlan(),
            Boletos(Joao, 1, 1000m).ToList(),
            Chain(Joao),
            [new GroupMembership(Coordenacao, Coordenador), new GroupMembership(Coordenacao, other)]);

        var totals = TotalsByBeneficiary(entries);
        Assert.Equal(10.00m, totals[Coordenador]);
        Assert.Equal(10.00m, totals[other]);
    }

    [Fact]
    public void Plan_without_own_rule_pays_only_the_upline()
    {
        var plan = new CommissionPlan(Guid.NewGuid(), new DateOnly(2026, 8, 1),
            [new CommissionRule(Guid.NewGuid(), RuleType.Upline, 0.02m, Level: 1)]);

        var entry = Assert.Single(CommissionEngine.Calculate(plan, Boletos(Maria, 1, 500m).ToList(), Chain(Joao, Maria), []));
        Assert.Equal((Joao, 10.00m), (entry.BeneficiaryId, entry.Amount));
    }

    [Fact]
    public void Entry_carries_the_rule_the_base_and_the_origin()
    {
        var plan = AprovecPlan();
        var boleto = new PaidBoleto(Guid.NewGuid(), Maria, 300m);

        var entries = CommissionEngine.Calculate(plan, [boleto], Chain(Joao, Maria), []);
        var upline = Assert.Single(entries, e => e.RuleType == RuleType.Upline);

        Assert.Equal(
            new CommissionEntry(Joao, boleto.Id, Maria, plan.Rules[1].Id, RuleType.Upline, 1, null, 0.02m, 300m, 6.00m),
            upline);
    }

    [Theory]
    [InlineData("2.50", "0.05", "0.12")]
    [InlineData("12.50", "0.07", "0.88")]
    [InlineData("0.30", "0.05", "0.02")]
    [InlineData("0.10", "0.05", "0.00")]
    public void Each_entry_is_rounded_half_to_even(string amount, string rate, string expected)
    {
        var plan = new CommissionPlan(Guid.NewGuid(), new DateOnly(2026, 8, 1),
            [new CommissionRule(Guid.NewGuid(), RuleType.Own, decimal.Parse(rate, System.Globalization.CultureInfo.InvariantCulture))]);
        var boleto = new PaidBoleto(Guid.NewGuid(), Joao, decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture));

        var entry = Assert.Single(CommissionEngine.Calculate(plan, [boleto], [], []));
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), entry.Amount);
    }

    [Fact]
    public void Invalid_rules_are_rejected()
    {
        var boletos = Boletos(Joao, 1, 100m).ToList();
        CommissionPlan With(CommissionRule rule) => new(Guid.NewGuid(), new DateOnly(2026, 8, 1), [rule]);

        Assert.Throws<ArgumentException>(() => CommissionEngine.Calculate(With(new(Guid.NewGuid(), RuleType.Upline, 0.02m)), boletos, [], []));
        Assert.Throws<ArgumentException>(() => CommissionEngine.Calculate(With(new(Guid.NewGuid(), RuleType.Global, 0.01m)), boletos, [], []));
        Assert.Throws<ArgumentException>(() => CommissionEngine.Calculate(With(new(Guid.NewGuid(), RuleType.Own, 0m)), boletos, [], []));
        Assert.Throws<ArgumentException>(() => CommissionEngine.Calculate(With(new(Guid.NewGuid(), RuleType.Own, 1.5m)), boletos, [], []));
    }

    [Fact]
    public void Non_positive_boleto_is_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            CommissionEngine.Calculate(AprovecPlan(), [new PaidBoleto(Guid.NewGuid(), Joao, 0m)], [], []));
    }

    [Fact]
    public void Inconsistent_hierarchy_is_rejected()
    {
        var paths = new[] { new HierarchyPath(Joao, Pedro, 1), new HierarchyPath(Maria, Pedro, 1) };
        Assert.Throws<InvalidOperationException>(() =>
            CommissionEngine.Calculate(AprovecPlan(), Boletos(Pedro, 1, 100m).ToList(), paths, []));
    }
}
```

Conferência do arredondamento: 2,50 × 0,05 = 0,125 → 0,12 (par); 12,50 × 0,07 = 0,875 → 0,88 (par); 0,30 × 0,05 = 0,015 → 0,02 (par); 0,10 × 0,05 = 0,005 → 0,00 (par).

- [ ] **Step 2: Rodar e confirmar que falha**

Run: `dotnet test tests/Recorrencia.Domain.Tests --filter "FullyQualifiedName~CommissionEngineTests"`
Expected: FAIL de compilação (`CommissionEngine` não existe).

- [ ] **Step 3: Implementar**

`src/Recorrencia.Domain/Commissions/CommissionEngine.cs`:

```csharp
namespace Recorrencia.Domain.Commissions;

public static class CommissionEngine
{
    public static IReadOnlyList<CommissionEntry> Calculate(
        CommissionPlan plan,
        IEnumerable<PaidBoleto> boletos,
        IEnumerable<HierarchyPath> paths,
        IEnumerable<GroupMembership> memberships)
    {
        foreach (var rule in plan.Rules)
            Validate(rule);

        var ancestorAt = BuildAncestorIndex(paths);
        var membersByGroup = memberships
            .GroupBy(m => m.GroupId)
            .ToDictionary(g => g.Key, g => g.Select(m => m.UserId).Distinct().ToList());

        var entries = new List<CommissionEntry>();
        foreach (var boleto in boletos)
        {
            if (boleto.Amount <= 0)
                throw new ArgumentException($"O boleto {boleto.Id} tem valor inválido.", nameof(boletos));

            foreach (var rule in plan.Rules)
            {
                switch (rule.Type)
                {
                    case RuleType.Own:
                        entries.Add(Entry(boleto.OwnerId, boleto, rule));
                        break;
                    case RuleType.Upline:
                        if (ancestorAt.TryGetValue((boleto.OwnerId, rule.Level!.Value), out var ancestor))
                            entries.Add(Entry(ancestor, boleto, rule));
                        break;
                    case RuleType.Global:
                        if (membersByGroup.TryGetValue(rule.GroupId!.Value, out var members))
                            entries.AddRange(members.Select(member => Entry(member, boleto, rule)));
                        break;
                }
            }
        }
        return entries;
    }

    private static void Validate(CommissionRule rule)
    {
        if (rule.Rate <= 0 || rule.Rate > 1)
            throw new ArgumentException($"A regra {rule.Id} tem taxa fora do intervalo (0, 1].");
        if (rule.Type == RuleType.Upline && rule.Level is not >= 1)
            throw new ArgumentException($"A regra {rule.Id} do tipo upline precisa de nível >= 1.");
        if (rule.Type == RuleType.Global && rule.GroupId is null)
            throw new ArgumentException($"A regra {rule.Id} do tipo global precisa de grupo.");
    }

    private static Dictionary<(Guid Descendant, int Depth), Guid> BuildAncestorIndex(IEnumerable<HierarchyPath> paths)
    {
        var index = new Dictionary<(Guid, int), Guid>();
        foreach (var path in paths.Where(p => p.Depth > 0))
        {
            var key = (path.DescendantId, path.Depth);
            if (index.TryGetValue(key, out var existing) && existing != path.AncestorId)
                throw new InvalidOperationException($"Hierarquia inconsistente para {path.DescendantId} na profundidade {path.Depth}.");
            index[key] = path.AncestorId;
        }
        return index;
    }

    private static CommissionEntry Entry(Guid beneficiary, PaidBoleto boleto, CommissionRule rule) => new(
        beneficiary,
        boleto.Id,
        boleto.OwnerId,
        rule.Id,
        rule.Type,
        rule.Level,
        rule.GroupId,
        rule.Rate,
        boleto.Amount,
        Math.Round(boleto.Amount * rule.Rate, 2, MidpointRounding.ToEven));
}
```

- [ ] **Step 4: Rodar os testes e confirmar que passam**

Run: `dotnet test tests/Recorrencia.Domain.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Recorrencia.Domain tests/Recorrencia.Domain.Tests
git commit -m "feat(domain): calculate own, multi-level upline and global commissions"
```

---

### Task 3: Agregação por beneficiário

**Files:**
- Create: `src/Recorrencia.Domain/Commissions/CommissionSummary.cs`
- Test: `tests/Recorrencia.Domain.Tests/CommissionSummaryTests.cs`

**Interfaces:**
- Consumes: `CommissionEntry`, `CommissionEngine` (Tasks 1–2), `Builders` (Task 2).
- Produces:
  - `sealed record RuleTotal(RuleType RuleType, int? Level, Guid? GroupId, decimal Rate, decimal Base, decimal Amount)`
  - `sealed record BeneficiaryTotal(Guid BeneficiaryId, decimal Total, IReadOnlyList<RuleTotal> ByRule)`
  - `static class CommissionSummary { static IReadOnlyList<BeneficiaryTotal> ByBeneficiary(IEnumerable<CommissionEntry> entries) }` — agrupa por beneficiário e, dentro dele, por `(RuleType, Level, GroupId, Rate)`. `ByRule` vem ordenado por `RuleType` e depois `Level`. A lista de beneficiários vem ordenada por `Total` decrescente e depois por `BeneficiaryId`.

- [ ] **Step 1: Escrever os testes (falhando)**

`tests/Recorrencia.Domain.Tests/CommissionSummaryTests.cs`:

```csharp
using static Recorrencia.Domain.Tests.Builders;

namespace Recorrencia.Domain.Tests;

public class CommissionSummaryTests
{
    [Fact]
    public void Groups_the_pdf_example_by_beneficiary_and_rule()
    {
        var boletos = Boletos(Joao, 70, 200m)
            .Concat(Boletos(Joao, 20, 300m))
            .Concat(Boletos(Maria, 50, 200m))
            .Concat(Boletos(Pedro, 25, 200m))
            .ToList();
        var entries = CommissionEngine.Calculate(AprovecPlan(), boletos, Chain(Joao, Maria, Pedro), [new(Coordenacao, Coordenador)]);

        var summary = CommissionSummary.ByBeneficiary(entries);

        Assert.Equal(new[] { Joao, Maria, Pedro, Coordenador }.OrderBy(x => x).ToHashSet(), summary.Select(s => s.BeneficiaryId).ToHashSet());
        var joao = summary.Single(s => s.BeneficiaryId == Joao);
        Assert.Equal(1600.00m, joao.Total);
        Assert.Equal(
            new[]
            {
                new RuleTotal(RuleType.Own, null, null, 0.07m, 20000m, 1400.00m),
                new RuleTotal(RuleType.Upline, 1, null, 0.02m, 10000m, 200.00m),
            },
            joao.ByRule);

        var coordenador = summary.Single(s => s.BeneficiaryId == Coordenador);
        Assert.Equal(new[] { new RuleTotal(RuleType.Global, null, Coordenacao, 0.01m, 35000m, 350.00m) }, coordenador.ByRule);
    }

    [Fact]
    public void Orders_beneficiaries_by_total_descending()
    {
        var entries = CommissionEngine.Calculate(
            AprovecPlan(),
            Boletos(Joao, 100, 200m).Concat(Boletos(Maria, 50, 200m)).Concat(Boletos(Pedro, 25, 200m)).ToList(),
            Chain(Joao, Maria, Pedro),
            []);

        var order = CommissionSummary.ByBeneficiary(entries).Select(s => s.BeneficiaryId).ToList();
        Assert.Equal(new[] { Joao, Maria, Pedro }, order);
    }

    [Fact]
    public void Total_is_the_sum_of_rounded_entries()
    {
        var plan = new CommissionPlan(Guid.NewGuid(), new DateOnly(2026, 8, 1), [new CommissionRule(Guid.NewGuid(), RuleType.Own, 0.05m)]);
        var entries = CommissionEngine.Calculate(plan, Boletos(Joao, 3, 2.50m).ToList(), [], []);

        var joao = Assert.Single(CommissionSummary.ByBeneficiary(entries));
        Assert.Equal(0.36m, joao.Total);
        Assert.Equal(7.50m, joao.ByRule.Single().Base);
    }

    [Fact]
    public void Empty_input_gives_empty_summary()
    {
        Assert.Empty(CommissionSummary.ByBeneficiary([]));
    }
}
```

Em `Total_is_the_sum_of_rounded_entries`: cada lançamento vale 0,12 (0,125 arredondado para par), e 3 × 0,12 = 0,36. Arredondar o total (7,50 × 0,05 = 0,375 → 0,38) daria outro valor. É isso que o teste protege.

- [ ] **Step 2: Rodar e confirmar que falha**

Run: `dotnet test tests/Recorrencia.Domain.Tests --filter "FullyQualifiedName~CommissionSummaryTests"`
Expected: FAIL de compilação (`CommissionSummary` não existe).

- [ ] **Step 3: Implementar**

`src/Recorrencia.Domain/Commissions/CommissionSummary.cs`:

```csharp
namespace Recorrencia.Domain.Commissions;

public sealed record RuleTotal(RuleType RuleType, int? Level, Guid? GroupId, decimal Rate, decimal Base, decimal Amount);

public sealed record BeneficiaryTotal(Guid BeneficiaryId, decimal Total, IReadOnlyList<RuleTotal> ByRule);

public static class CommissionSummary
{
    public static IReadOnlyList<BeneficiaryTotal> ByBeneficiary(IEnumerable<CommissionEntry> entries) =>
        entries
            .GroupBy(e => e.BeneficiaryId)
            .Select(beneficiary => new BeneficiaryTotal(
                beneficiary.Key,
                beneficiary.Sum(e => e.Amount),
                beneficiary
                    .GroupBy(e => (e.RuleType, e.Level, e.GroupId, e.Rate))
                    .Select(rule => new RuleTotal(
                        rule.Key.RuleType,
                        rule.Key.Level,
                        rule.Key.GroupId,
                        rule.Key.Rate,
                        rule.Sum(e => e.Base),
                        rule.Sum(e => e.Amount)))
                    .OrderBy(r => r.RuleType)
                    .ThenBy(r => r.Level)
                    .ToList()))
            .OrderByDescending(b => b.Total)
            .ThenBy(b => b.BeneficiaryId)
            .ToList();
}
```

- [ ] **Step 4: Rodar os testes e confirmar que passam**

Run: `dotnet test tests/Recorrencia.Domain.Tests`
Expected: PASS em todas as classes.

- [ ] **Step 5: Commit**

```bash
git add src/Recorrencia.Domain tests/Recorrencia.Domain.Tests
git commit -m "feat(domain): summarize commission entries by beneficiary and rule"
```
