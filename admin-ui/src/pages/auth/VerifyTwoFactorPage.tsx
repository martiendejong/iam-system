import { useState, useEffect } from 'react';
import { useSearchParams, useNavigate, Link } from 'react-router-dom';
import { api } from '../../services/api';
import { useAuth } from '../../context/AuthContext';

export default function VerifyTwoFactorPage() {
  const [searchParams] = useSearchParams();
  const userId = searchParams.get('userId') ?? '';
  const code = searchParams.get('code') ?? '';
  const [status, setStatus] = useState<'loading' | 'success' | 'error'>('loading');
  const navigate = useNavigate();
  const { setCurrentUser } = useAuth();

  useEffect(() => {
    if (!userId || !code) {
      setStatus('error');
      return;
    }
    api.verifyLoginTwoFactor(userId, code)
      .then((response) => {
        if (response.user) {
          setCurrentUser(response.user);
        }
        setStatus('success');
        setTimeout(() => navigate('/dashboard'), 1500);
      })
      .catch(() => setStatus('error'));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [userId, code]);

  return (
    <div className="min-h-screen flex items-center justify-center bg-gray-50">
      <div className="max-w-md w-full p-8 bg-white rounded-lg shadow-lg text-center space-y-6">
        <h2 className="text-3xl font-bold text-gray-900">IAM System</h2>

        {status === 'loading' && (
          <>
            <div className="flex justify-center">
              <div className="w-10 h-10 border-2 border-indigo-500 border-t-transparent rounded-full animate-spin" />
            </div>
            <p className="text-sm text-gray-600">Verifying your sign-in…</p>
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
            <p className="text-sm text-gray-600">Redirecting to your dashboard…</p>
          </>
        )}

        {status === 'error' && (
          <>
            <div className="inline-flex items-center justify-center w-16 h-16 rounded-full bg-red-100 mx-auto">
              <svg className="w-8 h-8 text-red-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
              </svg>
            </div>
            <h3 className="text-lg font-medium text-gray-900">Verification failed</h3>
            <p className="text-sm text-gray-600">
              This link is invalid or has expired. Please sign in again to request a new code.
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
