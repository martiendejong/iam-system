import { describe, it, expect, beforeEach, vi, afterEach } from 'vitest';
import {
  rememberTwoFactorChallengeFrom,
  getTwoFactorChallenge,
  clearTwoFactorChallenge,
} from './api';

describe('2FA challenge token storage (task 5566)', () => {
  beforeEach(() => localStorage.clear());
  afterEach(() => vi.useRealTimers());

  it('keeps the token from a requiresTwoFactor response, per user', () => {
    rememberTwoFactorChallengeFrom({ requiresTwoFactor: true, userId: 'u1', challengeToken: 'tok-1' });
    expect(getTwoFactorChallenge('u1')).toBe('tok-1');
    expect(getTwoFactorChallenge('u2')).toBeUndefined();
  });

  it('ignores responses that are not a 2FA challenge', () => {
    rememberTwoFactorChallengeFrom({ accessToken: 'x', userId: 'u1', challengeToken: 'tok-1' });
    rememberTwoFactorChallengeFrom(undefined);
    expect(getTwoFactorChallenge('u1')).toBeUndefined();
  });

  it('forgets the token after about 10 minutes', () => {
    vi.useFakeTimers();
    rememberTwoFactorChallengeFrom({ requiresTwoFactor: true, userId: 'u1', challengeToken: 'tok-1' });
    vi.advanceTimersByTime(10 * 60 * 1000 + 1000);
    expect(getTwoFactorChallenge('u1')).toBeUndefined();
  });

  it('clears the token on request', () => {
    rememberTwoFactorChallengeFrom({ requiresTwoFactor: true, userId: 'u1', challengeToken: 'tok-1' });
    clearTwoFactorChallenge('u1');
    expect(getTwoFactorChallenge('u1')).toBeUndefined();
  });
});
