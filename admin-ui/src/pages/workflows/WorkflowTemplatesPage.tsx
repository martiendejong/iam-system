import { useState, useEffect } from 'react';
import DashboardLayout from '../../components/layout/DashboardLayout';
import { workflowTemplateApi } from '../../services/accessRequestApi';
import type { WorkflowTemplate, WorkflowTemplateDto } from '../../services/accessRequestApi';

export default function WorkflowTemplatesPage() {
  const [templates, setTemplates] = useState<WorkflowTemplate[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [successMessage, setSuccessMessage] = useState('');
  const [showForm, setShowForm] = useState(false);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [formData, setFormData] = useState<WorkflowTemplateDto>({
    name: '',
    description: '',
    resourceType: 'Role',
    steps: '[]',
    autoExpireHours: 72,
    isActive: true,
  });

  useEffect(() => {
    loadTemplates();
  }, []);

  const loadTemplates = async () => {
    try {
      setLoading(true);
      setError('');
      const data = await workflowTemplateApi.getTemplates();
      setTemplates(data);
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to load templates');
    } finally {
      setLoading(false);
    }
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    try {
      setError('');
      if (editingId) {
        await workflowTemplateApi.updateTemplate(editingId, formData);
        setSuccessMessage('Template updated successfully');
      } else {
        await workflowTemplateApi.createTemplate(formData);
        setSuccessMessage('Template created successfully');
      }
      resetForm();
      setTimeout(() => setSuccessMessage(''), 3000);
      await loadTemplates();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to save template');
    }
  };

  const handleEdit = async (id: string) => {
    try {
      const template = await workflowTemplateApi.getTemplate(id);
      setFormData({
        name: template.name,
        description: template.description || '',
        resourceType: template.resourceType,
        tenantId: template.tenantId || undefined,
        steps: template.steps,
        autoExpireHours: template.autoExpireHours || undefined,
        autoApproveRules: template.autoApproveRules || undefined,
        isActive: template.isActive,
      });
      setEditingId(id);
      setShowForm(true);
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to load template');
    }
  };

  const handleDelete = async (id: string) => {
    if (!confirm('Are you sure you want to delete this workflow template?')) return;
    try {
      setError('');
      await workflowTemplateApi.deleteTemplate(id);
      setSuccessMessage('Template deleted successfully');
      setTimeout(() => setSuccessMessage(''), 3000);
      await loadTemplates();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to delete template');
    }
  };

  const resetForm = () => {
    setFormData({
      name: '',
      description: '',
      resourceType: 'Role',
      steps: '[]',
      autoExpireHours: 72,
      isActive: true,
    });
    setEditingId(null);
    setShowForm(false);
  };

  const isValidJson = (str: string): boolean => {
    try {
      JSON.parse(str);
      return true;
    } catch {
      return false;
    }
  };

  const parseSteps = (stepsJson: string): any[] => {
    try {
      return JSON.parse(stepsJson);
    } catch {
      return [];
    }
  };

  return (
    <DashboardLayout>
      <div className="px-4 sm:px-6 lg:px-8">
        {/* Header */}
        <div className="sm:flex sm:items-center">
          <div className="sm:flex-auto">
            <h1 className="text-2xl font-semibold text-gray-900">Workflow Templates</h1>
            <p className="mt-2 text-sm text-gray-700">
              Define approval workflows for different resource types. Templates determine how access
              requests are routed through approval chains.
            </p>
          </div>
          <div className="mt-4 sm:mt-0 sm:ml-16 sm:flex-none">
            <button
              onClick={() => { resetForm(); setShowForm(true); }}
              className="inline-flex items-center justify-center rounded-md border border-transparent bg-indigo-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-indigo-700"
            >
              New Template
            </button>
          </div>
        </div>

        {/* Alerts */}
        {error && (
          <div className="mt-4 rounded-md bg-red-50 p-4">
            <p className="text-sm text-red-700">{error}</p>
          </div>
        )}
        {successMessage && (
          <div className="mt-4 rounded-md bg-green-50 p-4">
            <p className="text-sm text-green-700">{successMessage}</p>
          </div>
        )}

        {/* Form */}
        {showForm && (
          <div className="mt-6 bg-white shadow rounded-lg p-6">
            <h3 className="text-lg font-medium text-gray-900 mb-4">
              {editingId ? 'Edit Template' : 'Create New Template'}
            </h3>
            <form onSubmit={handleSubmit} className="space-y-4">
              <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                <div>
                  <label className="block text-sm font-medium text-gray-700">Name</label>
                  <input
                    type="text"
                    required
                    value={formData.name}
                    onChange={(e) => setFormData({ ...formData, name: e.target.value })}
                    className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                  />
                </div>
                <div>
                  <label className="block text-sm font-medium text-gray-700">Resource Type</label>
                  <select
                    value={formData.resourceType}
                    onChange={(e) => setFormData({ ...formData, resourceType: e.target.value })}
                    className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                  >
                    <option value="Role">Role</option>
                    <option value="Building">Building</option>
                    <option value="Device">Device</option>
                    <option value="Permission">Permission</option>
                    <option value="Other">Other</option>
                  </select>
                </div>
              </div>

              <div>
                <label className="block text-sm font-medium text-gray-700">Description</label>
                <textarea
                  rows={2}
                  value={formData.description || ''}
                  onChange={(e) => setFormData({ ...formData, description: e.target.value })}
                  className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                />
              </div>

              <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                <div>
                  <label className="block text-sm font-medium text-gray-700">Auto-Expire Hours</label>
                  <input
                    type="number"
                    value={formData.autoExpireHours || ''}
                    onChange={(e) => setFormData({ ...formData, autoExpireHours: e.target.value ? parseInt(e.target.value) : undefined })}
                    className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                    placeholder="e.g., 72"
                  />
                </div>
                <div>
                  <label className="block text-sm font-medium text-gray-700">Tenant ID (optional)</label>
                  <input
                    type="text"
                    value={formData.tenantId || ''}
                    onChange={(e) => setFormData({ ...formData, tenantId: e.target.value || undefined })}
                    className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                    placeholder="Global if empty"
                  />
                </div>
              </div>

              <div>
                <label className="block text-sm font-medium text-gray-700">
                  Approval Steps (JSON)
                </label>
                <p className="text-xs text-gray-500 mb-1">
                  Format: [{'{'}  "order": 1, "approverRoleId": "guid", "approverUserId": "guid", "quorumCount": 1 {'}'}]
                </p>
                <textarea
                  rows={4}
                  value={formData.steps || '[]'}
                  onChange={(e) => setFormData({ ...formData, steps: e.target.value })}
                  className={`mt-1 block w-full rounded-md shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm font-mono text-xs ${
                    isValidJson(formData.steps || '[]') ? 'border-gray-300' : 'border-red-300 bg-red-50'
                  }`}
                />
                {!isValidJson(formData.steps || '[]') && (
                  <p className="text-xs text-red-500 mt-1">Invalid JSON format</p>
                )}
              </div>

              <div>
                <label className="block text-sm font-medium text-gray-700">
                  Auto-Approve Rules (JSON, optional)
                </label>
                <p className="text-xs text-gray-500 mb-1">
                  Format: [{'{'}  "resourceType": "Role", "roleId": "guid", "maxPriority": "Normal" {'}'}]
                </p>
                <textarea
                  rows={3}
                  value={formData.autoApproveRules || ''}
                  onChange={(e) => setFormData({ ...formData, autoApproveRules: e.target.value || undefined })}
                  className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm font-mono text-xs"
                  placeholder="Leave empty for no auto-approve"
                />
              </div>

              <div className="flex items-center">
                <input
                  type="checkbox"
                  checked={formData.isActive ?? true}
                  onChange={(e) => setFormData({ ...formData, isActive: e.target.checked })}
                  className="h-4 w-4 text-indigo-600 focus:ring-indigo-500 border-gray-300 rounded"
                />
                <label className="ml-2 block text-sm text-gray-900">Active</label>
              </div>

              <div className="flex space-x-3">
                <button
                  type="submit"
                  disabled={!isValidJson(formData.steps || '[]')}
                  className="inline-flex justify-center rounded-md bg-indigo-600 px-4 py-2 text-sm font-medium text-white hover:bg-indigo-700 disabled:opacity-50"
                >
                  {editingId ? 'Update' : 'Create'} Template
                </button>
                <button
                  type="button"
                  onClick={resetForm}
                  className="inline-flex justify-center rounded-md bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm ring-1 ring-inset ring-gray-300 hover:bg-gray-50"
                >
                  Cancel
                </button>
              </div>
            </form>
          </div>
        )}

        {/* Template List */}
        <div className="mt-6">
          {loading ? (
            <div className="text-center py-12">
              <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-indigo-600 mx-auto"></div>
              <p className="mt-2 text-sm text-gray-500">Loading...</p>
            </div>
          ) : (
            <div className="overflow-hidden shadow ring-1 ring-black ring-opacity-5 rounded-lg">
              <table className="min-w-full divide-y divide-gray-300">
                <thead className="bg-gray-50">
                  <tr>
                    <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">Name</th>
                    <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">Resource Type</th>
                    <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">Steps</th>
                    <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">Auto-Expire</th>
                    <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">Status</th>
                    <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">Actions</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-gray-200 bg-white">
                  {templates.length === 0 ? (
                    <tr>
                      <td colSpan={6} className="px-3 py-8 text-center text-sm text-gray-500">
                        No workflow templates defined yet. Create one to get started.
                      </td>
                    </tr>
                  ) : templates.map((template) => (
                    <tr key={template.id} className="hover:bg-gray-50">
                      <td className="px-3 py-4 text-sm">
                        <div className="font-medium text-gray-900">{template.name}</div>
                        {template.description && (
                          <div className="text-gray-500 text-xs mt-0.5">{template.description}</div>
                        )}
                        {template.tenantName && (
                          <div className="text-xs text-gray-400 mt-0.5">Tenant: {template.tenantName}</div>
                        )}
                      </td>
                      <td className="whitespace-nowrap px-3 py-4 text-sm text-gray-700">
                        {template.resourceType}
                      </td>
                      <td className="whitespace-nowrap px-3 py-4 text-sm text-gray-700">
                        {parseSteps(template.steps).length} step(s)
                      </td>
                      <td className="whitespace-nowrap px-3 py-4 text-sm text-gray-700">
                        {template.autoExpireHours ? `${template.autoExpireHours}h` : '-'}
                      </td>
                      <td className="whitespace-nowrap px-3 py-4 text-sm">
                        <span className={`inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium ${
                          template.isActive ? 'bg-green-100 text-green-800' : 'bg-gray-100 text-gray-500'
                        }`}>
                          {template.isActive ? 'Active' : 'Inactive'}
                        </span>
                      </td>
                      <td className="whitespace-nowrap px-3 py-4 text-sm">
                        <button
                          onClick={() => handleEdit(template.id)}
                          className="text-indigo-600 hover:text-indigo-900 mr-3"
                        >
                          Edit
                        </button>
                        <button
                          onClick={() => handleDelete(template.id)}
                          className="text-red-600 hover:text-red-900"
                        >
                          Delete
                        </button>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>
      </div>
    </DashboardLayout>
  );
}
