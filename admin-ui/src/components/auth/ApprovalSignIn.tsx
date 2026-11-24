import { useCallback, useEffect, useRef, useState } from 'react';
import QRCode from 'qrcode';
import { approvalApi, approvalUrl } from '../../services/approvalApi';
import type { ApprovalStart } from '../../services/approvalApi';
import type { User } from '../../types';

interface ApprovalSignInProps {
  /** Called with the signed-in user once the other device approved and this browser finished the sign-in. */
  onSignedIn: (user: User | undefined) => void;
}

type Phase = 'idle' | 'waiting' | 'finishing';

const POLL_MS = 2000;

/**
 * Sign in here by approving on a device that is already signed in. Shows a number and a QR code; the signed-in device
 * (the app, the popup in a signed-in tab, or a camera scan) shows three numbers and the user picks this one.
 * With an e-mail address the user's own devices are notified; without one only a QR scan can approve.
 */
export default function ApprovalSignIn({ onSignedIn }: ApprovalSignInProps) {
  const [email, setEmail] = useState('');
  const [phase, setPhase] = useState<Phase>('idle');
  const [request, setRequest] = useState<ApprovalStart | null>(null);
  const [qr, setQr] = useState('');
  const [error, setError] = useState('');
  const [left, setLeft] = useState(0);
  const stopped = useRef(false);

  const reset = useCallback((message = '') => {
    setPhase('idle');
    setRequest(null);
    setQr('');
    setError(message);
  }, []);

  useEffect(() => {
    stopped.current = false;
    return () => {
      stopped.current = true;
    };
  }, []);

  // Poll the request while waiting; finish the sign-in the moment it is approved.
  useEffect(() => {
    if (phase !== 'waiting' || !request) return;
    let busy = false;
    const timer = setInterval(async () => {
      if (busy || stopped.current) return;
      busy = true;
      try {
        const status = await approvalApi.status(request.id, request.pollSecret);
        if (stopped.current) return;
        if (status === 'approved') {
          setPhase('finishing');
          const login = await approvalApi.complete(request.id, request.pollSecret);
          if (!stopped.current) onSignedIn(login.user);
        } else if (status === 'denied') {
          reset('The other device refused this sign-in.');
        } else if (status === 'expired' || status === 'consumed') {
          reset('This sign-in request expired. Start a new one.');
        }
      } catch (err: any) {
        if (!stopped.current) reset(err.response?.data?.error || 'Could not finish the sign-in. Start a new request.');
      } finally {
        busy = false;
      }
    }, POLL_MS);
    return () => clearInterval(timer);
  }, [phase, request, onSignedIn, reset]);

  // Countdown
  useEffect(() => {
    if (phase !== 'waiting' || !request) return;
    const tick = () => setLeft(Math.max(0, Math.round((new Date(request.expiresAt).getTime() - Date.now()) / 1000)));
    tick();
    const timer = setInterval(tick, 1000);
    return () => clearInterval(timer);
  }, [phase, request]);

  const start = async (e: React.FormEvent) => {
    e.preventDefault();
    setError('');
    try {
      const started = await approvalApi.start(email, true);
      setRequest(started);
      setQr(await QRCode.toDataURL(approvalUrl(started.id), { width: 220, margin: 1 }));
      setPhase('waiting');
    } catch (err: any) {
      setError(err.response?.data?.error || 'Could not start the request. Try again.');
    }
  };

  if (phase === 'idle') {
    return (
      <form onSubmit={start} className="space-y-4" data-testid="approval-signin-form">
        {error && <div className="bg-red-50 border border-red-200 text-red-700 px-3 py-2 rounded text-sm">{error}</div>}
        <p className="text-sm text-gray-600">
          Approve this sign-in on a device where you are already signed in. Type your e-mail address to get a
          notification and a popup there, or leave it empty and scan the QR code.
        </p>
        <div>
          <label htmlFor="approval-email" className="block text-sm font-medium text-gray-700">
            Email (optional)
          </label>
          <input
            id="approval-email"
            type="email"
            autoComplete="username"
            value={email}
            onChange={(e) => setEmail(e.target.value)}
            className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2"
          />
        </div>
        <button type="submit" className="w-full py-2 px-4 rounded-md bg-indigo-600 text-white font-medium hover:bg-indigo-700">
          Show sign-in code
        </button>
      </form>
    );
  }

  return (
    <div className="space-y-4 text-center" data-testid="approval-signin-waiting">
      <p className="text-sm text-gray-600">
        {email.trim()
          ? 'We asked your signed-in devices. Open the notification, or scan the code below with one of them.'
          : 'Scan this code with a device where you are signed in.'}
      </p>
      {qr && <img src={qr} alt="QR code to approve this sign-in" className="mx-auto rounded border border-gray-200" width={220} height={220} />}
      <div>
        <p className="text-xs uppercase tracking-wide text-gray-500">Choose this number on the other device</p>
        <p className="text-5xl font-bold text-gray-900" data-testid="match-number">{request?.matchNumber}</p>
      </div>
      <p className="text-xs text-gray-500">
        {phase === 'finishing' ? 'Approved. Signing you in…' : left > 0 ? `Waiting for approval · ${left}s left` : 'Expired'}
      </p>
      <button type="button" onClick={() => reset()} className="text-sm text-gray-600 hover:underline">
        Cancel
      </button>
    </div>
  );
}
