using PiSharp.CodingAgent;

namespace PiSharp.WebUi;

public interface IChatStorageService
{
    Task SaveSessionAsync(string sessionId, IReadOnlyList<SessionChatMessage> messages, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SessionChatMessage>> LoadSessionAsync(string sessionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListSessionsAsync(CancellationToken cancellationToken = default);

    Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default);
}
