(() => {
  "use strict";
  const data = window.aprovecData;
  const model = window.aprovecNetwork;
  const money = (n) =>
    n.toLocaleString("pt-BR", { style: "currency", currency: "BRL" });
  const date = (v) => (v ? v.split("-").reverse().join("/") : "—");
  const esc = (v) =>
    String(v).replace(
      /[&<>"']/g,
      (c) =>
        ({
          "&": "&amp;",
          "<": "&lt;",
          ">": "&gt;",
          '"': "&quot;",
          "'": "&#39;",
        })[c],
    );
  const paths = {
    grid: '<rect x="3" y="3" width="7" height="7" rx="1.4"/><rect x="14" y="3" width="7" height="7" rx="1.4"/><rect x="3" y="14" width="7" height="7" rx="1.4"/><rect x="14" y="14" width="7" height="7" rx="1.4"/>',
    wallet:
      '<path d="M20 8V6a2 2 0 0 0-2-2H6a3 3 0 0 0 0 6h14v10H6a3 3 0 0 1-3-3V7"/><path d="M20 12h-5v4h5"/><path d="M16 14h.01"/>',
    percent:
      '<path d="M5 19 19 5"/><circle cx="6.5" cy="6.5" r="3"/><circle cx="17.5" cy="17.5" r="3"/>',
    calendar:
      '<rect x="3" y="5" width="18" height="16" rx="2"/><path d="M16 3v4M8 3v4M3 11h18M8 15h2M14 15h2"/>',
    arrow: '<path d="M5 12h14m-5-5 5 5-5 5"/>',
    chevron: '<path d="m9 5 7 7-7 7"/>',
    down: '<path d="m6 9 6 6 6-6"/>',
    search: '<circle cx="10.5" cy="10.5" r="7"/><path d="m16 16 5 5"/>',
    check: '<path d="m5 12 4 4L19 6"/>',
    circlecheck: '<circle cx="12" cy="12" r="9"/><path d="m8 12 3 3 5-6"/>',
    clock: '<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>',
    close: '<path d="m6 6 12 12M6 18 18 6"/>',
    info: '<circle cx="12" cy="12" r="9"/><path d="M12 11v6M12 7h.01"/>',
    receipt:
      '<path d="M6 3h9l4 4v14l-3-2-3 2-3-2-3 2-3-2V3h2"/><path d="M14 3v5h5M8 12h7M8 16h5"/>',
    people:
      '<circle cx="9" cy="7" r="3"/><path d="M3 21v-3a6 6 0 0 1 12 0v3M16 4a3 3 0 0 1 0 6M18 14a5 5 0 0 1 3 4v3"/>',
    download: '<path d="M12 3v12m-4-4 4 4 4-4M4 16v5h16v-5"/>',
    shield:
      '<path d="m12 3 8 3v6c0 5-8 9-8 9s-8-4-8-9V6l8-3"/><path d="m8 12 3 3 5-6"/>',
    car: '<path d="m5 6-3 7v6h3v-3h14v3h3v-6l-3-7H5ZM3 12h18M6 13v2m12-2v2"/>',
    file: '<path d="M14 2H5v20h14V7l-5-5ZM14 2v6h5M8 13h8M8 17h5"/>',
    back: '<path d="m14 5-7 7 7 7"/>',
    logout: '<path d="M9 3H4v18h5M13 7l5 5-5 5M8 12h13"/>',
  };
  const icon = (name, cls = "") =>
    `<svg class="icon ${cls}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.65" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${paths[name] || paths.file}</svg>`;
  const state = {
    role: "seller",
    page: "overview",
    filter: "todos",
    query: "",
    pagination: 1,
    source: "proprias",
    closing: 0,
    selectedNF: false,
    drawer: null,
  };
  const root = document.getElementById("aprovec-app");
  const terminology = [
    ["PAINEL DO VENDEDOR", "PAINEL DO CONSULTOR"],
    ["PAINEL DO GESTOR", "PAINEL DO COORDENADOR"],
    ["COMISSÃO DO GESTOR", "COMISSÃO DO COORDENADOR"],
    ["GESTÃO GLOBAL", "COORDENADOR"],
    ["Gestora da base", "Coordenador"],
    ["Gestor da base", "Coordenador"],
    ["Gestão de toda a base", "Coordenador"],
    ["Gestão da base", "Coordenador"],
    ["gestão da base", "coordenador"],
    ["Taxa de gestão", "Percentual do coordenador"],
    ["taxa de gestão", "percentual do coordenador"],
    ["Gestão global", "Coordenador"],
    ["gestão global", "coordenador"],
    ["Adicionar indicado", "Adicionar consultor"],
    ["Quem indicou este vendedor?", "Quem supervisiona este consultor?"],
    ["Minhas indicações diretas", "Meus consultores supervisionados"],
    ["minhas indicações diretas", "meus consultores supervisionados"],
    ["Indicações diretas", "Supervisor"],
    ["indicações diretas", "supervisor"],
    ["Indicação direta", "Supervisor"],
    ["indicação direta", "supervisor"],
    ["Indicador direto", "Supervisor"],
    ["indicador direto", "supervisor"],
    ["Recebidos dos indicados diretos", "Recebidos dos consultores supervisionados"],
    ["quem indicou diretamente", "quem supervisiona diretamente"],
    ["seus indicados diretos", "seus consultores supervisionados diretamente"],
    ["dos indicados diretos", "dos consultores supervisionados diretamente"],
    ["indicados diretos", "consultores supervisionados diretamente"],
    ["cada indicado", "cada consultor supervisionado"],
    ["um indicado", "um consultor supervisionado"],
    ["Vendas próprias", "Comissão do consultor"],
    ["vendas próprias", "comissão do consultor"],
    ["Venda própria", "Consultor · 7%"],
    ["venda própria", "comissão do consultor"],
    ["VENDEDOR", "CONSULTOR"],
    ["Vendedores", "Consultores"],
    ["vendedores", "consultores"],
    ["Vendedor", "Consultor"],
    ["vendedor", "consultor"],
    ["INDICAÇÃO", "SUPERVISÃO"],
    ["Indicação", "Supervisão"],
    ["indicação", "supervisão"],
    ["Indicador", "Supervisor"],
    ["indicador", "supervisor"],
    ["indicado", "consultor supervisionado"],
    ["Gestores", "Coordenadores"],
    ["gestores", "coordenadores"],
    ["Gestor", "Coordenador"],
    ["gestor", "coordenador"],
    ["% para", "% · Supervisor:"],
  ];
  const applyTerminology = (value) =>
    terminology.reduce((text, [from, to]) => text.replaceAll(from, to), value);
  function normalizeVisibleTerminology(scope) {
    if (!scope) return;
    const textNodes = [];
    const walker = document.createTreeWalker(scope, NodeFilter.SHOW_TEXT);
    let node = walker.nextNode();
    while (node) {
      textNodes.push(node);
      node = walker.nextNode();
    }
    textNodes.forEach((textNode) => {
      const normalized = applyTerminology(textNode.nodeValue);
      if (normalized !== textNode.nodeValue) textNode.nodeValue = normalized;
    });
    scope.querySelectorAll("[aria-label], [title], [placeholder]").forEach((element) => {
      ["aria-label", "title", "placeholder"].forEach((attribute) => {
        const value = element.getAttribute(attribute);
        if (value) element.setAttribute(attribute, applyTerminology(value));
      });
    });
  }
  let lastFocus = null;
  const navItems = [
    ["overview", "grid", "Visão geral"],
    ["wallet", "wallet", "Minha carteira"],
    ["commissions", "percent", "Comissões"],
    ["closing", "calendar", "Fechamento"],
  ];
  let ownReceived, ownCommission, totalCommission;
  function syncSeller() {
    const v = model.personTotals("joao");
    ownReceived = v.received;
    ownCommission = v.own;
    totalCommission = v.total;
    data.rows.forEach(
      (r) =>
        (r.commission =
          r.status === "recebido"
            ? model.round((r.value * model.rates.own) / 100)
            : 0),
    );
  }
  syncSeller();
  const menus = {
    seller: navItems,
    manager: [
      ["manager-overview", "grid", "Visão da base"],
      ["manager-bills", "wallet", "Boletos da operação"],
      ["manager-payroll", "percent", "Comissões por pessoa"],
      ["manager-tree", "people", "Árvore comissionada"],
    ],
    admin: [
      ["admin-tree", "people", "Árvore comissionada"],
      ["admin-people", "wallet", "Participantes"],
      ["admin-rates", "percent", "Remuneração"],
    ],
  };
  const network = window.createNetworkUI({
    money,
    icon,
    esc,
    badge: (...v) => badge(...v),
    drawer,
    toast,
    navigate,
    render,
    closeDrawer,
    closingState: () => state.closing,
  });
  const badge = (type, label) =>
    `<span class="status ${type}"><span></span>${label}</span>`;
  const status = (r) =>
    badge(
      r.status,
      r.status === "recebido"
        ? "Recebido"
        : r.status === "atraso"
          ? "Em atraso"
          : "Cancelado",
    );

  root.innerHTML = `<aside class="sidebar"><div class="brand"><img src="assets/logo-aprovec.webp" alt="APROVEC Brasil"><div class="brand-product">RECORRÊNCIA COMERCIAL</div></div><nav aria-label="Navegação principal">${navItems.map(([id, i, l]) => `<button data-page="${id}" class="nav-item" type="button">${icon(i)}<span>${l}</span>${id === "closing" ? '<span class="nav-count" id="closing-count">1</span>' : ""}</button>`).join("")}</nav><div class="sidebar-bottom"><div class="sidebar-note">Cada pagamento.<br><strong>Um resultado que cresce.</strong></div><div class="seller"><span class="avatar">JS</span><div><strong>João Silva</strong><small>Consultor comercial</small></div><span class="seller-dot" aria-label="Perfil ativo"></span></div></div></aside><div class="workspace"><header class="topbar"><div class="breadcrumb">Comercial <span>/</span> <strong id="breadcrumb-title">Visão geral</strong></div><label class="profile-switcher"><span>Explorar como</span><select id="profile-select" aria-label="Explorar como"><option value="seller">Vendedor</option><option value="manager">Gestor da base</option><option value="admin">Administrador</option></select></label><span class="demo-badge"><span></span>Dados demonstrativos</span></header><main id="main-content"></main><footer class="app-footer"><span>APROVEC Brasil <span>·</span> Recorrência comercial</span><button data-action="rules" class="text-button">${icon("info")}Regras do demonstrativo</button></footer></div><div id="overlay-root"></div><div class="toast" role="status" aria-live="polite" hidden></div>`;

  function heading(title, subtitle, closing = false) {
    return `<div class="page-heading"><div><div class="eyebrow">${closing ? "SEU FECHAMENTO MENSAL" : "PAINEL DO VENDEDOR"}</div><h1>${title}</h1><p>${subtitle}</p></div><div class="period"><span class="period-label">COMPETÊNCIA</span><div class="period-value">${icon("calendar")}<strong>${closing ? "Agosto" : "Setembro"} de 2026</strong>${badge(closing ? "neutral" : "progress", closing ? "Encerrada" : "Em apuração")}</div></div></div>${closing ? "" : `<div class="cutoff"><span class="live-dot"></span><span>Atualizado em 13/09/2026 às 08:00</span><span class="cutoff-separator">·</span><span>Pagamentos até <strong>10/09/2026</strong></span><button data-action="rules" class="tiny-tag">D-3 ${icon("info")}</button></div>`}`;
  }

  function commissionBand() {
    const t = model.personTotals("joao"),
      children = model.children("joao");
    return `<section class="commission-band" aria-label="Composição da comissão de setembro"><div class="commission-total"><span class="overline">Comissão apurada no mês</span><div class="hero-number">${money(totalCommission)}</div><span class="subtle">Sobre pagamentos confirmados</span></div><div class="commission-part"><div class="rate-badge">${model.rates.own}<span>%</span></div><div><span class="subtle">Vendas próprias<span class="compact-rate"> · ${model.rates.own}%</span></span><strong>${money(ownCommission)}</strong><span class="formula">${money(ownReceived)} recebidos</span></div></div><span class="plus">+</span><div class="commission-part"><div class="rate-badge secondary">${model.rates.referral}<span>%</span></div><div><span class="subtle">Indicação direta<span class="compact-rate"> · ${model.rates.referral}%</span></span><strong>${money(t.referral)}</strong><span class="formula">${money(t.referralBase)} · ${children.length} ${children.length === 1 ? "carteira" : "carteiras"}</span></div></div><button data-page="commissions" class="band-link" aria-label="Ver composição das comissões">${icon("arrow")}</button></section>`;
  }

  function health() {
    return `<section class="health-grid" aria-label="Saúde da carteira própria">${[
      [
        "recebido",
        "circlecheck",
        "Recebidos",
        20000,
        90,
        "Pagamentos que geram comissão",
      ],
      [
        "atraso",
        "clock",
        "Em atraso",
        3600,
        12,
        "Oportunidades de regularização",
      ],
      [
        "cancelado",
        "close",
        "Cancelados",
        800,
        4,
        "Não geram comissão no período",
      ],
    ]
      .map(
        ([key, i, label, value, count, note]) =>
          `<button class="health-cell ${key}" data-health="${key}"><div class="health-top"><span class="health-icon">${icon(i)}</span><span>${label}</span>${icon("arrow", "health-arrow")}</div><strong>${money(value)}</strong><div class="health-bottom"><span>${count} boletos</span><span>${note}</span></div></button>`,
      )
      .join("")}</section>`;
  }

  function table(rows, compact = false) {
    return `<div class="table-scroll"><table><thead><tr><th>Associado / veículo</th><th>Vencimento</th><th class="numeric">Valor do boleto</th><th>Situação</th><th class="numeric">Sua comissão</th><th><span class="sr-only">Detalhes</span></th></tr></thead><tbody>${
      rows.length
        ? rows
            .map(
              (r) =>
                `<tr><td><button class="associate-button" data-detail="${r.id}"><span class="row-avatar">${r.name
                  .split(" ")
                  .slice(0, 2)
                  .map((s) => s[0])
                  .join(
                    "",
                  )}</span><span><strong>${r.name}</strong><small class="plate">${r.plate} <span>·</span> ${compact ? "Carteira própria" : r.vehicle}</small></span></button></td><td>${date(r.due)}${r.status === "atraso" ? `<small class="late-days">${r.days} dias em atraso</small>` : ""}</td><td class="numeric amount">${money(r.value)}</td><td>${status(r)}${r.associate === "Inativo Jurídico" ? '<small class="legal-label">Associado no jurídico</small>' : ""}</td><td class="numeric amount ${r.commission ? "earned" : "muted"}">${money(r.commission)}${r.commission ? `<small>${model.rates.own}% do pagamento</small>` : ""}</td><td><button class="icon-button" data-detail="${r.id}" aria-label="Detalhes de ${r.name}">${icon("chevron")}</button></td></tr>`,
            )
            .join("")
        : `<tr><td colspan="6" class="empty-state">${icon("search")}<strong>Nenhum boleto encontrado</strong><span>Tente outro nome, placa ou situação.</span><button class="text-button" data-action="clear">Limpar busca e filtros</button></td></tr>`
    }</tbody></table></div>`;
  }

  function overview() {
    return `${heading("Minha recorrência", "Acompanhe sua carteira e a origem de cada comissão.")}${commissionBand()}<div class="section-heading"><h2>Saúde da minha carteira</h2><span class="section-meta">Carteira própria · setembro</span></div>${health()}<div class="overview-lower"><section class="panel activity-panel"><div class="panel-header"><div><h2>Últimos recebimentos</h2><p>Pagamentos confirmados na sua carteira</p></div><button class="text-button" data-health="recebido">Ver todos ${icon("arrow")}</button></div>${table(
      [0, 21, 42, 63, 74].map((i) => data.rows[i]),
      true,
    )}<div class="panel-footer"><span>${icon("shield")}Comissão calculada sobre o valor pago</span><strong>${model.rates.own}% por recebimento</strong></div></section><div class="right-column"><section class="closing-card"><div class="closing-icon">${icon("receipt")}</div><div class="closing-eyebrow">FECHAMENTO DISPONÍVEL</div><h2>Agosto de 2026</h2><p>Seu demonstrativo está pronto para conferência.</p><div class="closing-amount">${money(data.august.total)}</div><div class="closing-status">${badge(state.closing === 3 ? "recebido" : "warning", state.closing === 3 ? "Provisionado" : "Sua ação é necessária")}</div><button class="primary-button full" data-page="closing">${state.closing === 3 ? "Ver fechamento" : "Conferir demonstrativo"}${icon("arrow")}</button></section><section class="referral-card"><div class="panel-header"><h2>Minhas indicações diretas</h2>${icon("people")}</div><button class="referral-person" data-action="referral"><span class="avatar maria">${model.children("joao").length}</span><span><strong>${model.children("joao").length === 1 ? esc(model.children("joao")[0].name) : model.children("joao").length + " indicações diretas"}</strong><small>Carteiras de 1º nível</small></span>${icon("chevron")}</button><div class="referral-bottom"><span>Sua comissão <strong>${model.rates.referral}%</strong></span><strong>${money(model.personTotals("joao").referral)}</strong></div></section></div></div>`;
  }

  function filteredRows() {
    const q = state.query
      .normalize("NFD")
      .replace(/[\u0300-\u036f]/g, "")
      .toLowerCase();
    return data.rows.filter(
      (r) =>
        (state.filter === "todos" || r.status === state.filter) &&
        `${r.name} ${r.plate} ${r.id}`
          .normalize("NFD")
          .replace(/[\u0300-\u036f]/g, "")
          .toLowerCase()
          .includes(q),
    );
  }
  function walletResults() {
    const rows = filteredRows(),
      pages = Math.max(1, Math.ceil(rows.length / 8));
    state.pagination = Math.min(state.pagination, pages);
    const start = (state.pagination - 1) * 8;
    const sums = rows.reduce(
      (a, r) => ({
        value: a.value + r.value,
        commission: a.commission + r.commission,
      }),
      { value: 0, commission: 0 },
    );
    return `${table(rows.slice(start, start + 8))}<div class="results-summary"><span>${rows.length} ${rows.length === 1 ? "boleto" : "boletos"} no filtro</span><span>Valor dos boletos <strong>${money(sums.value)}</strong></span><span>Comissão apurada <strong>${money(sums.commission)}</strong></span></div><div class="pagination"><span>${rows.length ? `${start + 1}–${Math.min(start + 8, rows.length)} de ${rows.length} boletos` : "0 boletos"}</span><div><button class="icon-button" data-pagination="prev" ${state.pagination === 1 ? "disabled" : ""} aria-label="Página anterior">${icon("back")}</button><span>Página <strong>${state.pagination}</strong> de ${pages}</span><button class="icon-button" data-pagination="next" ${state.pagination >= pages ? "disabled" : ""} aria-label="Próxima página">${icon("chevron")}</button></div></div>`;
  }
  function wallet() {
    return `${heading("Minha carteira", "Os pagamentos e a saúde da base, boleto por boleto.")}<section class="panel wallet-panel"><div class="wallet-toolbar"><div class="filter-tabs" role="group" aria-label="Filtrar boletos">${[
      ["todos", "Todos", 106],
      ["recebido", "Recebidos", 90],
      ["atraso", "Em atraso", 12],
      ["cancelado", "Cancelados", 4],
    ]
      .map(
        ([v, l, c]) =>
          `<button data-filter="${v}" class="filter-tab ${state.filter === v ? "selected" : ""}" aria-pressed="${state.filter === v}">${l}<span>${c}</span></button>`,
      )
      .join(
        "",
      )}</div><label class="search-field">${icon("search")}<input id="wallet-search" type="search" placeholder="Buscar nome ou placa" aria-label="Buscar associado ou placa" value="${esc(state.query)}"></label></div><div id="wallet-results">${walletResults()}</div></section><div class="wallet-note">${icon("info")}A comissão é gerada após a confirmação do pagamento. A situação do associado pode ser consultada no detalhe.</div>`;
  }

  function commissions() {
    const t = model.personTotals("joao"),
      children = model.children("joao");
    return `${heading("Minhas comissões", "Veja como cada recebimento contribui para o seu resultado.")}${commissionBand()}<section class="panel commission-panel"><div class="panel-header"><div><h2>Origem dos ganhos</h2><p>Uma base de cálculo clara para cada comissão</p></div><button class="secondary-button" data-action="export">${icon("download")}Baixar demonstrativo</button></div><div class="commission-tabs" role="group" aria-label="Origem da comissão"><button class="${state.source === "proprias" ? "selected" : ""}" data-source="proprias">${icon("wallet")}Vendas próprias <span>${model.rates.own}%</span></button><button class="${state.source === "indicacoes" ? "selected" : ""}" data-source="indicacoes">${icon("people")}Indicação direta <span>${model.rates.referral}%</span></button></div>${
      state.source === "proprias"
        ? `<div class="source-summary"><div><span>Base recebida</span><strong>${money(ownReceived)}</strong></div><span class="operator">×</span><div><span>Sua taxa</span><strong>${model.rates.own}%</strong></div><span class="operator">=</span><div class="source-result"><span>Comissão própria</span><strong>${money(ownCommission)}</strong></div><button class="text-button" data-health="recebido">Conferir 90 pagamentos ${icon("arrow")}</button></div>${table(data.rows.filter((r) => r.status === "recebido").slice(0, 5), true)}<div class="panel-footer"><span>Exibindo 5 dos 90 pagamentos que compõem a comissão.</span><button class="text-button" data-health="recebido">Ver todos os pagamentos</button></div>`
        : `<div class="source-summary"><div><span>Recebidos dos indicados diretos</span><strong>${money(t.referralBase)}</strong></div><span class="operator">×</span><div><span>Indicação direta</span><strong>${model.rates.referral}%</strong></div><span class="operator">=</span><div class="source-result"><span>Sua comissão</span><strong>${money(t.referral)}</strong></div></div>${
            children
              .map(
                (p) =>
                  `<div class="referral-detail-row"><span class="avatar maria">${esc(
                    p.name
                      .split(" ")
                      .slice(0, 2)
                      .map((v) => v[0])
                      .join(""),
                  )}</span><div><strong>${esc(p.name)}</strong><p>Indicação de 1º nível · ${money(model.received(p.id))} recebidos</p></div><strong class="network-earned">${money(model.round((model.received(p.id) * model.rates.referral) / 100))}</strong></div>`,
              )
              .join("") ||
            '<div class="empty-state">Você ainda não tem indicações diretas cadastradas.</div>'
          }<div class="referral-rule"><div>${icon("people")}<strong>Uma indicação. Um nível de comissão.</strong></div><p>Você recebe ${model.rates.referral}% sobre os pagamentos das carteiras indicadas diretamente por você. As indicações feitas por essas pessoas não entram na sua base.</p></div>`
    }</section>`;
  }

  function closing() {
    const steps = [
      "Apuração concluída",
      "Sua conferência",
      "Nota fiscal",
      "Provisionamento",
    ];
    return `${heading("Fechamento mensal", "Confira seu demonstrativo e acompanhe as próximas etapas.", true)}<div class="closing-banner">${icon("info")}Você está conferindo agosto. A competência de setembro continua em apuração, com <strong>${money(totalCommission)}</strong> até o corte atual.</div><div class="stepper">${steps.map((s, i) => `<div class="step ${i === 0 || i <= state.closing ? "complete" : ""} ${i === state.closing + 1 ? "current" : ""}"><span>${i === 0 || i <= state.closing ? icon("check") : i + 1}</span><div><strong>${s}</strong><small>${i === 0 ? "Valores consolidados" : i === 1 ? (state.closing > 0 ? "Confirmado por você" : "Confira os valores") : i === 2 ? (state.closing > 1 ? "NF demonstrativa recebida" : "Documento do período") : state.closing === 3 ? "Registrado no exemplo" : "Encaminhamento financeiro"}</small></div></div>`).join("")}</div><div class="closing-layout"><section class="panel statement"><div class="panel-header"><div><h2>Demonstrativo de comissão</h2><p>João Silva · Competência 08/2026</p></div>${badge(state.closing === 3 ? "recebido" : "neutral", state.closing === 3 ? "Provisionado" : state.closing === 0 ? "Para conferência" : "Confirmado")}</div><div class="statement-row"><span><strong>Vendas próprias</strong><small>Pagamentos confirmados da sua carteira</small></span><span>${money(data.august.own)} <small>× 7%</small></span><strong>${money(data.august.ownCommission)}</strong></div><div class="statement-row"><span><strong>Indicação direta</strong><small>Maria Oliveira · 1º nível</small></span><span>${money(data.august.referral)} <small>× 2%</small></span><strong>${money(data.august.referralCommission)}</strong></div><div class="statement-total"><div><span>Seu total no período</span><small>Comissão apurada de agosto</small></div><strong>${money(data.august.total)}</strong></div><div class="statement-foot">${icon("shield")}O demonstrativo mantém separados o valor recebido, a taxa e a comissão.</div><div class="statement-actions"><button class="text-button" data-action="august-export">${icon("download")}Baixar demonstrativo de agosto</button><button class="text-button" data-action="rules">Entender as regras ${icon("info")}</button></div></section><section class="panel next-step">${closingAction()}</section></div><section class="panel period-list"><div class="panel-header"><h2>Competências</h2><span class="section-meta">Acompanhamento mensal</span></div><div class="period-row"><span>${icon("calendar")}<strong>Setembro de 2026</strong></span>${badge("progress", "Em apuração")}<strong>${money(totalCommission)}</strong><span class="muted">Disponível após o fechamento</span></div><div class="period-row"><span>${icon("calendar")}<strong>Agosto de 2026</strong></span>${badge(state.closing === 3 ? "recebido" : "warning", state.closing === 3 ? "Provisionado" : state.closing === 0 ? "Aguardando conferência" : "Aguardando NF")}<strong>${money(data.august.total)}</strong><span class="muted">${state.closing === 3 ? "Pagamento ainda não realizado" : "Competência em exibição"}</span></div></section>`;
  }
  function closingAction() {
    if (state.closing === 0)
      return `<div class="next-icon">${icon("receipt")}</div><span class="eyebrow">PRÓXIMA ETAPA</span><h2>Confira seus valores</h2><p>Revise a comissão própria e a indicação direta antes de confirmar o demonstrativo.</p><label class="confirm-label"><input type="checkbox" id="confirm-check"><span>Conferi os valores de agosto e estou de acordo com o demonstrativo.</span></label><button class="primary-button full" id="confirm-button" data-action="confirm" disabled>Confirmar demonstrativo ${icon("arrow")}</button><small class="simulation-label">Esta confirmação é apenas uma simulação.</small>`;
    if (state.closing < 3)
      return `<div class="next-icon">${icon("file")}</div><span class="eyebrow">PRÓXIMA ETAPA</span><h2>Envie sua nota fiscal</h2><p>Valor da NF: <strong>${money(data.august.total)}</strong><br>Referente à comissão de agosto de 2026.</p><button class="nf-select ${state.selectedNF ? "selected" : ""}" data-action="select-nf">${icon(state.selectedNF ? "circlecheck" : "file")}<strong>${state.selectedNF ? "NF-demonstrativa-agosto.pdf" : "Usar uma NF demonstrativa"}</strong><span>${state.selectedNF ? "Documento de exemplo selecionado" : "Nenhum arquivo real será enviado"}</span></button><button class="primary-button full" data-action="send-nf" ${state.selectedNF ? "" : "disabled"}>Simular envio da NF ${icon("arrow")}</button><small class="simulation-label">Simulação local, sem envio ao financeiro.</small>`;
    return `<div class="next-icon success">${icon("circlecheck")}</div><span class="eyebrow">ETAPA CONCLUÍDA NO EXEMPLO</span><h2>Comissão provisionada</h2><p>A NF demonstrativa foi vinculada e o provisionamento de <strong>${money(data.august.total)}</strong> foi criado nesta simulação.</p><div class="payment-pending">${icon("clock")}<span><strong>Aguardando pagamento</strong><small>Provisionado não significa pago.</small></span></div><button class="secondary-button full" data-action="reset-closing">Reiniciar simulação</button><small class="simulation-label">Nenhum lançamento financeiro real foi criado.</small>`;
  }

  function render() {
    syncSeller();
    document.getElementById("main-content").innerHTML =
      state.role === "seller"
        ? { overview, wallet, commissions, closing }[state.page]()
        : network.render(state.page);
    root.querySelectorAll(".nav-item[data-page]").forEach((b) => {
      b.classList.toggle("active", b.dataset.page === state.page);
      if (b.dataset.page === state.page) b.setAttribute("aria-current", "page");
      else b.removeAttribute("aria-current");
    });
    document.getElementById("breadcrumb-title").textContent = menus[
      state.role
    ].find((n) => n[0] === state.page)[2];
    if (document.getElementById("closing-count"))
      document.getElementById("closing-count").hidden = state.closing === 3;
    const p =
      state.role === "seller"
        ? model.person("joao")
        : state.role === "manager"
          ? model.person("gestor")
          : { name: "Admin APROVEC" };
    root.querySelector(".seller strong").textContent = p.name;
    root.querySelector(".seller small").textContent = {
      seller: "Consultor comercial",
      manager: "Gestora da base",
      admin: "Administração comercial",
    }[state.role];
    root.querySelector(".seller .avatar").textContent = p.name
      .split(" ")
      .slice(0, 2)
      .map((v) => v[0])
      .join("");
    network.afterRender();
    normalizeVisibleTerminology(root);
  }
  function setRole(role) {
    if (!menus[role]) return;
    closeDrawer();
    state.role = role;
    state.page = menus[role][0][0];
    root.querySelector(".sidebar nav").innerHTML = menus[role]
      .map(
        ([id, i, l]) =>
          `<button data-page="${id}" class="nav-item" type="button">${icon(i)}<span>${l}</span>${id === "closing" ? '<span class="nav-count" id="closing-count">1</span>' : ""}</button>`,
      )
      .join("");
    render();
    window.scrollTo({ top: 0, behavior: "instant" });
  }

  function navigate(page) {
    state.page = page;
    render();
    window.scrollTo({ top: 0, behavior: "instant" });
  }
  function toast(message) {
    const el = root.querySelector(".toast");
    el.textContent = message;
    el.hidden = false;
    clearTimeout(toast.timer);
    toast.timer = setTimeout(() => (el.hidden = true), 4500);
  }
  function closeDrawer() {
    document.getElementById("overlay-root").innerHTML = "";
    document.body.style.overflow = "";
    state.drawer = null;
    if (lastFocus && document.contains(lastFocus)) lastFocus.focus();
  }
  function drawer(title, subtitle, content) {
    lastFocus = document.activeElement;
    state.drawer = true;
    document.body.style.overflow = "hidden";
    document.getElementById("overlay-root").innerHTML =
      `<div class="overlay"><button class="overlay-backdrop" aria-label="Fechar detalhe" data-action="close-drawer"></button><section class="drawer" role="dialog" aria-modal="true" aria-labelledby="drawer-title"><div class="drawer-header"><div><h2 id="drawer-title">${title}</h2><p>${subtitle}</p></div><button class="icon-button" data-action="close-drawer" aria-label="Fechar detalhe">${icon("close")}</button></div><div class="drawer-content">${content}</div></section></div>`;
    normalizeVisibleTerminology(document.getElementById("overlay-root"));
    root.querySelector('.drawer [data-action="close-drawer"]').focus();
  }
  function showDetail(id) {
    const r = data.rows.find((r) => r.id === id);
    if (!r) return;
    drawer(
      "Detalhe do boleto",
      `${r.id} · Carteira própria`,
      `<div class="drawer-person"><span class="avatar">${r.name
        .split(" ")
        .slice(0, 2)
        .map((s) => s[0])
        .join(
          "",
        )}</span><div><h3>${r.name}</h3><p>${r.phone} · Contato demonstrativo</p></div></div><div class="vehicle-line">${icon("car")}<strong>${r.plate}</strong><span>${r.vehicle}</span></div><dl class="detail-list"><div><dt>Situação do boleto</dt><dd>${status(r)}</dd></div><div><dt>Situação do associado</dt><dd>${r.associate}</dd></div><div><dt>Vencimento</dt><dd>${date(r.due)}</dd></div><div><dt>${r.status === "atraso" ? "Tempo em atraso" : "Pagamento confirmado"}</dt><dd>${r.status === "atraso" ? `${r.days} dias` : date(r.paid)}</dd></div><div><dt>Valor do boleto</dt><dd>${money(r.value)}</dd></div></dl><section class="calculation"><span class="eyebrow">CÁLCULO DA SUA COMISSÃO</span><div><span>${money(r.status === "recebido" ? r.value : 0)}</span><span>×</span><span>${model.rates.own}%</span><span>=</span><strong>${money(r.commission)}</strong></div><p>${r.status === "recebido" ? "Valor efetivamente pago × taxa de venda própria." : r.status === "atraso" ? "Ainda não há pagamento confirmado. Este boleto não compõe sua comissão apurada." : "Boleto cancelado. Não gera comissão no período."}</p></section>${r.associate === "Inativo Jurídico" ? `<div class="drawer-note">${icon("info")}O associado atingiu 61 dias de atraso. A situação jurídica não representa automaticamente o cancelamento deste boleto.</div>` : ""}<div class="drawer-note">${icon("shield")}Registro fictício para demonstrar a experiência do painel.</div>`,
    );
  }
  function referral() {
    const t = model.personTotals("joao");
    drawer(
      "Comissão por indicação",
      "Suas indicações diretas · 1º nível",
      `<dl class="detail-list">${
        model
          .children("joao")
          .map(
            (p) =>
              `<div><dt>${esc(p.name)}</dt><dd>${money(model.received(p.id))} recebidos</dd></div>`,
          )
          .join("") || "<p>Nenhum indicado direto cadastrado.</p>"
      }</dl><section class="calculation"><span class="eyebrow">SUA COMISSÃO POR INDICAÇÃO</span><div><span>${money(t.referralBase)}</span><span>×</span><span>${model.rates.referral}%</span><span>=</span><strong>${money(t.referral)}</strong></div><p>A base é o valor recebido nas carteiras indicadas diretamente, não a comissão dessas pessoas.</p></section><div class="drawer-note">${icon("people")}A indicação alcança apenas um nível. Carteiras de segundo nível não geram comissão para você.</div><button class="primary-button full" data-action="open-referrals">Ver minhas comissões ${icon("arrow")}</button>`,
    );
  }

  function rules() {
    drawer(
      "Regras do demonstrativo",
      "O que orienta este mockup",
      `<div class="rules-section"><h3>Comissão sobre pagamentos</h3><p>Vendas próprias geram ${model.rates.own}% do valor efetivamente pago. Indicações diretas geram ${model.rates.referral}% dos pagamentos da carteira do indicado. Apenas um nível é elegível. O gestor recebe ${model.rates.manager}% sobre toda a operação, sem duplicar boletos.</p></div><div class="rules-section"><h3>Atualização com D-3</h3><p>O exemplo usa 13/09/2026 como referência e pagamentos até 10/09/2026. A definição final de dias úteis ou corridos ainda precisa ser validada.</p></div><div class="rules-section"><h3>Saúde da carteira</h3><p>Até 30 dias de atraso: primeira faixa de inadimplência. De 31 a 60 dias: segunda faixa. A partir de 61 dias: Inativo Jurídico. Essa situação não equivale automaticamente a cancelamento.</p></div><div class="rules-section"><h3>Fechamento mensal</h3><p>Apuração → conferência → confirmação → NF → provisionamento. O calendário de fechamento, incluindo a exceção para dia 1 em uma segunda-feira, permanece a validar.</p></div><div class="rules-section"><h3>Pontos para nossa próxima conversa</h3><p>Tratamento de estornos, pagamentos parciais, mudança de vendedor/indicador e data financeira usada na competência.</p></div><div class="drawer-note">${icon("info")}Todos os nomes, boletos e valores desta interface são demonstrativos. As ações não enviam dados para outros sistemas.</div>`,
    );
  }
  function download(august = false) {
    let lines = august
      ? [
          ["Origem", "Base recebida", "Taxa", "Comissão"],
          ["Vendas próprias", "18000,00", "7%", "1260,00"],
          ["Maria Oliveira - indicação direta", "9000,00", "2%", "180,00"],
          ["TOTAL", "", "", "1440,00"],
        ]
      : [
          ["Referência", "Origem", "Base recebida", "Taxa", "Comissão"],
          ...data.rows
            .filter((r) => r.status === "recebido")
            .map((r) => [
              r.id,
              r.name,
              r.value.toFixed(2).replace(".", ","),
              model.rates.own + "%",
              r.commission.toFixed(2).replace(".", ","),
            ]),
          ...model.children("joao").map((p) => [
            "IND-" + p.id,
            p.name + " - indicação direta",
            model.received(p.id).toFixed(2).replace(".", ","),
            model.rates.referral + "%",
            model
              .round((model.received(p.id) * model.rates.referral) / 100)
              .toFixed(2)
              .replace(".", ","),
          ]),
          ["TOTAL", "", "", "", totalCommission.toFixed(2).replace(".", ",")],
        ];
    const blob = new Blob(
      [
        "\ufeffDADOS DEMONSTRATIVOS - APROVEC\r\n" +
          lines
            .map((row) =>
              row.map((v) => `"${String(v).replace(/"/g, '""')}"`).join(";"),
            )
            .join("\r\n"),
      ],
      { type: "text/csv;charset=utf-8;" },
    );
    const url = URL.createObjectURL(blob),
      a = document.createElement("a");
    a.href = url;
    a.download = `aprovec-demonstrativo-${august ? "agosto" : "setembro"}-2026.csv`;
    a.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
    toast("Demonstrativo de exemplo baixado.");
  }

  root.addEventListener("click", (e) => {
    const b = e.target.closest("button");
    if (!b || b.disabled) return;
    if (network.click(b)) return;
    if (b.dataset.page) {
      navigate(b.dataset.page);
      return;
    }
    if (b.dataset.health) {
      state.filter = b.dataset.health;
      state.query = "";
      state.pagination = 1;
      navigate("wallet");
      return;
    }
    if (b.dataset.filter) {
      state.filter = b.dataset.filter;
      state.pagination = 1;
      render();
      return;
    }
    if (b.dataset.pagination) {
      state.pagination += b.dataset.pagination === "next" ? 1 : -1;
      document.getElementById("wallet-results").innerHTML = walletResults();
      return;
    }
    if (b.dataset.source) {
      state.source = b.dataset.source;
      render();
      return;
    }
    if (b.dataset.detail) {
      showDetail(b.dataset.detail);
      return;
    }
    switch (b.dataset.action) {
      case "rules":
        rules();
        break;
      case "referral":
        referral();
        break;
      case "close-drawer":
        closeDrawer();
        break;
      case "open-referrals":
        closeDrawer();
        state.source = "indicacoes";
        navigate("commissions");
        break;
      case "clear":
        state.filter = "todos";
        state.query = "";
        state.pagination = 1;
        render();
        break;
      case "confirm":
        if (document.getElementById("confirm-check")?.checked) {
          state.closing = 1;
          render();
          toast("Demonstrativo confirmado nesta simulação.");
        }
        break;
      case "select-nf":
        state.selectedNF = !state.selectedNF;
        render();
        break;
      case "send-nf":
        if (state.selectedNF) {
          state.closing = 3;
          render();
          toast("Simulação concluída: NF vinculada e comissão provisionada.");
        }
        break;
      case "reset-closing":
        state.closing = 0;
        state.selectedNF = false;
        render();
        break;
      case "export":
        download();
        break;
      case "august-export":
        download(true);
        break;
    }
  });
  root.addEventListener("input", (e) => {
    network.input(e);
    if (e.target.id === "wallet-search") {
      state.query = e.target.value;
      state.pagination = 1;
      document.getElementById("wallet-results").innerHTML = walletResults();
    }
  });
  root.addEventListener("submit", (e) => network.submit(e));
  root.addEventListener("change", (e) => {
    network.change(e);
    if (e.target.id === "profile-select") {
      setRole(e.target.value);
      return;
    }
    if (e.target.id === "confirm-check")
      document.getElementById("confirm-button").disabled = !e.target.checked;
  });
  document.addEventListener("keydown", (e) => {
    if (!state.drawer) return;
    if (e.key === "Escape") {
      closeDrawer();
      return;
    }
    if (e.key === "Tab") {
      const focusable = [
        ...root.querySelectorAll(
          ".drawer button:not([disabled]),.drawer input,.drawer select,.drawer a",
        ),
      ];
      const first = focusable[0],
        last = focusable[focusable.length - 1];
      if (e.shiftKey && document.activeElement === first) {
        e.preventDefault();
        last.focus();
      } else if (!e.shiftKey && document.activeElement === last) {
        e.preventDefault();
        first.focus();
      }
    }
  });
  render();
})();
