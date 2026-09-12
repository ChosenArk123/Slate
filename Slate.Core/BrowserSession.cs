namespace Slate.Core;

public sealed class BrowserSession
{
    public const int MaximumTabs = 100;
    public BrowserState State { get; }
    public BrowserSession(BrowserState? state = null)
    {
        State = state ?? new();
        Normalize();
    }

    public Workspace ActiveWorkspace => State.Workspaces.First(w => w.Id == State.ActiveWorkspaceId);
    public BrowserTab ActiveTab => State.Tabs.First(t => t.Id == ActiveWorkspace.ActiveTabId);
    public IEnumerable<BrowserTab> VisibleTabs => State.Tabs.Where(t => t.WorkspaceId == State.ActiveWorkspaceId)
        .OrderByDescending(t => t.IsPinned);

    private void Normalize()
    {
        State.Settings ??= new(); State.Window ??= new();
        State.Workspaces ??= []; State.Tabs ??= []; State.RecentlyClosed ??= []; State.History ??= []; State.Downloads ??= []; State.Bookmarks ??= [];
        State.Workspaces = State.Workspaces.DistinctBy(w => w.Id).Take(MaximumTabs).ToList();
        if (State.Workspaces.Count == 0) State.Workspaces.Add(new());
        foreach (var workspace in State.Workspaces) workspace.Name = BrowserText.SanitizeLabel(workspace.Name, 40);
        if (!State.Workspaces.Any(w => w.Id == State.ActiveWorkspaceId)) State.ActiveWorkspaceId = State.Workspaces[0].Id;
        State.Tabs = State.Tabs.Where(t => (!t.IsTemporary || t.IsPinned) && !t.IsPrivate).DistinctBy(t => t.Id).Take(MaximumTabs).ToList();
        if (!State.Settings.RestoreSession) State.Tabs.RemoveAll(t => !t.IsPinned);
        foreach (var tab in State.Tabs)
        {
            if (!State.Workspaces.Any(w => w.Id == tab.WorkspaceId)) tab.WorkspaceId = State.ActiveWorkspaceId;
            if (!Navigation.IsWebUrl(tab.Url)) tab.Url = Navigation.NewTab;
            tab.Title = BrowserText.SanitizeTitle(tab.Title, tab.Url == Navigation.NewTab ? "New tab" : Navigation.DisplayHost(tab.Url));
            tab.Favicon = null;
            tab.IsSleeping = tab.Url != Navigation.NewTab;
            tab.IsLoading = false;
        }
        foreach (var workspace in State.Workspaces) EnsureActiveTab(workspace);
        State.Bookmarks = State.Bookmarks.Where(b => Navigation.IsWebUrl(b.Url)).DistinctBy(b => b.Id).Take(1000).ToList();
        foreach (var bookmark in State.Bookmarks)
        {
            bookmark.Title = BrowserText.SanitizeTitle(bookmark.Title, Navigation.DisplayHost(bookmark.Url));
            bookmark.Folder = string.IsNullOrWhiteSpace(bookmark.Folder) ? null : BrowserText.SanitizeLabel(bookmark.Folder, 128);
        }
        State.History = State.History.Where(h => Navigation.IsWebUrl(h.Url)).Take(2000).ToList();
        foreach (var entry in State.History) entry.Title = BrowserText.SanitizeTitle(entry.Title, Navigation.DisplayHost(entry.Url));
        State.RecentlyClosed = State.RecentlyClosed.Where(t => !t.IsTemporary && !t.IsPrivate && Navigation.IsWebUrl(t.Url)).Take(30).ToList();
        foreach (var tab in State.RecentlyClosed) tab.Favicon = null;
        State.Downloads = State.Downloads.Where(download => !download.IsPrivate).Take(100).ToList();
        foreach (var download in State.Downloads)
        {
            if (download.Status is "Downloading" or "Paused") download.Status = "Interrupted";
            download.FileName = DownloadSafety.SanitizeFileName(download.FileName);
            if (!string.IsNullOrEmpty(download.Path))
            {
                if (!DownloadSafety.IsSafeLocalFilePath(download.Path) || Navigation.IsDangerousExtension(Path.GetExtension(download.Path)))
                    download.Path = "";
            }
        }
        if (!string.IsNullOrEmpty(State.Settings.DownloadPath) && !DownloadSafety.IsSafeLocalDirectory(State.Settings.DownloadPath))
            State.Settings.DownloadPath = null;
        State.Settings.SleepAfterMinutes = Math.Clamp(State.Settings.SleepAfterMinutes, 0, 120);
        State.Settings.DefaultZoomPercent = Math.Clamp(State.Settings.DefaultZoomPercent, 25, 500);
        if (State.Settings.Theme is not ("System" or "Light" or "Dark")) State.Settings.Theme = "Dark";
        if (State.Settings.AccentTheme is not ("Slate" or "Cobalt" or "Moss" or "Plum" or "Clay" or "Amber" or "System")) State.Settings.AccentTheme = "Slate";
    }

    private void EnsureActiveTab(Workspace workspace)
    {
        var tabs = State.Tabs.Where(t => t.WorkspaceId == workspace.Id).ToList();
        if (tabs.Count == 0)
        {
            if (State.Tabs.Count >= MaximumTabs)
            {
                var donorGroup = State.Tabs.GroupBy(t => t.WorkspaceId).FirstOrDefault(group => group.Count() > 1);
                var donorWorkspace = donorGroup is null ? null : State.Workspaces.FirstOrDefault(w => w.Id == donorGroup.Key);
                var donor = donorGroup?.FirstOrDefault(t => t.Id != donorWorkspace?.ActiveTabId) ?? donorGroup?.LastOrDefault();
                if (donor is not null) State.Tabs.Remove(donor);
                else throw new InvalidOperationException($"A session cannot contain more than {MaximumTabs} tabs.");
            }
            var tab = new BrowserTab { WorkspaceId = workspace.Id };
            State.Tabs.Add(tab); tabs.Add(tab);
        }
        if (!tabs.Any(t => t.Id == workspace.ActiveTabId)) workspace.ActiveTabId = tabs[0].Id;
    }

    public BrowserTab AddTab(string url = Navigation.NewTab, bool temporary = false, bool isPrivate = false)
    {
        if (State.Tabs.Count >= MaximumTabs) throw new InvalidOperationException($"Slate supports up to {MaximumTabs} open tabs.");
        var tab = new BrowserTab { WorkspaceId = State.ActiveWorkspaceId, Url = url, IsTemporary = temporary, IsPrivate = isPrivate,
            Title = isPrivate ? "InPrivate tab" : url == Navigation.NewTab ? "New tab" : Navigation.DisplayHost(url) };
        State.Tabs.Add(tab); Activate(tab.Id); return tab;
    }

    public bool Activate(Guid id)
    {
        var tab = State.Tabs.FirstOrDefault(t => t.Id == id);
        if (tab is null) return false;
        State.ActiveWorkspaceId = tab.WorkspaceId;
        ActiveWorkspace.ActiveTabId = id;
        tab.LastAccessed = DateTimeOffset.UtcNow;
        return true;
    }

    public void CloseTab(Guid id)
    {
        var tab = State.Tabs.FirstOrDefault(t => t.Id == id);
        if (tab is null) return;
        var workspace = State.Workspaces.First(w => w.Id == tab.WorkspaceId);
        var siblings = State.Tabs.Where(t => t.WorkspaceId == workspace.Id).OrderByDescending(t => t.IsPinned).ToList();
        var index = siblings.IndexOf(tab);
        State.Tabs.Remove(tab);
        if (!tab.IsTemporary && !tab.IsPrivate && Navigation.IsWebUrl(tab.Url))
        {
            State.RecentlyClosed.Insert(0, tab);
            State.RecentlyClosed = State.RecentlyClosed.Take(30).ToList();
        }
        siblings.Remove(tab);
        if (workspace.ActiveTabId == id) workspace.ActiveTabId = siblings.Count > 0 ? siblings[Math.Min(index, siblings.Count - 1)].Id : null;
        EnsureActiveTab(workspace);
    }

    public BrowserTab? RestoreClosed(Guid? id = null)
    {
        if (State.Tabs.Count >= MaximumTabs) return null;
        var tab = id.HasValue ? State.RecentlyClosed.FirstOrDefault(t => t.Id == id) : State.RecentlyClosed.FirstOrDefault();
        if (tab is null) return null;
        State.RecentlyClosed.Remove(tab);
        if (State.Tabs.Any(t => t.Id == tab.Id)) tab.Id = Guid.NewGuid();
        tab.WorkspaceId = State.ActiveWorkspaceId;
        tab.IsSleeping = true; tab.IsLoading = false;
        State.Tabs.Add(tab); Activate(tab.Id); return tab;
    }

    public void Pin(Guid id)
    {
        var tab = State.Tabs.First(t => t.Id == id);
        tab.IsPinned = !tab.IsPinned;
        if (tab.IsPinned) tab.IsTemporary = false;
    }

    public bool MoveTab(Guid id, int delta)
    {
        var tab = State.Tabs.FirstOrDefault(t => t.Id == id);
        if (tab is null || delta == 0) return false;
        var siblings = State.Tabs.Where(t => t.WorkspaceId == tab.WorkspaceId && t.IsPinned == tab.IsPinned).ToList();
        var currentIndex = siblings.IndexOf(tab);
        var targetIndex = currentIndex + delta;
        if (targetIndex < 0 || targetIndex >= siblings.Count) return false;

        var targetSibling = siblings[targetIndex];
        State.Tabs.Remove(tab);
        var targetPos = State.Tabs.IndexOf(targetSibling);
        State.Tabs.Insert(delta > 0 ? targetPos + 1 : targetPos, tab);
        return true;
    }

    public bool ReorderTab(Guid sourceId, Guid targetId)
    {
        var source = State.Tabs.FirstOrDefault(t => t.Id == sourceId);
        var target = State.Tabs.FirstOrDefault(t => t.Id == targetId);
        if (source is null || target is null || source.Id == target.Id) return false;

        source.WorkspaceId = target.WorkspaceId;
        source.IsPinned = target.IsPinned;
        if (source.IsPinned) source.IsTemporary = false;

        State.Tabs.Remove(source);
        var targetPos = State.Tabs.IndexOf(target);
        State.Tabs.Insert(targetPos, source);
        return true;
    }

    public List<BrowserTab> CloseOtherTabs(Guid keepTabId)
    {
        var target = State.Tabs.FirstOrDefault(t => t.Id == keepTabId);
        if (target is null) return [];
        var toClose = State.Tabs
            .Where(t => t.WorkspaceId == target.WorkspaceId && t.Id != target.Id && !t.IsPinned)
            .ToList();
        foreach (var tab in toClose) CloseTab(tab.Id);
        Activate(target.Id);
        return toClose;
    }

    public List<BrowserTab> CloseTabsBelow(Guid tabId)
    {
        var target = State.Tabs.FirstOrDefault(t => t.Id == tabId);
        if (target is null) return [];
        var siblings = State.Tabs.Where(t => t.WorkspaceId == target.WorkspaceId && !t.IsPinned).ToList();
        var index = siblings.IndexOf(target);
        if (index < 0 || index >= siblings.Count - 1) return [];
        var toClose = siblings.Skip(index + 1).ToList();
        foreach (var tab in toClose) CloseTab(tab.Id);
        return toClose;
    }

    public Workspace AddWorkspace(string name)
    {
        if (State.Tabs.Count >= MaximumTabs) throw new InvalidOperationException($"Close a tab before creating another workspace. Slate supports up to {MaximumTabs} open tabs.");
        var workspace = new Workspace { Name = BrowserText.SanitizeLabel(name, 40) };
        State.Workspaces.Add(workspace); State.ActiveWorkspaceId = workspace.Id; EnsureActiveTab(workspace); return workspace;
    }

    public void RemoveWorkspace(Guid id)
    {
        if (State.Workspaces.Count < 2) return;
        var target = State.Workspaces.First(w => w.Id != id);
        foreach (var tab in State.Tabs.Where(t => t.WorkspaceId == id)) tab.WorkspaceId = target.Id;
        State.Workspaces.RemoveAll(w => w.Id == id);
        if (State.ActiveWorkspaceId == id) State.ActiveWorkspaceId = target.Id;
        EnsureActiveTab(target);
    }

    public void RecordVisit(BrowserTab tab)
    {
        if (!Navigation.IsWebUrl(tab.Url) || tab.IsTemporary || tab.IsPrivate) return;
        State.History.RemoveAll(h => h.Url == tab.Url);
        State.History.Insert(0, new() { Url = tab.Url, Title = BrowserText.SanitizeTitle(tab.Title, Navigation.DisplayHost(tab.Url)) });
        if (State.History.Count > 2000) State.History.RemoveRange(2000, State.History.Count - 2000);
    }

    public bool IsBookmarked(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || url == Navigation.NewTab) return false;
        return State.Bookmarks.Any(b => string.Equals(b.Url, url, StringComparison.OrdinalIgnoreCase));
    }

    public BookmarkEntry? AddBookmark(string url, string title, string? folder = null)
    {
        if (!Navigation.IsWebUrl(url)) return null;
        var existing = State.Bookmarks.FirstOrDefault(b => string.Equals(b.Url, url, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (!string.IsNullOrWhiteSpace(title)) existing.Title = BrowserText.SanitizeTitle(title, Navigation.DisplayHost(url));
            if (folder is not null) existing.Folder = string.IsNullOrWhiteSpace(folder) ? null : BrowserText.SanitizeLabel(folder, 128);
            return existing;
        }

        var entry = new BookmarkEntry
        {
            Url = url,
            Title = BrowserText.SanitizeTitle(title, Navigation.DisplayHost(url)),
            Folder = string.IsNullOrWhiteSpace(folder) ? null : BrowserText.SanitizeLabel(folder, 128)
        };
        State.Bookmarks.Insert(0, entry);
        if (State.Bookmarks.Count > 1000) State.Bookmarks.RemoveRange(1000, State.Bookmarks.Count - 1000);
        return entry;
    }

    public bool RemoveBookmark(Guid id)
    {
        return State.Bookmarks.RemoveAll(b => b.Id == id) > 0;
    }

    public bool RemoveBookmarkByUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        return State.Bookmarks.RemoveAll(b => string.Equals(b.Url, url, StringComparison.OrdinalIgnoreCase)) > 0;
    }

    public bool ToggleBookmark(string url, string title)
    {
        if (IsBookmarked(url))
        {
            RemoveBookmarkByUrl(url);
            return false;
        }

        AddBookmark(url, title);
        return true;
    }

    public bool RemoveHistoryEntry(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        return State.History.RemoveAll(h => string.Equals(h.Url, url, StringComparison.OrdinalIgnoreCase)) > 0;
    }

    public int ClearHistory(DateTimeOffset? since = null, string? query = null)
    {
        return State.History.RemoveAll(h =>
        {
            bool matchTime = since is null || h.VisitedAt >= since.Value;
            bool matchQuery = string.IsNullOrWhiteSpace(query) ||
                h.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                h.Url.Contains(query, StringComparison.OrdinalIgnoreCase);
            return matchTime && matchQuery;
        });
    }
}
