using Codec.Models;
using Codec.Services.Logging;

namespace Codec.Services.Importing
{
    public enum ImportEnqueueResultStatus
    {
        Accepted,
        Duplicate,
        Invalid,
        /// <summary>The path was fine but the file is no longer there.</summary>
        Missing,
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
        bool IsManual = false,
        string? LaunchScriptPath = null,
        int? IgdbId = null,
        string? EpicAppId = null,
        ScanLogBatch? LogBatch = null,
        string? MetadataLookupName = null);

    public enum ImportNotificationSeverity
    {
        Informational,
        Success,
        Warning,
        Error
    }

    /// <summary>
    /// ResultStatus carries why a manual import ended, so the view can tell a file that is not
    /// a game from an import that failed on the network or on artwork.
    /// </summary>
    public sealed record ImportNotification(
        string Title,
        string Message,
        ImportNotificationSeverity Severity,
        bool AutoHide = true,
        bool IsManual = false,
        GameImportResultStatus? ResultStatus = null);

    /// <summary>How the last scan of an import session ended. None while running or when no scan ran.</summary>
    public enum ScanOutcome
    {
        None,
        Completed,
        /// <summary>Ran to the end, but a source could not be checked or some games could not be added.</summary>
        Incomplete,
        /// <summary>Stopped on an error before it reached the end.</summary>
        Stopped,
        Cancelled
    }

    public sealed record GameImportStatusSnapshot(
        bool IsActive,
        bool IsScanning,
        string Message,
        int QueuedCount,
        int ProcessingCount,
        int AddedCount,
        int SkippedCount,
        int FailedCount,
        ScanOutcome Outcome = ScanOutcome.None);
}
