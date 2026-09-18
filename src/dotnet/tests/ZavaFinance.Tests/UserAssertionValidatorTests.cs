// Copyright (c) Microsoft Corporation.

using Microsoft.Extensions.Logging.Abstractions;
using ZavaFinance.Agent;
using ZavaFinance.Core.Identity;
using Xunit;

namespace ZavaFinance.Tests;

/// <summary>
/// The hosted agent's identity boundary.
/// <para>
/// The assertion arrives as a forwarded header that Foundry neither authenticates nor interprets,
/// so this agent owns the validation — and the session key, which is the isolation boundary for
/// every piece of per-user state, is derived from its claims.
/// </para>
/// </summary>
public sealed class UserAssertionValidatorTests
{
    private static UserAssertionValidator Validator() =>
        new("00000000-1111-2222-3333-444444444444",
            "api://zavafinance",
            NullLogger<UserAssertionValidator>.Instance);

    /// <summary>
    /// A missing header must end the turn. Continuing without a user would mean either failing
    /// at the first downstream call or, far worse, answering as the application — which would
    /// bypass row-level security and show one user another user's figures.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MissingAssertionIsRefused(string? assertion)
    {
        await Assert.ThrowsAsync<CallerIdentityException>(
            () => Validator().ValidateAsync(assertion, CancellationToken.None));
    }

    /// <summary>
    /// A self-signed or otherwise unverifiable token must be refused <b>before</b> its claims are
    /// used, not merely allowed to fail later at the On-Behalf-Of exchange.
    /// <para>
    /// The distinction matters: the session key is derived from these claims and is used to load
    /// and then overwrite stored state. A forgery that never obtains a downstream token could
    /// still read another user's Copilot Studio conversation handle and latest KPI, and poison
    /// their session — all without a single successful call to Entra.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ForgedAssertionIsRefusedBeforeItsClaimsAreUsed()
    {
        // Well-formed JWT shape, attacker-chosen claims, no valid signature.
        const string Forged =
            "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9"
            + ".eyJ0aWQiOiIwMDAwMDAwMC0xMTExLTIyMjItMzMzMy00NDQ0NDQ0NDQ0NDQiLCJvaWQiOiJ2aWN0aW0ifQ"
            + ".not-a-real-signature";

        await Assert.ThrowsAsync<CallerIdentityException>(
            () => Validator().ValidateAsync(Forged, CancellationToken.None));
    }

    [Fact]
    public async Task MalformedTokenIsRefused()
    {
        await Assert.ThrowsAsync<CallerIdentityException>(
            () => Validator().ValidateAsync("not-a-jwt", CancellationToken.None));
    }

    /// <summary>
    /// The failure reported to the caller must not say why validation failed. A precise reason
    /// is a probing oracle: it tells an attacker whether the signature, the audience, the tenant
    /// or the lifetime was wrong, and therefore what to change next.
    /// </summary>
    [Fact]
    public async Task RefusalDoesNotLeakTheReason()
    {
        CallerIdentityException error =
            await Assert.ThrowsAsync<CallerIdentityException>(
                () => Validator().ValidateAsync("not-a-jwt", CancellationToken.None));

        foreach (string leak in new[] { "signature", "audience", "issuer", "expired", "kid" })
        {
            Assert.DoesNotContain(leak, error.Message, StringComparison.OrdinalIgnoreCase);
        }
    }
}
