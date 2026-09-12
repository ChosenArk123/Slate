using System.Text.Json;

namespace Slate.Core;

public sealed class StateStore(string directory)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public string DirectoryPath { get; } = directory;
    public string? LastError { get; private set; }
    private string StatePath => Path.Combine(DirectoryPath, "session.json");

    public BrowserState Load()
    {
        foreach (var path in new[] { StatePath, StatePath + ".bak" })
        {
            if (!File.Exists(path)) continue;
            try
            {
                var state = JsonSerializer.Deserialize<BrowserState>(File.ReadAllText(path), Json);
                if (state is null || state.Version != 1) throw new JsonException("Unsupported session format.");
                // Validate nested records before trusting a partially edited/corrupt file.
                _ = new BrowserSession(state);
                return state;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NullReferenceException or ArgumentException)
            {
                LastError = "The previous session could not be read. A backup or new session was loaded.";
            }
        }
        return new();
    }

    public bool Save(BrowserState state)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            SanitizeTextFields(state);
            var persistedTabs = state.Tabs.Where(tab => (!tab.IsTemporary || tab.IsPinned) && !tab.IsPrivate &&
                (tab.Url == Navigation.NewTab || Navigation.IsWebUrl(tab.Url))).ToList();
            var persistedWorkspaces = state.Workspaces.Select(w =>
            {
                var tabExists = persistedTabs.Any(t => t.WorkspaceId == w.Id && t.Id == w.ActiveTabId);
                return new Workspace
                {
                    Id = w.Id,
                    Name = w.Name,
                    ActiveTabId = tabExists ? w.ActiveTabId : persistedTabs.FirstOrDefault(t => t.WorkspaceId == w.Id)?.Id
                };
            }).ToList();
            var persisted = new BrowserState
            {
                Version = state.Version,
                Tabs = persistedTabs,
                Workspaces = persistedWorkspaces,
                ActiveWorkspaceId = state.ActiveWorkspaceId,
                RecentlyClosed = state.RecentlyClosed.Where(tab => !tab.IsTemporary && !tab.IsPrivate && Navigation.IsWebUrl(tab.Url)).ToList(),
                Bookmarks = state.Bookmarks,
                History = state.History,
                Downloads = state.Downloads.Where(download => !download.IsPrivate).ToList(),
                Settings = state.Settings,
                Window = state.Window
            };
            var data = JsonSerializer.SerializeToUtf8Bytes(persisted, Json);
            var temp = StatePath + ".tmp";
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                file.Write(data); file.Flush(true);
            }
            if (File.Exists(StatePath)) File.Replace(temp, StatePath, StatePath + ".bak");
            else File.Move(temp, StatePath);
            // Keep the recovery copy free of legacy temporary-tab records as well.
            var backupTemp = StatePath + ".bak.tmp";
            using (var backup = new FileStream(backupTemp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                backup.Write(data); backup.Flush(true);
            }
            File.Move(backupTemp, StatePath + ".bak", true);
            LastError = null; return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LastError = "Slate could not save this session: " + ex.Message; return false;
        }
    }

    private static void SanitizeTextFields(BrowserState state)
    {
        foreach (var workspace in state.Workspaces) workspace.Name = BrowserText.SanitizeLabel(workspace.Name, 40);
        foreach (var tab in state.Tabs.Concat(state.RecentlyClosed))
            tab.Title = BrowserText.SanitizeTitle(tab.Title, tab.Url == Navigation.NewTab ? "New tab" : Navigation.DisplayHost(tab.Url));
        foreach (var bookmark in state.Bookmarks)
        {
            bookmark.Title = BrowserText.SanitizeTitle(bookmark.Title, Navigation.DisplayHost(bookmark.Url));
            bookmark.Folder = string.IsNullOrWhiteSpace(bookmark.Folder) ? null : BrowserText.SanitizeLabel(bookmark.Folder, 128);
        }
        foreach (var history in state.History)
            history.Title = BrowserText.SanitizeTitle(history.Title, Navigation.DisplayHost(history.Url));
    }
}
