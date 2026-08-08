import { useState, useEffect } from 'react';
import { useSearchParams, useNavigate, Link } from 'react-router-dom';
import { api } from '../../services/api';
import { useAuth } from '../../context/AuthContext';
import { sanitizeReturnUrl, navigateAfterAuth } from './returnUrl';

export default function MagicLinkCallbackPage() {
  const [searchParams] = useSearchParams();
  const token = searchParams.get('token') ?? '';
  const returnUrl = sanitizeReturnUrl(searchParams.get('returnUrl'));
  const [status, setStatus] = useState<'loading' | 'twoFactor' | 'success' | 'error'>('loading');
  const [twoFactorUserId, setTwoFactorUserId] = useState('');
  const [twoFactorCode, setTwoFactorCode] = useState('');
  const [twoFactorMessage, setTwoFactorMessage] = useState('');
  const [twoFactorError, setTwoFactorError] = useState('');
  const [verifying, setVerifying] = useState(false);
  const navigate = useNavigate();
  const { setCurrentUser } = useAuth();

  useEffect(() => {
    if (!token) {
      setStatus('error');
      return;
    }
    api.verifyMagicLink(token)
      .then((response) => {
        // The magic link only proves the user controls the mailbox. If the account
        // also has email 2FA enabled, that is a separate factor that still needs to
        // be verified before completing sign-in — mirrors the password + 2FA flow.
        if (response.requiresTwoFactor && response.userId) {
          setTwoFactorUserId(response.userId);
          setTwoFactorMessage(response.message || 'A verification code has been sent to your email.');
          setStatus('twoFactor');
          return;
        }
        if (response.user) {
          setCurrentUser(response.user);
        }
        setStatus('success');
        setTimeout(() => navigateAfterAuth(returnUrl, navigate), 1500);
      })
      .catch(() => setStatus('error'));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [token]);

  const handleTwoFactorVerify = async (e: React.FormEvent) => {
    e.preventDefault();
    setTwoFactorError('');
    setVerifying(true);
    try {
      const response = await api.verifyLoginTwoFactor(twoFactorUserId, twoFactorCode);
      if (response.user) {
        setCurrentUser(response.user);
      }
      setStatus('success');
      setTimeout(() => navigateAfterAuth(returnUrl, navigate), 1500);
    } catch {
      setTwoFactorError('Invalid or expired code. Please try again.');
    } finally {
      setVerifying(false);
    }
  };

  const handleResendCode = async () => {
    setTwoFactorError('');
    try {
      await api.resendLoginTwoFactorCode(twoFactorUserId, returnUrl);
      setTwoFactorMessage('A new verification code has been sent to your email.');
    } catch {
      setTwoFactorError('Failed to resend code. Please try again.');
    }
  };

  return (
    <div className="min-h-screen flex items-center justify-center bg-gray-50">
      <div className="max-w-md w-full p-8 bg-white rounded-lg shadow-lg text-center space-y-6">
        <h2 className="text-3xl font-bold text-gray-900">IAM System</h2>

        {status === 'loading' && (
          <>
            <div className="flex justify-center">
              <div className="w-10 h-10 border-2 border-indigo-500 border-t-transparent rounded-full animate-spin" />
            </div>
            <p className="text-sm text-gray-600">Signing you in…</p>
          </>
        )}

        {status === 'twoFactor' && (
          <>
            <p className="text-sm text-gray-600">{twoFactorMessage}</p>
            {twoFactorError && (
              <div className="bg-red-50 border border-red-200 text-red-700 px-4 py-3 rounded text-sm">
                {twoFactorError}
              </div>
            )}
            <form className="space-y-6 text-left" onSubmit={handleTwoFactorVerify}>
              <div>
                <label htmlFor="magic-link-two-factor-code" className="block text-sm font-medium text-gray-700">
                  Verification code
                </label>
                <input
                  id="magic-link-two-factor-code"
                  name="code"
                  type="text"
                  inputMode="numeric"
                  pattern="[0-9]{6}"
                  maxLength={6}
                  required
                  placeholder="000000"
                  autoFocus
                  value={twoFactorCode}
                  onChange={(e) => setTwoFactorCode(e.target.value.replace(/\D/g, '').slice(0, 6))}
                  className="mt-1 block w-full px-3 py-2 border border-gray-300 rounded-md shadow-sm focus:outline-none focus:ring-indigo-500 focus:border-indigo-500 text-center text-2xl tracking-widest font-mono"
                />
              </div>
              <button
                type="submit"
                disabled={verifying || twoFactorCode.length !== 6}
                className="w-full flex justify-center py-2 px-4 border border-transparent rounded-md shadow-sm text-sm font-medium text-white bg-indigo-600 hover:bg-indigo-700 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-indigo-500 disabled:opacity-50"
              >
                {verifying ? 'Verifying...' : 'Verify and Sign In'}
              </button>
              <div className="flex items-center justify-between text-sm">
                <button
                  type="button"
                  onClick={handleResendCode}
                  disabled={verifying}
                  className="text-indigo-600 hover:text-indigo-500"
                >
                  Resend code
                </button>
                <Link to="/login" className="text-indigo-600 hover:text-indigo-500">
                  Back to sign in
                </Link>
              </div>
            </form>
          </>
        )}

        {status === 'success' && (
          <>
            <div className="inline-flex items-center justify-center w-16 h-16 rounded-full bg-green-100 mx-auto">
              <svg className="w-8 h-8 text-green-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 13l4 4L19 7" />
              </svg>
            </div>
            <h3 className="text-lg font-medium text-gray-900">You're signed in!</h3>
            <p className="text-sm text-gray-600">Redirecting…</p>
          </>
        )}

        {status === 'error' && (
          <>
            <div className="inline-flex items-center justify-center w-16 h-16 rounded-full bg-red-100 mx-auto">
              <svg className="w-8 h-8 text-red-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
              </svg>
            </div>
            <h3 className="text-lg font-medium text-gray-900">Sign-in failed</h3>
            <p className="text-sm text-gray-600">
              This link is invalid or has expired. Please sign in again to request a new one.
            </p>
            <Link
              to="/login"
              className="inline-block px-6 py-2 bg-indigo-600 text-white text-sm font-medium rounded-md hover:bg-indigo-700 transition-colors"
            >
              Back to sign in
            </Link>
          </>
        )}
      </div>
    </div>
  );
}
