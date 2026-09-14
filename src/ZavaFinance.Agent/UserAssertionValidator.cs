// Copyright (c) Microsoft Corporation.

using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using ZavaFinance.Core.Identity;

namespace ZavaFinance.Agent;

/// <summary>
/// Validates the forwarded user assertion and derives the caller's identity from it.
/// <para>
/// <b>SECURITY CRITICAL.</b> The session key — the isolation boundary for all per-user state — is
/// derived from the <c>tid</c> and <c>oid</c> claims of this token. Reading those claims without
/// first verifying the token's signature, issuer, audience and lifetime would let any caller who
/// can reach the agent mint a self-signed JWT and address another user's stored state: their
/// Copilot Studio conversation handle, their latest KPI, their routing history.
/// </para>
/// <para>
/// It is not sufficient to argue that a forged assertion would fail the later On-Behalf-Of
/// exchange. That check happens <i>after</i> the session key has already been used to load and
/// later overwrite state, so a forgery that never obtains a downstream token can still read and
/// poison another user's session. Validation therefore happens first, and a token that fails it
/// ends the turn.
/// </para>
/// <para>
/// Signing keys are fetched from the tenant's OpenID Connect metadata and cached with automatic
/// refresh, so key rollover does not require a redeployment.
/// </para>
/// </summary>
public sealed class UserAssertionValidator
{
    private readonly ConfigurationManager<OpenIdConnectConfiguration> _configurationManager;
    private readonly string _tenantId;
    private readonly string[] _validAudiences;
    private readonly ILogger<UserAssertionValidator> _logger;

    public UserAssertionValidator(
        string tenantId,
        string audience,
        ILogger<UserAssertionValidator> logger)
    {
        _tenantId = tenantId;
        _logger = logger;

        // The configured value may be the application id, the identifier URI, or a
        // semicolon-separated list. Every accepted form is derived once here so a token that is
        // legitimately addressed to this API is never rejected on formatting alone.
        _validAudiences = audience
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(value => new[]
            {
                value,
                $"api://{value}",
                $"api://botid-{value}"
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _configurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
            $"https://login.microsoftonline.com/{tenantId}/v2.0/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever());
    }

    /// <summary>
    /// Verifies the assertion and returns the identity it represents.
    /// </summary>
    /// <exception cref="CallerIdentityException">
    /// The token is absent, malformed, untrusted, expired, issued for a different audience or
    /// tenant, or missing the claims the session key requires.
    /// </exception>
    public async Task<CallerIdentity> ValidateAsync(
        string? assertion, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(assertion))
        {
            throw new CallerIdentityException(
                "No user assertion was supplied. This agent answers only on behalf of a "
                + "signed-in user.");
        }

        OpenIdConnectConfiguration configuration;

        try
        {
            configuration = await _configurationManager.GetConfigurationAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Fail closed. An unreachable metadata endpoint must never downgrade to
            // "accept the claims unverified".
            _logger.LogError(ex, "Could not retrieve OpenID Connect metadata.");

            throw new CallerIdentityException(
                "The signing keys needed to verify the user assertion are unavailable.");
        }

        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuers =
            [
                $"https://login.microsoftonline.com/{_tenantId}/v2.0",
                $"https://sts.windows.net/{_tenantId}/"
            ],
            ValidateAudience = true,

            // A Teams SSO token can carry the audience as the bare application id or as the
            // exposed identifier URI, depending on the token version the connection issues.
            // Accepting both avoids an opaque validation failure that looks identical to a
            // misconfigured OAuth connection.
            ValidAudiences = _validAudiences,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = configuration.SigningKeys,
            ClockSkew = TimeSpan.FromMinutes(2)
        };

        TokenValidationResult result =
            await new JsonWebTokenHandler().ValidateTokenAsync(assertion, parameters);

        if (!result.IsValid)
        {
            // The reason is logged for operators but never returned to the caller, because a
            // precise validation failure is a probing oracle.
            _logger.LogWarning(
                result.Exception, "User assertion failed validation.");

            throw new CallerIdentityException("The user assertion could not be validated.");
        }

        result.Claims.TryGetValue("tid", out object? tenant);
        result.Claims.TryGetValue("oid", out object? objectId);

        // Create throws when either claim is missing, so an assertion without a stable user
        // identity cannot produce a session key.
        return CallerIdentity.Create(tenant?.ToString(), objectId?.ToString());
    }
}
