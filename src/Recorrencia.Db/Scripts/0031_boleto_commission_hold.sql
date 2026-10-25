-- A payment that was previously attributed to one mapped volunteer can become
-- ambiguous when another volunteer on the same boleto is linked later. Keep the
-- payment and its vehicle evidence, but stop it from producing commission until
-- the attribution is unambiguous again.
alter table boletos add column commission_hold_reason text
  check (commission_hold_reason in ('unmapped', 'multiple_mapped_volunteers', 'payment_not_confirmed'));

create or replace function app.commission_source_boletos(p_competencia date, p_beneficiaries uuid[])
returns table (boleto_id uuid, participante_id uuid, valor numeric, pago_em date)
language plpgsql stable security definer set search_path = pg_catalog, public, app
as $$
#variable_conflict use_column
declare
  v_plan uuid;
  v_max_level int;
  v_has_global boolean;
begin
  perform app.assert_commission_access(p_competencia, p_beneficiaries);
  v_plan := app.commission_plan_for(p_competencia);
  if v_plan is null then return; end if;

  v_max_level := app.commission_max_level(v_plan);
  select exists (
    select 1
      from commission_rules r
      join commission_group_members m on m.group_id = r.group_id
     where r.plan_id = v_plan and r.type = 'global' and m.user_id = any (p_beneficiaries)
  ) into v_has_global;

  return query
    select b.id, b.participante_id, b.valor, b.pago_em
      from boletos b
     where b.tenant_id = app.current_tenant()
       and b.status = 'recebido'
       and b.commission_hold_reason is null
       and b.pago_em >= p_competencia
       and b.pago_em < (p_competencia + interval '1 month')::date
       and (
         v_has_global
         or b.participante_id in (
           select hp.descendant_id
             from hierarchy_paths hp
            where hp.tenant_id = app.current_tenant()
              and hp.ancestor_id = any (p_beneficiaries)
              and hp.depth <= v_max_level));
end
$$;
