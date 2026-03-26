import { useState, useEffect, useCallback } from 'react';
import DashboardLayout from '../../components/layout/DashboardLayout';
import { api } from '../../services/api';

interface Tenant {
  id: string;
  name: string;
  slug: string;
}

interface BulkOperation {
  id: string;
  tenantId: string;
  type: string;
  status: string;
  format: string;
  totalRows: number;
  processedRows: number;
  successRows: number;
  errorRows: number;
  errorDetails: string | null;
  fileName: string | null;
  dryRun: boolean;
  hasResult: boolean;
  createdByUserId: string;
  startedAt: string | null;
  completedAt: string | null;
  createdAt: string;
}

type ActiveTab = 'import' | 'export' | 'history';

export default function BulkOperationsPage() {
  const [tenants, setTenants] = useState<Tenant[]>([]);
  const [selectedTenantId, setSelectedTenantId] = useState<string>('');
  const [operations, setOperations] = useState<BulkOperation[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [success, setSuccess] = useState('');
  const [activeTab, setActiveTab] = useState<ActiveTab>('import');

  // Import state
  const [importFile, setImportFile] = useState<File | null>(null);
  const [importFormat, setImportFormat] = useState<string>('');
  const [importing, setImporting] = useState(false);
  const [dryRunResult, setDryRunResult] = useState<BulkOperation | null>(null);

  // Export state
  const [exportFormat, setExportFormat] = useState<string>('csv');
  const [exporting, setExporting] = useState(false);

  const client = api.getClient();

  useEffect(() => {
    loadTenants();
  }, []);

  useEffect(() => {
    if (selectedTenantId) {
      loadOperations();
    }
  }, [selectedTenantId]);

  const loadTenants = async () => {
    try {
      const data = await api.getTenants();
      setTenants(data);
      if (data.length > 0 && !selectedTenantId) {
        setSelectedTenantId(data[0].id);
      }
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to load tenants');
    }
  };

  const loadOperations = useCallback(async () => {
    try {
      setLoading(true);
      const response = await client.get('/bulk/operations', {
        params: { tenantId: selectedTenantId, take: 50 }
      });
      setOperations(response.data);
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to load operations');
    } finally {
      setLoading(false);
    }
  }, [selectedTenantId, client]);

  const handleDryRun = async () => {
    if (!importFile || !selectedTenantId) return;
    setError('');
    setSuccess('');
    setImporting(true);
    setDryRunResult(null);

    try {
      const formData = new FormData();
      formData.append('file', importFile);
      formData.append('tenantId', selectedTenantId);
      if (importFormat) formData.append('format', importFormat);

      const response = await client.post('/bulk/import/dry-run', formData, {
        headers: { 'Content-Type': 'multipart/form-data' }
      });
      setDryRunResult(response.data);
      setSuccess(`Dry run complete: ${response.data.successRows} valid, ${response.data.errorRows} errors out of ${response.data.totalRows} rows`);
    } catch (err: any) {
      setError(err.response?.data?.error || 'Dry run failed');
    } finally {
      setImporting(false);
    }
  };

  const handleImport = async () => {
    if (!importFile || !selectedTenantId) return;
    setError('');
    setSuccess('');
    setImporting(true);

    try {
      const formData = new FormData();
      formData.append('file', importFile);
      formData.append('tenantId', selectedTenantId);
      if (importFormat) formData.append('format', importFormat);

      const response = await client.post('/bulk/import', formData, {
        headers: { 'Content-Type': 'multipart/form-data' }
      });
      setSuccess(`Import complete: ${response.data.successRows} users created, ${response.data.errorRows} errors`);
      setImportFile(null);
      setDryRunResult(null);
      loadOperations();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Import failed');
    } finally {
      setImporting(false);
    }
  };

  const handleExport = async () => {
    if (!selectedTenantId) return;
    setError('');
    setSuccess('');
    setExporting(true);

    try {
      const response = await client.post('/bulk/export', {
        tenantId: selectedTenantId,
        format: exportFormat
      });
      setSuccess(`Export complete: ${response.data.successRows} users exported`);
      loadOperations();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Export failed');
    } finally {
      setExporting(false);
    }
  };

  const handleDownload = async (operationId: string) => {
    try {
      const response = await client.get(`/bulk/operations/${operationId}/download`, {
        responseType: 'blob'
      });

      const contentDisposition = response.headers['content-disposition'];
      let filename = `export-${operationId}.dat`;
      if (contentDisposition) {
        const match = contentDisposition.match(/filename[^;=\n]*=((['"]).*?\2|[^;\n]*)/);
        if (match) filename = match[1].replace(/['"]/g, '');
      }

      const url = window.URL.createObjectURL(new Blob([response.data]));
      const link = document.createElement('a');
      link.href = url;
      link.setAttribute('download', filename);
      document.body.appendChild(link);
      link.click();
      link.remove();
      window.URL.revokeObjectURL(url);
    } catch (err: any) {
      setError(err.response?.data?.error || 'Download failed');
    }
  };

  const getStatusBadge = (status: string) => {
    const colors: Record<string, string> = {
      Pending: 'bg-yellow-100 text-yellow-800',
      Processing: 'bg-blue-100 text-blue-800',
      Completed: 'bg-green-100 text-green-800',
      Failed: 'bg-red-100 text-red-800'
    };
    return (
      <span className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium ${colors[status] || 'bg-gray-100 text-gray-800'}`}>
        {status}
      </span>
    );
  };

  const getTypeBadge = (type: string, dryRun: boolean) => {
    if (dryRun) {
      return <span className="inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium bg-purple-100 text-purple-800">Dry Run</span>;
    }
    return (
      <span className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium ${type === 'Import' ? 'bg-indigo-100 text-indigo-800' : 'bg-teal-100 text-teal-800'}`}>
        {type}
      </span>
    );
  };

  const parseErrors = (errorDetails: string | null): Array<{ row: number; errors: string[] }> => {
    if (!errorDetails) return [];
    try {
      return JSON.parse(errorDetails);
    } catch {
      return [];
    }
  };

  return (
    <DashboardLayout>
      <div className="space-y-6">
        <div className="sm:flex sm:items-center sm:justify-between">
          <div>
            <h1 className="text-2xl font-bold text-gray-900">Bulk Operations</h1>
            <p className="mt-1 text-sm text-gray-500">Import/export users and manage data portability (GDPR Art. 20)</p>
          </div>
          <div className="mt-4 sm:mt-0">
            <select
              value={selectedTenantId}
              onChange={(e) => setSelectedTenantId(e.target.value)}
              className="block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
            >
              <option value="">Select Tenant</option>
              {tenants.map((t) => (
                <option key={t.id} value={t.id}>{t.name}</option>
              ))}
            </select>
          </div>
        </div>

        {error && (
          <div className="rounded-md bg-red-50 p-4">
            <p className="text-sm text-red-700">{error}</p>
            <button onClick={() => setError('')} className="mt-1 text-xs text-red-500 underline">Dismiss</button>
          </div>
        )}

        {success && (
          <div className="rounded-md bg-green-50 p-4">
            <p className="text-sm text-green-700">{success}</p>
            <button onClick={() => setSuccess('')} className="mt-1 text-xs text-green-500 underline">Dismiss</button>
          </div>
        )}

        {/* Tabs */}
        <div className="border-b border-gray-200">
          <nav className="-mb-px flex space-x-8">
            {(['import', 'export', 'history'] as ActiveTab[]).map((tab) => (
              <button
                key={tab}
                onClick={() => setActiveTab(tab)}
                className={`whitespace-nowrap py-4 px-1 border-b-2 font-medium text-sm ${
                  activeTab === tab
                    ? 'border-indigo-500 text-indigo-600'
                    : 'border-transparent text-gray-500 hover:text-gray-700 hover:border-gray-300'
                }`}
              >
                {tab.charAt(0).toUpperCase() + tab.slice(1)}
              </button>
            ))}
          </nav>
        </div>

        {/* Import Tab */}
        {activeTab === 'import' && selectedTenantId && (
          <div className="bg-white shadow rounded-lg p-6 space-y-6">
            <h2 className="text-lg font-medium text-gray-900">Import Users</h2>
            <p className="text-sm text-gray-500">
              Upload a CSV or JSON file to bulk-create users. Use dry-run to validate before importing.
            </p>

            {/* Template Download */}
            <div className="bg-gray-50 rounded-md p-4">
              <h3 className="text-sm font-medium text-gray-700 mb-2">File Format</h3>
              <p className="text-xs text-gray-500 mb-2">
                CSV columns: <code className="bg-gray-200 px-1 rounded">Email, FirstName, LastName, PhoneNumber, Role</code>
              </p>
              <p className="text-xs text-gray-500">
                JSON format: array of objects with the same fields, or <code className="bg-gray-200 px-1 rounded">{'{ "users": [...] }'}</code>
              </p>
            </div>

            {/* File Upload */}
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">Upload File</label>
              <input
                type="file"
                accept=".csv,.json"
                onChange={(e) => {
                  setImportFile(e.target.files?.[0] || null);
                  setDryRunResult(null);
                  setSuccess('');
                  setError('');
                }}
                className="block w-full text-sm text-gray-500 file:mr-4 file:py-2 file:px-4 file:rounded-md file:border-0 file:text-sm file:font-semibold file:bg-indigo-50 file:text-indigo-700 hover:file:bg-indigo-100"
              />
              {importFile && (
                <p className="mt-1 text-xs text-gray-500">
                  Selected: {importFile.name} ({(importFile.size / 1024).toFixed(1)} KB)
                </p>
              )}
            </div>

            {/* Format Override */}
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">Format (auto-detected from extension)</label>
              <select
                value={importFormat}
                onChange={(e) => setImportFormat(e.target.value)}
                className="block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
              >
                <option value="">Auto-detect</option>
                <option value="csv">CSV</option>
                <option value="json">JSON</option>
              </select>
            </div>

            {/* Actions */}
            <div className="flex space-x-4">
              <button
                onClick={handleDryRun}
                disabled={!importFile || importing}
                className="inline-flex items-center px-4 py-2 border border-gray-300 shadow-sm text-sm font-medium rounded-md text-gray-700 bg-white hover:bg-gray-50 disabled:opacity-50"
              >
                {importing ? 'Validating...' : 'Dry Run (Validate)'}
              </button>
              <button
                onClick={handleImport}
                disabled={!importFile || importing}
                className="inline-flex items-center px-4 py-2 border border-transparent shadow-sm text-sm font-medium rounded-md text-white bg-indigo-600 hover:bg-indigo-700 disabled:opacity-50"
              >
                {importing ? 'Importing...' : 'Import Users'}
              </button>
            </div>

            {/* Dry Run Results */}
            {dryRunResult && (
              <div className="border rounded-md p-4 space-y-3">
                <h3 className="text-sm font-medium text-gray-900">Dry Run Results</h3>
                <div className="grid grid-cols-4 gap-4">
                  <div className="text-center">
                    <div className="text-2xl font-bold text-gray-900">{dryRunResult.totalRows}</div>
                    <div className="text-xs text-gray-500">Total Rows</div>
                  </div>
                  <div className="text-center">
                    <div className="text-2xl font-bold text-green-600">{dryRunResult.successRows}</div>
                    <div className="text-xs text-gray-500">Valid</div>
                  </div>
                  <div className="text-center">
                    <div className="text-2xl font-bold text-red-600">{dryRunResult.errorRows}</div>
                    <div className="text-xs text-gray-500">Errors</div>
                  </div>
                  <div className="text-center">
                    <div className="text-2xl font-bold text-gray-600">
                      {dryRunResult.totalRows > 0 ? Math.round((dryRunResult.successRows / dryRunResult.totalRows) * 100) : 0}%
                    </div>
                    <div className="text-xs text-gray-500">Success Rate</div>
                  </div>
                </div>

                {dryRunResult.errorDetails && (
                  <div className="mt-4">
                    <h4 className="text-sm font-medium text-red-700 mb-2">Errors</h4>
                    <div className="max-h-48 overflow-y-auto bg-red-50 rounded p-3">
                      {parseErrors(dryRunResult.errorDetails).map((err, idx) => (
                        <div key={idx} className="text-xs text-red-600 mb-1">
                          <span className="font-medium">Row {err.row}:</span> {err.errors.join(', ')}
                        </div>
                      ))}
                    </div>
                  </div>
                )}
              </div>
            )}
          </div>
        )}

        {/* Export Tab */}
        {activeTab === 'export' && selectedTenantId && (
          <div className="bg-white shadow rounded-lg p-6 space-y-6">
            <h2 className="text-lg font-medium text-gray-900">Export Users</h2>
            <p className="text-sm text-gray-500">
              Export all tenant user data for portability (GDPR Article 20). Includes user profiles, roles, and account status.
            </p>

            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">Export Format</label>
              <select
                value={exportFormat}
                onChange={(e) => setExportFormat(e.target.value)}
                className="block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
              >
                <option value="csv">CSV</option>
                <option value="json">JSON</option>
              </select>
            </div>

            <button
              onClick={handleExport}
              disabled={exporting}
              className="inline-flex items-center px-4 py-2 border border-transparent shadow-sm text-sm font-medium rounded-md text-white bg-indigo-600 hover:bg-indigo-700 disabled:opacity-50"
            >
              {exporting ? 'Exporting...' : 'Export All Users'}
            </button>
          </div>
        )}

        {/* History Tab */}
        {activeTab === 'history' && selectedTenantId && (
          <div className="bg-white shadow rounded-lg overflow-hidden">
            <div className="px-6 py-4 border-b border-gray-200 flex justify-between items-center">
              <h2 className="text-lg font-medium text-gray-900">Operation History</h2>
              <button
                onClick={loadOperations}
                disabled={loading}
                className="text-sm text-indigo-600 hover:text-indigo-800"
              >
                Refresh
              </button>
            </div>

            {loading ? (
              <div className="p-6 text-center text-gray-500">Loading...</div>
            ) : operations.length === 0 ? (
              <div className="p-6 text-center text-gray-500">No bulk operations yet</div>
            ) : (
              <div className="overflow-x-auto">
                <table className="min-w-full divide-y divide-gray-200">
                  <thead className="bg-gray-50">
                    <tr>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Type</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Status</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Format</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Progress</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">File</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Date</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Actions</th>
                    </tr>
                  </thead>
                  <tbody className="bg-white divide-y divide-gray-200">
                    {operations.map((op) => (
                      <tr key={op.id} className="hover:bg-gray-50">
                        <td className="px-6 py-4 whitespace-nowrap">
                          {getTypeBadge(op.type, op.dryRun)}
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap">
                          {getStatusBadge(op.status)}
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">
                          {op.format}
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">
                          <div className="flex items-center space-x-2">
                            <span className="text-green-600">{op.successRows}</span>
                            <span>/</span>
                            <span>{op.totalRows}</span>
                            {op.errorRows > 0 && (
                              <span className="text-red-500">({op.errorRows} errors)</span>
                            )}
                          </div>
                          {op.status === 'Processing' && op.totalRows > 0 && (
                            <div className="mt-1 w-full bg-gray-200 rounded-full h-1.5">
                              <div
                                className="bg-indigo-600 h-1.5 rounded-full"
                                style={{ width: `${Math.round((op.processedRows / op.totalRows) * 100)}%` }}
                              />
                            </div>
                          )}
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">
                          {op.fileName || '-'}
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">
                          {new Date(op.createdAt).toLocaleString()}
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm">
                          <div className="flex space-x-2">
                            {op.hasResult && op.status === 'Completed' && op.type === 'Export' && (
                              <button
                                onClick={() => handleDownload(op.id)}
                                className="text-indigo-600 hover:text-indigo-900"
                              >
                                Download
                              </button>
                            )}
                            {op.errorDetails && (
                              <button
                                onClick={() => {
                                  const errors = parseErrors(op.errorDetails);
                                  alert(errors.map(e => `Row ${e.row}: ${e.errors.join(', ')}`).join('\n'));
                                }}
                                className="text-red-600 hover:text-red-900"
                              >
                                View Errors
                              </button>
                            )}
                          </div>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </div>
        )}

        {!selectedTenantId && (
          <div className="bg-white shadow rounded-lg p-6 text-center text-gray-500">
            Please select a tenant to manage bulk operations.
          </div>
        )}
      </div>
    </DashboardLayout>
  );
}
