using System.Text.Json;
using Azure.Core;
using ZavaFinance.Agent;
using ZavaFinance.Core.Agent;
using Xunit;

namespace ZavaFinance.Tests;

public sealed class SessionStatePayloadTests
{
    [Fact]
    public void TypedPayloadIsOneJsonObjectWithOnlyAgentState()
    {
        var state = new OrchestratorSessionState
        {
            AgentSessionVersion = 1,
            AgentSessionJson = "{\"messages\":\"a \\\"quote\\\"\\n[1]\"}",
            CopilotStudioConversationId = "kpipedia-conversation",
            LastKpiName = "Net Revenue"
        };
        BinaryData data = FoundrySessionStore.Serialize(state);
        using JsonDocument document = JsonDocument.Parse(data);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        Assert.Equal(4, document.RootElement.EnumerateObject().Count());
        Assert.False(document.RootElement.TryGetProperty("type", out _));
        OrchestratorSessionState restored = FoundrySessionStore.Deserialize(data);
        Assert.Equal(state.AgentSessionVersion, restored.AgentSessionVersion);
        Assert.Equal(state.AgentSessionJson, restored.AgentSessionJson);
        Assert.Equal(state.CopilotStudioConversationId, restored.CopilotStudioConversationId);
        Assert.Equal(state.LastKpiName, restored.LastKpiName);
    }

    [Fact]
    public void LegacyEnvelopeMigratesWithoutLoadingOldAssemblyOrRetainingChannelState()
    {
        BinaryData legacy = BinaryData.FromString("""
            {
              "type": "ZavaFinance.Core.Agent.OrchestratorSessionState, ZavaFinance.Agent, Version=0.0.0.1, Culture=neutral",
              "value": {
                "agentSessionVersion": 1,
                "agentSessionJson": "{\"sessionId\":\"opaque\"}",
                "copilotStudioConversationId": "kpipedia-old",
                "lastKpiName": "EBIT",
                "hostedAgentConversationId": "conv-channel",
                "fabricThreadId": "obsolete",
                "lastSubagent": "get_kpi_info",
                "eTag": "*"
              }
            }
            """);
        OrchestratorSessionState state = FoundrySessionStore.Deserialize(legacy);
        Assert.Equal(1, state.AgentSessionVersion);
        Assert.Equal("{\"sessionId\":\"opaque\"}", state.AgentSessionJson);
        Assert.Equal("kpipedia-old", state.CopilotStudioConversationId);
        Assert.Equal("EBIT", state.LastKpiName);
        string rewritten = FoundrySessionStore.Serialize(state).ToString();
        Assert.DoesNotContain("conv-channel", rewritten);
        Assert.DoesNotContain("fabricThread", rewritten);
        Assert.DoesNotContain("lastSubagent", rewritten);
        Assert.DoesNotContain("eTag", rewritten);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"type\":\"System.String, System.Private.CoreLib\",\"value\":\"not a session\"}")]
    [InlineData("{\"type\":\"ZavaFinance.Core.Agent.OrchestratorSessionState, old\",\"value\":null}")]
    [InlineData("{\"type\":\"ZavaFinance.Core.Agent.OrchestratorSessionState, old\"}")]
    public void InvalidStateFailsExplicitlyRatherThanResettingTheConversation(string json)
    {
        Assert.Throws<JsonException>(() => FoundrySessionStore.Deserialize(BinaryData.FromString(json)));
    }

    [Fact]
    public void ExistingStorageKeysAreUnchanged()
    {
        Assert.Equal("orchestrator_USERA", FoundrySessionStore.KeyFor("USERA"));
        Assert.NotEqual(FoundrySessionStore.KeyFor("USERA"), FoundrySessionStore.KeyFor("USERB"));
        Assert.Throws<ArgumentException>(() => FoundrySessionStore.KeyFor(""));
    }

    [Fact]
    public void ConstructingTheStoreDoesNotContactFoundry()
    {
        using var store = new FoundrySessionStore("test-store", new NoNetworkCredential(), TimeSpan.FromDays(30));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2147483648)]
    public void InvalidTtlIsRejected(double seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FoundrySessionStore("test-store", new NoNetworkCredential(), TimeSpan.FromSeconds(seconds)));
    }

    private sealed class NoNetworkCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Construction must not authenticate.");
        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Construction must not authenticate.");
    }
}
