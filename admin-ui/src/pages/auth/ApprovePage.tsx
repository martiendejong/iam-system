import { useEffect, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { useAuth } from '../../context/AuthContext';
import ApprovalCard from '../../components/auth/ApprovalCard';
import { approvalApi, rememberPendingApproval } from '../../services/approvalApi';
import type { ApprovalRequestInfo } from '../../services/approvalApi';

type View = 'loading' | 'ready' | 'approved' | 'denied' | 'gone';

/**
 * Opened from the QR code or the push notification. When this browser is not signed in yet, it sends you to the normal
 * sign-in first (the very first sign-in on a device still needs your password or passkey) and comes back here.
 */
export default function ApprovePage() {
  const { id } = useParams<{ id: string }>();
  const { isAuthenticated, loading } = useAuth();
  const navigate = useNavigate();
  const [view, setView] = useState<View>('loading');
  const [request, setRequest] = useState<ApprovalRequestInfo | null>(null);

  useEffect(() => {
    if (loading || !id) return;
    if (!isAuthenticated) {
      rememberPendingApproval(id);
      navigate('/login', { replace: true });
      return;
    }

    let cancelled = false;
    approvalApi
      .get(id)
      .then((r) => {
        if (!cancelled) {
          setRequest(r);
          setView('ready');
        }
      })
      .catch(() => {
        if (!cancelled) setView('gone');
      });
    return () => {
      cancelled = true;
    };
  }, [id, isAuthenticated, loading, navigate]);

  const onDone = (outcome: 'approved' | 'denied' | 'gone') => setView(outcome);

  return (
    <div className="min-h-screen flex items-center justify-center bg-gray-50 px-4 py-8">
      <div className="w-full max-w-md bg-white rounded-xl shadow p-6">
        {view === 'loading' && <p className="text-gray-600">Loading&hellip;</p>}
        {view === 'ready' && request && <ApprovalCard request={request} onDone={onDone} />}
        {view === 'approved' && (
          <div className="space-y-3">
            <h2 className="text-lg font-semibold text-green-700">Approved</h2>
            <p className="text-sm text-gray-700">The other device is signing in now. You can close this page.</p>
            <Link to="/dashboard" className="text-sm text-indigo-700 hover:underline">Go to the dashboard</Link>
          </div>
        )}
        {view === 'denied' && (
          <div className="space-y-3">
            <h2 className="text-lg font-semibold text-gray-900">Denied</h2>
            <p className="text-sm text-gray-700">That sign-in was refused. If you did not start it, consider changing your password.</p>
          </div>
        )}
        {view === 'gone' && (
          <div className="space-y-3">
            <h2 className="text-lg font-semibold text-gray-900">No longer available</h2>
            <p className="text-sm text-gray-700">This sign-in request expired, was already handled, or belongs to another account.</p>
          </div>
        )}
      </div>
    </div>
  );
}
