// Copyright (c) Microsoft Corporation.

using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App.UserAuth;
using Microsoft.IdentityModel.JsonWebTokens;

using ZavaFinance.Core.Identity;

namespace ZavaFinance.Channel;

/// <summary>
/// Derives <see cref="CallerIdentity"/> for the current turn. SECURITY CRITICAL.
/// <para>
/// The inbound JWT from Azure Bot Service is a <b>service-to-service</b> token: it is issued
/// by <c>https://api.botframework.com</c> to the bot's app id and proves only that Bot
/// Service is the caller. It carries no user claims, so <c>tid</c> and <c>oid</c> cannot be
/// read from <c>turnContext.Identity</c>.
/// </para>
/// <para>
/// The user's identity comes from the <b>Teams SSO user token</b> acquired by the
/// user-authorization handler. That token is issued to the signed-in user, is validated by
/// Entra ID, and carries the real <c>oid</c> and <c>tid</c>. It is also the same token that
/// is later exchanged for the Copilot Studio scope, so identity and downstream authorization
/// cannot diverge.
/// </para>
/// <para>
/// The activity payload is never the source of identity. It is only cross-checked, and a
/// disagreement fails the turn.
/// </para>
/// </summary>
public interface ICallerIdentityResolver
{
    Task<CallerIdentity> ResolveAsync(
        ITurnContext turnContext,
        UserAuthorization userAuthorization,
        CancellationToken cancellationToken);
}

public sealed class CallerIdentityResolver : ICallerIdentityResolver
{
    private static readonly string[] TenantClaims =
    [
        "tid",
        "http://schemas.microsoft.com/identity/claims/tenantid"
    ];

    private static readonly string[] ObjectIdClaims =
    [
        "oid",
        "http://schemas.microsoft.com/identity/claims/objectidentifier"
    ];

    private readonly string _handlerName;

    public CallerIdentityResolver(string handlerName) => _handlerName = handlerName;

    public async Task<CallerIdentity> ResolveAsync(
        ITurnContext turnContext,
        UserAuthorization userAuthorization,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(turnContext);
        ArgumentNullException.ThrowIfNull(userAuthorization);

        string? token;

        try
        {
            token = await userAuthorization.GetTurnTokenAsync(
                turnContext, _handlerName, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new CallerIdentityException(
                $"Could not acquire the user token for handler '{_handlerName}': {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new CallerIdentityException(
                $"No user token available for handler '{_handlerName}'. The user is not "
                + "signed in, so their identity cannot be established.");
        }

        JsonWebToken parsed;

        try
        {
            parsed = new JsonWebToken(token);
        }
        catch (ArgumentException ex)
        {
            throw new CallerIdentityException($"The user token is malformed: {ex.Message}");
        }

        CallerIdentity caller = CallerIdentity.Create(
            FirstClaim(parsed, TenantClaims),
            FirstClaim(parsed, ObjectIdClaims));

        AssertPayloadAgrees(turnContext, caller);

        return caller;
    }

    /// <summary>
    /// Defence in depth. The token is authoritative; a disagreeing payload means one of the
    /// two is forged, so the turn fails rather than being reconciled.
    /// </summary>
    public static void AssertPayloadAgrees(ITurnContext turnContext, CallerIdentity caller)
    {
        string? payloadTenantId = turnContext.Activity?.Conversation?.TenantId;

        if (!string.IsNullOrWhiteSpace(payloadTenantId)
            && !string.Equals(
                payloadTenantId, caller.TenantId, StringComparison.OrdinalIgnoreCase))
        {
            throw new CallerIdentityException(
                "Activity tenant does not match the validated user token.");
        }

        string? payloadObjectId = turnContext.Activity?.From?.AadObjectId;

        if (!string.IsNullOrWhiteSpace(payloadObjectId)
            && !string.Equals(
                payloadObjectId, caller.UserObjectId, StringComparison.OrdinalIgnoreCase))
        {
            throw new CallerIdentityException(
                "Activity sender does not match the validated user token.");
        }
    }

    private static string? FirstClaim(JsonWebToken token, string[] claimTypes)
    {
        foreach (string claimType in claimTypes)
        {
            if (token.TryGetClaim(claimType, out System.Security.Claims.Claim? claim)
                && !string.IsNullOrWhiteSpace(claim.Value))
            {
                return claim.Value;
            }
        }

        return null;
    }
}
