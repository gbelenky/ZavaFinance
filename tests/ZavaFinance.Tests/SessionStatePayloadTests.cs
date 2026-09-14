// Copyright (c) Microsoft Corporation.

using System.Text.Json;
using ZavaFinance.Core.Agent;
using Xunit;

namespace ZavaFinance.Tests;

/// <summary>
/// Round-trip shape for state persisted to the platform state store.
/// <para>
/// The store writes every field it is given with <c>Utf8JsonWriter.WriteRawValue</c>, so each one
/// must be valid JSON on its own. Handing it a bare string — a type name, for instance — fails
/// with <c>'Z' is an invalid start of a value</c>, the Z being ZavaFinance. That failure is badly
/// placed as well as obscure: it happens on the write that follows a completed turn, so a sourced
/// subagent answer that took tens of seconds to produce is thrown away.
/// </para>
/// <para>
/// The item is therefore serialized as a <b>single JSON object</b> rather than as separate type
/// and value fields, which removes the possibility of writing a non-JSON field at all.
/// </para>
/// </summary>
public sealed class SessionStatePayloadTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record StoredItem(string Type, JsonElement Value);

    private static BinaryData Store(object value)
    {
        using JsonDocument inner = JsonDocument.Parse(
            JsonSerializer.Serialize(value, value.GetType(), Json));

        return BinaryData.FromObjectAsJson(
            new StoredItem(value.GetType().AssemblyQualifiedName!, inner.RootElement), Json);
    }

    /// <summary>
    /// Reproduces the production failure, so the shape that caused it cannot come back unnoticed.
    /// </summary>
    [Fact]
    public void ABareTypeNameIsNotValidJson()
    {
        string typeName = typeof(OrchestratorSessionState).AssemblyQualifiedName!;

        JsonException error = Assert.ThrowsAny<JsonException>(
            () => JsonDocument.Parse(BinaryData.FromString(typeName).ToString()));

        Assert.Contains("invalid start of a value", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Whatever is handed to the store must parse as a JSON object.</summary>
    [Fact]
    public void StoredItemIsAlwaysAJsonObject()
    {
        foreach (object value in new object[]
                 {
                     new OrchestratorSessionState { LastKpiName = "EBIT" },
                     new PendingTurn { Question = "What is net revenue?" }
                 })
        {
            using JsonDocument parsed = JsonDocument.Parse(Store(value).ToString());

            Assert.Equal(JsonValueKind.Object, parsed.RootElement.ValueKind);
            Assert.True(parsed.RootElement.TryGetProperty("type", out _));
            Assert.True(parsed.RootElement.TryGetProperty("value", out _));
        }
    }

    [Fact]
    public void SessionStateRoundTripsThroughItsStoredRepresentation()
    {
        var original = new OrchestratorSessionState
        {
            CopilotStudioConversationId = "conversation-1",
            HostedAgentConversationId = "conv_abc123",
            LastKpiName = "Net Revenue",
            LastSubagent = OrchestratorRoute.KpiInfoTool,
            AgentSessionJson = """{"sessionId":"opaque"}"""
        };

        StoredItem stored = Store(original).ToObjectFromJson<StoredItem>(Json)!;
        Type type = Type.GetType(stored.Type)!;
        var restored = (OrchestratorSessionState)stored.Value.Deserialize(type, Json)!;

        Assert.Equal(original.CopilotStudioConversationId, restored.CopilotStudioConversationId);
        Assert.Equal(original.HostedAgentConversationId, restored.HostedAgentConversationId);
        Assert.Equal(original.LastKpiName, restored.LastKpiName);
        Assert.Equal(original.LastSubagent, restored.LastSubagent);
        Assert.Equal(original.AgentSessionJson, restored.AgentSessionJson);
    }

    /// <summary>
    /// A stored answer can contain markdown, citations and quotes. None of it may break the
    /// envelope, because the tool's output is returned verbatim.
    /// </summary>
    [Fact]
    public void AwkwardContentSurvivesTheEnvelope()
    {
        var turn = new PendingTurn
        {
            Question = "What is \"net revenue\"?",
            Answer = "**Net revenue** is revenue less deductions.\n\n| A | B |\n|---|---|\n[1] source"
        };

        StoredItem stored = Store(turn).ToObjectFromJson<StoredItem>(Json)!;
        var restored = (PendingTurn)stored.Value.Deserialize(Type.GetType(stored.Type)!, Json)!;

        Assert.Equal(turn.Question, restored.Question);
        Assert.Equal(turn.Answer, restored.Answer);
    }
}
