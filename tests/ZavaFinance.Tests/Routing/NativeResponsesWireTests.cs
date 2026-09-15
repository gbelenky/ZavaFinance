using System.Net;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using ZavaFinance.Core.Agent;
using ZavaFinance.Core.Configuration;
using Xunit;

namespace ZavaFinance.Tests.Routing;

public sealed class NativeResponsesWireTests
{
    [Fact]
    public async Task ResponsesAdapterSendsNativeSchemasAndReplaysOnlyLocalPairedHistory()
    {
        using var handler = new ModelHandler();
        using var http = new HttpClient(handler);
        var openAI = new OpenAIClient(new ApiKeyCredential("unit-test-not-a-key"), new OpenAIClientOptions
        {
            Endpoint = new Uri("https://model.example.test"),
            Transport = new HttpClientPipelineTransport(http)
        });
#pragma warning disable OPENAI001 // Exercises the same preview Responses adapter used by the hosted agent.
        using IChatClient chat = openAI.GetResponsesClient().AsIChatClient("gpt-4.1-mini");
#pragma warning restore OPENAI001
        AIAgent agent = OrchestratorAgent.CreateRoutingAgent(chat, new FoundryOptions());
        AgentSession session = await agent.CreateSessionAsync();

        AgentResponse first = await OrchestratorAgent.SelectToolAsync(
            agent, "Show the figures", session, CancellationToken.None);
        FunctionCallContent selected = Assert.Single(first.Messages.SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>());
        Assert.Equal(FinanceToolNames.StatementTool, selected.Name);
        Assert.True(selected.Arguments is null || selected.Arguments.Count == 0);

        JsonElement saved = await agent.SerializeSessionAsync(session);
        session = await agent.DeserializeSessionAsync(saved);
        await OrchestratorAgent.SelectToolAsync(
            agent, "And for EMEA?", session, CancellationToken.None);

        Assert.Equal(2, handler.Bodies.Count);
        foreach (JsonElement body in handler.Bodies)
        {
            Assert.False(body.GetProperty("store").GetBoolean());
            Assert.False(body.GetProperty("parallel_tool_calls").GetBoolean());
            Assert.Equal("auto", body.GetProperty("tool_choice").GetString());
            Assert.False(body.TryGetProperty("previous_response_id", out _));
            Assert.False(body.TryGetProperty("conversation", out _));
            Assert.Equal(3, body.GetProperty("tools").GetArrayLength());
            foreach (JsonElement tool in body.GetProperty("tools").EnumerateArray())
            {
                Assert.Equal("function", tool.GetProperty("type").GetString());
                Assert.True(tool.GetProperty("parameters").TryGetProperty("properties", out _));
            }
            Assert.False(body.TryGetProperty("text", out JsonElement text)
                && text.TryGetProperty("format", out JsonElement format)
                && format.GetProperty("type").GetString() == "json_schema");
        }

        JsonElement[] input = handler.Bodies[1].GetProperty("input").EnumerateArray().ToArray();
        JsonElement call = Assert.Single(input, item => item.TryGetProperty("type", out var type)
            && type.GetString() == "function_call");
        JsonElement result = Assert.Single(input, item => item.TryGetProperty("type", out var type)
            && type.GetString() == "function_call_output");
        Assert.Equal(call.GetProperty("call_id").GetString(), result.GetProperty("call_id").GetString());
        // Restored result objects are JsonElements, so the adapter JSON-encodes this marker.
        Assert.Equal(OrchestratorAgent.WithheldResult,
            JsonSerializer.Deserialize<string>(result.GetProperty("output").GetString()!));
    }

    private sealed class ModelHandler : HttpMessageHandler
    {
        public List<JsonElement> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Bodies.Add(JsonSerializer.Deserialize<JsonElement>(body));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""
                    {
                      "id": "resp_{{Bodies.Count}}", "object": "response",
                      "created_at": 1750000000, "model": "gpt-4.1-mini", "status": "completed",
                      "output": [{
                        "type": "function_call", "id": "fc_{{Bodies.Count}}",
                        "call_id": "call_{{Bodies.Count}}", "name": "get_statement",
                        "arguments": "{}", "status": "completed"
                      }]
                    }
                    """, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }
}
