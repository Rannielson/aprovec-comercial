(() => {
  const names = ['Carlos Henrique','Fernanda Lima','Ricardo Alves','Juliana Costa','Marcos Pereira','Ana Beatriz','Paulo Roberto','Tatiane Martins','Bruno Moreira','Camila Ribeiro','Lucas Andrade','Patrícia Gomes','Rafael Nunes','Larissa Melo','Eduardo Rocha','Renata Souza','Gustavo Barros','Amanda Dias','Felipe Castro','Letícia Freitas'];
  const surnames = ['Almeida','Oliveira','Santos','Ferreira','Carvalho','Lopes'];
  const vehicles = ['Fiat Argo · 2022','Chevrolet Onix · 2023','Hyundai HB20 · 2021','Volkswagen Polo · 2024','Renault Kwid · 2023'];
  const referenceDate = new Date('2026-09-13T12:00:00Z');
  const rows = Array.from({length:106}, (_, i) => {
    const status = i < 90 ? 'recebido' : i < 102 ? 'atraso' : 'cancelado';
    const value = i < 70 || i >= 102 ? 200 : 300;
    const paidDay = String(10 - i % 10).padStart(2, '0');
    const lateDates = ['2026-09-04','2026-09-01','2026-08-28','2026-08-20','2026-08-15','2026-08-10','2026-08-05','2026-07-28','2026-07-20','2026-07-15','2026-07-10','2026-07-01'];
    const due = status === 'atraso' ? lateDates[i-90] : `2026-09-${String(5+i%6).padStart(2,'0')}`;
    const days = status === 'atraso' ? Math.round((referenceDate - new Date(`${due}T12:00:00Z`))/86400000) : 0;
    return {id:`APV-${String(i+1).padStart(4,'0')}`, name:`${names[i%20]} ${surnames[Math.floor(i/20)]}`, plate:`${['RZA','QYK','PFD','SOB','KLM'][i%5]}${i%10}${'ABCDEFGHJK'[Math.floor(i/10)%10]}${String(10+i%90).padStart(2,'0')}`, vehicle:vehicles[i%5], phone:'(81) 9••••-••••', value, status, due, days, paid:status==='recebido'?`2026-09-${paidDay}`:null, commission:status==='recebido'?Math.round(value*7)/100:0, associate:status==='cancelado'?'Cancelado':days>60?'Inativo Jurídico':days>30?'Inadimplente 31–60 dias':days>0?'Inadimplente até 30 dias':'Ativo'};
  });
  window.aprovecData = {rows, seller:'João Silva', referral:{name:'Maria Oliveira', received:10000, count:50, rate:.02, commission:200}, august:{own:18000, ownCommission:1260, referral:9000, referralCommission:180, total:1440}};
})();
