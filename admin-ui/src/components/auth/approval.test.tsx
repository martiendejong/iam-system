import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor, act, cleanup } from '@testing-library/react';

vi.mock('qrcode', () => ({ default: { toDataURL: vi.fn().mockResolvedValue('data:image/png;base64,AAAA') } }));

vi.mock('../../services/approvalApi', async () => {
  const actual = await vi.importActual<typeof import('../../services/approvalApi')>('../../services/approvalApi');
  return {
    ...actual,
    approvalApi: {
      start: vi.fn(),
      status: vi.fn(),
      complete: vi.fn(),
      pending: vi.fn(),
      get: vi.fn(),
      approve: vi.fn(),
      deny: vi.fn(),
      vapidPublicKey: vi.fn(),
      savePushSubscription: vi.fn(),
      removePushSubscription: vi.fn(),
    },
  };
});

import ApprovalCard from './ApprovalCard';
import ApprovalSignIn from './ApprovalSignIn';
import { approvalApi, approvalUrl, rememberPendingApproval, takePendingApproval } from '../../services/approvalApi';

const api = vi.mocked(approvalApi);

afterEach(() => cleanup());

const request = {
  id: '11111111-2222-3333-4444-555555555555',
  device: 'Brave on Android',
  ip: '10.0.0.1',
  createdAt: new Date().toISOString(),
  expiresAt: new Date(Date.now() + 120_000).toISOString(),
  choices: [42, 17, 88],
  namedYou: true,
};

describe('ApprovalCard', () => {
  beforeEach(() => vi.clearAllMocks());

  it('shows the device and three numbers, and approves with the number that was chosen', async () => {
    api.approve.mockResolvedValue();
    const onDone = vi.fn();
    render(<ApprovalCard request={request} onDone={onDone} />);

    expect(screen.getByText('Brave on Android')).toBeTruthy();
    expect(screen.getAllByRole('button', { name: /^\d\d$/ })).toHaveLength(3);

    fireEvent.click(screen.getByTestId('choice-17'));
    await waitFor(() => expect(onDone).toHaveBeenCalledWith('approved'));
    expect(api.approve).toHaveBeenCalledWith(request.id, 17);
  });

  it('keeps the card open with an error on a wrong number', async () => {
    api.approve.mockRejectedValue({ response: { status: 400, data: { error: 'That is not the number on the new device.', code: 'wrong_number' } } });
    const onDone = vi.fn();
    render(<ApprovalCard request={request} onDone={onDone} />);

    fireEvent.click(screen.getByTestId('choice-88'));
    await screen.findByText(/not the number on the new device/);
    expect(onDone).not.toHaveBeenCalled();
  });

  it('reports "gone" when the request was denied after too many wrong numbers', async () => {
    api.approve.mockRejectedValue({ response: { status: 400, data: { error: 'denied', code: 'denied' } } });
    const onDone = vi.fn();
    render(<ApprovalCard request={request} onDone={onDone} />);

    fireEvent.click(screen.getByTestId('choice-42'));
    await waitFor(() => expect(onDone).toHaveBeenCalledWith('gone'));
  });

  it('denies on "No, this is not me"', async () => {
    api.deny.mockResolvedValue();
    const onDone = vi.fn();
    render(<ApprovalCard request={request} onDone={onDone} />);

    fireEvent.click(screen.getByText(/this is not me/));
    await waitFor(() => expect(onDone).toHaveBeenCalledWith('denied'));
    expect(api.deny).toHaveBeenCalledWith(request.id);
  });
});

describe('ApprovalSignIn', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.useFakeTimers({ shouldAdvanceTime: true });
  });
  afterEach(() => vi.useRealTimers());

  const started = { id: request.id, pollSecret: 'secret', matchNumber: 42, expiresAt: new Date(Date.now() + 180_000).toISOString() };

  it('shows the number and a QR code, then finishes the sign-in once the other device approved', async () => {
    api.start.mockResolvedValue(started);
    api.status.mockResolvedValueOnce('pending').mockResolvedValue('approved');
    api.complete.mockResolvedValue({ accessToken: 't', user: { id: 'u1', email: 'a@b.c' } as any });
    const onSignedIn = vi.fn();
    render(<ApprovalSignIn onSignedIn={onSignedIn} />);

    fireEvent.change(screen.getByLabelText(/email/i), { target: { value: 'martien@example.com' } });
    fireEvent.click(screen.getByText('Show sign-in code'));

    expect((await screen.findByTestId('match-number')).textContent).toBe('42');
    expect(api.start).toHaveBeenCalledWith('martien@example.com', true);
    expect(screen.getByAltText(/QR code/)).toBeTruthy();

    await act(async () => { await vi.advanceTimersByTimeAsync(2100); });
    expect(onSignedIn).not.toHaveBeenCalled();
    await act(async () => { await vi.advanceTimersByTimeAsync(2100); });

    await waitFor(() => expect(onSignedIn).toHaveBeenCalledTimes(1));
    expect(api.complete).toHaveBeenCalledWith(started.id, 'secret');
  });

  it('goes back to the start with a message when the other device refuses', async () => {
    api.start.mockResolvedValue(started);
    api.status.mockResolvedValue('denied');
    const onSignedIn = vi.fn();
    render(<ApprovalSignIn onSignedIn={onSignedIn} />);

    fireEvent.click(screen.getByText('Show sign-in code'));
    await screen.findByTestId('match-number');
    await act(async () => { await vi.advanceTimersByTimeAsync(2100); });

    await screen.findByText(/refused this sign-in/);
    expect(onSignedIn).not.toHaveBeenCalled();
    expect(api.complete).not.toHaveBeenCalled();
  });

  it('shows the server message when too many requests were made', async () => {
    api.start.mockRejectedValue({ response: { status: 429, data: { error: 'Too many sign-in requests. Try again in a few minutes.' } } });
    render(<ApprovalSignIn onSignedIn={vi.fn()} />);

    fireEvent.click(screen.getByText('Show sign-in code'));
    await screen.findByText(/Too many sign-in requests/);
  });
});

describe('approval links', () => {
  it('builds the QR address inside the app base path', () => {
    expect(approvalUrl('abc')).toMatch(/\/approve\/abc$/);
    expect(approvalUrl('abc').startsWith(window.location.origin)).toBe(true);
  });

  it('remembers a request across the sign-in and hands it out once', () => {
    rememberPendingApproval(request.id);
    expect(takePendingApproval()).toBe(request.id);
    expect(takePendingApproval()).toBeNull();
  });

  it('ignores a stored value that is not a request id', () => {
    sessionStorage.setItem('iam.pendingApprove', 'javascript:alert(1)');
    expect(takePendingApproval()).toBeNull();
  });
});
