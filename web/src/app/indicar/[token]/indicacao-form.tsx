'use client';

import { useActionState, useState } from 'react';
import type { FormState } from '@/lib/form-state';
import { solicitarCadastro } from './actions';

type ViaCepResponse = { logradouro?: string; bairro?: string; localidade?: string; uf?: string; erro?: boolean };

export function IndicacaoForm({ token }: { token: string }) {
  const [state, formAction, pending] = useActionState<FormState, FormData>(solicitarCadastro, {});
  const [endereco, setEndereco] = useState({ logradouro: '', bairro: '', cidade: '', estado: '' });

  async function onCepBlur(event: React.FocusEvent<HTMLInputElement>) {
    const cep = event.target.value.replace(/\D/g, '');
    if (cep.length !== 8) return;
    try {
      const response = await fetch(`https://viacep.com.br/ws/${cep}/json/`);
      const data: ViaCepResponse = await response.json();
      if (data.erro) return;
      setEndereco({ logradouro: data.logradouro ?? '', bairro: data.bairro ?? '', cidade: data.localidade ?? '', estado: data.uf ?? '' });
    } catch {
      // CEP autopreenchimento é uma conveniência -- se a ViaCEP estiver fora do ar, a pessoa
      // ainda pode digitar o endereço manualmente nos campos abaixo.
    }
  }

  if (state.message) {
    return <p className="success">{state.message}</p>;
  }

  return (
    <form action={formAction} className="form">
      <input type="hidden" name="token" value={token} />
      <label>
        Nome completo
        <input name="nome" type="text" autoComplete="name" required />
      </label>
      <label>
        CPF
        <input name="cpf" type="text" autoComplete="off" required />
      </label>
      <label>
        Celular
        <input name="celular" type="tel" autoComplete="tel" required />
      </label>
      <label>
        E-mail
        <input name="email" type="email" autoComplete="email" required />
      </label>
      <label>
        CEP
        <input name="cep" type="text" autoComplete="postal-code" onBlur={onCepBlur} required />
      </label>
      <label>
        Endereço
        <input name="logradouro" type="text" autoComplete="address-line1" defaultValue={endereco.logradouro} key={endereco.logradouro} required />
      </label>
      <label>
        Número
        <input name="numero" type="text" required />
      </label>
      <label>
        Complemento (opcional)
        <input name="complemento" type="text" autoComplete="address-line2" />
      </label>
      <label>
        Bairro
        <input name="bairro" type="text" defaultValue={endereco.bairro} key={`bairro-${endereco.bairro}`} required />
      </label>
      <label>
        Cidade
        <input name="cidade" type="text" defaultValue={endereco.cidade} key={`cidade-${endereco.cidade}`} required />
      </label>
      <label>
        Estado
        <input name="estado" type="text" defaultValue={endereco.estado} key={`estado-${endereco.estado}`} required />
      </label>
      {state.error && <p role="alert" className="error">{state.error}</p>}
      <button type="submit" disabled={pending}>{pending ? 'Enviando…' : 'Solicitar cadastro'}</button>
    </form>
  );
}
