import { useEffect, useState } from 'react';
import { disablePush, enablePush, getPushState } from '../../services/pushClient';
import type { PushState } from '../../services/pushClient';

const COPY: Record<PushState, string> = {
  unsupported: 'This browser cannot show sign-in notifications. Install the app from the browser menu (Add to Home screen) or use Chrome.',
  blocked: 'Notifications are blocked for this site. Allow them in the browser settings to approve sign-ins from here.',
  unavailable: 'Sign-in notifications are not set up on this server yet.',
  off: 'Get a notification on this device when someone tries to sign in to your account, and approve it with one tap.',
  on: 'This device gets a notification when someone tries to sign in to your account.',
};

/** Turn "approve sign-ins from this device" on or off (Web Push). Shown on the sessions page. */
export default function PushToggle() {
  const [state, setState] = useState<PushState | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');

  useEffect(() => {
    let cancelled = false;
    getPushState()
      .then((s) => !cancelled && setState(s))
      .catch(() => !cancelled && setState('unavailable'));
    return () => {
      cancelled = true;
    };
  }, []);

  if (!state) return null;

  const toggle = async () => {
    setBusy(true);
    setError('');
    try {
      setState(state === 'on' ? await disablePush() : await enablePush());
    } catch (err: any) {
      setError(err.response?.data?.error || 'Could not change the notification setting.');
    } finally {
      setBusy(false);
    }
  };

  const actionable = state === 'on' || state === 'off';

  return (
    <div className="rounded-lg border border-gray-200 bg-white p-4 mb-6" data-testid="push-toggle">
      <h3 className="text-sm font-semibold text-gray-900">Approve sign-ins from this device</h3>
      <p className="mt-1 text-sm text-gray-600">{COPY[state]}</p>
      {error && <p className="mt-2 text-sm text-red-700">{error}</p>}
      {actionable && (
        <button
          type="button"
          disabled={busy}
          onClick={toggle}
          className="mt-3 px-3 py-2 rounded-md bg-indigo-600 text-white text-sm font-medium hover:bg-indigo-700 disabled:opacity-50"
        >
          {state === 'on' ? 'Turn off notifications' : 'Turn on notifications'}
        </button>
      )}
    </div>
  );
}
