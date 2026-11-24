import { approvalApi } from './approvalApi';

const BASE = import.meta.env.BASE_URL.endsWith('/') ? import.meta.env.BASE_URL : `${import.meta.env.BASE_URL}/`;

export type PushState = 'unsupported' | 'blocked' | 'off' | 'on' | 'unavailable';

export function pushSupported(): boolean {
  return typeof window !== 'undefined' && 'serviceWorker' in navigator && 'PushManager' in window && 'Notification' in window;
}

/** Registers the service worker (also what makes the site installable as an app). Safe to call repeatedly. */
export async function registerServiceWorker(): Promise<ServiceWorkerRegistration | null> {
  if (!('serviceWorker' in navigator)) return null;
  try {
    return await navigator.serviceWorker.register(`${BASE}sw.js`, { scope: BASE });
  } catch {
    return null;
  }
}

function urlBase64ToUint8Array(base64: string): Uint8Array<ArrayBuffer> {
  const padding = '='.repeat((4 - (base64.length % 4)) % 4);
  const raw = atob((base64 + padding).replace(/-/g, '+').replace(/_/g, '/'));
  const out = new Uint8Array(new ArrayBuffer(raw.length));
  for (let i = 0; i < raw.length; i++) out[i] = raw.charCodeAt(i);
  return out;
}

function toBase64Url(buffer: ArrayBuffer | null): string {
  if (!buffer) return '';
  let s = '';
  new Uint8Array(buffer).forEach((b) => (s += String.fromCharCode(b)));
  return btoa(s).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

export async function getPushState(): Promise<PushState> {
  if (!pushSupported()) return 'unsupported';
  if (Notification.permission === 'denied') return 'blocked';
  const key = await approvalApi.vapidPublicKey();
  if (!key) return 'unavailable';
  const reg = await registerServiceWorker();
  const sub = reg ? await reg.pushManager.getSubscription() : null;
  return sub && Notification.permission === 'granted' ? 'on' : 'off';
}

/** Ask for permission, subscribe this browser/app to push and tell the server. Returns the new state. */
export async function enablePush(label?: string): Promise<PushState> {
  if (!pushSupported()) return 'unsupported';
  const key = await approvalApi.vapidPublicKey();
  if (!key) return 'unavailable';

  const permission = await Notification.requestPermission();
  if (permission !== 'granted') return permission === 'denied' ? 'blocked' : 'off';

  const reg = await registerServiceWorker();
  if (!reg) return 'unsupported';
  await navigator.serviceWorker.ready;

  const sub =
    (await reg.pushManager.getSubscription()) ??
    (await reg.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: urlBase64ToUint8Array(key) }));

  await approvalApi.savePushSubscription({
    endpoint: sub.endpoint,
    p256dh: toBase64Url(sub.getKey('p256dh')),
    auth: toBase64Url(sub.getKey('auth')),
    label: label ?? navigator.userAgent.slice(0, 90),
  });
  return 'on';
}

export async function disablePush(): Promise<PushState> {
  const reg = await registerServiceWorker();
  const sub = reg ? await reg.pushManager.getSubscription() : null;
  if (sub) {
    await approvalApi.removePushSubscription(sub.endpoint).catch(() => undefined);
    await sub.unsubscribe();
  }
  return 'off';
}
