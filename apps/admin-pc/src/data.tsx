import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import {
  KUNLUN_PERMISSIONS as P, rememberProducts,
  type ContainerSummaryItem, type FinanceSummaryReport, type MemberProfile, type MemberQuery,
  type OverviewReport, type RechargePlan, type WaterOrder, type WaterProduct,
} from '@kunlun/shared';
import { useAuth, useToast } from '@kunlun/ui';

/** Members per page of the 会员管理 list. */
export const MEMBER_PAGE_SIZE = 20;
/** Newest orders kept for the dashboard and badges; the 水单管理 table pages on its own. */
const RECENT_ORDERS = 50;

export type MemberFilter = Pick<MemberQuery, 'status' | 'water'>;

export interface AdminSnapshot {
  /** The current page of the 会员管理 list (search, filters and page below). */
  members: MemberProfile[];
  memberTotal: number;
  plans: RechargePlan[];
  products: WaterProduct[];
  orders: WaterOrder[];
  orderTotal: number;
  containers: ContainerSummaryItem[];
  finance: FinanceSummaryReport | null;
  overview: OverviewReport | null;
  /** Refund requests waiting for review; null when this account cannot review any. */
  pendingRefunds: number | null;
}

interface AdminData extends AdminSnapshot {
  loaded: boolean;
  membersLoaded: boolean;
  memberError: string;
  /** Increments after every successful refresh so pages with their own queries can reload too. */
  version: number;
  memberQuery: string;
  setMemberQuery(query: string): void;
  memberFilter: MemberFilter;
  setMemberFilter(filter: MemberFilter): void;
  memberPage: number;
  setMemberPage(page: number): void;
  /** The member whose 360° profile was opened last; operation dialogs preselect them. */
  selectedMemberId: string | null;
  setSelectedMemberId(id: string | null): void;
  refresh(): Promise<void>;
}

const EMPTY: Omit<AdminSnapshot, 'members' | 'memberTotal'> = { plans: [], products: [], orders: [], orderTotal: 0, containers: [], finance: null, overview: null, pendingRefunds: null };
const AdminDataContext = createContext<AdminData | null>(null);

export function useAdminData() {
  const value = useContext(AdminDataContext);
  if (!value) throw new Error('AdminDataProvider is missing');
  return value;
}

export function AdminDataProvider({ children }: { children: ReactNode }) {
  const { api, can } = useAuth();
  const toast = useToast();
  const [snapshot, setSnapshot] = useState(EMPTY);
  const [loaded, setLoaded] = useState(false);
  const [version, setVersion] = useState(0);
  const [memberQuery, setQuery] = useState('');
  const [memberFilter, setFilter] = useState<MemberFilter>({});
  const [memberPage, setMemberPage] = useState(1);
  const [selectedMemberId, setSelectedMemberId] = useState<string | null>(null);
  const generation = useRef(0);
  const params = useMemo(() => ({ q: memberQuery.trim(), ...memberFilter, page: memberPage, pageSize: MEMBER_PAGE_SIZE }), [memberQuery, memberFilter, memberPage]);
  const latestParams = useRef(params);
  latestParams.current = params;
  const memberGeneration = useRef(0);
  const [memberResult, setMemberResult] = useState<{ params: typeof params; rows: MemberProfile[]; total: number; error?: string } | null>(null);

  const refreshMembers = useCallback(async () => {
    const requested = latestParams.current;
    const mine = ++memberGeneration.current;
    try {
      const result = await api.membersPage(requested);
      if (mine === memberGeneration.current && requested === latestParams.current) setMemberResult({ params: requested, rows: result.rows, total: result.total });
    } catch (error) {
      if (mine !== memberGeneration.current || requested !== latestParams.current) return;
      setMemberResult({ params: requested, rows: [], total: 0, error: error instanceof Error ? error.message : '会员查询失败' });
      throw error;
    }
  }, [api]);

  const refreshSummary = useCallback(async () => {
    const mine = ++generation.current;
    const reports = can(P.ReportExport);
    const reviews = can(P.DepositRefundApprove) || can(P.RechargeRefund);
    const [plans, orders, containers, finance, overview, products, pendingRefunds] = await Promise.all([
      api.listPlans(),
      api.ordersPage({ pageSize: RECENT_ORDERS }),
      api.containerSummary(),
      reports ? api.financeSummary() : Promise.resolve(null),
      reports ? api.overview() : Promise.resolve(null),
      api.listProducts(),
      reviews ? api.refundRequests({ status: 'PENDING', pageSize: 1 }).then(p => p.total) : Promise.resolve(null),
    ]);
    if (mine !== generation.current) return;
    rememberProducts(products);
    setSnapshot({ plans, products, orders: orders.rows, orderTotal: orders.total, containers, finance, overview, pendingRefunds });
    setLoaded(true);
    setVersion(v => v + 1);
  }, [api, can]);

  const refresh = useCallback(async () => {
    await Promise.all([refreshMembers(), refreshSummary()]);
  }, [refreshMembers, refreshSummary]);

  useEffect(() => { void refreshSummary().catch(e => toast(e.message, 'error')); }, [refreshSummary, toast]);
  useEffect(() => {
    const timer = window.setTimeout(() => refreshMembers().catch(e => toast(e.message, 'error')), params.q ? 200 : 0);
    return () => window.clearTimeout(timer);
  }, [params, refreshMembers, toast]);
  const currentMembers = memberResult?.params === params ? memberResult : null;

  // A new search or filter starts again from the first page.
  const setMemberQuery = useCallback((query: string) => { setQuery(query); setMemberPage(1); }, []);
  const setMemberFilter = useCallback((filter: MemberFilter) => { setFilter(filter); setMemberPage(1); }, []);

  return (
    <AdminDataContext.Provider value={{
      ...snapshot, loaded, version,
      members: currentMembers?.rows ?? [], memberTotal: currentMembers?.total ?? 0,
      membersLoaded: !!currentMembers && !currentMembers.error, memberError: currentMembers?.error ?? '',
      memberQuery, setMemberQuery, memberFilter, setMemberFilter, memberPage, setMemberPage,
      selectedMemberId, setSelectedMemberId, refresh,
    }}>
      {children}
    </AdminDataContext.Provider>
  );
}
