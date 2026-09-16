using ZavaFinance.Agent;
using ZavaFinance.Contracts;
using Xunit;

namespace ZavaFinance.Tests.Routing;

public sealed class FinanceReplyFormattingTests
{
    private static FinanceReply Reply => new("Which organization?\n\n1. First scope\n2. Second scope",
        new("request-1", "org", "Which organization?",
            [new("scope-1", "First scope"), new("scope-2", "Second scope")], "v1"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v0")]
    public void DirectCallersKeepPlainTextWithNumberedChoices(string? format)
    {
        Assert.Equal(Reply.Text, ZavaFinanceResponseHandler.FormatReply(Reply, format));
    }

    [Fact]
    public void NegotiatedClarificationPreservesNumberedResultAndStructuredChoicesForChannelCards()
    {
        string wire = ZavaFinanceResponseHandler.FormatReply(Reply, FinanceReplyProtocol.ReplyFormat);
        FinanceReply decoded = FinanceReplyProtocol.DeserializeReply(wire);
        Assert.Equal(Reply.Text, decoded.Text);
        Assert.Contains("1. First scope", decoded.Text);
        Assert.Contains("2. Second scope", decoded.Text);
        Assert.Equal(Reply.Clarification!.RequestId, decoded.Clarification!.RequestId);
        Assert.Equal(Reply.Clarification.Options, decoded.Clarification.Options);
        Assert.Equal("v1", decoded.Clarification.CatalogVersion);
    }

    [Fact]
    public void NegotiatedStatementPreservesFinancialOutputVerbatim()
    {
        var reply = new FinanceReply("**Statement**\n298.0 M USD\n\nSource: finance");
        FinanceReply decoded = FinanceReplyProtocol.DeserializeReply(
            ZavaFinanceResponseHandler.FormatReply(reply, FinanceReplyProtocol.ReplyFormat));
        Assert.Equal(reply.Text, decoded.Text);
        Assert.Null(decoded.Clarification);
    }
}
