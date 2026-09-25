'use client';

import { useActionState } from 'react';
import type { FormState } from '@/lib/form-state';
import type { HinovaVoluntario, UserSummary } from '@/lib/types';
import { desvincularVoluntario, vincularVoluntario } from './actions';

export function VincularForm({ voluntario, usuarios }: { voluntario: HinovaVoluntario; usuarios: UserSummary[] }) {
  const [state, formAction, pending] = useActionState<FormState, FormData>(vincularVoluntario, {});

  // A confirmação de sucesso precisa vir do próprio componente, não de um branch no pai
  // (page.tsx alterna entre este form e o badge "Vinculado a ..." com base em
  // `voluntario.jaVinculado`). O vincularVoluntario chama revalidatePath, então o pai
  // re-renderiza com `jaVinculado: true` quase no mesmo instante em que a action
  // resolve — se o pai decidisse a troca, este form desmontaria antes do usuário
  // conseguir ver "Voluntário vinculado.". Mantendo o componente sempre montado (o pai
  // sempre renderiza <VincularForm>, nunca o badge diretamente) e decidindo aqui, o
  // `state.message` do useActionState sobrevive ao re-render do pai.
  if (state.message) {
    return <p className="success">{state.message}</p>;
  }

  if (voluntario.jaVinculado) {
    return <span className="badge neutral">Vinculado a {voluntario.vinculadoA}</span>;
  }

  return (
    <form action={formAction} className="inline">
      <input type="hidden" name="codigoVoluntario" value={voluntario.codigo} />
      <input type="hidden" name="nomeHinova" value={voluntario.nome} />
      <input type="hidden" name="cpfHinova" value={voluntario.cpf} />
      <label>
        Usuário APROVEC
        <select name="userId" required defaultValue="">
          <option value="" disabled>Selecione</option>
          {usuarios.map((u) => (
            <option key={u.id} value={u.id}>{u.name}</option>
          ))}
        </select>
      </label>
      <button type="submit" disabled={pending}>{pending ? 'Vinculando…' : 'Vincular'}</button>
      {state.error && <p role="alert" className="error">{state.error}</p>}
    </form>
  );
}

export function DesvincularForm({ userId }: { userId: string }) {
  const [state, formAction, pending] = useActionState<FormState, FormData>(desvincularVoluntario, {});
  return (
    <form action={formAction}>
      <input type="hidden" name="userId" value={userId} />
      <button type="submit" className="secondary" disabled={pending}>{pending ? 'Removendo…' : 'Desvincular'}</button>
      {state.error && <p role="alert" className="error">{state.error}</p>}
    </form>
  );
}
