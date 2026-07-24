import { useState, useEffect } from 'react';
import { useSearchParams, Link } from 'react-router-dom';
import { api } from '../../services/api';

export default function VerifyEmailPage() {
  const [searchParams] = useSearchParams();
  const token = searchParams.get('token') ?? '';
  const [status, setStatus] = useState<'loading' | 'success' | 'error'>(token ? 'loading' : 'error');

  useEffect(() => {
    if (!token) return;
    api.verifyEmail(token)
      .then(() => setStatus('success'))
      .catch(() => setStatus('error'));
  }, [token]);

  return (
    <div className="min-h-screen flex items-center justify-center bg-gray-50">
      <div className="max-w-md w-full p-8 bg-white rounded-lg shadow-lg text-center space-y-6">
        <h2 className="text-3xl font-bold text-gray-900">IAM System</h2>

        {status === 'loading' && (
          <>
            <div className="flex justify-center">
              <div className="w-10 h-10 border-2 border-indigo-500 border-t-transparent rounded-full animate-spin" />
            </div>
            <p className="text-sm text-gray-600">Verifying your email…</p>
          </>
        )}

        {status === 'success' && (
          <>
            <div className="inline-flex items-center justify-center w-16 h-16 rounded-full bg-green-100 mx-auto">
              <svg className="w-8 h-8 text-green-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 13l4 4L19 7" />
              </svg>
            </div>
            <h3 className="text-lg font-medium text-gray-900">Email verified!</h3>
            <p className="text-sm text-gray-600">Your email address has been verified. You can now sign in.</p>
            <Link
              to="/login"
              className="inline-block px-6 py-2 bg-indigo-600 text-white text-sm font-medium rounded-md hover:bg-indigo-700 transition-colors"
            >
              Sign in
            </Link>
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
              The verification link is invalid or has expired. Please request a new one from the admin or sign in to
              try again.
            </p>
            <Link
              to="/login"
              className="inline-block text-sm text-indigo-600 hover:text-indigo-500"
            >
              Back to sign in
            </Link>
          </>
        )}
      </div>
    </div>
  );
}
