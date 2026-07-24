import { useState, useEffect } from 'react';
import { useSearchParams, Link } from 'react-router-dom';
import { api } from '../../services/api';

interface InvitationDetails {
  email: string;
  tenantName: string;
  roleName: string;
  status: string;
  expiresAt: string;
  invitedBy: string | null;
}

export default function AcceptInvitePage() {
  const [searchParams] = useSearchParams();
  const token = searchParams.get('token') || '';

  const [loading, setLoading] = useState(true);
  const [invitation, setInvitation] = useState<InvitationDetails | null>(null);
  const [loadError, setLoadError] = useState('');

  const [firstName, setFirstName] = useState('');
  const [lastName, setLastName] = useState('');
  const [password, setPassword] = useState('');
  const [confirmPassword, setConfirmPassword] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [submitError, setSubmitError] = useState('');
  const [result, setResult] = useState<{ welcomeMessage: string | null; mfaSetupRequired: boolean } | null>(null);

  useEffect(() => {
    if (!token) {
      setLoadError('This invitation link is missing a token.');
      setLoading(false);
      return;
    }

    api
      .getInvitationByToken(token)
      .then((data) => setInvitation(data))
      .catch((err) => setLoadError(err.response?.data?.error || 'This invitation could not be found.'))
      .finally(() => setLoading(false));
  }, [token]);

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setSubmitError('');

    if (password && password !== confirmPassword) {
      setSubmitError('Passwords do not match');
      return;
    }

    setSubmitting(true);
    try {
      const response = await api.acceptInvitation(token, {
        password: password || undefined,
        firstName: firstName || undefined,
        lastName: lastName || undefined,
      });
      setResult({
        welcomeMessage: response.welcomeMessage ?? null,
        mfaSetupRequired: !!response.mfaSetupRequired,
      });
    } catch (err: any) {
      setSubmitError(err.response?.data?.error || 'Failed to accept invitation');
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <div className="min-h-screen flex items-center justify-center bg-gray-50">
      <div className="max-w-md w-full space-y-6 p-8 bg-white rounded-lg shadow-lg">
        <div>
          <h2 className="text-center text-3xl font-bold text-gray-900">IAM System</h2>
          <p className="mt-2 text-center text-sm text-gray-600">Accept your invitation</p>
        </div>

        {loading && <p className="text-center text-sm text-gray-500">Loading invitation...</p>}

        {!loading && loadError && (
          <div className="text-center space-y-4">
            <div className="bg-red-50 border border-red-200 text-red-700 px-4 py-3 rounded text-sm">
              {loadError}
            </div>
            <Link to="/login" className="text-sm text-indigo-600 hover:text-indigo-500">
              Back to sign in
            </Link>
          </div>
        )}

        {!loading && !loadError && invitation && !result && (
          <>
            {invitation.status !== 'Pending' ? (
              <div className="text-center space-y-4">
                <div className="bg-yellow-50 border border-yellow-200 text-yellow-800 px-4 py-3 rounded text-sm">
                  This invitation is {invitation.status.toLowerCase()} and can no longer be accepted.
                </div>
                <Link to="/login" className="text-sm text-indigo-600 hover:text-indigo-500">
                  Back to sign in
                </Link>
              </div>
            ) : new Date(invitation.expiresAt) < new Date() ? (
              <div className="text-center space-y-4">
                <div className="bg-yellow-50 border border-yellow-200 text-yellow-800 px-4 py-3 rounded text-sm">
                  This invitation has expired. Ask {invitation.invitedBy || 'the sender'} to send you a new one.
                </div>
                <Link to="/login" className="text-sm text-indigo-600 hover:text-indigo-500">
                  Back to sign in
                </Link>
              </div>
            ) : (
              <>
                <p className="text-sm text-gray-600 text-center">
                  {invitation.invitedBy || 'Someone'} invited <strong>{invitation.email}</strong> to join{' '}
                  <strong>{invitation.tenantName}</strong> as <strong>{invitation.roleName}</strong>.
                </p>

                {submitError && (
                  <div className="bg-red-50 border border-red-200 text-red-700 px-4 py-3 rounded text-sm">
                    {submitError}
                  </div>
                )}

                <form className="space-y-4" onSubmit={handleSubmit}>
                  <div className="grid grid-cols-2 gap-3">
                    <div>
                      <label className="block text-sm font-medium text-gray-700">First name</label>
                      <input
                        type="text"
                        value={firstName}
                        onChange={(e) => setFirstName(e.target.value)}
                        className="mt-1 block w-full px-3 py-2 border border-gray-300 rounded-md shadow-sm focus:outline-none focus:ring-indigo-500 focus:border-indigo-500"
                      />
                    </div>
                    <div>
                      <label className="block text-sm font-medium text-gray-700">Last name</label>
                      <input
                        type="text"
                        value={lastName}
                        onChange={(e) => setLastName(e.target.value)}
                        className="mt-1 block w-full px-3 py-2 border border-gray-300 rounded-md shadow-sm focus:outline-none focus:ring-indigo-500 focus:border-indigo-500"
                      />
                    </div>
                  </div>
                  <div>
                    <label className="block text-sm font-medium text-gray-700">Password</label>
                    <input
                      type="password"
                      value={password}
                      onChange={(e) => setPassword(e.target.value)}
                      className="mt-1 block w-full px-3 py-2 border border-gray-300 rounded-md shadow-sm focus:outline-none focus:ring-indigo-500 focus:border-indigo-500"
                      placeholder="Required for new accounts"
                    />
                  </div>
                  <div>
                    <label className="block text-sm font-medium text-gray-700">Confirm password</label>
                    <input
                      type="password"
                      value={confirmPassword}
                      onChange={(e) => setConfirmPassword(e.target.value)}
                      className="mt-1 block w-full px-3 py-2 border border-gray-300 rounded-md shadow-sm focus:outline-none focus:ring-indigo-500 focus:border-indigo-500"
                    />
                  </div>
                  <p className="text-xs text-gray-500">
                    If you already have an account with this email, you can leave the password fields blank
                    to join with your existing account.
                  </p>
                  <button
                    type="submit"
                    disabled={submitting}
                    className="w-full flex justify-center py-2 px-4 border border-transparent rounded-md shadow-sm text-sm font-medium text-white bg-indigo-600 hover:bg-indigo-700 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-indigo-500 disabled:opacity-50"
                  >
                    {submitting ? 'Joining...' : 'Accept Invitation'}
                  </button>
                </form>
              </>
            )}
          </>
        )}

        {result && (
          <div className="text-center space-y-4">
            <div className="inline-flex items-center justify-center w-16 h-16 rounded-full bg-green-100">
              <svg className="w-8 h-8 text-green-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 13l4 4L19 7" />
              </svg>
            </div>
            <h3 className="text-lg font-medium text-gray-900">You're in!</h3>
            {result.welcomeMessage && <p className="text-sm text-gray-600">{result.welcomeMessage}</p>}
            {result.mfaSetupRequired && (
              <div className="bg-yellow-50 border border-yellow-200 text-yellow-800 px-4 py-3 rounded text-sm">
                This organization requires multi-factor authentication. Please set up MFA from Security
                settings after signing in.
              </div>
            )}
            <Link
              to="/login"
              className="inline-block text-sm font-medium text-white bg-indigo-600 hover:bg-indigo-700 rounded-md py-2 px-4"
            >
              Sign in now
            </Link>
          </div>
        )}
      </div>
    </div>
  );
}
