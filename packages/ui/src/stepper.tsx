import { NumberInput } from './feedback';

/**
 * Round takeout-style quantity control; the minus button and count appear once something is chosen.
 * With an `id` it is a form field: the count is always shown and can also be typed in.
 */
export function Stepper({ id, value, onChange, min = 0, max, label, showZero = false }: {
  id?: string;
  value: number;
  onChange: (value: number) => void;
  min?: number;
  max?: number;
  label: string;
  showZero?: boolean;
}) {
  const shown = showZero || id !== undefined || value > 0;
  const set = (n: number) => onChange(Math.min(max ?? Infinity, Math.max(min, Math.trunc(n) || 0)));
  return (
    <div className="stepper">
      {shown && <button type="button" className="stepper-minus" aria-label={`减少${label}`} disabled={value <= min} onClick={() => set(value - 1)}>−</button>}
      {shown && (id === undefined
        ? <span aria-label={label}>{value}</span>
        : <NumberInput id={id} aria-label={label} className="stepper-input" inputMode="numeric" min={min} max={max} step={1} required value={value} onChange={set} />)}
      <button type="button" className="stepper-plus" aria-label={`增加${label}`} disabled={max !== undefined && value >= max} onClick={() => set(value + 1)}>+</button>
    </div>
  );
}
