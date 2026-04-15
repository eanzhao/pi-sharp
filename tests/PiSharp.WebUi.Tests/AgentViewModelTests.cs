using Microsoft.Extensions.AI;
using PiSharp.Agent;
using PiSharp.WebUi.Tests.Support;
using AgentRuntime = PiSharp.Agent.Agent;

namespace PiSharp.WebUi.Tests;

public sealed class AgentViewModelTests
{
    [Fact]
    public async Task SendAsync_ForwardsPromptAndPublishesUpdatedTranscript()
    {
        var client = new FakeChatClient(
            [
                TestFixtures.CreateUpdate(new TextContent("Ready."), ChatFinishReason.Stop),
            ]);

        var agent = new AgentRuntime(
            client,
            new AgentOptions
            {
                Model = TestFixtures.TestModel,
            });

        using var viewModel = new PiSharp.WebUi.AgentViewModel(agent);
        var changedCount = 0;
        viewModel.Changed += (_, _) => changedCount++;

        await viewModel.SendAsync("Status?");

        Assert.True(changedCount > 0);
        Assert.False(viewModel.IsStreaming);
        Assert.Equal(2, viewModel.Messages.Count);
        Assert.Equal(ChatRole.User, viewModel.Messages[0].Role);
        Assert.Equal(ChatRole.Assistant, viewModel.Messages[1].Role);
        Assert.Equal("Ready.", Assert.IsType<TextContent>(Assert.Single(viewModel.Messages[1].Contents)).Text);
    }
}
