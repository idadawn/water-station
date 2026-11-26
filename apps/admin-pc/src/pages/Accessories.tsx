import { useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import {
  dateFilterRange,
  money,
  payMethodLabel,
  shortDateTime,
  type AccessoryProduct,
  type AccessoryStatus,
  type DateFilter,
} from '@kunlun/shared';
import {
  AccessoryCheckout,
  Dialog,
  AccessorySettlement,
  Field,
  FormDialog,
  accessoryStatus,
  useApi,
  useDialogs,
  useLoader,
} from '@kunlun/ui';
import { DateFilterSelect, EmptyRow, Pager, StatCard, usePagedTable } from '../common';
import { useAdminData } from '../data';

export function Accessories() {
  const [tab, setTab] = useState<'sales' | 'products'>('sales');
  const { refresh } = useAdminData();
  const dialogs = useDialogs();
  const edit = (product?: AccessoryProduct) =>
    dialogs.open((close) => <ProductForm product={product} onClose={close} onDone={refresh} />);
  return (
    <>
      <div className="page-head">
        <div>
          <h2>配件销售</h2>
          <p>登记门店配件收款，核对配送待收款和退款。</p>
        </div>
        <div className="head-actions">
          {tab === 'sales' ? (
            <button type="button" className="btn primary" onClick={() => dialogs.open(close =>
              <Dialog title="登记配件销售" onClose={close}><AccessoryCheckout onSold={() => { close(); void refresh(); }} /></Dialog>)}>
              登记销售
            </button>
          ) : (
            <button type="button" className="btn primary" onClick={() => edit()}>
              新增配件
            </button>
          )}
        </div>
      </div>
      <div className="tabs" role="tablist">
        {(['sales', 'products'] as const).map((t) => (
          <button
            key={t}
            type="button"
            role="tab"
            aria-selected={tab === t}
            className={'tab' + (tab === t ? ' active' : '')}
            onClick={() => setTab(t)}
          >
            {t === 'sales' ? '销售流水' : '配件目录'}
          </button>
        ))}
      </div>
      {tab === 'sales' ? <Sales /> : <Catalog edit={edit} />}
    </>
  );
}

function Catalog({ edit }: { edit(p: AccessoryProduct): void }) {
  const api = useApi();
  const { version } = useAdminData();
  const products = useLoader(() => api.accessoryProducts(true), [api, version]);
  return (
    <div className="table-card">
      <table>
        <thead>
          <tr>
            <th>名称</th>
            <th>单位</th>
            <th>零售价</th>
            <th>状态</th>
            <th>操作</th>
          </tr>
        </thead>
        <tbody>
          {products.data?.map((p) => (
            <tr key={p.id}>
              <td>
                <b>{p.name}</b>
              </td>
              <td>{p.unit}</td>
              <td>¥ {money(p.price)}</td>
              <td>{p.active ? '在售' : '停售'}</td>
              <td>
                <button type="button" className="link" onClick={() => edit(p)}>
                  编辑
                </button>
              </td>
            </tr>
          ))}
          {!products.data?.length && (
            <EmptyRow colSpan={5}>{products.error || (products.data ? '暂无配件，可点击「新增配件」' : '正在加载…')}</EmptyRow>
          )}
        </tbody>
      </table>
    </div>
  );
}

function ProductForm({ product: p, onClose, onDone }: { product?: AccessoryProduct; onClose(): void; onDone(): Promise<void> }) {
  const api = useApi();
  const [name, setName] = useState(p?.name ?? '');
  const [unit, setUnit] = useState(p?.unit ?? '个');
  const [price, setPrice] = useState(p ? String(p.price) : '');
  const [active, setActive] = useState(p?.active ?? true);
  return (
    <FormDialog
      title={p ? '编辑配件' : '新增配件'}
      onClose={onClose}
      onSubmit={async () => {
        await api.saveAccessory(p?.id, { name, unit, price: Number(price), active });
        await onDone();
      }}
    >
      <Field label="配件名称" htmlFor="acc-name">
        <input className="input" id="acc-name" required maxLength={80} value={name} onChange={(e) => setName(e.target.value)} />
      </Field>
      <Field label="单位" htmlFor="acc-unit">
        <input className="input" id="acc-unit" required maxLength={12} value={unit} onChange={(e) => setUnit(e.target.value)} />
      </Field>
      <Field label="零售价（元）" htmlFor="acc-price">
        <input
          className="input"
          id="acc-price"
          type="number"
          min=".01"
          max="99999999"
          step=".01"
          required
          value={price}
          onChange={(e) => setPrice(e.target.value)}
        />
      </Field>
      <Field label="销售状态" htmlFor="acc-status">
        <select
          className="select"
          id="acc-status"
          value={active ? 'on' : 'off'}
          onChange={(e) => setActive(e.target.value === 'on')}
        >
          <option value="on">在售</option>
          <option value="off">停售</option>
        </select>
      </Field>
    </FormDialog>
  );
}

function Sales() {
  const api = useApi();
  const dialogs = useDialogs();
  const { refresh, version } = useAdminData();
  const [params] = useSearchParams();
  const [date, setDate] = useState<DateFilter>(() => {
    const start = params.get('start'),
      end = params.get('end');
    return start &&
      end &&
      [start, end].every((d) => /^\d{4}-\d{2}-\d{2}$/.test(d) && Number.isFinite(Date.parse(d))) &&
      start <= end
      ? { start, end }
      : 'ALL';
  });
  const range = dateFilterRange(date);
  const [status, setStatus] = useState<AccessoryStatus | ''>('');
  const table = usePagedTable((page) => api.accessorySales({ ...range, status: status || undefined, page }), [range, status]);
  const summary = useLoader(() => api.expenseSummary(range), [api, range.from, range.to, version]);
  const s = summary.data;
  return (
    <>
      <div className="stats-grid">
        <StatCard label="配件实收" icon="wallet" value={s ? '¥ ' + money(s.accessoryReceived) : '—'} foot="按实际收款日期统计" />
        <StatCard label="配件退款" icon="refresh" value={s ? '¥ ' + money(s.accessoryRefunded) : '—'} foot="按实际退款日期统计" />
        <StatCard label="配件净收入" icon="chart" value={s ? '¥ ' + money(s.accessoryNet) : '—'} foot="配件实收减去退款" />
      </div>
      {summary.error && <p role="alert">{summary.error}</p>}
      <div className="filter-card">
        <DateFilterSelect value={date} onChange={setDate} label="业务日期" />
        <select
          className="select"
          aria-label="销售状态"
          value={status}
          onChange={(e) => setStatus(e.target.value as AccessoryStatus | '')}
        >
          <option value="">全部状态</option>
          {Object.entries(accessoryStatus).map(([value, text]) => (
            <option key={value} value={value}>
              {text}
            </option>
          ))}
        </select>
      </div>
      <div className="table-card ledger-table">
        <div style={{ overflowX: 'auto' }}>
          <table>
            <thead>
              <tr>
                <th>销售单 / 水单</th>
                <th>顾客</th>
                <th>配件明细</th>
                <th>金额</th>
                <th>状态 / 收退款</th>
                <th>操作</th>
              </tr>
            </thead>
            <tbody>
              {table.data?.rows.map((r) => (
                <tr key={r.id}>
                  <td>
                    <b className="order-no">{r.saleNo}</b>
                    <br />
                    {r.orderNo && (
                      <small>
                        {r.orderNo}
                        <br />
                      </small>
                    )}
                    <small>
                      {shortDateTime(r.createdAt)} · {r.operatorName}
                    </small>
                  </td>
                  <td>{r.memberName || '散客'}</td>
                  <td>
                    {r.items.map((i) => (
                      <div key={i.productId}>
                        {i.name} × {i.quantity}
                        {i.unit} · ¥{money(i.price)}/{i.unit}
                      </div>
                    ))}
                  </td>
                  <td>¥ {money(r.amount)}</td>
                  <td>
                    <b>{accessoryStatus[r.status]}</b>
                    {r.paidAt && r.payMethod && (
                      <div>
                        收 · {shortDateTime(r.paidAt)} · {payMethodLabel(r.payMethod)} · {r.paidBy}
                      </div>
                    )}
                    {r.refundedAt && r.refundMethod && (
                      <div>
                        退 · {shortDateTime(r.refundedAt)} · {payMethodLabel(r.refundMethod)} · {r.refundedBy}
                        <br />
                        {r.refundReason}
                      </div>
                    )}
                  </td>
                  <td>
                    {(r.status === 'PAID' || r.status === 'PENDING') && (
                      <button
                        type="button"
                        className="link"
                        onClick={() =>
                          dialogs.open((close) => (
                            <AccessorySettlement
                              sale={r}
                              action={r.status === 'PAID' ? 'refund' : 'collect'}
                              onClose={close}
                              onDone={refresh}
                            />
                          ))
                        }
                      >
                        {r.status === 'PAID' ? '全额退款' : '确认收款'}
                      </button>
                    )}
                  </td>
                </tr>
              ))}
              {!table.data?.rows.length && (
                <EmptyRow colSpan={6}>{table.error || (table.data ? '该条件下暂无配件销售流水' : '正在加载…')}</EmptyRow>
              )}
            </tbody>
          </table>
        </div>
        <div className="table-footer">
          <span>共 {table.total} 笔</span>
          <Pager page={table.page} total={table.total} onChange={table.setPage} />
        </div>
      </div>
    </>
  );
}
