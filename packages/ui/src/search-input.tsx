import { useRef, useState, type InputHTMLAttributes } from 'react';

type Props = Omit<InputHTMLAttributes<HTMLInputElement>, 'onChange' | 'onSearch' | 'value'> & {
  value: string;
  onSearch(value: string): void;
};

/** Show IME drafts immediately, but only search for text the user has committed. */
export function SearchInput({ value, onSearch, ...props }: Props) {
  const composing = useRef(false);
  const [draft, setDraft] = useState<string | null>(null);
  return <input {...props} value={draft ?? value}
    onCompositionStart={event => { composing.current = true; setDraft(event.currentTarget.value); }}
    onCompositionEnd={event => {
      composing.current = false;
      setDraft(null);
      if (event.currentTarget.value !== value) onSearch(event.currentTarget.value);
    }}
    onChange={event => {
      const text = event.currentTarget.value;
      if (composing.current) setDraft(text);
      else if (text !== value) onSearch(text);
    }} />;
}
