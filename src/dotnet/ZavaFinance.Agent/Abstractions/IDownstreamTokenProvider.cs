// Copyright (c) Microsoft Corporation.

namespace ZavaFinance.Core.Abstractions;

/// <summary>
/// Supplies a delegated access token for a downstream resource, on behalf of the signed-in user.
/// The hosted agent exchanges the validated <c>x-client-user-token</c> assertion with MSAL OBO.
/// The channel only forwards that assertion; it does not construct finance tools.
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
