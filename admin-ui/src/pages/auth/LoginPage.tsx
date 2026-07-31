import { useState, useEffect } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { useAuth } from '../../context/AuthContext';
import { api } from '../../services/api';
import { brandingApi } from '../../services/brandingApi';
import type { PublicTenantBranding } from '../../services/brandingApi';
import type { IdentityProvider } from '../../types';
import { sanitizeReturnUrl } from './returnUrl';

type LoginMethod = 'password' | 'magic-link' | 'sms';
type LoginView = 'login' | 'forgot';

export default function LoginPage() {
  const [view, setView] = useState<LoginView>('login');
  const [loginMethod, setLoginMethod] = useState<LoginMethod>('password');
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [phoneNumber, setPhoneNumber] = useState('');
  const [otpCode, setOtpCode] = useState('');
  const [error, setError] = useState('');
  const [message, setMessage] = useState('');
  const [loading, setLoading] = useState(false);
  const [otpSent, setOtpSent] = useState(false);
  const [magicLinkSent, setMagicLinkSent] = useState(false);
  const [forgotSent, setForgotSent] = useState(false);
  const [socialProviders, setSocialProviders] = useState<IdentityProvider[]>([]);
  const [branding, setBranding] = useState<PublicTenantBranding | null>(null);
  const [stepUpRequired, setStepUpRequired] = useState(false);
  const [stepUpCode, setStepUpCode] = useState('');
  const [twoFactorPending, setTwoFactorPending] = useState(false);
  const [twoFactorUserId, setTwoFactorUserId] = useState('');
  const [twoFactorCode, setTwoFactorCode] = useState('');
  const { login, setCurrentUser } = useAuth();
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  const returnUrl = sanitizeReturnUrl(searchParams.get('returnUrl'));
  const tenantSlug = searchParams.get('tenant');

  useEffect(() => {
    loadSocialProviders();
    loadBranding();
  }, []);

  const loadBranding = async () => {
    try {
      const data = tenantSlug
        ? await brandingApi.getPublicBranding(tenantSlug)
        : await brandingApi.getBrandingByDomain(window.location.hostname);
      setBranding(data);
    } catch {
      // No tenant branding configured for this slug/domain - fall back to platform defaults
    }
  };

  const primaryColor = branding?.primaryColor || '#4F46E5';

  const loadSocialProviders = async () => {
    try {
      const providers = await api.getIdentityProviders();
      setSocialProviders(providers.filter((p: IdentityProvider) => p.isActive));
    } catch {
      // Social providers are optional, don't show error
    }
  };

  const handleSocialLogin = async (provider: IdentityProvider) => {
    try {
      setError('');
      const redirectUri = `${window.location.origin}/auth/login`;
      const { authorizationUrl } = await api.getSocialAuthUrl(provider.id, redirectUri);
      window.location.href = authorizationUrl;
    } catch (err: any) {
      setError(err.response?.data?.error || `Failed to initiate ${provider.displayName} login`);
    }
  };

  const getSocialButtonStyle = (type: string) => {
    switch (type) {
      case 'Google':
        return 'bg-white border border-gray-300 text-gray-700 hover:bg-gray-50';
      case 'Microsoft':
        return 'bg-[#2f2f2f] text-white hover:bg-[#1a1a1a]';
      case 'GitHub':
        return 'bg-[#24292e] text-white hover:bg-[#1b1f23]';
      case 'Apple':
        return 'bg-black text-white hover:bg-gray-900';
      default:
        return 'bg-gray-600 text-white hover:bg-gray-700';
    }
  };

  const resetState = () => {
    setError('');
    setMessage('');
    setOtpSent(false);
    setMagicLinkSent(false);
    setForgotSent(false);
    setOtpCode('');
    setStepUpRequired(false);
    setStepUpCode('');
    setTwoFactorPending(false);
    setTwoFactorUserId('');
    setTwoFactorCode('');
  };

  const navigateAfterLogin = () => {
    // If returnUrl is an OIDC authorize request, use full page navigation
    // so the browser sends the session cookie to the backend
    if (returnUrl.startsWith('/connect/') || returnUrl.startsWith('/auth/connect/')) {
      const fullUrl = returnUrl.startsWith('/auth/') ? returnUrl : '/auth' + returnUrl;
      window.location.href = fullUrl;
    } else {
      navigate(returnUrl);
    }
  };

  const handleForgotPassword = async (e: React.FormEvent) => {
    e.preventDefault();
    setError('');
    setLoading(true);
    try {
      await api.forgotPassword(email);
      setForgotSent(true);
    } catch {
      // Always show success to avoid revealing whether email exists
      setForgotSent(true);
    } finally {
      setLoading(false);
    }
  };

  const handleTabChange = (method: LoginMethod) => {
    setLoginMethod(method);
    resetState();
  };

  const handlePasswordLogin = async (e: React.FormEvent) => {
    e.preventDefault();
    setError('');
    setMessage('');
    setLoading(true);

    try {
      const response = await login({ email, password });
      if (response.requiresStepUp) {
        setStepUpRequired(true);
        setMessage('Additional verification required. Check your email for a code.');
        return;
      }
      if (response.requiresTwoFactor && response.userId) {
        setTwoFactorUserId(response.userId);
        setTwoFactorPending(true);
        setMessage(response.message || 'A verification code has been sent to your email.');
        return;
      }
      navigateAfterLogin();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Login failed. Please try again.');
    } finally {
      setLoading(false);
    }
  };

  const handleStepUpVerify = async (e: React.FormEvent) => {
    e.preventDefault();
    setError('');
    setLoading(true);

    try {
      const response = await api.verifyStepUp(email, stepUpCode);
      if (response.user) {
        setCurrentUser(response.user);
      }
      navigateAfterLogin();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Invalid or expired code. Please try again.');
    } finally {
      setLoading(false);
    }
  };

  const handleTwoFactorVerify = async (e: React.FormEvent) => {
    e.preventDefault();
    setError('');
    setLoading(true);

    try {
      const response = await api.verifyLoginTwoFactor(twoFactorUserId, twoFactorCode);
      if (response.user) {
        setCurrentUser(response.user);
      }
      navigateAfterLogin();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Invalid or expired code. Please try again.');
    } finally {
      setLoading(false);
    }
  };

  const handleResendTwoFactorCode = async () => {
    setError('');
    setLoading(true);
    try {
      await api.resendLoginTwoFactorCode(twoFactorUserId);
      setMessage('A new verification code has been sent to your email.');
    } catch {
      setError('Failed to resend code. Please try again.');
    } finally {
      setLoading(false);
    }
  };

  const handleMagicLinkRequest = async (e: React.FormEvent) => {
    e.preventDefault();
    setError('');
    setMessage('');
    setLoading(true);

    try {
      const client = api.getClient();
      await client.post('/auth/magic-link/request', { email });
      setMagicLinkSent(true);
      setMessage('Check your email for the magic link.');
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to send magic link. Please try again.');
    } finally {
      setLoading(false);
    }
  };

  const handleSmsOtpRequest = async (e: React.FormEvent) => {
    e.preventDefault();
    setError('');
    setMessage('');
    setLoading(true);

    try {
      const client = api.getClient();
      await client.post('/auth/otp/sms/request', { phoneNumber });
      setOtpSent(true);
      setMessage('A verification code has been sent to your phone.');
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to send code. Please try again.');
    } finally {
      setLoading(false);
    }
  };

  const handleSmsOtpVerify = async (e: React.FormEvent) => {
    e.preventDefault();
    setError('');
    setLoading(true);

    try {
      const client = api.getClient();
      const response = await client.post('/auth/otp/sms/verify', { phoneNumber, code: otpCode });
      if (response.data.accessToken) {
        localStorage.setItem('accessToken', response.data.accessToken);
      }
      if (response.data.user) {
        setCurrentUser(response.data.user);
      }
      navigateAfterLogin();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Invalid code. Please try again.');
    } finally {
      setLoading(false);
    }
  };

  const tabClass = (method: LoginMethod) =>
    `flex-1 py-2 text-sm font-medium text-center rounded-md transition-colors ${
      loginMethod === method
        ? 'bg-indigo-600 text-white shadow-sm'
        : 'text-gray-600 hover:text-gray-900 hover:bg-gray-100'
    }`;

  return (
    <div
      className="min-h-screen flex items-center justify-center bg-gray-50"
      style={{
        backgroundImage: branding?.backgroundUrl ? `url(${branding.backgroundUrl})` : undefined,
        backgroundSize: 'cover',
        backgroundPosition: 'center',
      }}
    >
      {branding?.customCss && <style>{branding.customCss}</style>}
      <div className="max-w-md w-full space-y-8 p-8 bg-white rounded-lg shadow-lg">
        <div>
          {branding?.logoUrl ? (
            <img
              src={branding.logoUrl}
              alt={branding.tenantName || 'Logo'}
              className="h-12 mx-auto object-contain mb-2"
              onError={(e) => { (e.target as HTMLImageElement).style.display = 'none'; }}
            />
          ) : (
            !branding?.whiteLabelEnabled && (
              <h2 className="text-center text-3xl font-bold text-gray-900">
                IAM System
              </h2>
            )
          )}
          <h1 className="text-center text-xl font-semibold text-gray-900 mt-2">
            {branding?.loginTitle || (branding?.tenantName ? `Welcome to ${branding.tenantName}` : '')}
          </h1>
          <p className="mt-2 text-center text-sm text-gray-600">
            {branding?.loginSubtitle || 'Sign in to your account'}
          </p>
        </div>

        {/* Login method tabs */}
        {view === 'login' && !twoFactorPending && !stepUpRequired && <div className="flex gap-1 p-1 bg-gray-100 rounded-lg">
          <button
            type="button"
            onClick={() => handleTabChange('password')}
            className={tabClass('password')}
          >
            Email + Password
          </button>
          <button
            type="button"
            onClick={() => handleTabChange('magic-link')}
            className={tabClass('magic-link')}
          >
            Magic Link
          </button>
          <button
            type="button"
            onClick={() => handleTabChange('sms')}
            className={tabClass('sms')}
          >
            SMS Code
          </button>
        </div>}

        {error && (
          <div className="bg-red-50 border border-red-200 text-red-700 px-4 py-3 rounded">
            {error}
          </div>
        )}

        {message && (
          <div className="bg-green-50 border border-green-200 text-green-700 px-4 py-3 rounded">
            {message}
          </div>
        )}

        {/* Adaptive MFA step-up verification */}
        {loginMethod === 'password' && stepUpRequired && (
          <form className="mt-4 space-y-6" onSubmit={handleStepUpVerify}>
            <div>
              <label htmlFor="step-up-code" className="block text-sm font-medium text-gray-700">
                Verification code
              </label>
              <input
                id="step-up-code"
                name="code"
                type="text"
                inputMode="numeric"
                pattern="[0-9]{6}"
                maxLength={6}
                required
                placeholder="000000"
                autoFocus
                value={stepUpCode}
                onChange={(e) => setStepUpCode(e.target.value.replace(/\D/g, '').slice(0, 6))}
                className="mt-1 block w-full px-3 py-2 border border-gray-300 rounded-md shadow-sm focus:outline-none focus:ring-indigo-500 focus:border-indigo-500 text-center text-2xl tracking-widest font-mono"
              />
              <p className="mt-1 text-xs text-gray-500">
                Enter the 6-digit code sent to {email}
              </p>
            </div>
            <button
              type="submit"
              disabled={loading || stepUpCode.length !== 6}
              className="w-full flex justify-center py-2 px-4 border border-transparent rounded-md shadow-sm text-sm font-medium text-white bg-indigo-600 hover:bg-indigo-700 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-indigo-500 disabled:opacity-50"
            >
              {loading ? 'Verifying...' : 'Verify Code'}
            </button>
            <button
              type="button"
              onClick={() => { setStepUpRequired(false); setStepUpCode(''); setMessage(''); }}
              className="w-full text-sm text-indigo-600 hover:text-indigo-500"
            >
              Back to sign in
            </button>
          </form>
        )}

        {/* Two-factor code entry (email) */}
        {twoFactorPending && (
          <form className="mt-4 space-y-6" onSubmit={handleTwoFactorVerify}>
            <div>
              <label htmlFor="two-factor-code" className="block text-sm font-medium text-gray-700">
                Verification code
              </label>
              <input
                id="two-factor-code"
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
              <p className="mt-1 text-xs text-gray-500">
                Enter the code we emailed to {email}, or click the link in that email to sign in automatically.
              </p>
            </div>
            <button
              type="submit"
              disabled={loading || twoFactorCode.length !== 6}
              className="w-full flex justify-center py-2 px-4 border border-transparent rounded-md shadow-sm text-sm font-medium text-white bg-indigo-600 hover:bg-indigo-700 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-indigo-500 disabled:opacity-50"
            >
              {loading ? 'Verifying...' : 'Verify and Sign In'}
            </button>
            <div className="flex items-center justify-between text-sm">
              <button
                type="button"
                onClick={handleResendTwoFactorCode}
                disabled={loading}
                className="text-indigo-600 hover:text-indigo-500"
              >
                Resend code
              </button>
              <button
                type="button"
                onClick={() => { resetState(); }}
                className="text-indigo-600 hover:text-indigo-500"
              >
                Back to sign in
              </button>
            </div>
          </form>
        )}

        {/* Password login form */}
        {loginMethod === 'password' && !stepUpRequired && !twoFactorPending && (
          <form className="mt-4 space-y-6" onSubmit={handlePasswordLogin}>
            <div className="space-y-4">
              <div>
                <label htmlFor="email" className="block text-sm font-medium text-gray-700">
                  Email address
                </label>
                <input
                  id="email"
                  name="email"
                  type="email"
                  autoComplete="email"
                  required
                  value={email}
                  onChange={(e) => setEmail(e.target.value)}
                  className="mt-1 block w-full px-3 py-2 border border-gray-300 rounded-md shadow-sm focus:outline-none focus:ring-indigo-500 focus:border-indigo-500"
                />
              </div>
              <div>
                <label htmlFor="password" className="block text-sm font-medium text-gray-700">
                  Password
                </label>
                <input
                  id="password"
                  name="password"
                  type="password"
                  autoComplete="current-password"
                  required
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                  className="mt-1 block w-full px-3 py-2 border border-gray-300 rounded-md shadow-sm focus:outline-none focus:ring-indigo-500 focus:border-indigo-500"
                />
              </div>
            </div>
            <div>
              <button
                type="submit"
                disabled={loading}
                style={{ backgroundColor: primaryColor }}
                className="w-full flex justify-center py-2 px-4 border border-transparent rounded-md shadow-sm text-sm font-medium text-white hover:opacity-90 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-indigo-500 disabled:opacity-50"
              >
                {loading ? 'Signing in...' : 'Sign in'}
              </button>
            </div>
            <div className="text-center">
              <button
                type="button"
                onClick={() => { resetState(); setView('forgot'); }}
                className="text-sm text-indigo-600 hover:text-indigo-500"
              >
                Forgot your password?
              </button>
            </div>
          </form>
        )}

        {/* Forgot password view */}
        {view === 'forgot' && (
          <div className="mt-4 space-y-6">
            {!forgotSent ? (
              <form onSubmit={handleForgotPassword} className="space-y-4">
                <p className="text-sm text-gray-600">
                  Enter your email address and we'll send you a link to reset your password.
                </p>
                <div>
                  <label htmlFor="forgot-email" className="block text-sm font-medium text-gray-700">
                    Email address
                  </label>
                  <input
                    id="forgot-email"
                    name="email"
                    type="email"
                    autoComplete="email"
                    required
                    value={email}
                    onChange={(e) => setEmail(e.target.value)}
                    className="mt-1 block w-full px-3 py-2 border border-gray-300 rounded-md shadow-sm focus:outline-none focus:ring-indigo-500 focus:border-indigo-500"
                  />
                </div>
                <button
                  type="submit"
                  disabled={loading}
                  className="w-full flex justify-center py-2 px-4 border border-transparent rounded-md shadow-sm text-sm font-medium text-white bg-indigo-600 hover:bg-indigo-700 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-indigo-500 disabled:opacity-50"
                >
                  {loading ? 'Sending...' : 'Send reset link'}
                </button>
                <div className="text-center">
                  <button
                    type="button"
                    onClick={() => { resetState(); setView('login'); }}
                    className="text-sm text-indigo-600 hover:text-indigo-500"
                  >
                    Back to sign in
                  </button>
                </div>
              </form>
            ) : (
              <div className="text-center space-y-4">
                <div className="inline-flex items-center justify-center w-16 h-16 rounded-full bg-indigo-100">
                  <svg className="w-8 h-8 text-indigo-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M3 8l7.89 5.26a2 2 0 002.22 0L21 8M5 19h14a2 2 0 002-2V7a2 2 0 00-2-2H5a2 2 0 00-2 2v10a2 2 0 002 2z" />
                  </svg>
                </div>
                <h3 className="text-lg font-medium text-gray-900">Check your email</h3>
                <p className="text-sm text-gray-600">
                  If an account exists for <strong>{email}</strong>, a password reset link has been sent.
                </p>
                <button
                  type="button"
                  onClick={() => { resetState(); setView('login'); }}
                  className="text-sm text-indigo-600 hover:text-indigo-500"
                >
                  Back to sign in
                </button>
              </div>
            )}
          </div>
        )}

        {/* Magic link form */}
        {loginMethod === 'magic-link' && (
          <div className="mt-4 space-y-6">
            {!magicLinkSent ? (
              <form onSubmit={handleMagicLinkRequest} className="space-y-4">
                <div>
                  <label htmlFor="magic-email" className="block text-sm font-medium text-gray-700">
                    Email address
                  </label>
                  <input
                    id="magic-email"
                    name="email"
                    type="email"
                    autoComplete="email"
                    required
                    value={email}
                    onChange={(e) => setEmail(e.target.value)}
                    className="mt-1 block w-full px-3 py-2 border border-gray-300 rounded-md shadow-sm focus:outline-none focus:ring-indigo-500 focus:border-indigo-500"
                  />
                </div>
                <button
                  type="submit"
                  disabled={loading}
                  className="w-full flex justify-center py-2 px-4 border border-transparent rounded-md shadow-sm text-sm font-medium text-white bg-indigo-600 hover:bg-indigo-700 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-indigo-500 disabled:opacity-50"
                >
                  {loading ? 'Sending...' : 'Send Magic Link'}
                </button>
              </form>
            ) : (
              <div className="text-center space-y-4">
                <div className="inline-flex items-center justify-center w-16 h-16 rounded-full bg-indigo-100">
                  <svg className="w-8 h-8 text-indigo-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M3 8l7.89 5.26a2 2 0 002.22 0L21 8M5 19h14a2 2 0 002-2V7a2 2 0 00-2-2H5a2 2 0 00-2 2v10a2 2 0 002 2z" />
                  </svg>
                </div>
                <h3 className="text-lg font-medium text-gray-900">Check your email</h3>
                <p className="text-sm text-gray-600">
                  We sent a magic link to <strong>{email}</strong>. Click the link in the email to sign in.
                </p>
                <button
                  type="button"
                  onClick={() => { setMagicLinkSent(false); setMessage(''); }}
                  className="text-sm text-indigo-600 hover:text-indigo-500"
                >
                  Send again
                </button>
              </div>
            )}
          </div>
        )}

        {/* Social Login Buttons */}
        {socialProviders.length > 0 && !twoFactorPending && !stepUpRequired && (
          <div className="mt-2">
            <div className="relative">
              <div className="absolute inset-0 flex items-center">
                <div className="w-full border-t border-gray-300" />
              </div>
              <div className="relative flex justify-center text-sm">
                <span className="px-2 bg-white text-gray-500">Or continue with</span>
              </div>
            </div>

            <div className="mt-4 grid grid-cols-1 gap-3">
              {socialProviders.map((provider) => (
                <button
                  key={provider.id}
                  type="button"
                  onClick={() => handleSocialLogin(provider)}
                  className={`w-full inline-flex justify-center py-2 px-4 rounded-md shadow-sm text-sm font-medium focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-indigo-500 ${getSocialButtonStyle(provider.type)}`}
                >
                  {provider.displayName}
                </button>
              ))}
            </div>
          </div>
        )}

        {/* SMS OTP form */}
        {loginMethod === 'sms' && (
          <div className="mt-4 space-y-6">
            {!otpSent ? (
              <form onSubmit={handleSmsOtpRequest} className="space-y-4">
                <div>
                  <label htmlFor="phone" className="block text-sm font-medium text-gray-700">
                    Phone number
                  </label>
                  <input
                    id="phone"
                    name="phone"
                    type="tel"
                    autoComplete="tel"
                    required
                    placeholder="+1234567890"
                    value={phoneNumber}
                    onChange={(e) => setPhoneNumber(e.target.value)}
                    className="mt-1 block w-full px-3 py-2 border border-gray-300 rounded-md shadow-sm focus:outline-none focus:ring-indigo-500 focus:border-indigo-500"
                  />
                </div>
                <button
                  type="submit"
                  disabled={loading}
                  className="w-full flex justify-center py-2 px-4 border border-transparent rounded-md shadow-sm text-sm font-medium text-white bg-indigo-600 hover:bg-indigo-700 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-indigo-500 disabled:opacity-50"
                >
                  {loading ? 'Sending...' : 'Send Verification Code'}
                </button>
              </form>
            ) : (
              <form onSubmit={handleSmsOtpVerify} className="space-y-4">
                <div>
                  <label htmlFor="otp-code" className="block text-sm font-medium text-gray-700">
                    Verification code
                  </label>
                  <input
                    id="otp-code"
                    name="code"
                    type="text"
                    inputMode="numeric"
                    pattern="[0-9]{6}"
                    maxLength={6}
                    required
                    placeholder="000000"
                    autoFocus
                    value={otpCode}
                    onChange={(e) => setOtpCode(e.target.value.replace(/\D/g, '').slice(0, 6))}
                    className="mt-1 block w-full px-3 py-2 border border-gray-300 rounded-md shadow-sm focus:outline-none focus:ring-indigo-500 focus:border-indigo-500 text-center text-2xl tracking-widest font-mono"
                  />
                  <p className="mt-1 text-xs text-gray-500">
                    Enter the 6-digit code sent to {phoneNumber}
                  </p>
                </div>
                <button
                  type="submit"
                  disabled={loading || otpCode.length !== 6}
                  className="w-full flex justify-center py-2 px-4 border border-transparent rounded-md shadow-sm text-sm font-medium text-white bg-indigo-600 hover:bg-indigo-700 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-indigo-500 disabled:opacity-50"
                >
                  {loading ? 'Verifying...' : 'Verify Code'}
                </button>
                <button
                  type="button"
                  onClick={() => { setOtpSent(false); setMessage(''); setOtpCode(''); }}
                  className="w-full text-sm text-indigo-600 hover:text-indigo-500"
                >
                  Use a different phone number
                </button>
              </form>
            )}
          </div>
        )}
      </div>
    </div>
  );
}
