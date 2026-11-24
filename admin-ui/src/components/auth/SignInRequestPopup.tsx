import { useCallback, useEffect, useState } from 'react';
import { useAuth } from '../../context/AuthContext';
import ApprovalCard from './ApprovalCard';
import { approvalApi } from '../../services/approvalApi';
import type { ApprovalRequestInfo } from '../../services/approvalApi';

const POLL_MS = 5000;

/**
 * Mounted once for every signed-in screen. While a sign-in that names this account waits for approval (somebody typed
 * your e-mail address on a new device), it pops up here so you can approve from any tab or desktop you are already
 * signed in on. Polls only while the tab is visible.
 */
export default function SignInRequestPopup() {
  const { isAuthenticated } = useAuth();
  const [requests, setRequests] = useState<ApprovalRequestInfo[]>([]);
  const [dismissed, setDismissed] = useState<string[]>([]);

  const refresh = useCallback(async () => {
    try {
      setRequests(await approvalApi.pending());
    } catch {
      // not signed in any more or network trouble: the next poll tries again
    }
  }, []);

  useEffect(() => {
    if (!isAuthenticated) {
      setRequests([]);
      return;
    }

    void refresh();
    const timer = setInterval(() => {
      if (document.visibilityState === 'visible') void refresh();
    }, POLL_MS);
    const onVisible = () => {
      if (document.visibilityState === 'visible') void refresh();
    };
    document.addEventListener('visibilitychange', onVisible);
    return () => {
      clearInterval(timer);
      document.removeEventListener('visibilitychange', onVisible);
    };
  }, [isAuthenticated, refresh]);

  const current = requests.find((r) => !dismissed.includes(r.id));
  if (!isAuthenticated || !current) return null;

  return (
    <div
      className="fixed inset-0 z-50 flex items-end sm:items-center justify-center bg-black/40 px-4 py-4"
      role="dialog"
      aria-modal="true"
      aria-label="Sign-in request"
    >
      <div className="w-full max-w-md bg-white rounded-xl shadow-xl p-6 relative">
        <button
          type="button"
          onClick={() => setDismissed((d) => [...d, current.id])}
          className="absolute top-3 right-3 text-gray-400 hover:text-gray-700"
          aria-label="Close"
        >
          &times;
        </button>
        <ApprovalCard
          key={current.id}
          request={current}
          onDone={() => {
            setDismissed((d) => [...d, current.id]);
            void refresh();
          }}
        />
      </div>
    </div>
  );
}
