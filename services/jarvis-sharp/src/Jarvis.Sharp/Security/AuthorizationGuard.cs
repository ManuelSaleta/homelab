namespace Jarvis.Sharp.Security;

public class AuthorizationGuard
{
    private readonly HashSet<string> _allowedUsers;

    public AuthorizationGuard(string? commaSeparatedWhitelist = null)
    {
        _allowedUsers = new HashSet<string>(StringComparer.Ordinal);
        var raw = commaSeparatedWhitelist ?? Environment.GetEnvironmentVariable("USER_WHITELIST");
        if (!string.IsNullOrWhiteSpace(raw))
        {
            foreach (var item in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                _allowedUsers.Add(item);
            }
        }
    }

    public AuthorizationGuard(IEnumerable<string> allowedUsers)
    {
        _allowedUsers = new HashSet<string>(allowedUsers, StringComparer.Ordinal);
    }

    public IReadOnlySet<string> AllowedUsers => _allowedUsers;

    public bool IsAuthorized(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId) || _allowedUsers.Count == 0)
        {
            return false;
        }
        return _allowedUsers.Contains(userId.Trim());
    }

    public string RequireAuthorized(string? userId)
    {
        if (!IsAuthorized(userId))
        {
            throw new UnauthorizedAccessException(
                $"Unauthorized Slack user '{userId ?? "<empty>"}'. User ID is not present in USER_WHITELIST.");
        }
        return userId!.Trim();
    }
}
