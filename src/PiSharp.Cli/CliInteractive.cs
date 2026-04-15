using System.Text;
using Microsoft.Extensions.AI;
using PiSharp.Agent;
using PiSharp.Ai;
using PiSharp.CodingAgent;
using PiSharp.Tui;

namespace PiSharp.Cli;

internal sealed class CliInteractiveView : Component, IInputComponent, IFocusableComponent
{
    private readonly Input _input;
    private string _status = "PiSharp";
    private string _transcript = string.Empty;

    public CliInteractiveView(string prompt = "> ", string? placeholder = null)
    {
        _input = new Input(prompt, placeholder);
    }

    public event Action<string>? Submitted
    {
        add => _input.Submitted += value;
        remove => _input.Submitted -= value;
    }

    public bool IsFocused
    {
        get => _input.IsFocused;
        set => _input.IsFocused = value;
    }

    public string Status
    {
        get => _status;
        set
        {
            if (_status == value)
            {
                return;
            }

            _status = value;
            RaiseInvalidated();
        }
    }

    public string Transcript
    {
        get => _transcript;
        set
        {
            if (_transcript == value)
            {
                return;
            }

            _transcript = value;
            RaiseInvalidated();
        }
    }

    public string InputValue
    {
        get => _input.Value;
        set => _input.Value = value;
    }

    public string? Placeholder
    {
        get => _input.Placeholder;
        set => _input.Placeholder = value;
    }

    public bool HandleInput(KeyEvent keyEvent, ShortcutMap shortcuts) =>
        _input.HandleInput(keyEvent, shortcuts);

    public override IReadOnlyList<string> Render(RenderContext context)
    {
        if (context.Height <= 1)
        {
            return _input.Render(context);
        }

        var statusLine = TextLayout.PadToWidth(Status, context.Width);
        var inputLine = _input.Render(new RenderContext(context.Width, 1))[0];
        var transcriptHeight = Math.Max(0, context.Height - 2);
        var transcriptLines = TextLayout.Wrap(Transcript, context.Width)
            .TakeLast(transcriptHeight)
            .Select(line => TextLayout.PadToWidth(line, context.Width))
            .ToList();

        while (transcriptLines.Count < transcriptHeight)
        {
            transcriptLines.Add(new string(' ', context.Width));
        }

        var result = new List<string> { statusLine };
        result.AddRange(transcriptLines);
        result.Add(inputLine);
        return result;
    }
}

internal sealed class CliInteractiveController
{
    private readonly CodingAgentSession _session;
    private readonly CliInteractiveView _view;
    private readonly List<string> _entries = [];
    private readonly Dictionary<string, int> _toolEntryIndexes = new(StringComparer.Ordinal);

    private readonly string _providerName;
    private readonly string _modelId;
    private readonly string? _sessionId;
    private readonly bool _persisted;

    private int? _assistantEntryIndex;
    private bool _isBusy;

    public CliInteractiveController(
        CodingAgentSession session,
        CliInteractiveView view,
        string providerName,
        string modelId,
        string? sessionId,
        bool persisted)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _providerName = providerName;
        _modelId = modelId;
        _sessionId = sessionId;
        _persisted = persisted;

        _view.Placeholder = "Type a prompt and press Enter. Use /exit to quit.";
        _view.IsFocused = true;
        UpdateStatus();
        RefreshTranscript();
    }

    public bool ShouldExit { get; private set; }

    public string TranscriptText => string.Join("\n\n", _entries);

    public async Task SubmitAsync(string prompt, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _view.InputValue = string.Empty;
        prompt = prompt.Trim();

        if (string.IsNullOrWhiteSpace(prompt))
        {
            return;
        }

        if (string.Equals(prompt, "/exit", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(prompt, "/quit", StringComparison.OrdinalIgnoreCase))
        {
            ShouldExit = true;
            return;
        }

        if (_isBusy)
        {
            AppendEntry("System", "The agent is still working. Wait for the current turn to finish.");
            return;
        }

        AppendEntry("You", prompt);
        _assistantEntryIndex = null;
        _toolEntryIndexes.Clear();
        _isBusy = true;
        UpdateStatus();

        try
        {
            await _session.PromptAsync(prompt, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _isBusy = false;
            UpdateStatus();
        }
    }

    public ValueTask OnAgentEventAsync(AgentEvent @event, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        switch (@event)
        {
            case AgentEvent.MessageUpdated { Message.Role: var role, AssistantMessageEvent: AssistantMessageEvent.TextDelta } updated
                when role == ChatRole.Assistant:
                UpsertAssistantEntry(updated.Message, complete: false);
                break;

            case AgentEvent.MessageCompleted { Message.Role: var role } completed when role == ChatRole.Assistant:
                UpsertAssistantEntry(completed.Message, complete: true);
                break;

            case AgentEvent.ToolExecutionStarted started:
                UpsertToolEntry(started.ToolCallId, started.ToolName, "running", null);
                break;

            case AgentEvent.ToolExecutionUpdated updated:
                UpsertToolEntry(updated.ToolCallId, updated.ToolName, "running", FormatToolResult(updated.PartialResult));
                break;

            case AgentEvent.ToolExecutionCompleted completed:
                UpsertToolEntry(
                    completed.ToolCallId,
                    completed.ToolName,
                    completed.IsError ? "error" : "done",
                    FormatToolResult(completed.Result));
                break;
        }

        return ValueTask.CompletedTask;
    }

    private void AppendEntry(string label, string text)
    {
        _entries.Add($"{label}> {text}");
        RefreshTranscript();
    }

    private void UpsertAssistantEntry(ChatMessage message, bool complete)
    {
        var text = ExtractAssistantText(message);
        if (_assistantEntryIndex is null)
        {
            _entries.Add($"Assistant> {text}");
            _assistantEntryIndex = _entries.Count - 1;
        }
        else
        {
            _entries[_assistantEntryIndex.Value] = $"Assistant> {text}";
        }

        if (complete)
        {
            _assistantEntryIndex = null;
        }

        RefreshTranscript();
    }

    private void UpsertToolEntry(string toolCallId, string toolName, string status, string? details)
    {
        var builder = new StringBuilder();
        builder.Append("Tool> ");
        builder.Append(toolName);
        builder.Append(' ');
        builder.Append(status);

        if (!string.IsNullOrWhiteSpace(details))
        {
            builder.Append(" | ");
            builder.Append(details);
        }

        var line = builder.ToString();

        if (_toolEntryIndexes.TryGetValue(toolCallId, out var entryIndex))
        {
            _entries[entryIndex] = line;
        }
        else
        {
            _entries.Add(line);
            _toolEntryIndexes[toolCallId] = _entries.Count - 1;
        }

        RefreshTranscript();
    }

    private void RefreshTranscript()
    {
        _view.Transcript = TranscriptText;
    }

    private void UpdateStatus()
    {
        var persistence = _persisted
            ? $"session:{_sessionId}"
            : "ephemeral";
        var activity = _isBusy
            ? "running"
            : "idle";

        _view.Status = $"PiSharp | {_providerName}/{_modelId} | {persistence} | {activity}";
    }

    private static string ExtractAssistantText(ChatMessage message)
    {
        var parts = message.Contents
            .Select(content => content switch
            {
                TextContent text => text.Text,
                TextReasoningContent reasoning => reasoning.Text,
                FunctionCallContent toolCall => $"[{toolCall.Name}]",
                _ => null,
            })
            .Where(static part => !string.IsNullOrWhiteSpace(part))
            .ToArray();

        return parts.Length == 0
            ? string.Empty
            : string.Join("\n", parts);
    }

    private static string? FormatToolResult(AgentToolResult result)
    {
        var parts = result.Content
            .Select(content => content switch
            {
                TextContent text => text.Text,
                _ => content.ToString(),
            })
            .Where(static part => !string.IsNullOrWhiteSpace(part))
            .ToArray();

        if (parts.Length > 0)
        {
            return string.Join("\n", parts);
        }

        return result.Value?.ToString();
    }
}

internal static class ConsoleKeyMapper
{
    public static bool IsExitKey(ConsoleKeyInfo keyInfo) =>
        keyInfo.Key == ConsoleKey.C && keyInfo.Modifiers.HasFlag(ConsoleModifiers.Control);

    public static string? ToRawInput(ConsoleKeyInfo keyInfo) =>
        keyInfo.Key switch
        {
            ConsoleKey.Enter => "\r",
            ConsoleKey.Tab => "\t",
            ConsoleKey.Backspace => "\b",
            ConsoleKey.Delete => "\u001b[3~",
            ConsoleKey.LeftArrow => "\u001b[D",
            ConsoleKey.RightArrow => "\u001b[C",
            ConsoleKey.UpArrow => "\u001b[A",
            ConsoleKey.DownArrow => "\u001b[B",
            ConsoleKey.Home => "\u001b[H",
            ConsoleKey.End => "\u001b[F",
            ConsoleKey.PageUp => "\u001b[5~",
            ConsoleKey.PageDown => "\u001b[6~",
            ConsoleKey.Escape => "\u001b",
            _ when keyInfo.KeyChar != '\0' => keyInfo.KeyChar.ToString(),
            _ => null,
        };
}
