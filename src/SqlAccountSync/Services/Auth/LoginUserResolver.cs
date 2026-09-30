using SqlAccountSync.Models;

namespace SqlAccountSync.Services.Auth;

/// <summary>
/// Resolves login users from SQL Accounting <c>SY_USER</c> on the AWS tenant login database.
/// </summary>
public interface ILoginUserResolver
{
    Task<AppUser?> ResolveForLoginAsync(string email, CancellationToken cancellationToken = default);
}

public sealed class LoginUserResolver : ILoginUserResolver
{
    private readonly ILoginUserStore _users;

    public LoginUserResolver(ILoginUserStore users) => _users = users;

    public Task<AppUser?> ResolveForLoginAsync(string email, CancellationToken cancellationToken = default)
    {
        email = (email ?? "").Trim();
        if (email.Length == 0 || !email.Contains('@', StringComparison.Ordinal))
            return Task.FromResult<AppUser?>(null);
        return _users.FindActiveByEmailAsync(email, cancellationToken);
    }
}
