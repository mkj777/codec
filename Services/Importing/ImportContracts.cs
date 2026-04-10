using Codec.Models;

namespace Codec.Services.Importing
{
    public enum ImportEnqueueResultStatus
    {
        Accepted,
        Duplicate,
        Invalid,
        Error
    }

    public sealed record ImportEnqueueResult(ImportEnqueueResultStatus Status, string Message)
    {
        public bool IsAccepted => Status == ImportEnqueueResultStatus.Accepted;
    }

    public enum GameImportResultStatus
    {
        Added,
        Duplicate,
        Invalid,
        Failed
    }

    public sealed record GameImportResult(GameImportResultStatus Status, string Message, Game? Game)
    {
        public static GameImportResult Added(Game game, string message) => new(GameImportResultStatus.Added, message, game);
        public static GameImportResult Duplicate(string message) => new(GameImportResultStatus.Duplicate, message, null);
        public static GameImportResult Invalid(string message) => new(GameImportResultStatus.Invalid, message, null);
        public static GameImportResult Failed(string message) => new(GameImportResultStatus.Failed, message, null);
    }

    public sealed record GameImportRequest(
        string ExecutablePath,
        string FolderLocation,
        string NameHint,
        string ImportSource,
        int? SteamAppId = null,
        int? RawgId = null,
        bool IsManual = false);

    public enum ImportNotificationSeverity
    {
        Informational,
        Success,
        Warning,
        Error
    }

    public sealed record ImportNotification(
        string Title,
        string Message,
        ImportNotificationSeverity Severity,
        bool AutoHide = true);

    public sealed record GameImportStatusSnapshot(
        bool IsActive,
        bool IsScanning,
        string Message,
        int QueuedCount,
        int ProcessingCount,
        int AddedCount,
        int SkippedCount,
        int FailedCount);
}
