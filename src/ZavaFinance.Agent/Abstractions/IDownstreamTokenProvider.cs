// Copyright (c) Microsoft Corporation.

namespace ZavaFinance.Core.Abstractions;

/// <summary>
/// Supplies a delegated access token for a downstream resource, on behalf of the signed-in user.
/// <para>
/// This is the seam that lets one set of tools run under two different hosts. Every downstream
/// call in this solution — Copilot Studio, the Fabric SQL analytics endpoint, and the Fabric data
/// agent — is made <b>as the user</b>, never as the application, because row-level security and
/// workspace permissions only filter per user when the user's own identity reaches the resource.
/// An app-only token would show every caller the same data.
/// </para>
/// <para>
/// The two implementations differ only in where the user's identity comes from:
/// </para>
/// <list type="bullet">
/// <item>
/// In the Teams channel the Microsoft 365 Agents SDK already holds the caller's Teams SSO token
/// and performs the exchange itself.
/// </item>
/// <item>
/// In the Foundry hosted agent the caller's assertion arrives in a forwarded
/// <c>x-client-user-token</c> header and the agent performs the On-Behalf-Of exchange with MSAL.
/// </item>
/// </list>
/// <para>
/// Implementations must acquire tokens <b>late</b>, per call, and must not hand a token to any
/// component that persists it. A delegated user token never enters durable state, orchestration
/// payloads, conversation history, model input, or telemetry.
/// </para>
/// </summary>
public interface IDownstreamTokenProvider
{
    /// <summary>
    /// Acquires a delegated token for <paramref name="scopes"/>.
    /// </summary>
    /// <param name="scopes">
    /// The downstream audience. Always a resource-specific value — for Copilot Studio it must come
    /// from <c>CopilotClient.ScopeFromSettings</c> rather than being hand-written, because a
    /// hand-built URI fails with an opaque 401.
    /// </param>
    /// <param name="cancellationToken">Cancels the acquisition.</param>
    Task<string> GetTokenAsync(string[] scopes, CancellationToken cancellationToken);
}
