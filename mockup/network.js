(() => {
  "use strict";
  const key = "aprovec-network-v1";
  const initial = {
    version: 1,
    rates: { own: 7, referral: 2, manager: 1 },
    people: [
      {
        id: "gestor",
        name: "Renata Costa",
        email: "renata@exemplo.com",
        role: "manager",
        parentId: null,
      },
      {
        id: "joao",
        name: "João Silva",
        email: "joao@exemplo.com",
        role: "seller",
        parentId: null,
      },
      {
        id: "maria",
        name: "Maria Oliveira",
        email: "maria@exemplo.com",
        role: "seller",
        parentId: "joao",
      },
      {
        id: "pedro",
        name: "Pedro Santos",
        email: "pedro@exemplo.com",
        role: "seller",
        parentId: "maria",
      },
    ],
  };
  let state = JSON.parse(JSON.stringify(initial)),
    persistent = true;
  const round = (n) => Math.round((n + Number.EPSILON) * 100) / 100;
  function validateRates(r) {
    if (
      !["own", "referral", "manager"].every(
        (k) =>
          typeof r[k] === "number" &&
          Number.isFinite(r[k]) &&
          r[k] >= 0 &&
          r[k] <= 100,
      )
    )
      throw Error("Os percentuais devem estar entre 0 e 100.");
    if (r.own + r.referral + r.manager > 100)
      throw Error("A soma dos percentuais não pode ultrapassar 100%.");
  }
  function validatePerson(p, people) {
    if (!p.name || p.name.trim().length < 3 || p.name.length > 70)
      throw Error("Informe um nome de 3 a 70 caracteres.");
    if (!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(p.email))
      throw Error("Informe um e-mail válido.");
    if (
      people.some(
        (o) => o.id !== p.id && o.email.toLowerCase() === p.email.toLowerCase(),
      )
    )
      throw Error("Este e-mail já está cadastrado.");
    if (p.parentId === p.id)
      throw Error("Um consultor não pode ser seu próprio supervisor.");
    if (p.role === "manager" && p.parentId)
      throw Error("A coordenação da base não pertence à estrutura de supervisão.");
    let current = p.parentId,
      seen = new Set([p.id]);
    while (current) {
      if (seen.has(current))
        throw Error("Esse vínculo criaria um ciclo na estrutura de supervisão.");
      seen.add(current);
      const parent = people.find((o) => o.id === current);
      if (!parent || parent.role !== "seller")
        throw Error("Selecione um supervisor comercial válido.");
      current = parent.parentId;
    }
  }
  try {
    const saved = JSON.parse(localStorage.getItem(key) || "null");
    if (saved) {
      if (
        saved.version !== 1 ||
        !Array.isArray(saved.people) ||
        !["joao", "maria", "pedro", "gestor"].every((id) =>
          saved.people.some((p) => p.id === id),
        ) ||
        new Set(saved.people.map((p) => p.id)).size !== saved.people.length ||
        saved.people.filter((p) => p.role === "manager").length !== 1
      )
        throw Error("Cadastro inválido.");
      validateRates(saved.rates);
      saved.people.forEach((p) => {
        if (!["seller", "manager"].includes(p.role))
          throw Error("Perfil inválido.");
        validatePerson(p, saved.people);
      });
      state = saved;
    }
  } catch {
    state = JSON.parse(JSON.stringify(initial));
  }
  function persist() {
    try {
      localStorage.setItem(key, JSON.stringify(state));
      persistent = true;
    } catch {
      persistent = false;
    }
  }
  const rows = window.aprovecData.rows.map((r) => ({ ...r, ownerId: "joao" }));
  for (const [ownerId, count, late, cancelled, upcoming, prefix] of [
    ["maria", 50, 6, 2, 4, "MAR"],
    ["pedro", 25, 3, 2, 6, "PED"],
  ]) {
    for (let i = 0; i < count + late + cancelled + upcoming; i++) {
      const status =
        i < count
          ? "recebido"
          : i < count + late
            ? "atraso"
            : i < count + late + cancelled
              ? "cancelado"
              : "avencer";
      const lateIndex = i - count,
        days = status === "atraso" ? [8, 19, 35, 46, 62, 70][lateIndex] : 0;
      const due =
        status === "atraso"
          ? new Date(Date.UTC(2026, 8, 13 - days)).toISOString().slice(0, 10)
          : status === "avencer"
            ? "2026-09-25"
            : "2026-09-05";
      rows.push({
        id: `${prefix}-${String(i + 1).padStart(4, "0")}`,
        ownerId,
        name: `${["André Lima", "Beatriz Melo", "Daniel Costa", "Elisa Rocha", "Felipe Moura"][i % 5]} ${String(i + 1).padStart(2, "0")}`,
        plate: `${prefix}${i % 10}A${String(i).padStart(2, "0")}`,
        phone: "(81) 9••••-••••",
        vehicle: "Veículo demonstrativo",
        value: 200,
        status,
        due,
        days,
        paid: status === "recebido" ? "2026-09-10" : null,
        associate:
          status === "cancelado"
            ? "Cancelado"
            : days > 60
              ? "Inativo Jurídico"
              : days > 30
                ? "Inadimplente 31–60 dias"
                : days > 0
                  ? "Inadimplente até 30 dias"
                  : "Ativo",
      });
    }
  }
  const person = (id) => state.people.find((p) => p.id === id);
  const sellers = () => state.people.filter((p) => p.role === "seller");
  const children = (id) => sellers().filter((p) => p.parentId === id);
  const received = (id) =>
    rows
      .filter((r) => r.ownerId === id && r.status === "recebido")
      .reduce((s, r) => s + r.value, 0);
  function personTotals(id, rates = state.rates) {
    const p = person(id);
    if (!p)
      return { received: 0, own: 0, referralBase: 0, referral: 0, total: 0 };
    if (p.role === "manager") {
      const base = rows
        .filter((r) => r.status === "recebido")
        .reduce((s, r) => s + r.value, 0);
      return {
        received: base,
        own: 0,
        referralBase: 0,
        referral: 0,
        total: round((base * rates.manager) / 100),
      };
    }
    const base = received(id),
      referralBase = children(id).reduce((s, p) => s + received(p.id), 0),
      own = round((base * rates.own) / 100),
      referral = round((referralBase * rates.referral) / 100);
    return {
      received: base,
      own,
      referralBase,
      referral,
      total: round(own + referral),
    };
  }
  function totals(rates = state.rates) {
    const sum = (status) =>
        rows
          .filter((r) => r.status === status)
          .reduce((s, r) => s + r.value, 0),
      received = sum("recebido"),
      manager = round((received * rates.manager) / 100);
    return {
      received,
      manager,
      overdue: sum("atraso"),
      cancelled: sum("cancelado"),
      upcoming: sum("avencer"),
      own: round((received * rates.own) / 100),
      referral: round(
        sellers().reduce((s, p) => s + personTotals(p.id, rates).referral, 0),
      ),
      payroll: round(
        manager +
          sellers().reduce((s, p) => s + personTotals(p.id, rates).total, 0),
      ),
    };
  }
  function savePerson(input) {
    const previous = input.id ? person(input.id) : null;
    if (input.id && !previous) throw Error("Participante não encontrado.");
    const p = {
      id:
        previous?.id ||
        `p-${Date.now()}-${Math.random().toString(36).slice(2, 7)}`,
      name: String(input.name || "").trim(),
      email: String(input.email || "")
        .trim()
        .toLowerCase(),
      role: previous?.role || "seller",
      parentId: input.parentId || null,
    };
    validatePerson(p, state.people);
    if (previous)
      state.people = state.people.map((o) => (o.id === p.id ? p : o));
    else state.people.push(p);
    persist();
    return p;
  }
  function saveRates(r) {
    validateRates(r);
    state.rates = {
      own: round(r.own),
      referral: round(r.referral),
      manager: round(r.manager),
    };
    persist();
  }
  window.aprovecNetwork = {
    rows,
    round,
    person,
    sellers,
    children,
    received,
    personTotals,
    totals,
    savePerson,
    saveRates,
    get rates() {
      return { ...state.rates };
    },
    get people() {
      return state.people.map((p) => ({ ...p }));
    },
    get persistent() {
      return persistent;
    },
  };
})();
