using IAM.Core.Entities;

namespace IAM.Core.Services;

/// <summary>
/// The failed-password lockout rule of the login (task 5157): 5 failed tries lock the account for 15 minutes,
/// a correct password resets the counter. Anything that checks a user's current password outside the login
/// (for example the portal password change) must go through this so it cannot be used to guess around the lockout.
/// </summary>
public static class LoginLockout
{
    public const int MaxFailedAttempts = 5;
    public static readonly TimeSpan Duration = TimeSpan.FromMinutes(15);

    /// <summary>True while the lockout period has not ended.</summary>
    public static bool IsLocked(User user) => user.IsLockedOut && user.LockoutEnd > DateTime.UtcNow;

    /// <summary>Counts one wrong password and locks the account at the limit. Only stages the change.</summary>
    public static void RegisterFailure(User user)
    {
        user.FailedLoginAttempts++;
        if (user.FailedLoginAttempts >= MaxFailedAttempts)
        {
            user.IsLockedOut = true;
            user.LockoutEnd = DateTime.UtcNow.Add(Duration);
        }
    }

    /// <summary>Clears the counter and any lockout after a correct password. Only stages the change.</summary>
    public static void Reset(User user)
    {
        user.FailedLoginAttempts = 0;
        user.IsLockedOut = false;
        user.LockoutEnd = null;
    }
}
