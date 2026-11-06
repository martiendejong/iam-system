namespace IAM.Core.Services;

/// <summary>
/// Sliding-window counter used to rate limit unauthenticated "send me a code/link" requests.
/// It counts every request whether or not an account exists for the address, so callers can
/// silently skip the send over the limit without the response revealing anything about the account.
/// </summary>
public interface IRequestThrottle
{
    /// <summary>
    /// Records one request under <paramref name="key"/> and returns true when it is within
    /// <paramref name="limit"/> requests per <paramref name="window"/>; false when over the limit.
    /// Over-limit requests are not recorded (the window does not grow).
    /// </summary>
    bool TryAcquire(string key, int limit, TimeSpan window);
}
