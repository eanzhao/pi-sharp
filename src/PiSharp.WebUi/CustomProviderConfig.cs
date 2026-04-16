using Microsoft.Extensions.AI;
using OpenAI;

namespace PiSharp.WebUi;

public sealed record CustomProviderConfig(
    string EndpointUrl,
    string ApiKey,
    string ModelId,
    string DisplayName)
{
    public Uri GetEndpointUri()
    {
        if (!Uri.TryCreate(EndpointUrl.Trim(), UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"'{EndpointUrl}' is not a valid absolute endpoint URL.");
        }

        return uri;
    }

    public IChatClient CreateChatClient()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ApiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(ModelId);

        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = GetEndpointUri(),
        };

        return new OpenAI.Chat.ChatClient(
                ModelId.Trim(),
                new System.ClientModel.ApiKeyCredential(ApiKey.Trim()),
                clientOptions)
            .AsIChatClient();
    }
}
