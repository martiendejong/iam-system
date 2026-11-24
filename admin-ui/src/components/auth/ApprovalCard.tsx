import { useEffect, useState } from 'react';
import { approvalApi } from '../../services/approvalApi';
import type { ApprovalRequestInfo } from '../../services/approvalApi';

interface ApprovalCardProps {
  request: ApprovalRequestInfo;
  /** Called once the request is decided (approved, denied or no longer available). */
  onDone: (outcome: 'approved' | 'denied' | 'gone') => void;
}

function secondsLeft(expiresAt: string): number {
  return Math.max(0, Math.round((new Date(expiresAt).getTime() - Date.now()) / 1000));
}

/**
 * One sign-in request to approve: where it comes from, and three numbers. The right one is the number the new device
 * is showing; choosing it is the approval. Used by the approve page (QR / push) and by the popup in a signed-in tab.
 */
export default function ApprovalCard({ request, onDone }: ApprovalCardProps) {
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const [left, setLeft] = useState(() => secondsLeft(request.expiresAt));

  useEffect(() => {
    const t = setInterval(() => setLeft(secondsLeft(request.expiresAt)), 1000);
    return () => clearInterval(t);
  }, [request.expiresAt]);

  const choose = async (n: number) => {
    setBusy(true);
    setError('');
    try {
      await approvalApi.approve(request.id, n);
      onDone('approved');
    } catch (err: any) {
      const code = err.response?.data?.code;
      if (code === 'denied' || code === 'not_pending' || err.response?.status === 404) {
        onDone('gone');
        return;
      }
      setError(err.response?.data?.error || 'Could not approve. Try again.');
    } finally {
      setBusy(false);
    }
  };

  const deny = async () => {
    setBusy(true);
    try {
      await approvalApi.deny(request.id);
      onDone('denied');
    } catch {
      onDone('gone');
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="space-y-4" data-testid="approval-card">
      <div>
        <h2 className="text-lg font-semibold text-gray-900">Sign in on a new device?</h2>
        <p className="mt-1 text-sm text-gray-600">
          <strong>{request.device}</strong>
          {request.ip ? <> &middot; {request.ip}</> : null} is trying to sign in{request.namedYou ? ' to your account' : ''}.
        </p>
      </div>

      <div>
        <p className="text-sm font-medium text-gray-800">Choose the number shown on that device</p>
        <div className="mt-2 grid grid-cols-3 gap-3">
          {request.choices.map((n) => (
            <button
              key={n}
              type="button"
              disabled={busy || left === 0}
              onClick={() => choose(n)}
              className="py-4 text-2xl font-semibold rounded-lg border border-gray-300 bg-white hover:bg-indigo-50 hover:border-indigo-500 disabled:opacity-50"
              data-testid={`choice-${n}`}
            >
              {n}
            </button>
          ))}
        </div>
      </div>

      {error && <div className="bg-red-50 border border-red-200 text-red-700 px-3 py-2 rounded text-sm">{error}</div>}

      <div className="flex items-center justify-between">
        <button type="button" disabled={busy} onClick={deny} className="text-sm font-medium text-red-700 hover:underline disabled:opacity-50">
          No, this is not me
        </button>
        <span className="text-xs text-gray-500">{left > 0 ? `Expires in ${left}s` : 'Expired'}</span>
      </div>

      <p className="text-xs text-gray-500">
        Only approve if you are looking at that device right now. If you did not start this sign-in, choose &ldquo;No&rdquo;.
      </p>
    </div>
  );
}
