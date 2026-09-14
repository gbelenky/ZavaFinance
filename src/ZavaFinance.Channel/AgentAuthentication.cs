// Copyright (c) Microsoft Corporation.

using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using Microsoft.Agents.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.IdentityModel.Validators;

namespace ZavaFinance.Channel;

/// <summary>
/// Inbound token validation settings, bound from the <c>TokenValidation</c> configuration
/// section.
/// </summary>
public sealed class TokenValidationOptions
{
    /// <summary>Client ids of the Azure Bot registration. At least one is required.</summary>
    public List<string> Audiences { get; set; } = [];

    public string? TenantId { get; set; }

    /// <summary>Overrides the default trusted issuer list when supplied.</summary>
    public List<string>? ValidIssuers { get; set; }

    /// <summary>Metadata used to validate Azure Bot Service tokens.</summary>
    public string? AzureBotServiceOpenIdMetadataUrl { get; set; }

    /// <summary>Metadata used to validate Entra ID tokens.</summary>
    public string? OpenIdMetadataUrl { get; set; }

    public bool AzureBotServiceTokenHandling { get; set; } = true;

    public TimeSpan? OpenIdMetadataRefresh { get; set; }
}

/// <summary>
/// Inbound token validation for the Bot Service messaging endpoint. SECURITY CRITICAL.
/// <para>
/// The Agents SDK adapter reads an already-validated <c>ClaimsIdentity</c> off the request.
/// On Azure Functions there is no ASP.NET authentication middleware in the pipeline, so the
/// request must be authenticated explicitly before the adapter runs — otherwise the
/// anonymous endpoint would accept unauthenticated callers.
/// </para>
/// <para>
/// Azure Bot Service tokens are not Entra ID tokens and are signed with a different key set,
/// so the OpenID metadata endpoint is chosen per request from the token's issuer.
/// </para>
/// </summary>
public static class AgentAuthentication
{
    private static readonly ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>>
        MetadataCache = new();

    public static IServiceCollection AddAgentTokenValidation(
        this IServiceCollection services, IConfiguration configuration)
    {
        IConfigurationSection section = configuration.GetSection("TokenValidation");

        if (!section.Exists())
        {
            throw new InvalidOperationException(
                "Configuration section 'TokenValidation' is missing.");
        }

        TokenValidationOptions options = section.Get<TokenValidationOptions>()
            ?? throw new InvalidOperationException("'TokenValidation' could not be bound.");

        if (options.Audiences is not { Count: > 0 })
        {
            throw new InvalidOperationException(
                "TokenValidation:Audiences requires at least one client id.");
        }

        options.ValidIssuers = BuildValidIssuers(options);

        options.AzureBotServiceOpenIdMetadataUrl ??=
            AuthenticationConstants.PublicAzureBotServiceOpenIdMetadataUrl;

        options.OpenIdMetadataUrl ??= AuthenticationConstants.PublicOpenIdMetadataUrl;

        TimeSpan refresh = options.OpenIdMetadataRefresh
            ?? BaseConfigurationManager.DefaultAutomaticRefreshInterval;

        services
            .AddAuthentication(auth =>
            {
                auth.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                auth.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(jwt =>
            {
                jwt.SaveToken = true;
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    RequireSignedTokens = true,
                    ClockSkew = TimeSpan.FromMinutes(5),
                    ValidIssuers = options.ValidIssuers,
                    ValidAudiences = options.Audiences
                };

                jwt.TokenValidationParameters.EnableAadSigningKeyIssuerValidation();

                jwt.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        SelectMetadataEndpoint(context, options, refresh);
                        return Task.CompletedTask;
                    }
                };
            });

        return services;
    }

    /// <summary>
    /// Bot Service and Entra ID tokens are signed by different authorities. Pick the
    /// metadata endpoint from the issuer rather than assuming Entra.
    /// </summary>
    private static void SelectMetadataEndpoint(
        MessageReceivedContext context, TokenValidationOptions options, TimeSpan refresh)
    {
        string metadataUrl = options.OpenIdMetadataUrl!;

        string header = context.Request.Headers.Authorization.ToString();
        string[] parts = header.Split(' ');

        if (parts.Length == 2
            && parts[0].Equals("Bearer", StringComparison.Ordinal)
            && options.AzureBotServiceTokenHandling)
        {
            try
            {
                var token = new JsonWebToken(parts[1]);

                if (IsBotFrameworkIssuer(token.Issuer))
                {
                    metadataUrl = options.AzureBotServiceOpenIdMetadataUrl!;
                }
            }
            catch (ArgumentException)
            {
                // Malformed token: leave the Entra endpoint selected and let validation
                // reject it.
            }
        }

        context.Options.TokenValidationParameters.ConfigurationManager =
            MetadataCache.GetOrAdd(
                metadataUrl,
                url => new ConfigurationManager<OpenIdConnectConfiguration>(
                    url,
                    new OpenIdConnectConfigurationRetriever(),
                    new HttpClient())
                {
                    AutomaticRefreshInterval = refresh
                });
    }

    private static bool IsBotFrameworkIssuer(string issuer) =>
        AuthenticationConstants.BotFrameworkTokenIssuer.Equals(
            issuer, StringComparison.OrdinalIgnoreCase)
        || AuthenticationConstants.GovBotFrameworkTokenIssuer.Equals(
            issuer, StringComparison.OrdinalIgnoreCase)
        || AuthenticationConstants.ChinaBotFrameworkTokenIssuer.Equals(
            issuer, StringComparison.OrdinalIgnoreCase);

    private static List<string> BuildValidIssuers(TokenValidationOptions options)
    {
        if (options.ValidIssuers is { Count: > 0 })
        {
            return options.ValidIssuers;
        }

        List<string> issuers =
        [
            AuthenticationConstants.BotFrameworkTokenIssuer,
            "https://sts.windows.net/d6d49420-f39b-4df7-a1dc-d59a935871db/",
            "https://login.microsoftonline.com/d6d49420-f39b-4df7-a1dc-d59a935871db/v2.0",
            "https://sts.windows.net/f8cdef31-a31e-4b4a-93e4-5f571e91255a/",
            "https://login.microsoftonline.com/f8cdef31-a31e-4b4a-93e4-5f571e91255a/v2.0",
            "https://sts.windows.net/69e9b82d-4842-4902-8d1e-abc5b98a55e8/",
            "https://login.microsoftonline.com/69e9b82d-4842-4902-8d1e-abc5b98a55e8/v2.0"
        ];

        if (!string.IsNullOrEmpty(options.TenantId) && Guid.TryParse(options.TenantId, out _))
        {
            issuers.Add(string.Format(
                CultureInfo.InvariantCulture,
                AuthenticationConstants.ValidTokenIssuerUrlTemplateV1,
                options.TenantId));

            issuers.Add(string.Format(
                CultureInfo.InvariantCulture,
                AuthenticationConstants.ValidTokenIssuerUrlTemplateV2,
                options.TenantId));
        }

        return issuers;
    }
}
