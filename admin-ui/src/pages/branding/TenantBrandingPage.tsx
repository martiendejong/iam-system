import { useState, useEffect } from 'react';
import DashboardLayout from '../../components/layout/DashboardLayout';
import { brandingApi } from '../../services/brandingApi';
import type { TenantBranding, UpsertBrandingRequest } from '../../services/brandingApi';
import { api } from '../../services/api';

type TabType = 'editor' | 'email' | 'preview';

export default function TenantBrandingPage() {
  const [activeTab, setActiveTab] = useState<TabType>('editor');
  const [tenants, setTenants] = useState<any[]>([]);
  const [selectedTenantId, setSelectedTenantId] = useState<string>('');
  const [, setBranding] = useState<TenantBranding | null>(null);
  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const [successMessage, setSuccessMessage] = useState('');

  // Form state
  const [logoUrl, setLogoUrl] = useState('');
  const [primaryColor, setPrimaryColor] = useState('#4F46E5');
  const [secondaryColor, setSecondaryColor] = useState('#7C3AED');
  const [backgroundUrl, setBackgroundUrl] = useState('');
  const [customCss, setCustomCss] = useState('');
  const [emailHeaderHtml, setEmailHeaderHtml] = useState('');
  const [emailFooterHtml, setEmailFooterHtml] = useState('');
  const [faviconUrl, setFaviconUrl] = useState('');
  const [loginTitle, setLoginTitle] = useState('');
  const [loginSubtitle, setLoginSubtitle] = useState('');
  const [whiteLabelEnabled, setWhiteLabelEnabled] = useState(false);
  const [customDomain, setCustomDomain] = useState('');

  useEffect(() => {
    loadTenants();
  }, []);

  useEffect(() => {
    if (selectedTenantId) {
      loadBranding(selectedTenantId);
    }
  }, [selectedTenantId]);

  const loadTenants = async () => {
    try {
      const data = await api.getTenants();
      setTenants(data);
      if (data.length > 0) {
        setSelectedTenantId(data[0].id);
      }
    } catch {
      setError('Failed to load tenants');
    }
  };

  const loadBranding = async (tenantId: string) => {
    try {
      setLoading(true);
      setError('');
      const data = await brandingApi.getBranding(tenantId);
      setBranding(data);
      // Populate form
      setLogoUrl(data.logoUrl || '');
      setPrimaryColor(data.primaryColor || '#4F46E5');
      setSecondaryColor(data.secondaryColor || '#7C3AED');
      setBackgroundUrl(data.backgroundUrl || '');
      setCustomCss(data.customCss || '');
      setEmailHeaderHtml(data.emailHeaderHtml || '');
      setEmailFooterHtml(data.emailFooterHtml || '');
      setFaviconUrl(data.faviconUrl || '');
      setLoginTitle(data.loginTitle || '');
      setLoginSubtitle(data.loginSubtitle || '');
      setWhiteLabelEnabled(data.whiteLabelEnabled || false);
      setCustomDomain(data.customDomain || '');
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to load branding');
    } finally {
      setLoading(false);
    }
  };

  const handleSave = async () => {
    if (!selectedTenantId) return;

    try {
      setSaving(true);
      setError('');
      const request: UpsertBrandingRequest = {
        logoUrl: logoUrl || null,
        primaryColor: primaryColor || null,
        secondaryColor: secondaryColor || null,
        backgroundUrl: backgroundUrl || null,
        customCss: customCss || null,
        emailHeaderHtml: emailHeaderHtml || null,
        emailFooterHtml: emailFooterHtml || null,
        faviconUrl: faviconUrl || null,
        loginTitle: loginTitle || null,
        loginSubtitle: loginSubtitle || null,
        whiteLabelEnabled,
        customDomain: customDomain || null,
      };
      const result = await brandingApi.upsertBranding(selectedTenantId, request);
      setBranding(result);
      setSuccessMessage('Branding saved successfully');
      setTimeout(() => setSuccessMessage(''), 3000);
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to save branding');
    } finally {
      setSaving(false);
    }
  };

  const handleReset = async () => {
    if (!selectedTenantId) return;
    if (!confirm('Are you sure you want to reset all branding to platform defaults?')) return;

    try {
      setSaving(true);
      setError('');
      await brandingApi.deleteBranding(selectedTenantId);
      setBranding(null);
      setLogoUrl('');
      setPrimaryColor('#4F46E5');
      setSecondaryColor('#7C3AED');
      setBackgroundUrl('');
      setCustomCss('');
      setEmailHeaderHtml('');
      setEmailFooterHtml('');
      setFaviconUrl('');
      setLoginTitle('');
      setLoginSubtitle('');
      setWhiteLabelEnabled(false);
      setCustomDomain('');
      setSuccessMessage('Branding reset to defaults');
      setTimeout(() => setSuccessMessage(''), 3000);
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to reset branding');
    } finally {
      setSaving(false);
    }
  };

  const selectedTenant = tenants.find((t) => t.id === selectedTenantId);

  return (
    <DashboardLayout>
      <div className="px-4 sm:px-6 lg:px-8">
        {/* Header */}
        <div className="sm:flex sm:items-center sm:justify-between">
          <div className="sm:flex-auto">
            <h1 className="text-2xl font-semibold text-gray-900">Tenant Branding</h1>
            <p className="mt-2 text-sm text-gray-700">
              Customize login pages, colors, logos, and email templates per tenant.
            </p>
          </div>
          <div className="mt-4 sm:mt-0 flex gap-3">
            <button
              onClick={handleReset}
              disabled={saving || !selectedTenantId}
              className="inline-flex items-center px-4 py-2 border border-gray-300 text-sm font-medium rounded-md text-gray-700 bg-white hover:bg-gray-50 disabled:opacity-50"
            >
              Reset to Defaults
            </button>
            <button
              onClick={handleSave}
              disabled={saving || !selectedTenantId}
              className="inline-flex items-center px-4 py-2 border border-transparent text-sm font-medium rounded-md text-white bg-indigo-600 hover:bg-indigo-700 disabled:opacity-50"
            >
              {saving ? 'Saving...' : 'Save Branding'}
            </button>
          </div>
        </div>

        {/* Tenant Selector */}
        <div className="mt-6">
          <label htmlFor="tenant-select" className="block text-sm font-medium text-gray-700">
            Select Tenant
          </label>
          <select
            id="tenant-select"
            value={selectedTenantId}
            onChange={(e) => setSelectedTenantId(e.target.value)}
            className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
          >
            <option value="">-- Select a tenant --</option>
            {tenants.map((t) => (
              <option key={t.id} value={t.id}>
                {t.name} ({t.slug})
              </option>
            ))}
          </select>
        </div>

        {/* Messages */}
        {error && (
          <div className="mt-4 bg-red-50 border border-red-200 text-red-700 px-4 py-3 rounded text-sm">
            {error}
          </div>
        )}
        {successMessage && (
          <div className="mt-4 bg-green-50 border border-green-200 text-green-700 px-4 py-3 rounded text-sm">
            {successMessage}
          </div>
        )}

        {loading ? (
          <div className="text-center py-8">
            <div className="inline-block animate-spin rounded-full h-8 w-8 border-b-2 border-indigo-600"></div>
            <p className="mt-2 text-sm text-gray-500">Loading branding...</p>
          </div>
        ) : selectedTenantId ? (
          <>
            {/* Tabs */}
            <div className="mt-6 border-b border-gray-200">
              <nav className="-mb-px flex space-x-8">
                {(['editor', 'email', 'preview'] as TabType[]).map((tab) => (
                  <button
                    key={tab}
                    onClick={() => setActiveTab(tab)}
                    className={`py-4 px-1 border-b-2 font-medium text-sm capitalize ${
                      activeTab === tab
                        ? 'border-indigo-500 text-indigo-600'
                        : 'border-transparent text-gray-500 hover:text-gray-700 hover:border-gray-300'
                    }`}
                  >
                    {tab === 'editor' ? 'Theme Editor' : tab === 'email' ? 'Email Templates' : 'Login Preview'}
                  </button>
                ))}
              </nav>
            </div>

            {/* Theme Editor Tab */}
            {activeTab === 'editor' && (
              <div className="mt-6 grid grid-cols-1 gap-6 lg:grid-cols-2">
                {/* Left column: form */}
                <div className="space-y-6">
                  {/* Logo */}
                  <div className="bg-white rounded-lg shadow-sm p-6">
                    <h3 className="text-lg font-medium text-gray-900 mb-4">Logo & Favicon</h3>
                    <div className="space-y-4">
                      <div>
                        <label className="block text-sm font-medium text-gray-700">Logo URL</label>
                        <input
                          type="url"
                          value={logoUrl}
                          onChange={(e) => setLogoUrl(e.target.value)}
                          placeholder="https://example.com/logo.png"
                          className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                        />
                        {logoUrl && (
                          <div className="mt-2 p-2 bg-gray-50 rounded">
                            <img src={logoUrl} alt="Logo preview" className="h-12 object-contain" onError={(e) => { (e.target as HTMLImageElement).style.display = 'none'; }} />
                          </div>
                        )}
                      </div>
                      <div>
                        <label className="block text-sm font-medium text-gray-700">Favicon URL</label>
                        <input
                          type="url"
                          value={faviconUrl}
                          onChange={(e) => setFaviconUrl(e.target.value)}
                          placeholder="https://example.com/favicon.ico"
                          className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                        />
                      </div>
                    </div>
                  </div>

                  {/* Colors */}
                  <div className="bg-white rounded-lg shadow-sm p-6">
                    <h3 className="text-lg font-medium text-gray-900 mb-4">Brand Colors</h3>
                    <div className="grid grid-cols-2 gap-4">
                      <div>
                        <label className="block text-sm font-medium text-gray-700">Primary Color</label>
                        <div className="mt-1 flex items-center gap-2">
                          <input
                            type="color"
                            value={primaryColor}
                            onChange={(e) => setPrimaryColor(e.target.value)}
                            className="h-10 w-10 rounded cursor-pointer border border-gray-300"
                          />
                          <input
                            type="text"
                            value={primaryColor}
                            onChange={(e) => setPrimaryColor(e.target.value)}
                            className="block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                          />
                        </div>
                      </div>
                      <div>
                        <label className="block text-sm font-medium text-gray-700">Secondary Color</label>
                        <div className="mt-1 flex items-center gap-2">
                          <input
                            type="color"
                            value={secondaryColor}
                            onChange={(e) => setSecondaryColor(e.target.value)}
                            className="h-10 w-10 rounded cursor-pointer border border-gray-300"
                          />
                          <input
                            type="text"
                            value={secondaryColor}
                            onChange={(e) => setSecondaryColor(e.target.value)}
                            className="block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                          />
                        </div>
                      </div>
                    </div>
                  </div>

                  {/* Login Page */}
                  <div className="bg-white rounded-lg shadow-sm p-6">
                    <h3 className="text-lg font-medium text-gray-900 mb-4">Login Page</h3>
                    <div className="space-y-4">
                      <div>
                        <label className="block text-sm font-medium text-gray-700">Login Title</label>
                        <input
                          type="text"
                          value={loginTitle}
                          onChange={(e) => setLoginTitle(e.target.value)}
                          placeholder="Welcome to Acme Corp"
                          className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                        />
                      </div>
                      <div>
                        <label className="block text-sm font-medium text-gray-700">Login Subtitle</label>
                        <input
                          type="text"
                          value={loginSubtitle}
                          onChange={(e) => setLoginSubtitle(e.target.value)}
                          placeholder="Sign in to your account"
                          className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                        />
                      </div>
                      <div>
                        <label className="block text-sm font-medium text-gray-700">Background Image URL</label>
                        <input
                          type="url"
                          value={backgroundUrl}
                          onChange={(e) => setBackgroundUrl(e.target.value)}
                          placeholder="https://example.com/background.jpg"
                          className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                        />
                      </div>
                    </div>
                  </div>

                  {/* Advanced */}
                  <div className="bg-white rounded-lg shadow-sm p-6">
                    <h3 className="text-lg font-medium text-gray-900 mb-4">Advanced</h3>
                    <div className="space-y-4">
                      <div className="flex items-center gap-3">
                        <input
                          type="checkbox"
                          id="whiteLabel"
                          checked={whiteLabelEnabled}
                          onChange={(e) => setWhiteLabelEnabled(e.target.checked)}
                          className="h-4 w-4 rounded border-gray-300 text-indigo-600 focus:ring-indigo-500"
                        />
                        <label htmlFor="whiteLabel" className="text-sm font-medium text-gray-700">
                          White-label mode (hide platform branding)
                        </label>
                      </div>
                      <div>
                        <label className="block text-sm font-medium text-gray-700">Custom Domain</label>
                        <input
                          type="text"
                          value={customDomain}
                          onChange={(e) => setCustomDomain(e.target.value)}
                          placeholder="login.acme.com"
                          className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                        />
                        <p className="mt-1 text-xs text-gray-500">
                          Configure a CNAME record pointing to the IAM platform before enabling.
                        </p>
                      </div>
                      <div>
                        <label className="block text-sm font-medium text-gray-700">Custom CSS</label>
                        <textarea
                          value={customCss}
                          onChange={(e) => setCustomCss(e.target.value)}
                          rows={4}
                          placeholder=".login-form { border-radius: 12px; }"
                          className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm font-mono text-xs"
                        />
                      </div>
                    </div>
                  </div>
                </div>

                {/* Right column: live color preview */}
                <div>
                  <div className="bg-white rounded-lg shadow-sm p-6 sticky top-6">
                    <h3 className="text-lg font-medium text-gray-900 mb-4">Color Preview</h3>
                    <div className="space-y-4">
                      <div className="flex gap-4">
                        <div
                          className="w-24 h-24 rounded-lg shadow-inner flex items-center justify-center text-white text-xs font-medium"
                          style={{ backgroundColor: primaryColor }}
                        >
                          Primary
                        </div>
                        <div
                          className="w-24 h-24 rounded-lg shadow-inner flex items-center justify-center text-white text-xs font-medium"
                          style={{ backgroundColor: secondaryColor }}
                        >
                          Secondary
                        </div>
                      </div>
                      <div className="space-y-2">
                        <button
                          className="w-full px-4 py-2 text-white text-sm font-medium rounded-md"
                          style={{ backgroundColor: primaryColor }}
                        >
                          Primary Button
                        </button>
                        <button
                          className="w-full px-4 py-2 text-white text-sm font-medium rounded-md"
                          style={{ backgroundColor: secondaryColor }}
                        >
                          Secondary Button
                        </button>
                        <div
                          className="w-full h-2 rounded-full"
                          style={{
                            background: `linear-gradient(to right, ${primaryColor}, ${secondaryColor})`,
                          }}
                        />
                      </div>
                      {logoUrl && (
                        <div className="pt-4 border-t">
                          <p className="text-xs text-gray-500 mb-2">Logo Preview</p>
                          <img
                            src={logoUrl}
                            alt="Logo"
                            className="h-10 object-contain"
                            onError={(e) => {
                              (e.target as HTMLImageElement).style.display = 'none';
                            }}
                          />
                        </div>
                      )}
                    </div>
                  </div>
                </div>
              </div>
            )}

            {/* Email Templates Tab */}
            {activeTab === 'email' && (
              <div className="mt-6 space-y-6">
                <div className="bg-white rounded-lg shadow-sm p-6">
                  <h3 className="text-lg font-medium text-gray-900 mb-4">Email Header HTML</h3>
                  <p className="text-sm text-gray-500 mb-3">
                    HTML inserted at the top of all transactional emails (invitations, password resets, etc.)
                  </p>
                  <textarea
                    value={emailHeaderHtml}
                    onChange={(e) => setEmailHeaderHtml(e.target.value)}
                    rows={8}
                    placeholder='<div style="background-color: #4F46E5; padding: 20px; text-align: center;"><img src="https://..." alt="Logo" height="40" /></div>'
                    className="block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm font-mono text-xs"
                  />
                </div>
                <div className="bg-white rounded-lg shadow-sm p-6">
                  <h3 className="text-lg font-medium text-gray-900 mb-4">Email Footer HTML</h3>
                  <p className="text-sm text-gray-500 mb-3">
                    HTML inserted at the bottom of all transactional emails.
                  </p>
                  <textarea
                    value={emailFooterHtml}
                    onChange={(e) => setEmailFooterHtml(e.target.value)}
                    rows={8}
                    placeholder='<div style="padding: 20px; text-align: center; color: #6b7280; font-size: 12px;">&copy; 2026 Acme Corp</div>'
                    className="block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm font-mono text-xs"
                  />
                </div>
                {/* Email Preview */}
                {(emailHeaderHtml || emailFooterHtml) && (
                  <div className="bg-white rounded-lg shadow-sm p-6">
                    <h3 className="text-lg font-medium text-gray-900 mb-4">Email Preview</h3>
                    <div className="border rounded-lg overflow-hidden">
                      {emailHeaderHtml && (
                        <div dangerouslySetInnerHTML={{ __html: emailHeaderHtml }} />
                      )}
                      <div className="p-6">
                        <p className="text-gray-700">Hello User,</p>
                        <p className="mt-2 text-gray-600">
                          This is a preview of how your branded emails will look. The actual content will vary depending on the email type.
                        </p>
                        <div className="mt-4">
                          <a
                            href="#"
                            className="inline-block px-4 py-2 text-white text-sm font-medium rounded-md"
                            style={{ backgroundColor: primaryColor }}
                          >
                            Action Button
                          </a>
                        </div>
                      </div>
                      {emailFooterHtml && (
                        <div dangerouslySetInnerHTML={{ __html: emailFooterHtml }} />
                      )}
                    </div>
                  </div>
                )}
              </div>
            )}

            {/* Login Preview Tab */}
            {activeTab === 'preview' && (
              <div className="mt-6">
                <div
                  className="rounded-lg overflow-hidden shadow-lg"
                  style={{
                    minHeight: '600px',
                    backgroundImage: backgroundUrl ? `url(${backgroundUrl})` : undefined,
                    backgroundSize: 'cover',
                    backgroundPosition: 'center',
                    backgroundColor: backgroundUrl ? undefined : '#f3f4f6',
                  }}
                >
                  <div
                    className="min-h-[600px] flex items-center justify-center p-8"
                    style={{
                      backgroundColor: backgroundUrl ? 'rgba(0,0,0,0.4)' : 'transparent',
                    }}
                  >
                    <div className="bg-white rounded-xl shadow-2xl p-8 w-full max-w-md">
                      {/* Logo */}
                      <div className="text-center mb-6">
                        {logoUrl ? (
                          <img
                            src={logoUrl}
                            alt="Logo"
                            className="h-12 mx-auto object-contain"
                            onError={(e) => {
                              (e.target as HTMLImageElement).style.display = 'none';
                            }}
                          />
                        ) : (
                          !whiteLabelEnabled && (
                            <h2
                              className="text-2xl font-bold"
                              style={{ color: primaryColor }}
                            >
                              IAM System
                            </h2>
                          )
                        )}
                      </div>

                      {/* Title */}
                      <div className="text-center mb-6">
                        <h1 className="text-xl font-semibold text-gray-900">
                          {loginTitle || `Welcome${selectedTenant ? ` to ${selectedTenant.name}` : ''}`}
                        </h1>
                        <p className="mt-1 text-sm text-gray-500">
                          {loginSubtitle || 'Sign in to your account'}
                        </p>
                      </div>

                      {/* Mock form */}
                      <div className="space-y-4">
                        <div>
                          <label className="block text-sm font-medium text-gray-700 mb-1">Email</label>
                          <input
                            type="email"
                            disabled
                            placeholder="user@example.com"
                            className="block w-full rounded-md border-gray-300 shadow-sm sm:text-sm bg-gray-50"
                          />
                        </div>
                        <div>
                          <label className="block text-sm font-medium text-gray-700 mb-1">Password</label>
                          <input
                            type="password"
                            disabled
                            placeholder="********"
                            className="block w-full rounded-md border-gray-300 shadow-sm sm:text-sm bg-gray-50"
                          />
                        </div>
                        <button
                          disabled
                          className="w-full py-2 px-4 rounded-md text-white font-medium text-sm"
                          style={{ backgroundColor: primaryColor }}
                        >
                          Sign In
                        </button>
                      </div>

                      {/* Footer */}
                      {!whiteLabelEnabled && (
                        <p className="mt-6 text-center text-xs text-gray-400">
                          Powered by IAM System
                        </p>
                      )}
                    </div>
                  </div>
                </div>

                {/* Custom CSS note */}
                {customCss && (
                  <div className="mt-4 bg-yellow-50 border border-yellow-200 text-yellow-700 px-4 py-3 rounded text-sm">
                    Note: Custom CSS is not rendered in this preview. It will be applied on the actual login page.
                  </div>
                )}
              </div>
            )}
          </>
        ) : (
          <div className="mt-6 text-center py-12">
            <svg className="mx-auto h-12 w-12 text-gray-400" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M7 21a4 4 0 01-4-4V5a2 2 0 012-2h4a2 2 0 012 2v12a4 4 0 01-4 4zm0 0h12a2 2 0 002-2v-4a2 2 0 00-2-2h-2.343M11 7.343l1.657-1.657a2 2 0 012.828 0l2.829 2.829a2 2 0 010 2.828l-8.486 8.485M7 17h.01" />
            </svg>
            <h3 className="mt-2 text-sm font-medium text-gray-900">Select a tenant</h3>
            <p className="mt-1 text-sm text-gray-500">Choose a tenant above to configure its branding.</p>
          </div>
        )}
      </div>
    </DashboardLayout>
  );
}
