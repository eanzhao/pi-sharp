namespace PiSharp.Mom;

public static class MomDefaults
{
    public const string SlackAppTokenEnvironmentVariable = "MOM_SLACK_APP_TOKEN";
    public const string SlackBotTokenEnvironmentVariable = "MOM_SLACK_BOT_TOKEN";
    public const string SessionDirectoryName = ".pi-sharp/sessions";
    public const string ScratchDirectoryName = "scratch";
    public const string EventsDirectoryName = "events";
    public const string LogFileName = "log.jsonl";
    public const string MemoryFileName = "MEMORY.md";
    public const int MainMessageCharacterLimit = 35_000;
    public const int MaxQueuedEventsPerChannel = 5;
    public const int StartupBackfillMaxPages = 3;
    public const int InitialChannelBackfillMessageLimit = 50;
}
