using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Codec.Models;

namespace Codec.ViewModels;

/// <summary>
/// Codec v2 presentation state: the library tabs, the single notice pill, the
/// first-run scan screen, and the details disclosure. Nothing here reaches past
/// the view layer.
/// </summary>
public partial class MainViewModel
{
    private const int RecentGameCount = 12;

    // -----------------------------------------------------------------------------
    // Library tabs
    // -----------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAllTabActive))]
    [NotifyPropertyChangedFor(nameof(IsInstalledTabActive))]
    [NotifyPropertyChangedFor(nameof(IsRecentTabActive))]
    private int _libraryTab;

    public bool IsAllTabActive => LibraryTab == 0;
    public bool IsInstalledTabActive => LibraryTab == 1;
    public bool IsRecentTabActive => LibraryTab == 2;

    [RelayCommand]
    private void SelectLibraryTab(string? index)
    {
        if (int.TryParse(index, out int tab))
            LibraryTab = tab;
    }

    partial void OnLibraryTabChanged(int value)
    {
        _recentGames = null;
        RefreshSidebarFilteredGames();
        RefreshDisplayedGames();
    }

    private HashSet<Game>? _recentGames;

    /// <summary>
    /// Recent is the twelve most recently played games. LastPlayedUtc is only
    /// populated for Steam-owned titles, so anything else falls back to the date
    /// it was added.
    /// </summary>
    private bool IsRecentGame(Game game)
    {
        _recentGames ??= Games
            .Where(IsReadyForLibrary)
            .OrderByDescending(candidate => candidate.LastPlayedUtc ?? candidate.DateAdded)
            .Take(RecentGameCount)
            .ToHashSet();

        return _recentGames.Contains(game);
    }

    // -----------------------------------------------------------------------------
    // Sidebar rest state
    // -----------------------------------------------------------------------------

    /// <summary>
    /// True while any modal owns the screen. The notice pill stays visible over
    /// them so background work still reports itself, but stops taking clicks:
    /// its Restart action must not answer a question the user has not.
    /// </summary>
    public bool IsAnyModalOpen =>
        IsSettingsVisible ||
        IsOnboardingVisible ||
        IsCloseChoiceVisible ||
        IsMediaOverlayOpen ||
        IsFranchiseOverlayOpen ||
        IsGameSettingsOpen;

    /// <summary>
    /// The empty state and the cold-start loading panel are both centred in the
    /// same cell, and both conditions hold at launch. Loading wins.
    /// </summary>
    public bool IsEmptyLibraryVisible => IsEmptyLibrary && !IsInitialLoading;

    /// <summary>
    /// Set while onboarding drives the first scan, so <c>ScanGamesAsync</c> leaves
    /// the onboarding surface up instead of hiding the scan step it is showing.
    /// </summary>
    public bool IsOnboardingScanActive { get; set; }

    /// <summary>
    /// The reference's count is a single line. Two lines let a taller block push
    /// the view tabs down, so the size joins the count rather than stacking.
    /// </summary>
    public string LibrarySummaryText => $"{LibraryGameCount} {LibraryGameLabel}, {LibraryTotalSizeText}";

    public bool IsSidebarEmpty => SidebarFilteredGames.Count == 0;

    public string SidebarEmptyMessage => HasGames ? "Nothing found" : "No games yet";

    private void NotifySidebarEmptyChanged()
    {
        OnPropertyChanged(nameof(IsSidebarEmpty));
        OnPropertyChanged(nameof(SidebarEmptyMessage));
    }

    // -----------------------------------------------------------------------------
    // Details disclosure
    // -----------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailMoreLabel))]
    private bool _isDetailMoreOpen;

    public string DetailMoreLabel => IsDetailMoreOpen ? "Less" : "More";

    [RelayCommand]
    private void ToggleDetailMore() => IsDetailMoreOpen = !IsDetailMoreOpen;

    // -----------------------------------------------------------------------------
    // First-run scan, shown as the closing step of onboarding
    // -----------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScanScreenTitle))]
    [NotifyPropertyChangedFor(nameof(IsScanScreenBusy))]
    private bool _isScanScreenComplete;

    public string ScanScreenTitle => IsScanScreenComplete ? "Your shelf is ready." : "Finding your games";

    public bool IsScanScreenBusy => !IsScanScreenComplete;

    // -----------------------------------------------------------------------------
    // Loading
    // -----------------------------------------------------------------------------

    [ObservableProperty] private bool _isLoadingIndeterminate = true;
    [ObservableProperty] private double _loadingProgress;

    // -----------------------------------------------------------------------------
    // Notice pill
    // -----------------------------------------------------------------------------
    // One line at a time. The most urgent state wins; the rest wait their turn.

    public bool IsNoticeVisible => NoticeText.Length > 0;

    public string NoticeText
    {
        get
        {
            if (IsUpdateErrorVisible)
                return "Update failed";
            if (IsUpdateBannerVisible)
                return "Update ready";
            if (IsUpdateDownloadingVisible)
                return WithPercent("Downloading update", UpdateDownloadProgress);
            if (IsUpdateCheckingVisible)
                return "Checking for updates";
            if (IsSteamSyncProgressVisible)
                return IsSteamSyncProgressIndeterminate
                    ? SteamSyncProgressTitle
                    : WithPercent(SteamSyncProgressTitle, NoticeProgress);
            if (IsImportStatusVisible)
                return ImportStatusMessage;
            if (IsScanProgressVisible)
                return ScanProgressMessage;
            if (IsGameNotAddedToastVisible)
                return "That file is not a game";
            if (IsGameAlreadyAddedToastVisible)
                return "Already in your library";
            if (IsScanCompleteEffectiveVisible)
                return "Scan complete";
            if (IsUpdateNoUpdateVisible)
                return "Codec is up to date";
            if (IsAdminWarningVisible)
                return "Running as Administrator";

            return string.Empty;
        }
    }

    public bool IsNoticeBusy =>
        IsUpdateCheckingVisible ||
        IsUpdateDownloadingVisible ||
        IsImportStatusVisible ||
        IsScanProgressVisible ||
        IsSteamSyncProgressVisible;

    public bool IsNoticeProgressVisible => IsNoticeBusy;

    public bool IsNoticeIndeterminate
    {
        get
        {
            if (IsUpdateDownloadingVisible)
                return false;
            if (IsSteamSyncProgressVisible)
                return IsSteamSyncProgressIndeterminate;
            if (IsScanProgressVisible)
                return ScanProgressIsIndeterminate;

            return true;
        }
    }

    public double NoticeProgress
    {
        get
        {
            if (IsUpdateDownloadingVisible)
                return UpdateDownloadProgress;
            if (IsSteamSyncProgressVisible && SteamSyncProgressMaximum > 0)
                return (double)SteamSyncProgressValue / SteamSyncProgressMaximum * 100d;
            if (IsScanProgressVisible && ScanProgressMaximum > ScanProgressMinimum)
                return (ScanProgressValue - ScanProgressMinimum)
                    / (ScanProgressMaximum - ScanProgressMinimum) * 100d;

            return 0d;
        }
    }

    public bool HasNoticeAction => IsUpdateBannerVisible;

    public string NoticeActionText => "Restart";

    public bool IsNoticeDismissible =>
        !IsNoticeBusy && !IsUpdateBannerVisible && NoticeText.Length > 0;

    [RelayCommand]
    private void DismissNotice()
    {
        IsAdminWarningVisible = false;
        IsUpdateErrorVisible = false;
        IsUpdateNoUpdateVisible = false;
        IsScanCompleteVisible = false;
        IsGameNotAddedToastVisible = false;
        IsGameAlreadyAddedToastVisible = false;
        NotifyNoticeChanged();
    }

    /// <summary>Appends a percentage without ever exposing a raw count.</summary>
    internal static string WithPercent(string activity, double percent)
        => $"{activity} · {Math.Clamp(Math.Round(percent), 0, 100):0}%";

    /// <summary>
    /// The pill is derived from a dozen independent flags, so it re-reads itself
    /// whenever any of them moves. Notice properties are not in the trigger set,
    /// which keeps the notification from feeding back into itself.
    /// </summary>
    private static readonly HashSet<string> NoticeTriggers =
    [
        nameof(IsUpdateErrorVisible),
        nameof(IsUpdateBannerVisible),
        nameof(IsUpdateDownloadingVisible),
        nameof(UpdateDownloadProgress),
        nameof(IsUpdateCheckingVisible),
        nameof(IsUpdateNoUpdateVisible),
        nameof(IsSteamSyncProgressVisible),
        nameof(IsSteamSyncProgressIndeterminate),
        nameof(SteamSyncProgressTitle),
        nameof(SteamSyncProgressValue),
        nameof(SteamSyncProgressMaximum),
        nameof(IsImportStatusVisible),
        nameof(ImportStatusMessage),
        nameof(IsScanProgressVisible),
        nameof(ScanProgressMessage),
        nameof(ScanProgressIsIndeterminate),
        nameof(ScanProgressValue),
        nameof(ScanProgressMaximum),
        nameof(ScanProgressMinimum),
        nameof(IsGameNotAddedToastVisible),
        nameof(IsGameAlreadyAddedToastVisible),
        nameof(IsScanCompleteVisible),
        nameof(IsAdminWarningVisible),
    ];

    private static readonly HashSet<string> ModalTriggers =
    [
        nameof(IsSettingsVisible),
        nameof(IsOnboardingVisible),
        nameof(IsCloseChoiceVisible),
        nameof(IsMediaOverlayOpen),
        nameof(IsFranchiseOverlayOpen),
        nameof(IsGameSettingsOpen),
    ];

    private void InitializeNoticeTracking()
    {
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == null)
                return;

            if (NoticeTriggers.Contains(e.PropertyName))
                NotifyNoticeChanged();

            if (ModalTriggers.Contains(e.PropertyName))
                OnPropertyChanged(nameof(IsAnyModalOpen));
        };
    }

    internal void NotifyNoticeChanged()
    {
        OnPropertyChanged(nameof(IsNoticeVisible));
        OnPropertyChanged(nameof(NoticeText));
        OnPropertyChanged(nameof(IsNoticeBusy));
        OnPropertyChanged(nameof(IsNoticeProgressVisible));
        OnPropertyChanged(nameof(IsNoticeIndeterminate));
        OnPropertyChanged(nameof(NoticeProgress));
        OnPropertyChanged(nameof(HasNoticeAction));
        OnPropertyChanged(nameof(IsNoticeDismissible));
    }
}
