// Copyright (c) Microsoft Corporation.

namespace ZavaFinance.Core.Identity;

/// <summary>
/// The validated identity of the user on whose behalf a turn is being processed.
/// Both values originate from validated token claims, never from the activity payload.
/// </summary>
/// <param name="TenantId">The <c>tid</c> claim.</param>
/// <param name="UserObjectId">The <c>oid</c> claim.</param>
public sealed record CallerIdentity(string TenantId, string UserObjectId)
{
    public static CallerIdentity Create(string? tenantId, string? userObjectId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new CallerIdentityException("The 'tid' claim is missing or empty.");
        }

        if (string.IsNullOrWhiteSpace(userObjectId))
        {
            throw new CallerIdentityException("The 'oid' claim is missing or empty.");
        }

        return new CallerIdentity(tenantId.Trim(), userObjectId.Trim());
    }

    /// <summary>
    /// Stable partition value for the DSR deletion index. Contains no conversation content.
    /// </summary>
    public string DeletionIndexKey => $"{TenantId}|{UserObjectId}";
}

/// <summary>
/// Raised when identity cannot be established, or when claims disagree with the activity
/// payload. Never reconcile the two — fail the turn.
/// </summary>
public sealed class CallerIdentityException : Exception
{
    public CallerIdentityException(string message) : base(message)
    {
    }
}
