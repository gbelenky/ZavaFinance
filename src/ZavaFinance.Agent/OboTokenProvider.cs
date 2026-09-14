// Copyright (c) Microsoft Corporation.

using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using ZavaFinance.Core.Abstractions;

namespace ZavaFinance.Agent;

/// <summary>
/// Exchanges the caller's forwarded assertion for a delegated token, per downstream resource.
/// <para>
/// This is the hosted-agent half of <see cref="IDownstreamTokenProvider"/>. In the channel host
/// the Microsoft 365 Agents SDK performs this exchange; here the agent performs it itself with
/// MSAL, because the only thing it receives is the assertion.
/// </para>
/// <para>
/// One assertion is exchanged separately for each audience — Copilot Studio, the Fabric SQL
/// analytics endpoint, and the Fabric REST API. Each downstream delegated permission needs
/// tenant-wide admin consent, otherwise <c>.default</c> returns a token that silently lacks the
/// scope and the downstream call fails with an opaque 401.
/// </para>
/// <para>
/// The instance is per request, because the assertion it holds belongs to one user.
/// </para>
/// </summary>
public sealed class OboTokenProvider : IDownstreamTokenProvider
{
    private readonly IConfidentialClientApplication _application;
    private readonly string _assertion;
    private readonly ILogger _logger;

    public OboTokenProvider(
        IConfidentialClientApplication application, string assertion, ILogger logger)
    {
        _application = application;
        _assertion = assertion;
        _logger = logger;
    }

    public async Task<string> GetTokenAsync(
        string[] scopes, CancellationToken cancellationToken)
    {
        try
        {
            // MSAL keeps its own per-assertion token cache, so repeated calls for the same
            // audience within a turn do not re-hit Entra.
            AuthenticationResult result = await _application
                .AcquireTokenOnBehalfOf(scopes, new UserAssertion(_assertion))
                .ExecuteAsync(cancellationToken);

            return result.AccessToken;
        }
        catch (MsalException ex)
        {
            // The scope is safe to log; the assertion and the resulting token are not.
            _logger.LogError(
                ex,
                "On-Behalf-Of exchange failed. Scope={Scope} ErrorCode={ErrorCode} Status={Status}",
                string.Join(" ", scopes),
                ex.ErrorCode,
                ex is MsalServiceException service ? service.StatusCode : 0);

            throw;
        }
    }
}

/// <summary>
/// Builds the confidential client used for every On-Behalf-Of exchange.
/// <para>
/// The credential is a <b>managed-identity federated credential</b> rather than a client secret.
/// The reference implementation this pattern came from reads a secret from Key Vault; a federated
/// credential removes that secret altogether, which keeps the solution's "managed identity, no
/// keys" rule intact and avoids provisioning a private endpoint for a single value.
/// </para>
/// <para>
/// Configure it by adding a federated identity credential on the OBO app registration with issuer
/// <c>https://login.microsoftonline.com/{tenant}/v2.0</c> and the managed identity's object id as
/// subject. Set <c>Obo:ClientSecret</c> instead only where a federated credential is not
/// available.
/// </para>
/// </summary>
public static class ConfidentialClientFactory
{
    private const string TokenExchangeScope = "api://AzureADTokenExchange/.default";

    public static IConfidentialClientApplication Create(OboOptions options)
    {
        ConfidentialClientApplicationBuilder builder = ConfidentialClientApplicationBuilder
            .Create(options.ClientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, options.TenantId);

        if (!string.IsNullOrWhiteSpace(options.ClientSecret))
        {
            return builder.WithClientSecret(options.ClientSecret).Build();
        }

        var credential = new ManagedIdentityCredential(
            string.IsNullOrWhiteSpace(options.ManagedIdentityClientId)
                ? ManagedIdentityId.SystemAssigned
                : ManagedIdentityId.FromUserAssignedClientId(options.ManagedIdentityClientId));

        return builder
            .WithClientAssertion(async assertionOptions =>
            {
                AccessToken token = await credential.GetTokenAsync(
                    new TokenRequestContext([TokenExchangeScope]),
                    assertionOptions.CancellationToken);

                return token.Token;
            })
            .Build();
    }
}

/// <summary>Configuration for the On-Behalf-Of confidential client.</summary>
public sealed class OboOptions
{
    public const string SectionName = "Obo";

    /// <summary>The app registration that holds the delegated downstream permissions.</summary>
    public string ClientId { get; set; } = string.Empty;

    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Audience the forwarded assertion must carry. Normally the OBO app's own identifier URI.
    /// </summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>Managed identity used as a federated credential. Preferred over a secret.</summary>
    public string? ManagedIdentityClientId { get; set; }

    /// <summary>Fallback only. Prefer the federated credential.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// Header carrying the user assertion. Foundry forwards only <c>x-client-*</c> headers to the
    /// container, and deliberately does not forward <c>Authorization</c>, which authenticates the
    /// immediate hop instead.
    /// </summary>
    public string UserTokenHeader { get; set; } = "x-client-user-token";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(TenantId)
        && !string.IsNullOrWhiteSpace(Audience);
}
