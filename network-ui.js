window.createNetworkUI = function (h) {
  "use strict";
  const n = window.aprovecNetwork,
    { money, icon, esc, badge, drawer, toast, navigate } = h;
  const ui = {
    zoom: 1,
    collapsed: [],
    root: "",
    adminTree: false,
    selected: "joao",
    treeMode: "tree",
    peopleQuery: "",
    billQuery: "",
    billFilter: "todos",
    owner: "todos",
    page: 1,
  };
  const initials = (name) =>
    esc(
      name
        .split(" ")
        .slice(0, 2)
        .map((v) => v[0])
        .join(""),
    );
  const pct = (v) => Number(v).toLocaleString("pt-BR") + "%";
  const avatar = (p) =>
    `<span class="avatar network-avatar ${p.role === "manager" ? "manager-avatar" : ""}">${initials(p.name)}</span>`;
  const personLabel = (p) =>
    `<span class="network-person">${avatar(p)}<span><strong>${esc(p.name)}</strong><small>${p.role === "manager" ? "Gestão de toda a base" : esc(p.email)}</small></span></span>`;
  const btn = (label, action, cls = "secondary-button") =>
    `<button class="${cls}" data-network="${action}">${icon(action === "new-person" ? "people" : "arrow")}${label}</button>`;
  function head(title, subtitle, admin = false, action = "") {
    return `<div class="page-heading network-heading"><div><div class="eyebrow">${admin ? "ADMINISTRAÇÃO COMERCIAL" : "PAINEL DO GESTOR"}</div><h1>${title}</h1><p>${subtitle}</p></div>${action || `<div class="period"><span class="period-label">COMPETÊNCIA</span><div class="period-value">${icon("calendar")}<strong>Setembro de 2026</strong>${badge("progress", "Em apuração")}</div></div>`}</div>${admin ? '<div class="network-local">Configurações demonstrativas · salvas neste navegador</div>' : `<div class="cutoff"><span class="live-dot"></span>Atualizado em 13/09/2026 às 08:00 <span>·</span> Pagamentos até <strong>10/09/2026</strong><button class="tiny-tag" data-action="rules">D-3 ${icon("info")}</button></div>`}`;
  }
  function managerBand() {
    const t = n.totals();
    return `<section class="commission-band manager-band"><div class="commission-total"><span class="overline">Minha comissão sobre toda a base</span><div class="hero-number">${money(t.manager)}</div><span class="subtle">${pct(n.rates.manager)} de cada pagamento confirmado</span></div><div class="manager-formula"><div><span>Recebimentos da operação</span><strong>${money(t.received)}</strong></div><span>×</span><div><span>Taxa de gestão</span><strong>${pct(n.rates.manager)}</strong></div></div><div class="manager-band-note">${icon("shield")}<span>Todos os vendedores.<br>Todos os níveis da árvore.</span></div></section>`;
  }
  function healthCards() {
    const t = n.totals();
    return `<div class="network-kpis">${[
      ["recebido", "Recebidos", t.received, "circlecheck"],
      ["atraso", "Em atraso", t.overdue, "clock"],
      ["avencer", "A vencer", t.upcoming, "calendar"],
      ["cancelado", "Cancelados", t.cancelled, "close"],
    ]
      .map(
        ([id, label, val, i]) =>
          `<button class="network-kpi ${id}" data-network="health" data-value="${id}"><span>${icon(i)}${label}${icon("arrow")}</span><strong>${money(val)}</strong><small>${n.rows.filter((r) => r.status === id).length} boletos ${id === "recebido" ? "· geram comissão" : ""}</small></button>`,
      )
      .join("")}</div>`;
  }
  function payrollTable(includeManager = true) {
    const people = includeManager ? n.people : n.sellers(),
      t = n.totals();
    return `<div class="table-scroll"><table class="network-table"><thead><tr><th>Participante</th><th class="numeric">Base própria recebida</th><th class="numeric">Própria · ${pct(n.rates.own)}</th><th class="numeric">Indicação · ${pct(n.rates.referral)}</th><th class="numeric">Gestão · ${pct(n.rates.manager)}</th><th class="numeric">Total apurado</th><th></th></tr></thead><tbody>${people
      .map((p) => {
        const v = n.personTotals(p.id);
        return `<tr><td>${personLabel(p)}</td><td class="numeric">${p.role === "manager" ? "—" : money(v.received)}</td><td class="numeric">${p.role === "manager" ? "—" : money(v.own)}</td><td class="numeric">${p.role === "manager" ? "—" : money(v.referral)}</td><td class="numeric">${p.role === "manager" ? money(v.total) : "—"}</td><td class="numeric network-earned">${money(v.total)}</td><td><button class="icon-button" data-network="person-detail" data-id="${p.id}" aria-label="Ver comissão de ${esc(p.name)}">${icon("chevron")}</button></td></tr>`;
      })
      .join(
        "",
      )}</tbody>${includeManager ? `<tfoot><tr><td>Total da operação</td><td class="numeric">${money(t.received)}</td><td class="numeric">${money(t.own)}</td><td class="numeric">${money(t.referral)}</td><td class="numeric">${money(t.manager)}</td><td class="numeric network-earned">${money(t.payroll)}</td><td></td></tr></tfoot>` : ""}</table></div>`;
  }
  function managerOverview() {
    const t = n.totals();
    return `${head("A recorrência de toda a base", "Acompanhe a operação e o resultado de cada carteira.")}${managerBand()}<div class="section-heading"><h2>Saúde consolidada da base</h2><span class="section-meta">${n.sellers().length} vendedores · ${n.rows.length} boletos</span></div>${healthCards()}<div class="network-overview-lower"><section class="panel"><div class="panel-header"><div><h2>Contribuição por carteira</h2><p>A mesma taxa, aplicada uma única vez a cada pagamento</p></div></div><div class="contribution-list">${n
      .sellers()
      .map((p) => {
        const v = n.received(p.id);
        return `<button data-network="owner-bills" data-id="${p.id}" class="contribution-row">${personLabel(p)}<div class="contribution-track"><span style="width:${(v / t.received) * 100}%"></span></div><div><strong>${money(v)}</strong><small>Recebidos na carteira</small></div><div class="network-earned"><strong>${money(n.round((v * n.rates.manager) / 100))}</strong><small>Para você · ${pct(n.rates.manager)}</small></div>${icon("chevron")}</button>`;
      })
      .join(
        "",
      )}</div></section><section class="closing-card network-global-card"><div class="closing-icon">${icon("people")}</div><div class="closing-eyebrow">REMUNERAÇÃO DA OPERAÇÃO</div><h2>${money(t.payroll)}</h2><p>Comissões de vendedores, indicadores e gestor neste período.</p><div class="global-breakdown"><span>Vendas próprias<strong>${money(t.own)}</strong></span><span>Indicações diretas<strong>${money(t.referral)}</strong></span><span>Gestão da base<strong>${money(t.manager)}</strong></span></div><button class="primary-button full" data-page="manager-payroll">Ver comissões por pessoa ${icon("arrow")}</button></section></div><div class="network-rule-strip">${icon("people")}<div><strong>Gestão global. Indicação direta.</strong><p>A taxa de gestão alcança todas as carteiras. A comissão de indicação remunera somente quem indicou diretamente o vendedor.</p></div><button class="text-button" data-page="manager-tree">Explorar a árvore ${icon("arrow")}</button></div>`;
  }
  function billRows() {
    const q = ui.billQuery.toLocaleLowerCase("pt-BR");
    return n.rows.filter(
      (r) =>
        (ui.billFilter === "todos" || r.status === ui.billFilter) &&
        (ui.owner === "todos" || r.ownerId === ui.owner) &&
        `${r.name} ${r.plate} ${r.id} ${n.person(r.ownerId).name}`
          .toLocaleLowerCase("pt-BR")
          .includes(q),
    );
  }
  const billStatus = (r) =>
    badge(
      r.status === "avencer" ? "neutral" : r.status,
      {
        recebido: "Recebido",
        atraso: "Em atraso",
        avencer: "A vencer",
        cancelado: "Cancelado",
      }[r.status],
    );
  function billResults() {
    const rows = billRows(),
      pages = Math.max(1, Math.ceil(rows.length / 8));
    ui.page = Math.min(ui.page, pages);
    const start = (ui.page - 1) * 8;
    return `<div class="table-scroll"><table class="network-table"><thead><tr><th>Associado / carteira</th><th>Vencimento</th><th class="numeric">Valor do boleto</th><th>Situação</th><th class="numeric">Sua comissão · ${pct(n.rates.manager)}</th><th></th></tr></thead><tbody>${
      rows
        .slice(start, start + 8)
        .map(
          (r) =>
            `<tr><td><strong>${esc(r.name)}</strong><small>${esc(r.plate)} · ${esc(n.person(r.ownerId).name)}</small></td><td>${r.due.split("-").reverse().join("/")}${r.days ? `<small class="late-days">${r.days} dias em atraso</small>` : ""}</td><td class="numeric">${money(r.value)}</td><td>${billStatus(r)}</td><td class="numeric ${r.status === "recebido" ? "network-earned" : ""}">${money(r.status === "recebido" ? n.round((r.value * n.rates.manager) / 100) : 0)}</td><td><button class="icon-button" data-network="bill-detail" data-id="${r.id}" aria-label="Ver boleto ${r.id}">${icon("chevron")}</button></td></tr>`,
        )
        .join("") ||
      '<tr><td colspan="6" class="empty-state">Nenhum boleto encontrado. Ajuste a busca ou os filtros.</td></tr>'
    }</tbody></table></div><div class="results-summary"><span>${rows.length} ${rows.length === 1 ? "boleto" : "boletos"} no filtro</span><span>Valor total <strong>${money(rows.reduce((s, r) => s + r.value, 0))}</strong></span><span>Comissão apurada <strong>${money(n.round((rows.filter((r) => r.status === "recebido").reduce((s, r) => s + r.value, 0) * n.rates.manager) / 100))}</strong></span></div><div class="pagination"><span>${rows.length ? start + 1 : 0}–${Math.min(start + 8, rows.length)} de ${rows.length}</span><div><button class="icon-button" data-network="bill-prev" aria-label="Página anterior" ${ui.page === 1 ? "disabled" : ""}>${icon("back")}</button><span>Página ${ui.page} de ${pages}</span><button class="icon-button" data-network="bill-next" aria-label="Próxima página" ${ui.page === pages ? "disabled" : ""}>${icon("chevron")}</button></div></div>`;
  }
  function bills() {
    return `${head("Boletos da operação", "Consulte os pagamentos de todas as carteiras em um só lugar.")}<section class="panel"><div class="network-bill-toolbar"><div class="filter-tabs" role="group" aria-label="Situação dos boletos">${[
      ["todos", "Todos"],
      ["recebido", "Recebidos"],
      ["atraso", "Em atraso"],
      ["avencer", "A vencer"],
      ["cancelado", "Cancelados"],
    ]
      .map(
        ([id, l]) =>
          `<button class="filter-tab ${ui.billFilter === id ? "selected" : ""}" data-network="bill-filter" data-value="${id}" aria-pressed="${ui.billFilter === id}">${l}<span>${id === "todos" ? n.rows.length : n.rows.filter((r) => r.status === id).length}</span></button>`,
      )
      .join(
        "",
      )}</div><div class="network-filter-row"><label class="search-field">${icon("search")}<input type="search" id="network-bill-search" placeholder="Buscar associado, placa ou vendedor" aria-label="Buscar boletos da operação" value="${esc(ui.billQuery)}"></label><label class="network-select-label">Carteira<select id="network-owner"><option value="todos">Todas as carteiras</option>${n
      .sellers()
      .map(
        (p) =>
          `<option value="${p.id}" ${ui.owner === p.id ? "selected" : ""}>${esc(p.name)}</option>`,
      )
      .join(
        "",
      )}</select></label></div></div><div id="network-bill-results">${billResults()}</div></section>`;
  }
  function managerPayroll() {
    const s = h.closingState(),
      aug = [
        {
          name: n.person("joao").name,
          value: 1440,
          status:
            s === 3
              ? "Provisionado"
              : s > 0
                ? "Aguardando NF"
                : "Aguardando conferência",
        },
        { name: n.person("maria").name, value: 710, status: "Aguardando NF" },
        { name: n.person("pedro").name, value: 280, status: "Provisionado" },
        { name: n.person("gestor").name, value: 310, status: "Aguardando NF" },
      ];
    return `${head("Comissões por pessoa", "Confira a origem de cada valor e acompanhe os fechamentos.")}<section class="panel"><div class="panel-header"><div><h2>Demonstrativo da operação</h2><p>Setembro de 2026 · sobre pagamentos confirmados</p></div>${btn("Baixar demonstrativo", "export-payroll")}</div>${payrollTable()}</section><section class="panel network-history"><div class="panel-header"><div><h2>Fechamentos de agosto</h2><p>Exemplo de acompanhamento · valores históricos com 7% / 2% / 1%</p></div><span class="status neutral">Provisionado não significa pago</span></div><div class="history-summary"><div><small>Confirmados, incluindo provisionados</small><strong>${money(1300 + (s > 0 ? 1440 : 0))}</strong></div><div><small>Provisionados</small><strong>${money(280 + (s === 3 ? 1440 : 0))}</strong></div><div><small>Pagos</small><strong>${money(0)}</strong></div></div><div class="history-list">${aug.map((r) => `<div><strong>${esc(r.name)}</strong><span>${money(r.value)}</span>${badge(r.status === "Provisionado" ? "recebido" : "warning", r.status)}</div>`).join("")}</div></section>`;
  }
  const plusIcon = () =>
    '<svg class="icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" aria-hidden="true"><path d="M12 5v14M5 12h14"/></svg>';
  const treeLayout = () =>
    window.aprovecTreeLayout(n.sellers(), {
      collapsed: ui.collapsed,
      rootId: ui.root || null,
      newRoot: ui.adminTree,
    });
  function graph() {
    const l = treeLayout(),
      t = n.totals(),
      manager = n.person("gestor"),
      center = l.width / 2;
    const roots = l.nodes
      .filter((p) => p.depth === 0)
      .map((p) => ({ x: p.x + l.nodeWidth / 2, y: p.y }));
    if (l.newRoot)
      roots.push({ x: l.newRoot.x + l.nodeWidth / 2, y: l.newRoot.y });
    const globalLines = roots
      .map(
        (p) =>
          `<path class="global-connector" d="M${center} 136 V167 H${p.x} V${p.y}"/>`,
      )
      .join("");
    const directLines = l.edges
      .map(({ from, to }) => {
        const x = from.x + l.nodeWidth / 2,
          y = from.y + l.nodeHeight,
          toX = to.x + l.nodeWidth / 2,
          mid = y + 56;
        return `<path class="direct-connector" d="M${x} ${y} V${mid} H${toX} V${to.y}"/><rect class="connector-label-bg" x="${toX - 60}" y="${to.y - 24}" width="120" height="18" rx="5"/><text class="connector-label" x="${toX}" y="${to.y - 11}">${pct(n.rates.referral)} para ${esc(n.person(from.id).name.split(" ")[0].slice(0, 14))}</text>`;
      })
      .join("");
    return `<div class="pyramid-viewport" tabindex="0" role="region" aria-label="Árvores de indicação. Role para explorar as ramificações."><div class="pyramid-stage" style="width:${l.width * ui.zoom}px;height:${l.height * ui.zoom}px"><div class="pyramid-canvas" style="width:${l.width}px;height:${l.height}px;transform:scale(${ui.zoom})"><svg class="pyramid-lines" width="${l.width}" height="${l.height}" aria-hidden="true">${globalLines}${directLines}</svg><div class="pyramid-manager" style="left:${l.manager.x}px;top:${l.manager.y}px"><span class="pyramid-manager-avatar">${icon("shield")}</span><div><span>GESTÃO GLOBAL · ${pct(n.rates.manager)}</span><strong>${esc(manager.name)}</strong><small>Todas as árvores · ${money(t.received)} recebidos</small></div><b>${money(t.manager)}</b></div>${l.nodes
      .map((pos) => {
        const p = n.person(pos.id),
          v = n.personTotals(p.id);
        return `<div class="pyramid-node" data-person-id="${p.id}" style="left:${pos.x}px;top:${pos.y}px"><button class="pyramid-person ${ui.selected === p.id ? "selected" : ""}" data-network="select-node" data-id="${p.id}" aria-label="Ver ${esc(p.name)}" aria-pressed="${ui.selected === p.id}"><span class="pyramid-person-top">${avatar(p)}<span><strong>${esc(p.name)}</strong><small>${pos.depth === 0 ? "Topo da árvore" : "Carteira própria"}</small></span><span class="pyramid-rate">${pct(n.rates.own)}</span></span><span class="pyramid-person-value"><small>Recebidos na carteira</small><strong>${money(v.received)}</strong></span><span class="pyramid-person-total"><span>Comissão total</span><strong>${money(v.total)}</strong></span></button><div class="pyramid-node-actions">${ui.adminTree ? `<button class="pyramid-add" data-network="add-child" data-id="${p.id}" aria-label="Adicionar indicado a ${esc(p.name)}" title="Adicionar indicado a ${esc(p.name)}">${plusIcon()}</button>` : '<span class="pyramid-junction"></span>'}${pos.childCount ? `<button class="pyramid-collapse ${pos.collapsed ? "is-collapsed" : ""}" data-network="collapse-node" data-id="${p.id}" aria-expanded="${!pos.collapsed}" aria-label="${pos.collapsed ? "Expandir" : "Recolher"} ramo de ${esc(p.name)}">${icon("down")}<span>${pos.collapsed ? pos.descendants + " ocultos" : pos.childCount + " direto" + (pos.childCount === 1 ? "" : "s")}</span></button>` : ""}</div></div>`;
      })
      .join(
        "",
      )}${l.newRoot ? `<button class="pyramid-new-root" style="left:${l.newRoot.x}px;top:${l.newRoot.y}px" data-network="new-root"><span>${plusIcon()}</span><strong>Nova árvore</strong><small>Adicione um vendedor no topo<br>de uma nova ramificação</small><em>${pct(n.rates.own)} próprios · gestão global de ${pct(n.rates.manager)}</em></button>` : ""}</div></div></div>`;
  }
  function tree(admin = false) {
    const roots = n.sellers().filter((p) => !p.parentId);
    if (ui.root && !roots.some((p) => p.id === ui.root)) ui.root = "";
    return `${head("Árvore comissionada", admin ? "Construa cada ramificação, pessoa por pessoa." : "Explore as árvores de indicação de toda a operação.", admin, admin ? `<button class="primary-button" data-network="new-root">${plusIcon()}Nova árvore</button>` : "")}<section class="panel pyramid-panel"><div class="pyramid-header"><div><h2>Uma base. Várias árvores.</h2><p>${n.sellers().length} vendedores · ${roots.length} ${roots.length === 1 ? "árvore" : "árvores"} · ${admin ? "Use o + abaixo de cada pessoa para adicionar um indicado." : "Selecione uma pessoa para conferir sua remuneração."}</p></div><div class="segmented" role="group" aria-label="Visualização da estrutura"><button data-network="tree-mode" data-value="tree" class="${ui.treeMode === "tree" ? "selected" : ""}" aria-pressed="${ui.treeMode === "tree"}">${icon("people")}Árvore</button><button data-network="tree-mode" data-value="list" class="${ui.treeMode === "list" ? "selected" : ""}" aria-pressed="${ui.treeMode === "list"}">${icon("grid")}Lista</button></div></div>${ui.treeMode === "tree" ? `<div class="pyramid-toolbar"><label>${icon("people")}<select id="pyramid-root" aria-label="Árvore em exibição"><option value="">Todas as árvores</option>${roots.map((p) => `<option value="${p.id}" ${ui.root === p.id ? "selected" : ""}>Árvore de ${esc(p.name)}</option>`).join("")}</select></label><div class="pyramid-zoom"><button data-network="zoom-out" aria-label="Diminuir zoom" ${ui.zoom <= 0.4 ? "disabled" : ""}>−</button><span aria-live="polite">${Math.round(ui.zoom * 100)}%</span><button data-network="zoom-in" aria-label="Aumentar zoom" ${ui.zoom >= 1.4 ? "disabled" : ""}>+</button><button class="pyramid-fit" data-network="fit-tree">Enquadrar</button></div></div>${graph()}` : peopleTable(true)}<div class="pyramid-legend"><span><i class="legend-global"></i>Gestor: ${pct(n.rates.manager)} sobre toda a base</span><span><i class="legend-direct"></i>Indicador: ${pct(n.rates.referral)} sobre o nível direto</span><span><b>${pct(n.rates.own)}</b> Carteira própria de cada vendedor</span></div></section><div class="network-rule-strip">${icon("people")}<div><strong>Cada indicado pode formar a sua própria ramificação.</strong><p>A árvore cresce em novos níveis. Cada pessoa recebe ${pct(n.rates.own)} sobre sua carteira e ${pct(n.rates.referral)} sobre as carteiras de seus indicados diretos.</p></div></div>`;
  }
  function refreshTree(zoom = ui.zoom) {
    const v = document.querySelector(".pyramid-viewport"),
      old = ui.zoom,
      cx = v ? (v.scrollLeft + v.clientWidth / 2) / old : 0,
      cy = v ? (v.scrollTop + v.clientHeight / 2) / old : 0;
    ui.zoom = zoom;
    h.render();
    const next = document.querySelector(".pyramid-viewport");
    if (v && next) {
      next.scrollLeft = cx * zoom - next.clientWidth / 2;
      next.scrollTop = cy * zoom - next.clientHeight / 2;
    }
  }
  function focusTreeNode(id) {
    const el = [...document.querySelectorAll(".pyramid-node")].find(
      (e) => e.dataset.personId === id,
    );
    if (el)
      el.scrollIntoView({
        behavior: "instant",
        block: "nearest",
        inline: "center",
      });
  }
  function inspectTreeNode(id) {
    ui.selected = id;
    refreshTree();
    personDetail(id);
    if (ui.adminTree) {
      const footer = document.createElement("div");
      footer.className = "pyramid-detail-actions";
      footer.innerHTML = `<button class="primary-button full" data-network="add-child" data-id="${id}">${plusIcon()}Adicionar indicado</button><button class="secondary-button full" data-network="edit-person" data-id="${id}">Editar cadastro e vínculo ${icon("arrow")}</button>`;
      document.querySelector(".drawer-content").append(footer);
    }
  }
  function peopleTable(treeList = false) {
    const people = n.people.filter(
      (p) =>
        (!treeList || p.role === "seller") &&
        (treeList ||
          `${p.name} ${p.email}`
            .toLocaleLowerCase("pt-BR")
            .includes(ui.peopleQuery.toLocaleLowerCase("pt-BR"))),
    );
    return `<div class="table-scroll"><table class="network-table"><thead><tr><th>Participante</th><th>Perfil</th><th>Indicador direto</th><th class="numeric">${treeList ? "Comissão apurada" : "Indicações diretas"}</th><th></th></tr></thead><tbody>${people.map((p) => `<tr><td>${personLabel(p)}</td><td>${badge(p.role === "manager" ? "neutral" : "progress", p.role === "manager" ? "Gestor da base" : "Vendedor")}</td><td>${p.role === "manager" ? "Toda a operação" : p.parentId ? esc(n.person(p.parentId).name) : "Sem indicador"}</td><td class="numeric">${treeList ? money(n.personTotals(p.id).total) : p.role === "manager" ? "—" : n.children(p.id).length}</td><td><button class="text-button" data-network="${treeList ? "select-node" : "edit-person"}" data-id="${p.id}" aria-label="${treeList ? "Selecionar" : "Editar"} ${esc(p.name)}">${treeList ? "Ver detalhe" : "Editar"} ${icon("arrow")}</button></td></tr>`).join("") || '<tr><td colspan="5" class="empty-state">Nenhum participante encontrado.</td></tr>'}</tbody></table></div>`;
  }
  function people() {
    return `${head("Participantes", "Cadastre as pessoas e defina quem indicou cada vendedor.", true, btn("Novo participante", "new-person", "primary-button"))}<div class="people-stats"><span><strong>${n.people.length}</strong> participantes</span><span><strong>${n.sellers().length}</strong> vendedores</span><span><strong>1</strong> gestor de toda a base</span></div><section class="panel"><div class="panel-header"><div><h2>Pessoas da operação</h2><p>Um indicador direto por vendedor</p></div><label class="search-field">${icon("search")}<input type="search" id="network-people-search" placeholder="Buscar nome ou e-mail" aria-label="Buscar participantes" value="${esc(ui.peopleQuery)}"></label></div><div id="network-people-results">${peopleTable()}</div></section><div class="network-rule-strip">${icon("info")}<div><strong>Novos participantes começam sem recebimentos.</strong><p>O cadastro define o vínculo de indicação. A comissão passa a existir quando houver pagamentos confirmados na carteira.</p></div></div>`;
  }
  function ratePreview(r) {
    const t = n.totals(r);
    return `<div><small>Vendas próprias</small><strong>${money(t.own)}</strong></div><span>+</span><div><small>Indicações diretas</small><strong>${money(t.referral)}</strong></div><span>+</span><div><small>Gestão da base</small><strong>${money(t.manager)}</strong></div><span>=</span><div class="rate-preview-total"><small>Remuneração total</small><strong>${money(t.payroll)}</strong></div>`;
  }
  function remuneration() {
    return `${head("Sistema de remuneração", "Defina as taxas e confira o impacto sobre os pagamentos da base.", true)}<form id="network-rate-form"><div class="rate-cards">${[
      [
        "own",
        "wallet",
        "Venda própria",
        "Carteira do próprio vendedor",
        "Cada vendedor recebe sobre os pagamentos da sua carteira.",
      ],
      [
        "referral",
        "people",
        "Indicação direta",
        "Carteira do indicado direto",
        "O percentual pertence ao indicador direto. Não se propaga pela árvore.",
      ],
      [
        "manager",
        "shield",
        "Gestão da base",
        "Todas as carteiras da operação",
        "O gestor participa de todos os pagamentos, uma única vez por boleto.",
      ],
    ]
      .map(
        ([id, i, title, base, desc]) =>
          `<section class="panel rate-card"><span class="rate-card-icon">${icon(i)}</span><h2>${title}</h2><p>${desc}</p><label class="rate-field" for="rate-${id}"><span class="sr-only">Percentual de ${title.toLowerCase()}</span><input type="number" id="rate-${id}" name="${id}" min="0" max="100" step="0.01" required value="${n.rates[id]}"><span>%</span></label><span class="rate-base-label">BASE DE CÁLCULO</span><strong class="rate-base">${base}</strong></section>`,
      )
      .join(
        "",
      )}</div><section class="panel rate-conditions"><div><span>${icon("circlecheck")}Pagamento confirmado</span><p>Somente valores efetivamente recebidos geram comissão.</p></div><div><span>${icon("people")}Profundidade: 1 nível</span><p>A indicação remunera apenas o vínculo direto.</p></div><div><span>${icon("calendar")}Competência mensal · D-3</span><p>O calendário final e a contagem de dias serão validados.</p></div></section><section class="panel rate-impact"><div class="panel-header"><div><h2>Prévia da remuneração</h2><p>Setembro · ${money(n.totals().received)} recebidos na base demonstrativa</p></div><span class="status neutral">Simulação</span></div><div class="rate-preview" id="network-rate-preview">${ratePreview(n.rates)}</div><div class="rate-save"><p>Ao salvar, a simulação de setembro é recalculada nos três perfis. O fechamento de agosto mantém seus valores históricos.</p><button class="primary-button" type="submit">Salvar remuneração ${icon("check")}</button></div><p class="form-error" id="network-rate-error" role="alert" hidden></p></section></form>`;
  }
  function personForm(id, defaultParent) {
    const p = id ? n.person(id) : null,
      parentId = p ? p.parentId : defaultParent;
    drawer(
      p
        ? "Editar participante"
        : defaultParent
          ? "Adicionar indicado"
          : defaultParent === null
            ? "Nova árvore"
            : "Novo participante",
      defaultParent
        ? "Indicação direta de " + esc(n.person(defaultParent).name)
        : "Cadastro da árvore comissionada",
      `<form id="network-person-form" data-id="${p?.id || ""}" class="network-form"><div class="form-context">${icon(p?.role === "manager" ? "shield" : "people")}<span>${p?.role === "manager" ? "Gestor da base · abrangência global" : "Vendedor · carteira própria e indicação direta"}</span></div><label>Nome completo<input name="name" autocomplete="off" minlength="3" maxlength="70" required value="${esc(p?.name || "")}" placeholder="Ex.: Ana Souza"></label><label>E-mail<input name="email" type="email" autocomplete="off" required maxlength="120" value="${esc(p?.email || "")}" placeholder="nome@exemplo.com"></label>${
        p?.role === "manager"
          ? '<div class="drawer-note">O gestor recebe sobre toda a operação e não pertence à cadeia de indicação.</div>'
          : `<label>Quem indicou este vendedor?<select name="parentId"><option value="">Sem indicador · origem da cadeia</option>${n
              .sellers()
              .filter((v) => v.id !== p?.id)
              .map(
                (v) =>
                  `<option value="${v.id}" ${parentId === v.id ? "selected" : ""}>${esc(v.name)}</option>`,
              )
              .join(
                "",
              )}</select><small>Somente essa pessoa receberá os ${pct(n.rates.referral)} de indicação sobre esta carteira.</small></label>`
      }<div class="drawer-note">${icon("info")}${p ? "A alteração do vínculo recalcula somente a simulação atual. Agosto permanece com os valores históricos." : "O participante entra na árvore sem boletos ou comissões. Este cadastro é salvo apenas neste navegador."}</div><p class="form-error" id="network-person-error" role="alert" hidden></p><div class="form-actions"><button class="secondary-button" type="button" data-action="close-drawer">Cancelar</button><button class="primary-button" type="submit">${p ? "Salvar alterações" : "Cadastrar participante"} ${icon("check")}</button></div></form>`,
    );
  }
  function personDetail(id) {
    const p = n.person(id),
      v = n.personTotals(id);
    drawer(
      "Composição da comissão",
      `${esc(p.name)} · Setembro de 2026`,
      `${personLabel(p)}<dl class="detail-list">${p.role === "manager" ? `<div><dt>Recebimentos de toda a base</dt><dd>${money(v.received)}</dd></div><div><dt>Taxa de gestão</dt><dd>${pct(n.rates.manager)}</dd></div>` : `<div><dt>Base própria recebida</dt><dd>${money(v.received)}</dd></div><div><dt>Comissão própria · ${pct(n.rates.own)}</dt><dd>${money(v.own)}</dd></div><div><dt>Base dos indicados diretos</dt><dd>${money(v.referralBase)}</dd></div><div><dt>Comissão de indicação · ${pct(n.rates.referral)}</dt><dd>${money(v.referral)}</dd></div>`}</dl><section class="calculation"><span class="eyebrow">TOTAL APURADO</span><div><strong>${money(v.total)}</strong></div><p>${p.role === "manager" ? "Recebimentos de toda a operação × taxa de gestão. Cada boleto pago entra uma única vez." : "Comissão da carteira própria + comissão das carteiras indicadas diretamente."}</p></section>${
        p.role === "seller"
          ? `<div class="rules-section"><h3>Indicações diretas</h3>${
              n
                .children(id)
                .map(
                  (c) =>
                    `<p>${esc(c.name)} · ${money(n.received(c.id))} × ${pct(n.rates.referral)} = ${money(n.round((n.received(c.id) * n.rates.referral) / 100))}</p>`,
                )
                .join("") || "<p>Nenhum indicado direto.</p>"
            }</div>`
          : ""
      }`,
    );
  }
  function billDetail(id) {
    const r = n.rows.find((v) => v.id === id);
    drawer(
      "Boleto da operação",
      `${r.id} · ${esc(n.person(r.ownerId).name)}`,
      `<div class="drawer-person"><div><h3>${esc(r.name)}</h3><p>${esc(r.plate)} · ${r.phone}</p></div></div><dl class="detail-list"><div><dt>Situação do boleto</dt><dd>${billStatus(r)}</dd></div><div><dt>Situação do associado</dt><dd>${r.associate}</dd></div><div><dt>Vencimento</dt><dd>${r.due.split("-").reverse().join("/")}</dd></div><div><dt>Pagamento confirmado</dt><dd>${r.paid ? r.paid.split("-").reverse().join("/") : "—"}</dd></div><div><dt>Valor do boleto</dt><dd>${money(r.value)}</dd></div></dl><section class="calculation"><span class="eyebrow">COMISSÃO DO GESTOR</span><div><span>${money(r.status === "recebido" ? r.value : 0)}</span><span>×</span><span>${pct(n.rates.manager)}</span><span>=</span><strong>${money(r.status === "recebido" ? n.round((r.value * n.rates.manager) / 100) : 0)}</strong></div><p>${r.status === "recebido" ? "Pagamento confirmado, elegível para gestão." : "Sem pagamento confirmado, não há comissão apurada."}</p></section>`,
    );
  }
  function exportPayroll() {
    const lines = [
      [
        "Participante",
        "Base própria",
        "Própria",
        "Indicação",
        "Gestão",
        "Total",
      ],
      ...n.people.map((p) => {
        const t = n.personTotals(p.id);
        return [
          p.name,
          p.role === "manager" ? 0 : t.received,
          t.own,
          t.referral,
          p.role === "manager" ? t.total : 0,
          t.total,
        ];
      }),
    ];
    const safe = (v) => {
      let s =
        typeof v === "number" ? v.toFixed(2).replace(".", ",") : String(v);
      if (/^[=+@-]/.test(s)) s = "'" + s;
      return '"' + s.replace(/"/g, '""') + '"';
    };
    const a = document.createElement("a"),
      url = URL.createObjectURL(
        new Blob(
          [
            "\ufeffDADOS DEMONSTRATIVOS - APROVEC\r\n" +
              lines.map((r) => r.map(safe).join(";")).join("\r\n"),
          ],
          { type: "text/csv;charset=utf-8" },
        ),
      );
    a.href = url;
    a.download = "aprovec-comissoes-operacao-setembro-2026.csv";
    a.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
    toast("Demonstrativo local da operação baixado.");
  }
  function render(page) {
    ui.adminTree = page === "admin-tree";
    return {
      "manager-overview": managerOverview,
      "manager-bills": bills,
      "manager-payroll": managerPayroll,
      "manager-tree": () => tree(false),
      "admin-tree": () => tree(true),
      "admin-people": people,
      "admin-rates": remuneration,
    }[page]?.();
  }
  function click(b) {
    const a = b.dataset.network;
    if (!a) return false;
    switch (a) {
      case "new-person":
        personForm();
        break;
      case "new-root":
        personForm(null, null);
        break;
      case "add-child":
        personForm(null, b.dataset.id);
        break;
      case "edit-person":
        personForm(b.dataset.id);
        break;
      case "person-detail":
        personDetail(b.dataset.id);
        break;
      case "bill-detail":
        billDetail(b.dataset.id);
        break;
      case "health":
        ui.billFilter = b.dataset.value;
        ui.owner = "todos";
        ui.billQuery = "";
        ui.page = 1;
        navigate("manager-bills");
        break;
      case "owner-bills":
        ui.owner = b.dataset.id;
        ui.billFilter = "recebido";
        ui.billQuery = "";
        ui.page = 1;
        navigate("manager-bills");
        break;
      case "bill-filter":
        ui.billFilter = b.dataset.value;
        ui.page = 1;
        h.render();
        break;
      case "bill-prev":
        ui.page--;
        document.getElementById("network-bill-results").innerHTML =
          billResults();
        break;
      case "bill-next":
        ui.page++;
        document.getElementById("network-bill-results").innerHTML =
          billResults();
        break;
      case "select-node":
        inspectTreeNode(b.dataset.id);
        break;
      case "collapse-node":
        ui.collapsed = ui.collapsed.includes(b.dataset.id)
          ? ui.collapsed.filter((id) => id !== b.dataset.id)
          : [...ui.collapsed, b.dataset.id];
        refreshTree();
        break;
      case "zoom-in":
        refreshTree(Math.min(1.4, Math.round((ui.zoom + 0.1) * 100) / 100));
        break;
      case "zoom-out":
        refreshTree(Math.max(0.4, Math.round((ui.zoom - 0.1) * 100) / 100));
        break;
      case "fit-tree": {
        const v = document.querySelector(".pyramid-viewport"),
          l = treeLayout();
        ui.zoom = Math.max(
          0.4,
          Math.min(
            1,
            (v.clientWidth - 32) / l.width,
            (v.clientHeight - 32) / l.height,
          ),
        );
        h.render();
        const next = document.querySelector(".pyramid-viewport");
        next.scrollLeft = Math.max(
          0,
          (next.scrollWidth - next.clientWidth) / 2,
        );
        next.scrollTop = 0;
        break;
      }
      case "tree-mode":
        ui.treeMode = b.dataset.value;
        h.render();
        break;
      case "export-payroll":
        exportPayroll();
        break;
    }
    return true;
  }
  function input(e) {
    if (e.target.id === "network-bill-search") {
      ui.billQuery = e.target.value;
      ui.page = 1;
      document.getElementById("network-bill-results").innerHTML = billResults();
    }
    if (e.target.id === "network-people-search") {
      ui.peopleQuery = e.target.value;
      document.getElementById("network-people-results").innerHTML =
        peopleTable();
    }
    if (e.target.closest("#network-rate-form")) {
      const form = e.target.form,
        values = Object.fromEntries(
          ["own", "referral", "manager"].map((k) => [
            k,
            Number(form.elements[k].value),
          ]),
        ),
        valid =
          Object.values(values).every(
            (v) => Number.isFinite(v) && v >= 0 && v <= 100,
          ) && Object.values(values).reduce((s, v) => s + v, 0) <= 100;
      document.getElementById("network-rate-preview").innerHTML = valid
        ? ratePreview(values)
        : "<p>Informe percentuais válidos para calcular a prévia.</p>";
    }
  }
  function change(e) {
    if (e.target.id === "pyramid-root") {
      ui.root = e.target.value;
      h.render();
      return;
    }
    if (e.target.id === "network-owner") {
      ui.owner = e.target.value;
      ui.page = 1;
      document.getElementById("network-bill-results").innerHTML = billResults();
    }
  }
  function submit(e) {
    const f = e.target;
    if (!["network-person-form", "network-rate-form"].includes(f.id)) return;
    e.preventDefault();
    try {
      if (f.id === "network-person-form") {
        const fd = new FormData(f),
          p = n.savePerson({
            id: f.dataset.id || undefined,
            name: fd.get("name"),
            email: fd.get("email"),
            parentId: fd.get("parentId"),
          });
        ui.selected = p.role === "seller" ? p.id : ui.selected;
        if (p.role === "seller") {
          let ancestor = p.parentId;
          while (ancestor) {
            ui.collapsed = ui.collapsed.filter((id) => id !== ancestor);
            ancestor = n.person(ancestor)?.parentId;
          }
          if (ui.adminTree) ui.root = "";
        }
        h.closeDrawer();
        h.render();
        if (ui.adminTree) focusTreeNode(p.id);
        toast(
          n.persistent
            ? "Participante salvo neste navegador."
            : "Participante salvo nesta sessão; armazenamento local indisponível.",
        );
      } else {
        n.saveRates(
          Object.fromEntries(
            ["own", "referral", "manager"].map((k) => [
              k,
              Number(f.elements[k].value),
            ]),
          ),
        );
        h.render();
        toast(
          n.persistent
            ? "Remuneração salva. Setembro foi recalculado nesta simulação."
            : "Remuneração aplicada nesta sessão; armazenamento local indisponível.",
        );
      }
    } catch (err) {
      const el = document.getElementById(
        f.id === "network-person-form"
          ? "network-person-error"
          : "network-rate-error",
      );
      el.textContent = err.message;
      el.hidden = false;
    }
  }
  function afterRender() {
    const v = document.querySelector(".pyramid-viewport");
    if (v) v.scrollLeft = Math.max(0, (v.scrollWidth - v.clientWidth) / 2);
  }
  return { render, click, input, change, submit, afterRender };
};
