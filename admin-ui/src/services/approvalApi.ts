import { api } from './api';
import type { LoginResponse } from '../types';

export interface ApprovalStart {
  id: string;
  pollSecret: string;
  matchNumber: number;
  expiresAt: string;
}

export type ApprovalStatus = 'pending' | 'approved' | 'denied' | 'expired' | 'consumed';

export interface ApprovalRequestInfo {
  id: string;
  device: string;
  ip?: string | null;
  createdAt: string;
  expiresAt: string;
  /** The right number is one of these three; which one is only visible on the new device. */
  choices: number[];
  /** True when the request was started for this account (an e-mail address was typed); false for a QR scan. */
  namedYou: boolean;
}

/** The address a QR code / link has to open for a request: inside this app's base path, on this origin. */
export function approvalUrl(id: string): string {
  const base = import.meta.env.BASE_URL.endsWith('/') ? import.meta.env.BASE_URL : `${import.meta.env.BASE_URL}/`;
  return `${window.location.origin}${base}approve/${id}`;
}

const PENDING_APPROVE_KEY = 'iam.pendingApprove';

/** Remember a request the user was sent to approve while not signed in; the login page picks it up afterwards. */
export function rememberPendingApproval(id: string): void {
  try {
    sessionStorage.setItem(PENDING_APPROVE_KEY, id);
  } catch {
    // storage blocked: the user can open the link again after signing in
  }
}

export function takePendingApproval(): string | null {
  try {
    const id = sessionStorage.getItem(PENDING_APPROVE_KEY);
    if (id) sessionStorage.removeItem(PENDING_APPROVE_KEY);
    return id && /^[0-9a-f-]{36}$/i.test(id) ? id : null;
  } catch {
    return null;
  }
}

const client = () => api.getClient();

export const approvalApi = {
  async start(email: string | undefined, rememberMe: boolean): Promise<ApprovalStart> {
    const r = await client().post<ApprovalStart>('/auth/approval/request', { email: email?.trim() || null, rememberMe });
    return r.data;
  },

  async status(id: string, secret: string): Promise<ApprovalStatus> {
    const r = await client().post<{ status: ApprovalStatus }>(`/auth/approval/${id}/status`, { secret });
    return r.data.status;
  },

  /** Finishes the sign-in on the browser that started the request; stores the access token like a password login. */
  async complete(id: string, secret: string): Promise<LoginResponse> {
    const r = await client().post<LoginResponse>(`/auth/approval/${id}/complete`, { secret });
    if (r.data.accessToken) {
      localStorage.setItem('accessToken', r.data.accessToken);
    }
    return r.data;
  },

  async pending(): Promise<ApprovalRequestInfo[]> {
    const r = await client().get<ApprovalRequestInfo[]>('/auth/approval/pending');
    return r.data;
  },

  async get(id: string): Promise<ApprovalRequestInfo> {
    const r = await client().get<ApprovalRequestInfo>(`/auth/approval/${id}`);
    return r.data;
  },

  async approve(id: string, number: number): Promise<void> {
    await client().post(`/auth/approval/${id}/approve`, { number });
  },

  async deny(id: string): Promise<void> {
    await client().post(`/auth/approval/${id}/deny`);
  },

  async vapidPublicKey(): Promise<string | null> {
    try {
      const r = await client().get<{ publicKey: string }>('/push/vapid-public-key');
      return r.data.publicKey;
    } catch {
      return null;
    }
  },

  async savePushSubscription(sub: { endpoint: string; p256dh: string; auth: string; label?: string }): Promise<void> {
    await client().post('/push/subscriptions', sub);
  },

  async removePushSubscription(endpoint: string): Promise<void> {
    await client().post('/push/subscriptions/remove', { endpoint });
  },
};
