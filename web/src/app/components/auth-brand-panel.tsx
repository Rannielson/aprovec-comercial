/** The red brand panel shown beside every access form (login, redefinir/definir senha). */
export function AuthBrandPanel() {
  return (
    <section className="login-brand">
      <div className="login-brand-shapes" aria-hidden="true">
        <span />
        <span />
        <span />
        <span />
      </div>
      <img className="login-brand-mark" src="/logo-aprovec.webp" alt="APROVEC Brasil" width={168} height={44} />
      <div className="login-brand-body">
        <p className="login-brand-eyebrow">Recorrência comercial</p>
        <h1>Sua rede. Sua comissão. Sempre à vista.</h1>
        <p>
          Acompanhe cada indicação da sua árvore comissionada e a origem de cada centavo recebido — tudo no mesmo
          painel, atualizado a cada pagamento confirmado.
        </p>
      </div>
      <div className="login-brand-stats">
        <div>
          <strong>15+</strong>
          <span>ANOS DE MERCADO</span>
        </div>
        <div>
          <strong>35 mil+</strong>
          <span>ASSOCIADOS</span>
        </div>
        <div>
          <strong>16</strong>
          <span>UNIDADES NO BRASIL</span>
        </div>
      </div>
    </section>
  );
}
