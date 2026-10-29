namespace Oscova.Agents.Core.Interfaces;

/// <summary>Reads current operator grants at application execution boundaries.</summary>
public interface IUserPermissionService
{
    /// <summary>Gets the installation's single operator, or null before operator setup.</summary>
    Task<string?> GetOperatorIdAsync(CancellationToken ct = default);
    /// <summary>Reports whether the account is the installation's operator.</summary>
    Task<bool> IsOperatorAsync(string userId, CancellationToken ct = default);

    /// <summary>Returns the supported permission keys currently granted to the account.</summary>
    Task<IReadOnlyList<string>> GetGrantedAsync(string userId, CancellationToken ct = default);

    /// <summary>Rejects execution unless the account currently holds the named permission.</summary>
    Task RequireAsync(string userId, string permission, CancellationToken ct = default);

    /// <summary>Atomically replaces this account's explicit permission grants.</summary>
    Task SaveAsync(string userId, IReadOnlyList<string> permissions, CancellationToken ct = default);
}
