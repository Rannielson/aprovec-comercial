'use client';

import { useState, type InputHTMLAttributes } from 'react';

// Keep an unsubmitted filter in client state when fresh server data is merged.
export function LiveFilterInput({ initialValue, ...props }:
  Omit<InputHTMLAttributes<HTMLInputElement>, 'value' | 'defaultValue' | 'onChange'> & { initialValue: string }) {
  const [value, setValue] = useState(initialValue);
  return <input {...props} value={value} onChange={(event) => setValue(event.target.value)} />;
}
