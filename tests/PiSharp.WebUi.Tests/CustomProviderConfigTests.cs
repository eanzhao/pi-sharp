using Microsoft.Extensions.AI;

namespace PiSharp.WebUi.Tests;

public sealed class CustomProviderConfigTests
{
    [Fact]
    public void CreateChatClient_ReturnsOpenAiCompatibleChatClient()
    {
        var config = new CustomProviderConfig(
            "http://localhost:1234/v1",
            "test-key",
            "qwen2.5-coder",
            "LM Studio");

        var client = config.CreateChatClient();

        Assert.IsAssignableFrom<IChatClient>(client);
        Assert.Equal(new Uri("http://localhost:1234/v1"), config.GetEndpointUri());
    }
}
