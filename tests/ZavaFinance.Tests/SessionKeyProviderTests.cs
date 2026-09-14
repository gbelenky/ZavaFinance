// Copyright (c) Microsoft Corporation.

using ZavaFinance.Core.Identity;
using Xunit;

namespace ZavaFinance.Tests;

/// <summary>
/// The session key is the isolation boundary between users. These tests encode the
/// "definition of done for the security model": two users in one shared conversation must
/// never land on the same durable agent session.
/// </summary>
public sealed class SessionKeyProviderTests
{
    private const string Salt = "test-salt-value";
    private const string Tenant = "00000000-1111-2222-3333-444444444444";
    private const string GroupChat = "19:abc123def456@thread.tacv2";

    private static readonly SessionKeyProvider Provider = new(Salt);

    private static CallerIdentity User(string oid) => new(Tenant, oid);

    [Fact]
    public void DifferentUsersInSameConversationGetDifferentKeys()
    {
        string userA = Provider.GetSessionKey(User("11111111-1111-1111-1111-111111111111"), GroupChat);
        string userB = Provider.GetSessionKey(User("22222222-2222-2222-2222-222222222222"), GroupChat);

        Assert.NotEqual(userA, userB);
    }

    [Fact]
    public void SameUserAndConversationIsStable()
    {
        CallerIdentity caller = User("11111111-1111-1111-1111-111111111111");

        Assert.Equal(
            Provider.GetSessionKey(caller, GroupChat),
            Provider.GetSessionKey(caller, GroupChat));
    }

    [Fact]
    public void SameUserInDifferentConversationsGetsDifferentKeys()
    {
        CallerIdentity caller = User("11111111-1111-1111-1111-111111111111");

        Assert.NotEqual(
            Provider.GetSessionKey(caller, GroupChat),
            Provider.GetSessionKey(caller, "19:zzz999@thread.tacv2"));
    }

    [Fact]
    public void SameUserObjectIdInDifferentTenantsGetsDifferentKeys()
    {
        var tenantA = new CallerIdentity("aaaaaaaa-0000-0000-0000-000000000000", "shared-oid");
        var tenantB = new CallerIdentity("bbbbbbbb-0000-0000-0000-000000000000", "shared-oid");

        Assert.NotEqual(
            Provider.GetSessionKey(tenantA, GroupChat),
            Provider.GetSessionKey(tenantB, GroupChat));
    }

    [Fact]
    public void DifferentSaltProducesDifferentKeys()
    {
        var other = new SessionKeyProvider("a-different-salt");
        CallerIdentity caller = User("11111111-1111-1111-1111-111111111111");

        Assert.NotEqual(
            Provider.GetSessionKey(caller, GroupChat),
            other.GetSessionKey(caller, GroupChat));
    }

    /// <summary>
    /// Field boundaries must not be forgeable. Without length-prefixing, a crafted
    /// conversation id could shift bytes across fields and collide with another user.
    /// </summary>
    [Fact]
    public void FieldBoundariesCannotBeForgedByShiftingText()
    {
        string a = Provider.GetSessionKey(new CallerIdentity(Tenant, "user"), "12|extra");
        string b = Provider.GetSessionKey(new CallerIdentity(Tenant, "user|12"), "extra");

        Assert.NotEqual(a, b);
    }

    [Theory]
    [InlineData("19:abc@thread.tacv2")]
    [InlineData("a/b?c#d")]
    [InlineData("with space and \t tab")]
    [InlineData("29:1a2b3c/4d5e#6f")]
    public void KeyIsAlwaysALegalDurableEntityKey(string conversationId)
    {
        string key = Provider.GetSessionKey(User("11111111-1111-1111-1111-111111111111"), conversationId);

        Assert.NotEmpty(key);
        Assert.DoesNotContain('/', key);
        Assert.DoesNotContain('?', key);
        Assert.DoesNotContain('#', key);
        Assert.All(key, c => Assert.True(
            (c >= 'A' && c <= 'Z') || (c >= '2' && c <= '7'),
            $"Unexpected character '{c}' in session key."));
    }

    /// <summary>
    /// The entity id is visible in the Durable Task Scheduler dashboard, so it must not
    /// leak tenant, user or conversation identifiers.
    /// </summary>
    [Fact]
    public void KeyDoesNotLeakIdentifiers()
    {
        const string oid = "11111111-1111-1111-1111-111111111111";

        string key = Provider.GetSessionKey(User(oid), GroupChat);

        Assert.DoesNotContain(Tenant, key, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(oid, key, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("thread.tacv2", key, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmptyConversationIdIsRejected()
    {
        Assert.Throws<ArgumentException>(
            () => Provider.GetSessionKey(User("oid"), string.Empty));
    }

    [Fact]
    public void EmptySaltIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new SessionKeyProvider(string.Empty));
    }
}

public sealed class CallerIdentityTests
{
    [Theory]
    [InlineData(null, "oid")]
    [InlineData("", "oid")]
    [InlineData("  ", "oid")]
    [InlineData("tid", null)]
    [InlineData("tid", "")]
    public void IncompleteClaimsAreRejected(string? tenantId, string? objectId)
    {
        Assert.Throws<CallerIdentityException>(
            () => CallerIdentity.Create(tenantId, objectId));
    }

    [Fact]
    public void DeletionIndexKeyIsTenantAndUserScoped()
    {
        var caller = new CallerIdentity("tenant-a", "user-1");

        Assert.Equal("tenant-a|user-1", caller.DeletionIndexKey);
    }
}
