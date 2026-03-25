import { useState, useEffect } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { useForm, Controller } from 'react-hook-form';
import DashboardLayout from '../../components/layout/DashboardLayout';
import { policyApi } from '../../services/policyApi';
import { api } from '../../services/api';

interface PolicyFormData {
  name: string;
  description: string;
  tenantId: string;
  effect: 'Allow' | 'Deny';
  resource: string;
  action: string;
  priority: number;
  inheritanceScope: 'Self' | 'Children' | 'Descendants';
  conditions: string;
  timeConstraintsEnabled: boolean;
  timeStartTime: string;
  timeEndTime: string;
  timeDaysOfWeek: number[];
  timeTimezone: string;
  isActive: boolean;
}

const COMMON_ACTIONS = ['View', 'Create', 'Update', 'Delete', 'Execute', 'Control', 'Unlock', 'Admin', '*'];
const COMMON_RESOURCES = ['Door', 'Camera', 'HVAC', 'Light', 'Elevator', 'Parking', 'Server', 'Database', 'File', '*'];
const DAYS_OF_WEEK = [
  { value: 1, label: 'Mon' },
  { value: 2, label: 'Tue' },
  { value: 3, label: 'Wed' },
  { value: 4, label: 'Thu' },
  { value: 5, label: 'Fri' },
  { value: 6, label: 'Sat' },
  { value: 7, label: 'Sun' },
];

export default function PolicyFormPage() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const isEditMode = !!id;

  const [loading, setLoading] = useState(isEditMode);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const [tenants, setTenants] = useState<any[]>([]);
  const [selectedActions, setSelectedActions] = useState<string[]>([]);
  const [customAction, setCustomAction] = useState('');
  const [showResourceSuggestions, setShowResourceSuggestions] = useState(false);

  const {
    register,
    handleSubmit,
    reset,
    watch,
    control,
    setValue,
    formState: { errors },
  } = useForm<PolicyFormData>({
    defaultValues: {
      effect: 'Allow',
      priority: 100,
      inheritanceScope: 'Self',
      conditions: '',
      timeConstraintsEnabled: false,
      timeStartTime: '09:00',
      timeEndTime: '17:00',
      timeDaysOfWeek: [1, 2, 3, 4, 5],
      timeTimezone: 'UTC',
      isActive: true,
    },
  });

  const watchEffect = watch('effect');
  const watchTimeEnabled = watch('timeConstraintsEnabled');
  const watchResource = watch('resource');

  useEffect(() => {
    loadTenants();
    if (isEditMode) {
      loadPolicy();
    }
  }, [id]);

  const loadTenants = async () => {
    try {
      const data = await api.getTenants();
      setTenants(data);
    } catch (err) {
      console.error('Failed to load tenants:', err);
    }
  };

  const loadPolicy = async () => {
    try {
      setLoading(true);
      const policy = await policyApi.getPolicy(id!);

      // Parse time constraints
      let timeEnabled = false;
      let startTime = '09:00';
      let endTime = '17:00';
      let daysOfWeek = [1, 2, 3, 4, 5];
      let timezone = 'UTC';

      if (policy.timeConstraints) {
        try {
          const tc = JSON.parse(policy.timeConstraints);
          timeEnabled = true;
          startTime = tc.startTime || '09:00';
          endTime = tc.endTime || '17:00';
          daysOfWeek = tc.daysOfWeek || [1, 2, 3, 4, 5];
          timezone = tc.timezone || 'UTC';
        } catch {
          // Invalid JSON, ignore
        }
      }

      // Parse action into array
      const actions = policy.action ? policy.action.split(',').map((a: string) => a.trim()) : [];
      setSelectedActions(actions);

      reset({
        name: policy.name,
        description: policy.description || '',
        tenantId: policy.tenantId,
        effect: policy.effect,
        resource: policy.resource,
        action: policy.action,
        priority: policy.priority,
        inheritanceScope: policy.inheritanceScope,
        conditions: policy.conditions || '',
        timeConstraintsEnabled: timeEnabled,
        timeStartTime: startTime,
        timeEndTime: endTime,
        timeDaysOfWeek: daysOfWeek,
        timeTimezone: timezone,
        isActive: policy.isActive,
      });
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to load policy');
    } finally {
      setLoading(false);
    }
  };

  const toggleAction = (action: string) => {
    setSelectedActions((prev) => {
      const next = prev.includes(action)
        ? prev.filter((a) => a !== action)
        : [...prev, action];
      setValue('action', next.join(', '));
      return next;
    });
  };

  const addCustomAction = () => {
    const trimmed = customAction.trim();
    if (trimmed && !selectedActions.includes(trimmed)) {
      const next = [...selectedActions, trimmed];
      setSelectedActions(next);
      setValue('action', next.join(', '));
      setCustomAction('');
    }
  };

  const removeAction = (action: string) => {
    const next = selectedActions.filter((a) => a !== action);
    setSelectedActions(next);
    setValue('action', next.join(', '));
  };

  const onSubmit = async (data: PolicyFormData) => {
    try {
      setSaving(true);
      setError('');

      // Build time constraints JSON
      let timeConstraints: string | undefined;
      if (data.timeConstraintsEnabled) {
        timeConstraints = JSON.stringify({
          startTime: data.timeStartTime,
          endTime: data.timeEndTime,
          daysOfWeek: data.timeDaysOfWeek,
          timezone: data.timeTimezone,
        });
      }

      // Build conditions - validate JSON if provided
      let conditions: string | undefined;
      if (data.conditions && data.conditions.trim()) {
        try {
          JSON.parse(data.conditions);
          conditions = data.conditions.trim();
        } catch {
          setError('Conditions must be valid JSON');
          setSaving(false);
          return;
        }
      }

      const payload = {
        name: data.name,
        description: data.description || undefined,
        tenantId: data.tenantId,
        effect: data.effect,
        resource: data.resource,
        action: selectedActions.join(', '),
        priority: data.priority,
        inheritanceScope: data.inheritanceScope,
        conditions,
        timeConstraints,
        isActive: data.isActive,
      };

      if (isEditMode) {
        await policyApi.updatePolicy(id!, payload);
      } else {
        await policyApi.createPolicy(payload);
      }

      navigate('/policies');
    } catch (err: any) {
      setError(err.response?.data?.message || `Failed to ${isEditMode ? 'update' : 'create'} policy`);
    } finally {
      setSaving(false);
    }
  };

  const filteredResourceSuggestions = COMMON_RESOURCES.filter(
    (r) =>
      !watchResource ||
      r.toLowerCase().includes((watchResource || '').toLowerCase())
  );

  if (loading) {
    return (
      <DashboardLayout>
        <div className="flex items-center justify-center min-h-screen">
          <div className="text-center">
            <div className="inline-block animate-spin rounded-full h-8 w-8 border-b-2 border-indigo-600"></div>
            <p className="mt-2 text-sm text-gray-500">Loading policy...</p>
          </div>
        </div>
      </DashboardLayout>
    );
  }

  return (
    <DashboardLayout>
      <div className="max-w-3xl mx-auto px-4 sm:px-6 lg:px-8">
        {/* Header */}
        <div className="md:flex md:items-center md:justify-between">
          <div className="flex-1 min-w-0">
            <h2 className="text-2xl font-bold leading-7 text-gray-900 sm:text-3xl sm:truncate">
              {isEditMode ? 'Edit Policy' : 'Create New Policy'}
            </h2>
            <p className="mt-1 text-sm text-gray-500">
              {isEditMode
                ? 'Modify the access control policy settings below.'
                : 'Define a new access control policy for your tenant hierarchy.'}
            </p>
          </div>
          <div className="mt-4 flex md:mt-0 md:ml-4">
            <button
              type="button"
              onClick={() => navigate('/policies')}
              className="inline-flex items-center px-4 py-2 border border-gray-300 rounded-md shadow-sm text-sm font-medium text-gray-700 bg-white hover:bg-gray-50 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-indigo-500"
            >
              Cancel
            </button>
          </div>
        </div>

        {/* Policy Form */}
        <div className="mt-6 bg-white shadow sm:rounded-lg">
          <div className="px-4 py-5 sm:p-6">
            {error && (
              <div className="mb-4 bg-red-50 border border-red-200 text-red-700 px-4 py-3 rounded">
                {error}
              </div>
            )}

            <form onSubmit={handleSubmit(onSubmit)} className="space-y-6">
              {/* Policy Name */}
              <div>
                <label htmlFor="name" className="block text-sm font-medium text-gray-700">
                  Policy Name *
                </label>
                <input
                  type="text"
                  id="name"
                  {...register('name', { required: 'Policy name is required' })}
                  className={`mt-1 block w-full rounded-md shadow-sm sm:text-sm ${
                    errors.name
                      ? 'border-red-300 focus:ring-red-500 focus:border-red-500'
                      : 'border-gray-300 focus:ring-indigo-500 focus:border-indigo-500'
                  }`}
                  placeholder="e.g., Allow Door Access - Building A"
                />
                {errors.name && (
                  <p className="mt-1 text-sm text-red-600">{errors.name.message}</p>
                )}
              </div>

              {/* Description */}
              <div>
                <label htmlFor="description" className="block text-sm font-medium text-gray-700">
                  Description
                </label>
                <textarea
                  id="description"
                  rows={3}
                  {...register('description')}
                  className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:ring-indigo-500 focus:border-indigo-500 sm:text-sm"
                  placeholder="Describe what this policy controls..."
                />
              </div>

              {/* Tenant */}
              <div>
                <label htmlFor="tenantId" className="block text-sm font-medium text-gray-700">
                  Tenant (Scope) *
                </label>
                <select
                  id="tenantId"
                  {...register('tenantId', { required: 'Tenant is required' })}
                  className={`mt-1 block w-full rounded-md shadow-sm sm:text-sm ${
                    errors.tenantId
                      ? 'border-red-300 focus:ring-red-500 focus:border-red-500'
                      : 'border-gray-300 focus:ring-indigo-500 focus:border-indigo-500'
                  }`}
                >
                  <option value="">Select a tenant</option>
                  {tenants.map((tenant) => (
                    <option key={tenant.id} value={tenant.id}>
                      {tenant.name} ({tenant.type})
                    </option>
                  ))}
                </select>
                {errors.tenantId && (
                  <p className="mt-1 text-sm text-red-600">{errors.tenantId.message}</p>
                )}
                <p className="mt-1 text-xs text-gray-500">
                  The tenant where this policy is defined. Inheritance scope determines how it propagates.
                </p>
              </div>

              {/* Effect */}
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-2">Effect *</label>
                <div className="flex gap-4">
                  <label
                    className={`flex-1 relative flex cursor-pointer rounded-lg border p-4 focus:outline-none ${
                      watchEffect === 'Allow'
                        ? 'border-green-500 bg-green-50 ring-2 ring-green-500'
                        : 'border-gray-300 bg-white hover:bg-gray-50'
                    }`}
                  >
                    <input
                      type="radio"
                      value="Allow"
                      {...register('effect', { required: 'Effect is required' })}
                      className="sr-only"
                    />
                    <div className="flex items-center">
                      <div
                        className={`h-8 w-8 rounded-full flex items-center justify-center ${
                          watchEffect === 'Allow' ? 'bg-green-500' : 'bg-gray-200'
                        }`}
                      >
                        <svg className="h-5 w-5 text-white" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 13l4 4L19 7" />
                        </svg>
                      </div>
                      <div className="ml-3">
                        <span className={`block text-sm font-medium ${watchEffect === 'Allow' ? 'text-green-900' : 'text-gray-900'}`}>
                          Allow
                        </span>
                        <span className="block text-xs text-gray-500">Grant access to the resource</span>
                      </div>
                    </div>
                  </label>

                  <label
                    className={`flex-1 relative flex cursor-pointer rounded-lg border p-4 focus:outline-none ${
                      watchEffect === 'Deny'
                        ? 'border-red-500 bg-red-50 ring-2 ring-red-500'
                        : 'border-gray-300 bg-white hover:bg-gray-50'
                    }`}
                  >
                    <input
                      type="radio"
                      value="Deny"
                      {...register('effect', { required: 'Effect is required' })}
                      className="sr-only"
                    />
                    <div className="flex items-center">
                      <div
                        className={`h-8 w-8 rounded-full flex items-center justify-center ${
                          watchEffect === 'Deny' ? 'bg-red-500' : 'bg-gray-200'
                        }`}
                      >
                        <svg className="h-5 w-5 text-white" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
                        </svg>
                      </div>
                      <div className="ml-3">
                        <span className={`block text-sm font-medium ${watchEffect === 'Deny' ? 'text-red-900' : 'text-gray-900'}`}>
                          Deny
                        </span>
                        <span className="block text-xs text-gray-500">Explicitly deny access (overrides Allow)</span>
                      </div>
                    </div>
                  </label>
                </div>
              </div>

              {/* Resource */}
              <div className="relative">
                <label htmlFor="resource" className="block text-sm font-medium text-gray-700">
                  Resource *
                </label>
                <input
                  type="text"
                  id="resource"
                  {...register('resource', { required: 'Resource is required' })}
                  onFocus={() => setShowResourceSuggestions(true)}
                  onBlur={() => setTimeout(() => setShowResourceSuggestions(false), 200)}
                  className={`mt-1 block w-full rounded-md shadow-sm sm:text-sm ${
                    errors.resource
                      ? 'border-red-300 focus:ring-red-500 focus:border-red-500'
                      : 'border-gray-300 focus:ring-indigo-500 focus:border-indigo-500'
                  }`}
                  placeholder="e.g., Door, Camera, HVAC, * (all)"
                />
                {errors.resource && (
                  <p className="mt-1 text-sm text-red-600">{errors.resource.message}</p>
                )}
                <p className="mt-1 text-xs text-gray-500">
                  Resource type to control access to. Use * for all resources.
                </p>

                {/* Resource Suggestions Dropdown */}
                {showResourceSuggestions && (
                  <div className="absolute z-10 mt-1 w-full bg-white shadow-lg rounded-md border border-gray-300 max-h-48 overflow-auto">
                    {filteredResourceSuggestions.map((resource) => (
                      <button
                        key={resource}
                        type="button"
                        onMouseDown={(e) => {
                          e.preventDefault();
                          setValue('resource', resource);
                          setShowResourceSuggestions(false);
                        }}
                        className="w-full text-left px-3 py-2 text-sm hover:bg-indigo-50 hover:text-indigo-700"
                      >
                        {resource}
                      </button>
                    ))}
                  </div>
                )}
              </div>

              {/* Actions */}
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-2">Actions *</label>
                <input type="hidden" {...register('action', { required: 'At least one action is required' })} />

                {/* Selected Actions Tags */}
                {selectedActions.length > 0 && (
                  <div className="flex flex-wrap gap-2 mb-3">
                    {selectedActions.map((action) => (
                      <span
                        key={action}
                        className="inline-flex items-center px-2.5 py-1 rounded-full text-xs font-medium bg-indigo-100 text-indigo-800"
                      >
                        {action}
                        <button
                          type="button"
                          onClick={() => removeAction(action)}
                          className="ml-1.5 inline-flex items-center justify-center w-4 h-4 rounded-full text-indigo-400 hover:bg-indigo-200 hover:text-indigo-600"
                        >
                          <svg className="h-3 w-3" fill="currentColor" viewBox="0 0 20 20">
                            <path
                              fillRule="evenodd"
                              d="M4.293 4.293a1 1 0 011.414 0L10 8.586l4.293-4.293a1 1 0 111.414 1.414L11.414 10l4.293 4.293a1 1 0 01-1.414 1.414L10 11.414l-4.293 4.293a1 1 0 01-1.414-1.414L8.586 10 4.293 5.707a1 1 0 010-1.414z"
                              clipRule="evenodd"
                            />
                          </svg>
                        </button>
                      </span>
                    ))}
                  </div>
                )}

                {/* Common Actions Grid */}
                <div className="flex flex-wrap gap-2 mb-3">
                  {COMMON_ACTIONS.map((action) => (
                    <button
                      key={action}
                      type="button"
                      onClick={() => toggleAction(action)}
                      className={`px-3 py-1.5 text-xs font-medium rounded-md border ${
                        selectedActions.includes(action)
                          ? 'bg-indigo-600 text-white border-indigo-600'
                          : 'bg-white text-gray-700 border-gray-300 hover:bg-gray-50'
                      }`}
                    >
                      {action}
                    </button>
                  ))}
                </div>

                {/* Custom Action Input */}
                <div className="flex gap-2">
                  <input
                    type="text"
                    value={customAction}
                    onChange={(e) => setCustomAction(e.target.value)}
                    onKeyDown={(e) => {
                      if (e.key === 'Enter') {
                        e.preventDefault();
                        addCustomAction();
                      }
                    }}
                    placeholder="Add custom action..."
                    className="flex-1 rounded-md border-gray-300 shadow-sm focus:ring-indigo-500 focus:border-indigo-500 sm:text-sm"
                  />
                  <button
                    type="button"
                    onClick={addCustomAction}
                    className="px-3 py-2 border border-gray-300 rounded-md text-sm font-medium text-gray-700 bg-white hover:bg-gray-50"
                  >
                    Add
                  </button>
                </div>
                {errors.action && (
                  <p className="mt-1 text-sm text-red-600">{errors.action.message}</p>
                )}
              </div>

              {/* Priority & Inheritance Row */}
              <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
                {/* Priority */}
                <div>
                  <label htmlFor="priority" className="block text-sm font-medium text-gray-700">
                    Priority *
                  </label>
                  <input
                    type="number"
                    id="priority"
                    min={0}
                    max={1000}
                    {...register('priority', {
                      required: 'Priority is required',
                      min: { value: 0, message: 'Minimum priority is 0' },
                      max: { value: 1000, message: 'Maximum priority is 1000' },
                      valueAsNumber: true,
                    })}
                    className={`mt-1 block w-full rounded-md shadow-sm sm:text-sm ${
                      errors.priority
                        ? 'border-red-300 focus:ring-red-500 focus:border-red-500'
                        : 'border-gray-300 focus:ring-indigo-500 focus:border-indigo-500'
                    }`}
                  />
                  {errors.priority && (
                    <p className="mt-1 text-sm text-red-600">{errors.priority.message}</p>
                  )}
                  <p className="mt-1 text-xs text-gray-500">
                    Higher priority wins in conflicts (0-1000)
                  </p>
                </div>

                {/* Inheritance Scope */}
                <div>
                  <label htmlFor="inheritanceScope" className="block text-sm font-medium text-gray-700">
                    Inheritance Scope *
                  </label>
                  <select
                    id="inheritanceScope"
                    {...register('inheritanceScope', { required: 'Inheritance scope is required' })}
                    className={`mt-1 block w-full rounded-md shadow-sm sm:text-sm ${
                      errors.inheritanceScope
                        ? 'border-red-300 focus:ring-red-500 focus:border-red-500'
                        : 'border-gray-300 focus:ring-indigo-500 focus:border-indigo-500'
                    }`}
                  >
                    <option value="Self">Self Only - No inheritance</option>
                    <option value="Children">Children - Direct children only</option>
                    <option value="Descendants">Descendants - Entire subtree</option>
                  </select>
                  {errors.inheritanceScope && (
                    <p className="mt-1 text-sm text-red-600">{errors.inheritanceScope.message}</p>
                  )}
                  <p className="mt-1 text-xs text-gray-500">
                    How this policy propagates through the tenant hierarchy
                  </p>
                </div>
              </div>

              {/* Inheritance Scope Visualization */}
              <div className="bg-gray-50 rounded-md p-4">
                <h4 className="text-xs font-semibold text-gray-700 uppercase tracking-wide mb-2">
                  Inheritance Preview
                </h4>
                <div className="flex items-center gap-2 text-xs text-gray-600">
                  <span className="inline-flex items-center px-2 py-0.5 rounded bg-indigo-100 text-indigo-700 font-medium">
                    Organization
                  </span>
                  <svg className="h-4 w-4 text-gray-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 5l7 7-7 7" />
                  </svg>
                  <span className="inline-flex items-center px-2 py-0.5 rounded bg-blue-100 text-blue-700 font-medium">
                    Building
                  </span>
                  <svg className="h-4 w-4 text-gray-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 5l7 7-7 7" />
                  </svg>
                  <span className="inline-flex items-center px-2 py-0.5 rounded bg-green-100 text-green-700 font-medium">
                    Floor
                  </span>
                  <svg className="h-4 w-4 text-gray-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 5l7 7-7 7" />
                  </svg>
                  <span className="inline-flex items-center px-2 py-0.5 rounded bg-yellow-100 text-yellow-700 font-medium">
                    Room
                  </span>
                  <svg className="h-4 w-4 text-gray-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 5l7 7-7 7" />
                  </svg>
                  <span className="inline-flex items-center px-2 py-0.5 rounded bg-orange-100 text-orange-700 font-medium">
                    Device
                  </span>
                </div>
              </div>

              {/* Conditions (JSON) */}
              <div>
                <label htmlFor="conditions" className="block text-sm font-medium text-gray-700">
                  Conditions (JSON)
                </label>
                <textarea
                  id="conditions"
                  rows={4}
                  {...register('conditions')}
                  className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:ring-indigo-500 focus:border-indigo-500 sm:text-sm font-mono text-xs"
                  placeholder='{ "ip_whitelist": ["10.0.0.0/8"], "device_health": "trusted" }'
                />
                <p className="mt-1 text-xs text-gray-500">
                  Optional JSON conditions. Available keys: ip_whitelist, device_health, location
                </p>
              </div>

              {/* Time Constraints Toggle */}
              <div>
                <div className="flex items-center justify-between">
                  <label className="block text-sm font-medium text-gray-700">
                    Time Constraints
                  </label>
                  <Controller
                    name="timeConstraintsEnabled"
                    control={control}
                    render={({ field }) => (
                      <button
                        type="button"
                        onClick={() => field.onChange(!field.value)}
                        className={`relative inline-flex h-6 w-11 flex-shrink-0 cursor-pointer rounded-full border-2 border-transparent transition-colors duration-200 ease-in-out focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:ring-offset-2 ${
                          field.value ? 'bg-indigo-600' : 'bg-gray-200'
                        }`}
                      >
                        <span
                          className={`pointer-events-none inline-block h-5 w-5 transform rounded-full bg-white shadow ring-0 transition duration-200 ease-in-out ${
                            field.value ? 'translate-x-5' : 'translate-x-0'
                          }`}
                        />
                      </button>
                    )}
                  />
                </div>

                {watchTimeEnabled && (
                  <div className="mt-3 p-4 border border-gray-200 rounded-lg bg-gray-50 space-y-4">
                    <div className="grid grid-cols-2 gap-4">
                      <div>
                        <label htmlFor="timeStartTime" className="block text-xs font-medium text-gray-600">
                          Start Time
                        </label>
                        <input
                          type="time"
                          id="timeStartTime"
                          {...register('timeStartTime')}
                          className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:ring-indigo-500 focus:border-indigo-500 sm:text-sm"
                        />
                      </div>
                      <div>
                        <label htmlFor="timeEndTime" className="block text-xs font-medium text-gray-600">
                          End Time
                        </label>
                        <input
                          type="time"
                          id="timeEndTime"
                          {...register('timeEndTime')}
                          className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:ring-indigo-500 focus:border-indigo-500 sm:text-sm"
                        />
                      </div>
                    </div>

                    {/* Days of Week */}
                    <div>
                      <label className="block text-xs font-medium text-gray-600 mb-2">
                        Days of Week
                      </label>
                      <Controller
                        name="timeDaysOfWeek"
                        control={control}
                        render={({ field }) => (
                          <div className="flex gap-1">
                            {DAYS_OF_WEEK.map((day) => (
                              <button
                                key={day.value}
                                type="button"
                                onClick={() => {
                                  const current = field.value || [];
                                  const next = current.includes(day.value)
                                    ? current.filter((d) => d !== day.value)
                                    : [...current, day.value].sort();
                                  field.onChange(next);
                                }}
                                className={`px-2.5 py-1.5 text-xs font-medium rounded ${
                                  (field.value || []).includes(day.value)
                                    ? 'bg-indigo-600 text-white'
                                    : 'bg-white text-gray-700 border border-gray-300'
                                }`}
                              >
                                {day.label}
                              </button>
                            ))}
                          </div>
                        )}
                      />
                    </div>

                    {/* Timezone */}
                    <div>
                      <label htmlFor="timeTimezone" className="block text-xs font-medium text-gray-600">
                        Timezone
                      </label>
                      <select
                        id="timeTimezone"
                        {...register('timeTimezone')}
                        className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:ring-indigo-500 focus:border-indigo-500 sm:text-sm"
                      >
                        <option value="UTC">UTC</option>
                        <option value="Europe/Amsterdam">Europe/Amsterdam (CET/CEST)</option>
                        <option value="Europe/London">Europe/London (GMT/BST)</option>
                        <option value="America/New_York">America/New_York (EST/EDT)</option>
                        <option value="America/Los_Angeles">America/Los_Angeles (PST/PDT)</option>
                        <option value="Asia/Tokyo">Asia/Tokyo (JST)</option>
                      </select>
                    </div>
                  </div>
                )}
              </div>

              {/* Active Toggle */}
              <div className="flex items-center justify-between py-3 border-t border-gray-200">
                <div>
                  <label className="text-sm font-medium text-gray-700">Policy Active</label>
                  <p className="text-xs text-gray-500">Inactive policies are not evaluated during access checks</p>
                </div>
                <Controller
                  name="isActive"
                  control={control}
                  render={({ field }) => (
                    <button
                      type="button"
                      onClick={() => field.onChange(!field.value)}
                      className={`relative inline-flex h-6 w-11 flex-shrink-0 cursor-pointer rounded-full border-2 border-transparent transition-colors duration-200 ease-in-out focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:ring-offset-2 ${
                        field.value ? 'bg-indigo-600' : 'bg-gray-200'
                      }`}
                    >
                      <span
                        className={`pointer-events-none inline-block h-5 w-5 transform rounded-full bg-white shadow ring-0 transition duration-200 ease-in-out ${
                          field.value ? 'translate-x-5' : 'translate-x-0'
                        }`}
                      />
                    </button>
                  )}
                />
              </div>

              {/* Submit Buttons */}
              <div className="flex justify-end space-x-3 pt-4 border-t border-gray-200">
                <button
                  type="button"
                  onClick={() => navigate('/policies')}
                  className="px-4 py-2 border border-gray-300 rounded-md shadow-sm text-sm font-medium text-gray-700 bg-white hover:bg-gray-50 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-indigo-500"
                >
                  Cancel
                </button>
                <button
                  type="submit"
                  disabled={saving}
                  className="inline-flex justify-center py-2 px-4 border border-transparent shadow-sm text-sm font-medium rounded-md text-white bg-indigo-600 hover:bg-indigo-700 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-indigo-500 disabled:opacity-50"
                >
                  {saving ? 'Saving...' : isEditMode ? 'Update Policy' : 'Create Policy'}
                </button>
              </div>
            </form>
          </div>
        </div>
      </div>
    </DashboardLayout>
  );
}
