import { useEffect, useState } from 'react';
import { PrincipalKind } from '../../types';
import type { User } from '../../types';

interface PrincipalEditorProps {
  /** Current manager user id (null = no manager) */
  managerUserId: string | null;
  /** Current kind: Human / Agent / Service */
  principalKind: string;
  /** Users offered in the manager picker (may be empty when the caller cannot list users) */
  users: User[];
  /** User id to hide from the picker (a user cannot be their own manager) */
  excludeUserId?: string;
  /** Persist both fields; PUT replaces both, null manager clears it */
  onSave: (managerUserId: string | null, principalKind: string) => Promise<void>;
  /** Smaller paddings for use inside table rows */
  compact?: boolean;
}

const KIND_OPTIONS = [PrincipalKind.Human, PrincipalKind.Agent, PrincipalKind.Service];

/**
 * Shared manager-picker + kind-selector (task 4057), used on the user edit page
 * and the service accounts page so both stay identical. The manager is
 * clearable ("No manager"); saving always sends BOTH fields.
 */
export default function PrincipalEditor({
  managerUserId,
  principalKind,
  users,
  excludeUserId,
  onSave,
  compact = false,
}: PrincipalEditorProps) {
  const [selectedManager, setSelectedManager] = useState<string>(managerUserId ?? '');
  const [selectedKind, setSelectedKind] = useState<string>(principalKind || PrincipalKind.Human);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const [success, setSuccess] = useState('');

  // Keep local state in sync when the loaded entity changes
  useEffect(() => {
    setSelectedManager(managerUserId ?? '');
    setSelectedKind(principalKind || PrincipalKind.Human);
  }, [managerUserId, principalKind]);

  const pickableUsers = users.filter(u => u.id !== excludeUserId);
  const currentManagerMissing =
    !!selectedManager && !pickableUsers.some(u => u.id === selectedManager);

  const handleSave = async () => {
    try {
      setSaving(true);
      setError('');
      setSuccess('');
      await onSave(selectedManager || null, selectedKind);
      setSuccess('Saved');
      setTimeout(() => setSuccess(''), 2500);
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to save manager / kind');
    } finally {
      setSaving(false);
    }
  };

  const selectClass = `w-full border border-gray-300 rounded-lg text-sm focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-transparent ${compact ? 'px-2 py-1.5' : 'px-3 py-2'}`;

  return (
    <div className={compact ? 'space-y-2' : 'space-y-4'}>
      {error && (
        <div className="p-2 bg-red-50 border border-red-200 text-red-700 rounded-lg text-sm">{error}</div>
      )}
      {success && (
        <div className="p-2 bg-green-50 border border-green-200 text-green-700 rounded-lg text-sm">{success}</div>
      )}
      <div className={`grid gap-3 ${compact ? 'grid-cols-1 sm:grid-cols-3 items-end' : 'grid-cols-1 sm:grid-cols-2'}`}>
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">Manager</label>
          <select
            value={selectedManager}
            onChange={e => setSelectedManager(e.target.value)}
            className={selectClass}
          >
            <option value="">No manager</option>
            {currentManagerMissing && (
              <option value={selectedManager}>Current manager ({selectedManager.slice(0, 8)}…)</option>
            )}
            {pickableUsers.map(u => (
              <option key={u.id} value={u.id}>
                {`${u.firstName} ${u.lastName}`.trim() || u.email} ({u.email})
              </option>
            ))}
          </select>
        </div>
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">Kind</label>
          <select
            value={selectedKind}
            onChange={e => setSelectedKind(e.target.value)}
            className={selectClass}
          >
            {KIND_OPTIONS.map(k => (
              <option key={k} value={k}>{k}</option>
            ))}
          </select>
        </div>
        {compact && (
          <div>
            <button
              type="button"
              onClick={handleSave}
              disabled={saving}
              className="px-4 py-1.5 bg-indigo-600 text-white text-sm font-medium rounded-lg hover:bg-indigo-700 transition-colors disabled:opacity-50"
            >
              {saving ? 'Saving...' : 'Save'}
            </button>
          </div>
        )}
      </div>
      {!compact && (
        <div className="flex justify-end">
          <button
            type="button"
            onClick={handleSave}
            disabled={saving}
            className="px-5 py-2 bg-indigo-600 text-white text-sm font-medium rounded-lg hover:bg-indigo-700 transition-colors disabled:opacity-50 shadow-sm"
          >
            {saving ? 'Saving...' : 'Save Manager & Kind'}
          </button>
        </div>
      )}
    </div>
  );
}
