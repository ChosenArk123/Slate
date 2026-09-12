using System.Text.Json.Serialization;

namespace Slate.Core;

public sealed class BrowserTab
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    private string _title = "New tab";
    public string Title
    {
        get => _title;
        set => _title = BrowserText.SanitizeTitle(value, Url == Navigation.NewTab ? "New tab" : Navigation.DisplayHost(Url));
    }
    public string Url { get; set; } = Navigation.NewTab;
    public string? Favicon { get; set; }
    public bool IsPinned { get; set; }
    public bool IsSleeping { get; set; }
    public bool IsTemporary { get; set; }
    public bool IsPrivate { get; set; }
    public DateTimeOffset LastAccessed { get; set; } = DateTimeOffset.UtcNow;
    [JsonIgnore] public bool IsLoading { get; set; }
}

public sealed class Workspace
{
    public Guid Id { get; set; } = Guid.NewGuid();
    private string _name = "Personal";
    public string Name
    {
        get => _name;
        set => _name = BrowserText.SanitizeLabel(value, 40, "Personal");
    }
    public Guid? ActiveTabId { get; set; }
}

public sealed class BookmarkEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    private string _title = "";
    public string Title
    {
        get => _title;
        set => _title = BrowserText.SanitizeTitle(value, Navigation.DisplayHost(Url));
    }
    public string Url { get; set; } = "";
    private string? _folder;
    public string? Folder
    {
        get => _folder;
        set => _folder = string.IsNullOrWhiteSpace(value) ? null : BrowserText.SanitizeLabel(value, 128);
    }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class BrowserSettings
{
    public static readonly string[] SleepOptions = ["Never", "5 minutes", "15 minutes", "30 minutes", "1 hour", "2 hours"];
    public static readonly string[] StartupBehaviorOptions = ["Restore previous session", "Open new tab page"];

    public string SearchEngine { get; set; } = "DuckDuckGo";
    public string Theme { get; set; } = "Dark";
    public string AccentTheme { get; set; } = "Slate";
    public int SleepAfterMinutes { get; set; } = 15;

    private bool _restoreSession = true;
    public bool RestoreSession
    {
        get => _restoreSession;
        set
        {
            _restoreSession = value;
            _startupBehavior = value ? "Restore previous session" : "Open new tab page";
        }
    }

    private string _startupBehavior = "Restore previous session";
    public string StartupBehavior
    {
        get => _startupBehavior;
        set
        {
            if (string.Equals(value, "Open new tab page", StringComparison.OrdinalIgnoreCase))
            {
                _startupBehavior = "Open new tab page";
                _restoreSession = false;
            }
            else
            {
                _startupBehavior = "Restore previous session";
                _restoreSession = true;
            }
        }
    }

    public bool SidebarCollapsed { get; set; }
    public bool CompactSidebarDensity { get; set; }
    public bool ReduceMotion { get; set; }
    public int DefaultZoomPercent { get; set; } = 100;
    public bool DeveloperToolsEnabled { get; set; }
    public bool ShowBookmarksBar { get; set; } = true;
    public string? DownloadPath { get; set; }
    public bool AskDownloadLocation { get; set; }
    public bool AutofillPasswords { get; set; } = true;
    public bool HardenedIsolation { get; set; }

    public static int SleepOptionToMinutes(string? option)
    {
        if (string.IsNullOrWhiteSpace(option)) return 15;
        var trimmed = option.Trim();
        if (trimmed.Equals("Never", StringComparison.OrdinalIgnoreCase) || trimmed == "0") return 0;
        if (trimmed.Equals("5 minutes", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("5 min", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("5m", StringComparison.OrdinalIgnoreCase) || trimmed == "5") return 5;
        if (trimmed.Equals("15 minutes", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("15 min", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("15m", StringComparison.OrdinalIgnoreCase) || trimmed == "15") return 15;
        if (trimmed.Equals("30 minutes", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("30 min", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("30m", StringComparison.OrdinalIgnoreCase) || trimmed == "30") return 30;
        if (trimmed.Equals("1 hour", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("1 hr", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("60 minutes", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("60m", StringComparison.OrdinalIgnoreCase) || trimmed == "60") return 60;
        if (trimmed.Equals("2 hours", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("2 hrs", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("120 minutes", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("120m", StringComparison.OrdinalIgnoreCase) || trimmed == "120") return 120;

        if (int.TryParse(trimmed, out var parsed))
        {
            return NormalizeSleepMinutes(parsed);
        }
        return 15;
    }

    public static int NormalizeSleepMinutes(int minutes)
    {
        if (minutes <= 0) return 0;
        if (minutes <= 10) return 5;
        if (minutes <= 20) return 15;
        if (minutes <= 45) return 30;
        if (minutes <= 90) return 60;
        return 120;
    }

    public static string MinutesToSleepOption(int minutes)
    {
        return minutes switch
        {
            <= 0 => "Never",
            <= 10 => "5 minutes",
            <= 20 => "15 minutes",
            <= 45 => "30 minutes",
            <= 90 => "1 hour",
            _ => "2 hours"
        };
    }
}

public sealed class HistoryEntry
{
    private string _title = "";
    public string Title
    {
        get => _title;
        set => _title = BrowserText.SanitizeTitle(value, Navigation.DisplayHost(Url));
    }
    public string Url { get; set; } = "";
    public DateTimeOffset VisitedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class DownloadEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FileName { get; set; } = "";
    public string Path { get; set; } = "";
    public string Status { get; set; } = "Downloading";
    public long BytesReceived { get; set; }
    public long? TotalBytes { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool IsPrivate { get; set; }
}

public sealed class WindowPlacement
{
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 840;
    public int? X { get; set; }
    public int? Y { get; set; }
    public bool Maximized { get; set; }
}

public sealed class BrowserState
{
    public int Version { get; set; } = 1;
    public List<BrowserTab> Tabs { get; set; } = [];
    public List<Workspace> Workspaces { get; set; } = [];
    public Guid ActiveWorkspaceId { get; set; }
    public List<BrowserTab> RecentlyClosed { get; set; } = [];
    public List<BookmarkEntry> Bookmarks { get; set; } = [];
    public List<HistoryEntry> History { get; set; } = [];
    public List<DownloadEntry> Downloads { get; set; } = [];
    public BrowserSettings Settings { get; set; } = new();
    public WindowPlacement Window { get; set; } = new();
}
