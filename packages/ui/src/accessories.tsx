import { Stepper } from './stepper';
import { useState } from 'react';
import {
  PAY_METHODS,
  money,
  payMethodLabel,
  type AccessoryBasket,
  type AccessoryProduct,
  type AccessorySale,
  type PayMethod,
} from '@kunlun/shared';
import { useApi } from './auth';
import { Field, FormDialog, useDialogs, useToast } from './feedback';
import { useIdempotencyKey, useLoader } from './hooks';

export const accessoryStatus = { PENDING: '待收款', PAID: '已收款', REFUNDED: '已全额退款', VOID: '已取消' };
export const emptyBasket = (): AccessoryBasket => ({ items: [], payment: 'WECHAT' });
export const accessoryAmount = (basket: AccessoryBasket) =>
  basket.items.reduce((sum, i) => sum + Math.round(i.price * 100) * i.quantity, 0) / 100;

export function AccessoryPicker({
  basket,
  onChange,
  delivery = false,
}: {
  basket: AccessoryBasket;
  onChange(b: AccessoryBasket): void;
  delivery?: boolean;
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

export function AccessoryOnly({
  memberId,
  name,
  onDone,
}: {
  memberId?: string;
  name?: string;
  onDone?: () => void | Promise<void>;
}) {
  const dialogs = useDialogs();
  return (
    <button
      type="button"
      className="btn"
      onClick={() => dialogs.open((close) => <StandaloneSale memberId={memberId} name={name} onDone={onDone} onClose={close} />)}
    >
      {memberId ? '只买配件' : '散客购买配件'}
    </button>
  );
}

function StandaloneSale({
  memberId,
  name,
  onDone,
  onClose,
}: {
  memberId?: string;
  name?: string;
  onDone?: () => void | Promise<void>;
  onClose(): void;
}) {
  const api = useApi();
  const toast = useToast();
  const key = useIdempotencyKey();
  const [basket, setBasket] = useState(emptyBasket);
  return (
    <FormDialog
      title="配件销售"
      description={name || '散客'}
      onClose={onClose}
      submitLabel={'确认已收 ¥' + money(accessoryAmount(basket))}
      onSubmit={async () => {
        if (!basket.items.length) throw new Error('请先选择配件数量');
        await api.sellAccessories({ memberId, idempotencyKey: key, basket });
        toast('配件收款已登记');
        await onDone?.();
      }}
    >
      <div className="accessory-fields">
        <AccessoryPicker basket={basket} onChange={setBasket} />
      </div>
    </FormDialog>
  );
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
