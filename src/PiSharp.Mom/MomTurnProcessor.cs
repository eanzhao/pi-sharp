using Microsoft.Extensions.AI;
using PiSharp.CodingAgent;

namespace PiSharp.Mom;

public sealed class MomTurnProcessor
{
    private readonly MomConsoleEnvironment _environment;
    private readonly MomRuntimeOptions _options;
    private readonly CodingAgentProviderCatalog _providerCatalog;
    private readonly Func<string, string, SettingsManager> _createSettingsManager;
    private readonly ISlackMessagingClient _slackClient;
    private readonly MomChannelStore _store;

    public MomTurnProcessor(
        MomConsoleEnvironment environment,
        MomRuntimeOptions options,
        CodingAgentProviderCatalog providerCatalog,
        Func<string, string, SettingsManager> createSettingsManager,
        ISlackMessagingClient slackClient,
        MomChannelStore? store = null)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _providerCatalog = providerCatalog ?? throw new ArgumentNullException(nameof(providerCatalog));
        _createSettingsManager = createSettingsManager ?? throw new ArgumentNullException(nameof(createSettingsManager));
        _slackClient = slackClient ?? throw new ArgumentNullException(nameof(slackClient));
        _store = store ?? new MomChannelStore(options.WorkspaceDirectory);
    }

    public async Task ProcessAsync(SlackIncomingEvent incomingEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(incomingEvent);

        var prompt = CreatePrompt(incomingEvent);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            await _slackClient.PostMessageAsync(
                    incomingEvent.ChannelId,
                    "What do you need me to do?",
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var channelDirectory = _store.GetChannelDirectory(incomingEvent.ChannelId);
        var sessionDirectory = _store.GetSessionDirectory(incomingEvent.ChannelId);

        var placeholderTs = await _slackClient.PostMessageAsync(
                incomingEvent.ChannelId,
                incomingEvent.StatusText ?? "_Thinking..._",
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var agentDirectory = Path.Combine(_environment.GetHomeDirectory(), ".pi-sharp");
            var settingsManager = _createSettingsManager(channelDirectory, agentDirectory);
            var bootstrap = new CodingAgentRuntimeBootstrap(_providerCatalog, settingsManager);
            var persistenceManager = new SessionManager(sessionDirectory, channelDirectory);

            SessionContext? existingSession = null;
            IReadOnlyList<ChatMessage> synchronizedLogMessages = Array.Empty<ChatMessage>();
            var latestSession = persistenceManager.FindLatestSessionFile();
            if (latestSession is not null)
            {
                await persistenceManager.LoadSessionAsync(latestSession, cancellationToken).ConfigureAwait(false);
            }

            synchronizedLogMessages = MomSessionSync.SyncLogToSessionManager(
                persistenceManager,
                _store.GetLogFilePath(incomingEvent.ChannelId),
                incomingEvent.Timestamp);

            existingSession = persistenceManager.BuildContext();

            var runConfiguration = bootstrap.Resolve(
                new CodingAgentBootstrapRequest
                {
                    WorkingDirectory = channelDirectory,
                    Provider = _options.Provider,
                    Model = _options.Model,
                    ApiKey = _options.ApiKey,
                    ExistingSession = existingSession,
                    SessionDirectory = sessionDirectory,
                    LoadContextFiles = false,
                },
                _environment.GetEnvironmentVariable);

            var systemPrompt = MomSystemPrompt.Build(
                new MomSystemPromptOptions
                {
                    WorkspaceDirectory = _options.WorkspaceDirectory,
                    ChannelId = incomingEvent.ChannelId,
                    ChannelDirectory = channelDirectory,
                    Memory = _store.ReadMemory(incomingEvent.ChannelId),
                    CurrentTime = DateTimeOffset.UtcNow,
                });

            using var chatClient = runConfiguration.ProviderFactory.Create(runConfiguration.Model.Id, runConfiguration.ApiKey);
            using var session = await CodingAgentSession.CreateAsync(
                    chatClient,
                    new CodingAgentSessionOptions
                    {
                        Model = runConfiguration.Model,
                        WorkingDirectory = channelDirectory,
                        ThinkingLevel = runConfiguration.ThinkingLevel,
                        ActiveToolNames = BuiltInToolNames.All,
                        Messages = existingSession?.Messages,
                        OverrideSystemPrompt = systemPrompt,
                        Extensions =
                        [
                            MomSlackTools.CreateAttachExtension(
                                _options.WorkspaceDirectory,
                                channelDirectory,
                                incomingEvent.ChannelId,
                                _slackClient),
                            MomThreadReporter.CreateExtension(
                                incomingEvent.ChannelId,
                                placeholderTs,
                                _slackClient),
                        ],
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            PreparePersistenceManager(
                persistenceManager,
                latestSession is not null,
                session,
                runConfiguration,
                synchronizedLogMessages);

            var persistedMessageCount = session.State.Messages.Count;
            await session.PromptAsync(prompt, cancellationToken: cancellationToken).ConfigureAwait(false);
            PersistNewMessages(persistenceManager, session, ref persistedMessageCount);

            var assistantText = session.State.Messages
                .Where(static message => message.Role == ChatRole.Assistant)
                .Select(static message => message.Text?.Trim())
                .LastOrDefault(static text => !string.IsNullOrWhiteSpace(text));

            if (string.Equals(assistantText, "[SILENT]", StringComparison.Ordinal))
            {
                await _slackClient.DeleteMessageAsync(
                        incomingEvent.ChannelId,
                        placeholderTs,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var responseText = string.IsNullOrWhiteSpace(assistantText)
                ? "_Done._"
                : SlackMrkdwnFormatter.Limit(SlackMrkdwnFormatter.Format(assistantText));

            await _slackClient.UpdateMessageAsync(
                    incomingEvent.ChannelId,
                    placeholderTs,
                    responseText,
                    cancellationToken)
                .ConfigureAwait(false);

            await _store.LogBotResponseAsync(incomingEvent.ChannelId, responseText, placeholderTs, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            const string stoppedMessage = "_Stopped._";
            await _slackClient.UpdateMessageAsync(
                    incomingEvent.ChannelId,
                    placeholderTs,
                    stoppedMessage,
                    CancellationToken.None)
                .ConfigureAwait(false);

            await _store.LogBotResponseAsync(incomingEvent.ChannelId, stoppedMessage, placeholderTs, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var errorText = SlackMrkdwnFormatter.Limit(
                $"Sorry, something went wrong.\n```text\n{exception.Message}\n```");

            await _slackClient.UpdateMessageAsync(
                    incomingEvent.ChannelId,
                    placeholderTs,
                    errorText,
                    CancellationToken.None)
                .ConfigureAwait(false);

            await _store.LogBotResponseAsync(incomingEvent.ChannelId, errorText, placeholderTs, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    public static string NormalizePrompt(SlackIncomingEvent incomingEvent)
    {
        ArgumentNullException.ThrowIfNull(incomingEvent);

        var text = incomingEvent.Text.Trim();
        if (string.Equals(incomingEvent.EventType, "app_mention", StringComparison.OrdinalIgnoreCase))
        {
            text = System.Text.RegularExpressions.Regex.Replace(text, "<@[A-Z0-9]+>", string.Empty).Trim();
        }

        return text;
    }

    private string CreatePrompt(SlackIncomingEvent incomingEvent)
    {
        var normalizedPrompt = NormalizePrompt(incomingEvent);
        var loggedMessage = _store.ReadLoggedMessage(incomingEvent.ChannelId, incomingEvent.Timestamp);
        if (loggedMessage is not null &&
            (!string.IsNullOrWhiteSpace(loggedMessage.Text) || loggedMessage.Attachments.Count > 0))
        {
            return MomSessionSync.FormatForContext(loggedMessage);
        }

        if (incomingEvent.Files is { Count: > 0 })
        {
            return MomSessionSync.FormatForContext(
                new MomLoggedMessage
                {
                    Ts = incomingEvent.Timestamp,
                    User = incomingEvent.UserId,
                    Text = normalizedPrompt,
                    Attachments = incomingEvent.Files
                        .Select(static file => new MomLoggedAttachment(file.Name))
                        .ToArray(),
                    IsBot = false,
                });
        }

        return normalizedPrompt;
    }

    private static void PreparePersistenceManager(
        SessionManager persistenceManager,
        bool hasExistingSession,
        CodingAgentSession session,
        CodingAgentRunConfiguration runConfiguration,
        IReadOnlyList<ChatMessage> synchronizedLogMessages)
    {
        if (hasExistingSession)
        {
            persistenceManager.UpdateHeader(header => header with
            {
                Cwd = session.WorkingDirectory,
                ProviderId = runConfiguration.ProviderFactory.Configuration.ProviderId.Value,
                ModelId = runConfiguration.Model.Id,
                ThinkingLevel = runConfiguration.ThinkingLevel.ToString().ToLowerInvariant(),
                SystemPrompt = session.SystemPrompt,
                ToolNames = session.ActiveToolNames.ToArray(),
            });
            return;
        }

        persistenceManager.NewSession(
            providerId: runConfiguration.ProviderFactory.Configuration.ProviderId.Value,
            modelId: runConfiguration.Model.Id,
            thinkingLevel: runConfiguration.ThinkingLevel.ToString().ToLowerInvariant(),
            systemPrompt: session.SystemPrompt,
            toolNames: session.ActiveToolNames);

        foreach (var synchronizedMessage in synchronizedLogMessages)
        {
            persistenceManager.AppendEntry(SessionMessageEntry.FromChatMessage(synchronizedMessage));
        }
    }

    private static void PersistNewMessages(
        SessionManager persistenceManager,
        CodingAgentSession session,
        ref int persistedMessageCount)
    {
        var messages = session.State.Messages;
        for (var index = persistedMessageCount; index < messages.Count; index++)
        {
            persistenceManager.AppendEntry(SessionMessageEntry.FromChatMessage(messages[index]));
        }

        persistedMessageCount = messages.Count;
    }
}
