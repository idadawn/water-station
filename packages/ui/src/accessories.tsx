import { Stepper } from './stepper';
import { useState, type FormEvent } from 'react';
import {
  PAY_METHODS,
  money,
  payMethodLabel,
  type AccessoryBasket,
  type AccessoryProduct,
  type AccessorySale,
  type PayMethod,
  type MemberProfile,
} from '@kunlun/shared';
import { useApi } from './auth';
import { MemberPicker } from './operations/common';
import { Field, FormDialog, useToast } from './feedback';
import { newRequestKey, useLoader } from './hooks';

export const accessoryStatus = { PENDING: '待收款', PAID: '已收款', REFUNDED: '已全额退款', VOID: '已取消' };
export const emptyBasket = (): AccessoryBasket => ({ items: [], payment: 'WECHAT' });
export const accessoryAmount = (basket: AccessoryBasket) =>
  basket.items.reduce((sum, i) => sum + Math.round(i.price * 100) * i.quantity, 0) / 100;

export function AccessoryPicker({
  basket,
  onChange,
  delivery,
}: {
  basket: AccessoryBasket;
  delivery: boolean;
  onChange(b: AccessoryBasket): void;
}) {
  const api = useApi();
  const products = useLoader(() => api.accessoryProducts(), [api]);
  const setQty = (p: AccessoryProduct, qty: number) => {
    const other = basket.items.filter((i) => i.productId !== p.id);
    onChange({ ...basket, items: qty > 0 ? [...other, { productId: p.id, price: p.price, quantity: qty }] : other });
  };
  return (
    <div className="accessory-picker">
      {products.error ? (
        <p role="alert">
          {products.error}{' '}
          <button type="button" className="link" onClick={products.reload}>
            重新加载
          </button>
        </p>
      ) : !products.data ? (
        <p>正在读取配件…</p>
      ) : !products.data.length ? (
        <p>暂无在售配件</p>
      ) : (
        products.data.map((p) => (
          <div className="accessory-row" key={p.id}>
            <span>
              <b>{p.name}</b>
              <small>
                ¥{money(p.price)} / {p.unit}
              </small>
            </span>
            <Stepper
              id={'accessory-qty-' + p.id}
              label={p.name + '数量'}
              max={9999}
              value={basket.items.find((i) => i.productId === p.id)?.quantity ?? 0}
              onChange={(qty) => setQty(p, qty)}
            />
          </div>
        ))
      )}
      {basket.items.length > 0 && (
        <>
          <label className="accessory-row">
            <b>配件应收 ¥{money(accessoryAmount(basket))}</b>
            <select
              className="select"
              value={basket.payment}
              aria-label="配件收款方式"
              onChange={(e) => onChange({ ...basket, payment: e.target.value as AccessoryBasket['payment'] })}
            >
              {PAY_METHODS.map((p) => (
                <option key={p.value} value={p.value}>
                  {p.label}
                </option>
              ))}
              {delivery && <option value="COD">送达收款</option>}
            </select>
          </label>
        </>
      )}
    </div>
  );
}

export function AccessoryCheckout({ onSold }: { onSold?: () => void } = {}) {
  const api = useApi();
  const toast = useToast();
  const [basket, setBasket] = useState(emptyBasket);
  const [customer, setCustomer] = useState<'guest' | 'member'>('guest');
  const [member, setMember] = useState<MemberProfile | null>(null);
  const [key, setKey] = useState(newRequestKey);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [orderId, setOrderId] = useState('');
  const memberId = customer === 'member' ? member?.id : undefined;
  const deliveries = useLoader(async () => ({ memberId, orders: memberId ? await api.accessoryDeliveries(memberId) : [] }), [api, memberId]);
  const options = deliveries.data?.memberId === memberId ? deliveries.data?.orders ?? [] : [];
  const activeOrder = options.find(o => o.id === orderId);
  const payment = !activeOrder && basket.payment === 'COD' ? 'WECHAT' : basket.payment;
  const total = accessoryAmount(basket);

  async function submit(event: FormEvent) {
    event.preventDefault();
    if (busy || !basket.items.length || (customer === 'member' && !member)) return;
    setBusy(true); setError('');
    try {
      await api.sellAccessories({ memberId, orderId: activeOrder?.id, idempotencyKey: key, basket: { ...basket, payment } });
      toast((payment === 'COD' ? '已登记送达收款 · ¥' : '配件收款已登记 · ¥') + money(total));
      setBasket(emptyBasket()); setOrderId(''); setMember(null); setCustomer('guest'); setKey(newRequestKey());
      onSold?.();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  return <form onSubmit={submit} className="accessory-checkout">
    <fieldset disabled={busy}>
      <legend>购买顾客</legend>
      <div className="slot-chips">{(['guest', 'member'] as const).map(type =>
        <button type="button" key={type} aria-pressed={customer === type} className={customer === type ? 'active' : ''}
          onClick={() => { setCustomer(type); setOrderId(''); }}>{type === 'guest' ? '散客' : '会员'}</button>)}</div>
      {customer === 'member' && <MemberPicker id="accessory-member" value={member} onChange={m => { setMember(m); setOrderId(''); }} />}
    </fieldset>
    {memberId && options.length > 0 && <label>配送方式
      <select className="select" value={activeOrder?.id ?? ''} onChange={e => setOrderId(e.target.value)} disabled={busy}>
        <option value="">门店取件</option>
        {options.map(o => <option key={o.id} value={o.id}>随水配送 · {o.orderNo} · {o.address}</option>)}
      </select></label>}
    {memberId && deliveries.error && <p role="alert">配送单读取失败 <button type="button" className="link" onClick={deliveries.reload}>重试</button></p>}
    <fieldset disabled={busy}><legend>选择配件</legend><AccessoryPicker basket={{ ...basket, payment }} delivery={!!activeOrder} onChange={setBasket} /></fieldset>
    {error && <p className="account-error" role="alert">{error}</p>}
    <button type="submit" className="btn primary" disabled={busy || !basket.items.length || (customer === 'member' && !member)}>
      {busy ? '正在登记…' : (payment === 'COD' ? '登记送达收款 ¥' : '确认已收 ¥') + money(total)}
    </button>
  </form>;
}

type SettlementSale = Pick<AccessorySale, 'id' | 'saleNo' | 'amount' | 'payMethod'>;
export function AccessorySettlement({
  sale,
  action,
  onClose,
  onDone,
}: {
  sale: SettlementSale;
  action: 'collect' | 'refund';
  onClose(): void;
  onDone(): void | Promise<void>;
}) {
  const api = useApi();
  const [method, setMethod] = useState<PayMethod>(sale.payMethod ?? 'WECHAT');
  const [reason, setReason] = useState('');
  const refund = action === 'refund';
  return (
    <FormDialog
      title={refund ? '配件全额退款' : '确认收到配件款'}
      description={sale.saleNo}
      onClose={onClose}
      submitLabel={(refund ? '确认已退 ' : '确认已收 ') + '¥' + money(sale.amount)}
      onSubmit={async () => {
        await api.settleAccessory(sale.id, action, { payMethod: method, reason: refund ? reason : undefined });
        await onDone();
      }}
    >
      <Field label={refund ? '退款方式' : '收款方式'} htmlFor="accessory-pay">
        <select id="accessory-pay" className="select" value={method} onChange={(e) => setMethod(e.target.value as PayMethod)}>
          {PAY_METHODS.map((p) => (
            <option key={p.value} value={p.value}>
              {payMethodLabel(p.value)}
            </option>
          ))}
        </select>
      </Field>
      {refund && (
        <Field label="退款原因" htmlFor="accessory-reason">
          <input
            id="accessory-reason"
            className="input"
            required
            maxLength={500}
            value={reason}
            onChange={(e) => setReason(e.target.value)}
          />
        </Field>
      )}
    </FormDialog>
  );
}
