using System.Text.Json;
using Slate.Core;

int passed = 0;

void Check(string name, Action test)
{
    try { test(); Console.WriteLine("PASS  " + name); passed++; }
    catch (Exception ex) { Console.Error.WriteLine("FAIL  " + name + ": " + ex.Message); Environment.ExitCode = 1; }
}
void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
void Equal<T>(T expected, T actual) { Assert(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}, got {actual}"); }

Check("Blank input opens a new tab", () => Equal(Navigation.NewTab, Navigation.Resolve("  ")));
Check("Bare domains use HTTPS", () => Equal("https://example.com/docs", Navigation.Resolve(" example.com/docs ")));
Check("Explicit URLs preserve query and fragments", () => Equal("https://example.com/?q=a%20b#top", Navigation.Resolve("https://example.com/?q=a%20b#top")));
Check("Localhost with a port uses HTTP", () => Equal("http://localhost:3000/test", Navigation.Resolve("localhost:3000/test")));
Check("Domains that resemble localhost still use HTTPS", () => Equal("https://localhost.example.com/", Navigation.Resolve("localhost.example.com")));
Check("Loopback IPv6 with a port uses HTTP", () => Equal("http://[::1]:8080/", Navigation.Resolve("[::1]:8080")));
Check("Phrases use the configured search engine", () => Equal("https://www.google.com/search?q=quiet%20web", Navigation.Resolve("quiet web", "Google")));
Check("Unsupported schemes cannot execute", () => Assert(Navigation.Resolve("javascript:alert(1)").StartsWith("https://duckduckgo.com/?q=")));
Check("File URLs cannot navigate directly", () => Assert(Navigation.Resolve("file:///C:/secret.txt").StartsWith("https://duckduckgo.com/?q=")));
Check("Frames reject file and custom schemes but retain normal web schemes", () =>
{
    Assert(!Navigation.IsAllowedFrameUrl("file:///C:/secret.txt"));
    Assert(!Navigation.IsAllowedFrameUrl("calculator://"));
    Assert(!Navigation.IsAllowedFrameUrl("https:missing-host"));
    Assert(!Navigation.IsAllowedFrameUrl("javascript:alert(1)"));
    Assert(Navigation.IsAllowedFrameUrl("https://example.com/frame"));
    Assert(Navigation.IsAllowedFrameUrl("blob:https://example.com/id"));
    Assert(Navigation.IsAllowedFrameUrl("about:blank"));
});
Check("Automatic password delivery is restricted to HTTPS origins", () =>
{
    Assert(Navigation.IsHttpsOrigin("https://accounts.example.com/login"));
    Assert(!Navigation.IsHttpsOrigin("http://accounts.example.com/login"));
    Assert(!Navigation.IsHttpsOrigin("http://localhost:3000/login"));
    Assert(!Navigation.IsHttpsOrigin("http://127.0.0.1:8080/login"));
    Assert(!Navigation.IsHttpsOrigin("file:///C:/safe.html"));
});
Check("Search terms are escaped", () => Assert(Navigation.Search("a&b#c?d", "Bing").EndsWith("a%26b%23c%3Fd")));
Check("Initial session always has a workspace and active tab", () => { var s = new BrowserSession(); Equal(1, s.State.Workspaces.Count); Equal(s.ActiveTab.WorkspaceId, s.ActiveWorkspace.Id); });
Check("New sessions use Slate's dark theme and accent", () => { var s = new BrowserSession(); Equal("Dark", s.State.Settings.Theme); Equal("Slate", s.State.Settings.AccentTheme); });
Check("Unknown appearance settings recover to supported values", () => { var s = new BrowserSession(new() { Settings = new() { Theme = "Neon", AccentTheme = "Rainbow" } }); Equal("Dark", s.State.Settings.Theme); Equal("Slate", s.State.Settings.AccentTheme); });
Check("Amber appearance setting is preserved by session recovery", () => { var s = new BrowserSession(new() { Settings = new() { Theme = "Dark", AccentTheme = "Amber" } }); Equal("Dark", s.State.Settings.Theme); Equal("Amber", s.State.Settings.AccentTheme); });
Check("Daily-browser password and zoom settings normalize and persist", () =>
{
    var state = new BrowserState { Settings = new() { DefaultZoomPercent = 900, AutofillPasswords = false } };
    var session = new BrowserSession(state);
    Equal(500, session.State.Settings.DefaultZoomPercent);
    Assert(!session.State.Settings.AutofillPasswords);
});
Check("Closing the last tab leaves a usable new tab", () => { var s = new BrowserSession(); s.CloseTab(s.ActiveTab.Id); Equal(1, s.State.Tabs.Count); Equal(Navigation.NewTab, s.ActiveTab.Url); });
Check("Closing active tab selects its adjacent sibling", () => { var s = new BrowserSession(); var a = s.ActiveTab; var b = s.AddTab("https://example.com/"); var c = s.AddTab(); s.Activate(b.Id); s.CloseTab(b.Id); Equal(c.Id, s.ActiveTab.Id); Assert(s.State.Tabs.Contains(a)); });
Check("Closed tabs can be restored with their URL", () => { var s = new BrowserSession(); var tab = s.AddTab("https://example.com/"); s.CloseTab(tab.Id); var restored = s.RestoreClosed(); Equal(tab.Id, restored!.Id); Equal("https://example.com/", s.ActiveTab.Url); });
Check("Pinned tabs sort ahead of regular tabs", () => { var s = new BrowserSession(); var tab = s.AddTab(); s.Pin(tab.Id); Equal(tab.Id, s.VisibleTabs.First().Id); });
Check("Pinning a temporary tab keeps it", () => { var s = new BrowserSession(); var tab = s.AddTab("https://example.com/", true); s.Pin(tab.Id); Assert(!tab.IsTemporary); var loaded = new BrowserSession(s.State); Assert(loaded.State.Tabs.Any(t => t.Id == tab.Id)); });
Check("Temporary tabs are discarded on restart", () => { var s = new BrowserSession(); var tab = s.AddTab("https://example.com/", true); var loaded = new BrowserSession(s.State); Assert(!loaded.State.Tabs.Any(t => t.Id == tab.Id)); Assert(loaded.ActiveTab != null); });
Check("Temporary tab visits are not recorded", () => { var s = new BrowserSession(); s.RecordVisit(s.AddTab("https://example.com/", true)); Equal(0, s.State.History.Count); });
Check("Temporary tabs are never written to primary or backup session files", () =>
{
    var directory = Path.Combine(Path.GetTempPath(), "slate-test-" + Guid.NewGuid());
    try
    {
        var store = new StateStore(directory); var session = new BrowserSession();
        var temporary = session.AddTab("https://temporary.example/", true);
        Assert(store.Save(session.State)); Assert(store.Save(session.State));
        Assert(!File.ReadAllText(Path.Combine(directory, "session.json")).Contains(temporary.Url, StringComparison.Ordinal));
        Assert(!File.ReadAllText(Path.Combine(directory, "session.json.bak")).Contains(temporary.Url, StringComparison.Ordinal));
    }
    finally { Directory.Delete(directory, true); }
});
Check("Workspaces preserve their active tabs", () => { var s = new BrowserSession(); var first = s.ActiveTab; var secondSpace = s.AddWorkspace("Research"); var second = s.ActiveTab; s.Activate(first.Id); Equal(first.Id, s.ActiveTab.Id); s.Activate(second.Id); Equal(secondSpace.Id, s.ActiveWorkspace.Id); });
Check("Removing a workspace moves its tabs without data loss", () => { var s = new BrowserSession(); var first = s.ActiveWorkspace; var space = s.AddWorkspace("Research"); var tab = s.ActiveTab; s.RemoveWorkspace(space.Id); Equal(first.Id, s.ActiveWorkspace.Id); Assert(s.State.Tabs.Contains(tab)); Equal(first.Id, tab.WorkspaceId); });
Check("Invalid active IDs and orphan tabs recover", () => { var s = new BrowserSession(new() { ActiveWorkspaceId = Guid.NewGuid(), Tabs = [new() { WorkspaceId = Guid.NewGuid(), Url = "https://example.com/" }] }); Equal(s.ActiveWorkspace.Id, s.ActiveTab.WorkspaceId); });
Check("Disabling session restore retains pinned tabs only", () => { var s = new BrowserSession(); var pinned = s.AddTab("https://example.com/"); s.Pin(pinned.Id); s.AddTab("https://example.org/"); s.State.Settings.RestoreSession = false; var loaded = new BrowserSession(s.State); Equal(1, loaded.State.Tabs.Count); Equal(pinned.Id, loaded.ActiveTab.Id); });
Check("History is deduplicated and capped", () => { var s = new BrowserSession(); var tab = s.ActiveTab; for (int i = 0; i < 2020; i++) { tab.Url = $"https://example.com/{i}"; s.RecordVisit(tab); } Equal(2000, s.State.History.Count); s.RecordVisit(tab); Equal(2000, s.State.History.Count); Equal(tab.Url, s.State.History[0].Url); });
Check("Sessions round trip through atomic JSON storage", () =>
{
    var directory = Path.Combine(Path.GetTempPath(), "slate-test-" + Guid.NewGuid());
    try
    {
        var store = new StateStore(directory); var session = new BrowserSession(); session.AddWorkspace("Reading"); session.AddTab("https://example.com/");
        Assert(store.Save(session.State)); var restored = new BrowserSession(store.Load()); Equal("Reading", restored.ActiveWorkspace.Name); Equal("https://example.com/", restored.ActiveTab.Url); Assert(restored.ActiveTab.IsSleeping);
    }
    finally { Directory.Delete(directory, true); }
});
Check("Corrupt primary session recovers from backup", () =>
{
    var directory = Path.Combine(Path.GetTempPath(), "slate-test-" + Guid.NewGuid());
    try
    {
        var store = new StateStore(directory); var s = new BrowserSession(); s.AddTab("https://example.com/"); Assert(store.Save(s.State)); Assert(store.Save(s.State));
        File.WriteAllText(Path.Combine(directory, "session.json"), "{broken");
        var loaded = new BrowserSession(store.Load()); Equal("https://example.com/", loaded.ActiveTab.Url); Assert(store.LastError is not null);
    }
    finally { Directory.Delete(directory, true); }
});
Check("Interrupted downloads are not reported as active after restart", () => { var s = new BrowserSession(new() { Downloads = [new()] }); Equal("Interrupted", s.State.Downloads[0].Status); });
Check("Sessions enforce a global tab cap", () =>
{
    var state = new BrowserState();
    var workspace = new Workspace(); state.Workspaces.Add(workspace); state.ActiveWorkspaceId = workspace.Id;
    for (int i = 0; i < BrowserSession.MaximumTabs + 10; i++) state.Tabs.Add(new() { WorkspaceId = workspace.Id, Url = $"https://example.com/{i}" });
    var session = new BrowserSession(state);
    Equal(BrowserSession.MaximumTabs, session.State.Tabs.Count);
    bool rejected = false;
    try { session.AddTab(); } catch (InvalidOperationException) { rejected = true; }
    Assert(rejected, "The runtime tab cap was not enforced.");
    session.State.RecentlyClosed.Add(new() { Url = "https://example.com/closed" });
    Assert(session.RestoreClosed() is null, "Restoring a closed tab bypassed the global cap.");
    Equal(BrowserSession.MaximumTabs, session.State.Tabs.Count);
});
Check("FaviconStore differentiates scheme and ports while preserving www", () =>
{
    // Scheme differences produce different keys
    Assert(FaviconStore.GetKey("http://example.com") != FaviconStore.GetKey("https://example.com"));

    // Non-default port differences produce different keys
    Assert(FaviconStore.GetKey("http://example.com:8080") != FaviconStore.GetKey("http://example.com:3000"));
    Assert(FaviconStore.GetKey("http://localhost:3000") != FaviconStore.GetKey("http://localhost:5000"));

    // Default ports normalize to the same key
    Equal(FaviconStore.GetKey("http://example.com"), FaviconStore.GetKey("http://example.com:80"));
    Equal(FaviconStore.GetKey("https://example.com"), FaviconStore.GetKey("https://example.com:443"));

    // www is preserved per requirement (not automatically stripped)
    Assert(FaviconStore.GetKey("https://www.example.com") != FaviconStore.GetKey("https://example.com"));

    // Query and path are excluded from origin key
    Equal(FaviconStore.GetKey("https://example.com/docs?query=1#top"), FaviconStore.GetKey("https://example.com/"));

    // Bare host fallback
    Equal("example.com", FaviconStore.NormalizeOriginOrHost(" example.com "));
    Equal(FaviconStore.GetKey("example.com"), FaviconStore.GetKey("EXAMPLE.COM"));
});
Check("FaviconStore validates PNG data and handles corrupt files gracefully", () =>
{
    var validPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
    Assert(FaviconStore.IsValidPng(validPng));
    Assert(!FaviconStore.IsValidPng(null));
    Assert(!FaviconStore.IsValidPng([]));
    Assert(!FaviconStore.IsValidPng(System.Text.Encoding.UTF8.GetBytes("This is not a PNG file")));
    Assert(!FaviconStore.IsValidPng(validPng[..15]));
});
Check("FaviconStore handles concurrent writes to the same key without corruption", () =>
{
    var directory = Path.Combine(Path.GetTempPath(), "slate-favicons-concurrent-" + Guid.NewGuid());
    try
    {
        var store = new FaviconStore(directory);
        var validPng1 = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        var validPng2 = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAFElEQVR42mNk+M9QzwAEjDAGYzAAAEvEAwX4n3y1AAAAAElFTkSuQmCC");

        var tasks = Enumerable.Range(0, 20).Select(i =>
            Task.Run(() => store.SaveFaviconAsync("https://example.com/page", (i % 2 == 0) ? validPng1 : validPng2))
        ).ToArray();

        Task.WaitAll(tasks);

        Assert(store.HasFavicon("https://example.com/"));
        var loaded = store.GetFaviconBytes("https://example.com/");
        Assert(loaded is not null && (loaded.SequenceEqual(validPng1) || loaded.SequenceEqual(validPng2)));

        // No orphaned temporary files left behind
        var tempFiles = Directory.GetFiles(directory, "*.tmp");
        Equal(0, tempFiles.Length);
    }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
});
Check("FaviconStore cache overwrite replaces stale image and clear purges disk cache", () =>
{
    var directory = Path.Combine(Path.GetTempPath(), "slate-favicons-overwrite-" + Guid.NewGuid());
    try
    {
        var store = new FaviconStore(directory);
        var validPng1 = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        var validPng2 = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAFElEQVR42mNk+M9QzwAEjDAGYzAAAEvEAwX4n3y1AAAAAElFTkSuQmCC");

        store.SaveFaviconAsync("https://example.com", validPng1).GetAwaiter().GetResult();
        Assert(store.GetFaviconBytes("https://example.com")!.SequenceEqual(validPng1));

        // Overwrite with validPng2
        store.SaveFaviconAsync("https://example.com", validPng2).GetAwaiter().GetResult();
        Assert(store.GetFaviconBytes("https://example.com")!.SequenceEqual(validPng2));

        // Corrupt file on disk is rejected
        var corruptKey = FaviconStore.GetKey("https://corrupt.example");
        File.WriteAllText(Path.Combine(directory, corruptKey + ".png"), "not a valid png");
        Assert(!store.HasFavicon("https://corrupt.example"));
        Assert(store.GetFaviconBytes("https://corrupt.example") is null);

        // Clear purges cache
        store.Clear();
        Assert(!store.HasFavicon("https://example.com"));
        Assert(!Directory.Exists(directory));
    }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
});
Check("FaviconStore pending save followed by ClearAsync cannot recreate cache files or directory", () =>
{
    var directory = Path.Combine(Path.GetTempPath(), "slate-favicons-race-" + Guid.NewGuid());
    try
    {
        var store = new FaviconStore(directory);
        var validPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

        var saveTasks = Enumerable.Range(0, 10).Select(i =>
            store.SaveFaviconAsync($"https://example{i}.com", validPng)
        ).ToArray();

        store.ClearAsync().GetAwaiter().GetResult();
        Task.WaitAll(saveTasks);

        Assert(!store.HasFavicon("https://example0.com"));
        if (Directory.Exists(directory))
        {
            var files = Directory.GetFiles(directory, "*.png");
            Equal(0, files.Length);
            var tmpFiles = Directory.GetFiles(directory, "*.tmp");
            Equal(0, tmpFiles.Length);
        }
    }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
});
Check("FaviconStore simultaneous reads for the same origin succeed without conflict", () =>
{
    var directory = Path.Combine(Path.GetTempPath(), "slate-favicons-reads-" + Guid.NewGuid());
    try
    {
        var store = new FaviconStore(directory);
        var validPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        store.SaveFaviconAsync("https://example.com", validPng).GetAwaiter().GetResult();

        var readTasks = Enumerable.Range(0, 20).Select(_ =>
            Task.Run(async () =>
            {
                var bytes = await store.GetFaviconBytesAsync("https://example.com");
                Assert(bytes is not null && bytes.SequenceEqual(validPng));
            })
        ).ToArray();

        Task.WaitAll(readTasks);
    }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
});
Check("BrowserCommandRegistry matches all browser shortcuts", () =>
{
    Assert(BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyT, ShortcutModifiers.Control, out var cmd) && cmd == BrowserCommand.NewTab);
    Assert(BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyT, ShortcutModifiers.Control | ShortcutModifiers.Shift, out cmd) && cmd == BrowserCommand.RestoreClosedTab);
    Assert(BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyW, ShortcutModifiers.Control, out cmd) && cmd == BrowserCommand.CloseTab);
    Assert(BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyTab, ShortcutModifiers.Control, out cmd) && cmd == BrowserCommand.NextTab);
    Assert(BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyTab, ShortcutModifiers.Control | ShortcutModifiers.Shift, out cmd) && cmd == BrowserCommand.PreviousTab);
    Assert(BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyLeft, ShortcutModifiers.Alt, out cmd) && cmd == BrowserCommand.GoBack);
    Assert(BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyRight, ShortcutModifiers.Alt, out cmd) && cmd == BrowserCommand.GoForward);
    Assert(BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyR, ShortcutModifiers.Control, out cmd) && cmd == BrowserCommand.Reload);
    Assert(BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyF5, ShortcutModifiers.None, out cmd) && cmd == BrowserCommand.Reload);
    Assert(BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyB, ShortcutModifiers.Control, out cmd) && cmd == BrowserCommand.ToggleSidebar);
    Assert(BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyK, ShortcutModifiers.Control, out cmd) && cmd == BrowserCommand.CommandPalette);
    Assert(BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyP, ShortcutModifiers.Control | ShortcutModifiers.Shift, out cmd) && cmd == BrowserCommand.CommandPalette);
    Assert(BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyH, ShortcutModifiers.Control, out cmd) && cmd == BrowserCommand.History);
    Assert(BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyJ, ShortcutModifiers.Control, out cmd) && cmd == BrowserCommand.Downloads);
    Assert(BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyComma, ShortcutModifiers.Control, out cmd) && cmd == BrowserCommand.Settings);
    Assert(BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyS, ShortcutModifiers.Control | ShortcutModifiers.Shift, out cmd) && cmd == BrowserCommand.ToggleSplitView);
    Assert(BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyN, ShortcutModifiers.Control | ShortcutModifiers.Shift, out cmd) && cmd == BrowserCommand.NewTemporaryTab);
    Assert(BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyL, ShortcutModifiers.Control, out cmd) && cmd == BrowserCommand.FocusAddress);
    Assert(BrowserCommandRegistry.TryMatch(0x31, ShortcutModifiers.Control, out cmd) && cmd == BrowserCommand.SelectTab1);
    Assert(BrowserCommandRegistry.TryMatch(0x38, ShortcutModifiers.Control, out cmd) && cmd == BrowserCommand.SelectTab8);
    Assert(BrowserCommandRegistry.TryMatch(0x39, ShortcutModifiers.Control, out cmd) && cmd == BrowserCommand.SelectLastTab);
    Assert(BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyF, ShortcutModifiers.Control, out cmd) && cmd == BrowserCommand.FindInPage);
    Assert(BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyF3, ShortcutModifiers.None, out cmd) && cmd == BrowserCommand.FindNext);
    Assert(BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyF3, ShortcutModifiers.Shift, out cmd) && cmd == BrowserCommand.FindPrevious);
    Assert(BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyPlus, ShortcutModifiers.Control, out cmd) && cmd == BrowserCommand.ZoomIn);
    Assert(BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyMinus, ShortcutModifiers.Control, out cmd) && cmd == BrowserCommand.ZoomOut);
    Assert(BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.Key0, ShortcutModifiers.Control, out cmd) && cmd == BrowserCommand.ZoomReset);
    Assert(BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyF11, ShortcutModifiers.None, out cmd) && cmd == BrowserCommand.ToggleFullScreen);
    Assert(BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyP, ShortcutModifiers.Control, out cmd) && cmd == BrowserCommand.Print);
    Assert(BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyF5, ShortcutModifiers.Control, out cmd) && cmd == BrowserCommand.HardReload);
    Assert(BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyR, ShortcutModifiers.Control | ShortcutModifiers.Shift, out cmd) && cmd == BrowserCommand.HardReload);
    Assert(BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyF12, ShortcutModifiers.None, out cmd) && cmd == BrowserCommand.OpenDevTools);
    Assert(BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyI, ShortcutModifiers.Control | ShortcutModifiers.Shift, out cmd) && cmd == BrowserCommand.OpenDevTools);
    Assert(BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyEscape, ShortcutModifiers.None, out cmd) && cmd == BrowserCommand.StopLoading);
});

Check("BrowserCommandRegistry rejects editing shortcuts and web-native keys", () =>
{
    // Editing shortcuts are identified
    Assert(BrowserCommandRegistry.IsTextEditingShortcut(BrowserCommandRegistry.KeyC, ShortcutModifiers.Control));
    Assert(BrowserCommandRegistry.IsTextEditingShortcut(BrowserCommandRegistry.KeyV, ShortcutModifiers.Control));
    Assert(BrowserCommandRegistry.IsTextEditingShortcut(BrowserCommandRegistry.KeyA, ShortcutModifiers.Control));
    Assert(BrowserCommandRegistry.IsTextEditingShortcut(BrowserCommandRegistry.KeyZ, ShortcutModifiers.Control));
    Assert(BrowserCommandRegistry.IsTextEditingShortcut(BrowserCommandRegistry.KeyX, ShortcutModifiers.Control));
    Assert(BrowserCommandRegistry.IsTextEditingShortcut(BrowserCommandRegistry.KeyZ, ShortcutModifiers.Control | ShortcutModifiers.Shift));
    Assert(!BrowserCommandRegistry.IsTextEditingShortcut(BrowserCommandRegistry.KeyF, ShortcutModifiers.Control));

    // Editing shortcuts do not match any browser command
    Assert(!BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyC, ShortcutModifiers.Control, out _));
    Assert(!BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyV, ShortcutModifiers.Control, out _));
    Assert(!BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyA, ShortcutModifiers.Control, out _));
    Assert(!BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyZ, ShortcutModifiers.Control, out _));
    Assert(!BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyX, ShortcutModifiers.Control, out _));

    // Batch D: Ctrl+S matches SavePage
    Assert(BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyS, ShortcutModifiers.Control, out var cmdSave) && cmdSave == BrowserCommand.SavePage);
});

Check("BrowserCommandRegistry rejects typing and modifier-less keys", () =>
{
    Assert(!BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyT, ShortcutModifiers.None, out _));
    Assert(!BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyW, ShortcutModifiers.None, out _));
    Assert(!BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.Key1, ShortcutModifiers.None, out _));
    Assert(!BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyLeft, ShortcutModifiers.None, out _));
    Assert(!BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyRight, ShortcutModifiers.None, out _));
    Assert(!BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyT, ShortcutModifiers.Shift, out _));
    Assert(!BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyT, ShortcutModifiers.Alt, out _));
    Assert(!BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyW, ShortcutModifiers.Alt, out _));
});

Check("BrowserCommandRegistry produces correct display labels", () =>
{
    Equal("Ctrl+T", BrowserCommandRegistry.GetShortcutLabel(BrowserCommand.NewTab));
    Equal("Ctrl+W", BrowserCommandRegistry.GetShortcutLabel(BrowserCommand.CloseTab));
    Equal("Ctrl+Shift+T", BrowserCommandRegistry.GetShortcutLabel(BrowserCommand.RestoreClosedTab));
    Equal("Ctrl+,", BrowserCommandRegistry.GetShortcutLabel(BrowserCommand.Settings));
    Equal("Ctrl+K", BrowserCommandRegistry.GetShortcutLabel(BrowserCommand.CommandPalette));
    Equal("Alt+Left", BrowserCommandRegistry.GetShortcutLabel(BrowserCommand.GoBack));
});

Check("DownloadSafety sanitizes filenames against path traversal, reserved names, and invalid characters", () =>
{
    // Path traversal stripped
    Equal("passwd.txt", DownloadSafety.SanitizeFileName("../../etc/passwd.txt"));
    Equal("evil.exe", DownloadSafety.SanitizeFileName(@"..\..\evil.exe"));
    Equal("test.bin", DownloadSafety.SanitizeFileName("c:/foo/bar/test.bin"));

    // Reserved Windows device names neutralized
    Equal("_CON.txt", DownloadSafety.SanitizeFileName("CON.txt"));
    Equal("_aux.dat", DownloadSafety.SanitizeFileName("aux.dat"));
    Equal("_nul.tar.gz", DownloadSafety.SanitizeFileName("nul.tar.gz"));
    Equal("_COM1.exe", DownloadSafety.SanitizeFileName("COM1.exe"));
    Equal("_LPT3.log", DownloadSafety.SanitizeFileName("LPT3.log"));
    Equal("_prn", DownloadSafety.SanitizeFileName("prn"));

    // Alternate data stream colon stripped
    Equal("file.txt_stream", DownloadSafety.SanitizeFileName("file.txt:stream"));

    // Trailing dots and spaces stripped
    Equal("payload.exe", DownloadSafety.SanitizeFileName("payload.exe. . ."));

    // Invalid chars replaced
    Equal("_test__pipe_.dat", DownloadSafety.SanitizeFileName("<test>|pipe|.dat"));

    // Empty, whitespace, or bare dots fallback to default
    Equal("download", DownloadSafety.SanitizeFileName(""));
    Equal("download", DownloadSafety.SanitizeFileName("   "));
    Equal("download", DownloadSafety.SanitizeFileName(".."));
    Equal("download", DownloadSafety.SanitizeFileName("."));

    // Length capping
    var longName = new string('a', 250) + ".pdf";
    var sanitizedLong = DownloadSafety.SanitizeFileName(longName);
    Assert(sanitizedLong.Length <= 200, "Length was not clamped");
    Assert(sanitizedLong.EndsWith(".pdf"), "Extension was lost");
});

Check("DownloadSafety resolves collision-free paths within designated directory", () =>
{
    var tempDir = Path.Combine(Path.GetTempPath(), "slate-download-test-" + Guid.NewGuid());
    try
    {
        Directory.CreateDirectory(tempDir);
        var path1 = DownloadSafety.GetSafeDestinationPath(tempDir, "file.txt");
        Equal(Path.Combine(tempDir, "file.txt"), path1);
        File.WriteAllText(path1, "content1");

        var path2 = DownloadSafety.GetSafeDestinationPath(tempDir, "file.txt");
        Equal(Path.Combine(tempDir, "file (1).txt"), path2);
        File.WriteAllText(path2, "content2");

        var path3 = DownloadSafety.GetSafeDestinationPath(tempDir, "file.txt");
        Equal(Path.Combine(tempDir, "file (2).txt"), path3);

        // Path traversal in suggested path does not escape tempDir
        var pathEscape = DownloadSafety.GetSafeDestinationPath(tempDir, "../../secret.bat");
        Assert(pathEscape.StartsWith(tempDir, StringComparison.OrdinalIgnoreCase), "Escaped download directory");
        Equal(Path.Combine(tempDir, "secret.bat"), pathEscape);
    }
    finally
    {
        if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
    }
});

Check("DownloadSafety validates safe local directory paths and rejects UNC shares", () =>
{
    var localRoot = Path.GetPathRoot(Environment.SystemDirectory)!;
    Assert(DownloadSafety.IsSafeLocalDirectory(Path.Combine(localRoot, "Users", "Slate", "Downloads")));
    Assert(!DownloadSafety.IsSafeLocalDirectory(@"\\evil.com\share"));
    Assert(!DownloadSafety.IsSafeLocalDirectory(@"//evil.com/share"));
    Assert(!DownloadSafety.IsSafeLocalDirectory(@"Downloads\sub"));
    Assert(!DownloadSafety.IsSafeLocalDirectory(""));
    Assert(!DownloadSafety.IsSafeLocalDirectory(null));
});

Check("Navigation rejects URLs with UserInfo to prevent URL spoofing and phishing", () =>
{
    Assert(Navigation.Resolve("https://google.com@evil.com/").StartsWith("https://duckduckgo.com/?q="));
    Assert(Navigation.Resolve("https://user:pass@bank.com/").StartsWith("https://duckduckgo.com/?q="));
    Assert(!Navigation.IsWebUrl("https://google.com@evil.com/"));
    Assert(!Navigation.IsWebUrl("https://user:pass@bank.com/"));
    Assert(Navigation.IsWebUrl("https://example.com/normal"));
});

Check("Navigation rejects URLs with backslashes in authority", () =>
{
    Assert(Navigation.Resolve(@"https://attacker.com\path").StartsWith("https://duckduckgo.com/?q="));
    Assert(!Navigation.IsWebUrl(@"https://attacker.com\path"));
});

Check("Navigation.IsAllowedFrameUrl rejects arbitrary about: schemes while allowing blank and srcdoc", () =>
{
    Assert(Navigation.IsAllowedFrameUrl("about:blank"));
    Assert(Navigation.IsAllowedFrameUrl("about:srcdoc"));
    Assert(!Navigation.IsAllowedFrameUrl("about:flags"));
    Assert(!Navigation.IsAllowedFrameUrl("about:config"));
    Assert(!Navigation.IsAllowedFrameUrl("about:version"));
});

Check("Navigation.IsSecureOrigin identifies secure HTTPS and loopback origins", () =>
{
    Assert(Navigation.IsSecureOrigin("https://example.com"));
    Assert(Navigation.IsSecureOrigin("https://example.com:8443"));
    Assert(Navigation.IsSecureOrigin("http://localhost:3000"));
    Assert(Navigation.IsSecureOrigin("http://127.0.0.1:8080"));
    Assert(Navigation.IsSecureOrigin("http://[::1]:80"));
    Assert(!Navigation.IsSecureOrigin("http://insecure-site.example.com"));
    Assert(!Navigation.IsSecureOrigin("http://example.com:8080"));
    Assert(!Navigation.IsSecureOrigin("javascript:alert(1)"));
    Assert(!Navigation.IsSecureOrigin(null));
});

Check("BrowserSession.Normalize sanitizes persisted download filenames and removes UNC paths", () =>
{
    var session = new BrowserSession(new()
    {
        Downloads =
        [
            new DownloadEntry { FileName = "../../dangerous.exe", Path = @"\\attacker\share\dangerous.exe" },
            new DownloadEntry { FileName = "CON.txt", Path = @"C:\Downloads\CON.txt" }
        ]
    });
    Equal("dangerous.exe", session.State.Downloads[0].FileName);
    Equal("", session.State.Downloads[0].Path); // UNC path cleared
    Equal("_CON.txt", session.State.Downloads[1].FileName);
    Equal("", session.State.Downloads[1].Path); // Reserved device target cleared
});

Check("BrowserSession.MoveTab moves tab up and down relative to siblings", () =>
{
    var session = new BrowserSession();
    var t1 = session.ActiveTab;
    var t2 = session.AddTab("https://site2.example/");
    var t3 = session.AddTab("https://site3.example/");
    
    // Currently order is [t1, t2, t3]
    var visible = session.VisibleTabs.ToList();
    Equal(t1.Id, visible[0].Id);
    Equal(t2.Id, visible[1].Id);
    Equal(t3.Id, visible[2].Id);

    // Move t3 up (-1) -> should be [t1, t3, t2]
    Assert(session.MoveTab(t3.Id, -1));
    visible = session.VisibleTabs.ToList();
    Equal(t1.Id, visible[0].Id);
    Equal(t3.Id, visible[1].Id);
    Equal(t2.Id, visible[2].Id);

    // Move t1 down (+1) -> should be [t3, t1, t2]
    Assert(session.MoveTab(t1.Id, 1));
    visible = session.VisibleTabs.ToList();
    Equal(t3.Id, visible[0].Id);
    Equal(t1.Id, visible[1].Id);
    Equal(t2.Id, visible[2].Id);

    // Cannot move t3 up past boundary
    Assert(!session.MoveTab(t3.Id, -1));
});

Check("BrowserSession.ReorderTab places source before target tab", () =>
{
    var session = new BrowserSession();
    var t1 = session.ActiveTab;
    var t2 = session.AddTab("https://site2.example/");
    var t3 = session.AddTab("https://site3.example/");

    // Reorder t3 to t1's position -> [t3, t1, t2]
    Assert(session.ReorderTab(t3.Id, t1.Id));
    var visible = session.VisibleTabs.ToList();
    Equal(t3.Id, visible[0].Id);
    Equal(t1.Id, visible[1].Id);
    Equal(t2.Id, visible[2].Id);
});

Check("BrowserSession.CloseOtherTabs closes unpinned siblings and preserves target", () =>
{
    var session = new BrowserSession();
    var t1 = session.ActiveTab;
    var t2 = session.AddTab("https://site2.example/");
    var t3 = session.AddTab("https://site3.example/");
    session.Pin(t1.Id);

    // Close other tabs from perspective of t2 -> t3 is closed, t2 is kept, t1 is pinned so kept
    var closed = session.CloseOtherTabs(t2.Id);
    Equal(1, closed.Count);
    Equal(t3.Id, closed[0].Id);
    Assert(session.State.Tabs.Any(t => t.Id == t1.Id));
    Assert(session.State.Tabs.Any(t => t.Id == t2.Id));
    Assert(!session.State.Tabs.Any(t => t.Id == t3.Id));
    Equal(t2.Id, session.ActiveTab.Id);
});

Check("BrowserSession.CloseTabsBelow closes unpinned tabs positioned after target", () =>
{
    var session = new BrowserSession();
    var t1 = session.ActiveTab;
    var t2 = session.AddTab("https://site2.example/");
    var t3 = session.AddTab("https://site3.example/");

    // Close tabs below t1 -> closes t2 and t3
    var closed = session.CloseTabsBelow(t1.Id);
    Equal(2, closed.Count);
    Assert(session.State.Tabs.Any(t => t.Id == t1.Id));
    Assert(!session.State.Tabs.Any(t => t.Id == t2.Id));
    Assert(!session.State.Tabs.Any(t => t.Id == t3.Id));
});

Check("OmniboxService ranks search, open tabs, and history without duplicates", () =>
{
    var session = new BrowserSession();
    var tab = session.AddTab("https://developer.mozilla.org/en-US/");
    tab.Title = "MDN Web Docs";
    session.State.History.Add(new HistoryEntry { Url = "https://developer.mozilla.org/en-US/docs/Web/API", Title = "Web APIs | MDN" });
    session.State.History.Add(new HistoryEntry { Url = "https://example.com/", Title = "Example Domain" });

    var suggestions = OmniboxService.GetSuggestions("mozilla", session.State.Tabs, session.State.History, "DuckDuckGo");
    Assert(suggestions.Count >= 2);
    Equal(OmniboxSuggestionKind.Search, suggestions[0].Kind);
    Assert(suggestions[0].Title.Contains("DuckDuckGo"));
    Equal(OmniboxSuggestionKind.OpenTab, suggestions[1].Kind);
    Equal(tab.Id, suggestions[1].TabId);
    Assert(suggestions[1].Subtitle.Contains("developer.mozilla.org"));

    // Blank or whitespace input returns empty suggestions
    Equal(0, OmniboxService.GetSuggestions("  ", session.State.Tabs, session.State.History, "DuckDuckGo").Count);
});

Check("OmniboxService strictly ignores open InPrivate tabs", () =>
{
    var session = new BrowserSession();
    var normalTab = session.AddTab("https://public.example.com/");
    normalTab.Title = "Public Page";
    var privateTab = session.AddTab("https://secret.example.com/", isPrivate: true);
    privateTab.Title = "Secret InPrivate Page";

    var suggestions = OmniboxService.GetSuggestions("secret", session.State.Tabs, session.State.History, "DuckDuckGo");
    Assert(!suggestions.Any(s => s.Kind == OmniboxSuggestionKind.OpenTab && s.TabId == privateTab.Id));
    Assert(!suggestions.Any(s => s.Subtitle.Contains("secret.example.com")));
});

Check("BrowserCommandRegistry matches Batch B navigation shortcuts", () =>
{
    Assert(BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyPageUp, ShortcutModifiers.Control | ShortcutModifiers.Shift, out var cmdUp));
    Equal(BrowserCommand.MoveTabUp, cmdUp);

    Assert(BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyPageDown, ShortcutModifiers.Control | ShortcutModifiers.Shift, out var cmdDown));
    Equal(BrowserCommand.MoveTabDown, cmdDown);

    Assert(BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyD, ShortcutModifiers.Control | ShortcutModifiers.Shift, out var cmdDup));
    Equal(BrowserCommand.DuplicateTab, cmdDup);
});

Check("BrowserSession.Normalize normalizes bookmarks and validates download path", () =>
{
    var session = new BrowserSession(new()
    {
        Bookmarks =
        [
            new BookmarkEntry { Url = "javascript:alert(1)", Title = "Bad" },
            new BookmarkEntry { Url = "https://example.com/page", Title = "" },
            new BookmarkEntry { Url = "https://example.com/page", Title = "Dupe" }
        ],
        Settings = new() { DownloadPath = @"\\attacker\share\downloads" }
    });

    // Invalid scheme filtered out, missing title fallback to host
    Assert(session.State.Bookmarks.All(b => b.Url.StartsWith("https://")));
    Assert(session.State.Bookmarks.Any(b => b.Title == "example.com"));
    // UNC download path cleared
    Equal(null, session.State.Settings.DownloadPath);
});

Check("BrowserSession bookmarks management operations", () =>
{
    var session = new BrowserSession();
    Assert(!session.IsBookmarked("https://slate.local/page"));

    var b1 = session.AddBookmark("https://slate.local/page", "Slate Page");
    Assert(b1 is not null);
    Assert(session.IsBookmarked("https://slate.local/page"));
    Equal("Slate Page", b1!.Title);

    // Re-adding existing updates title
    var b1Updated = session.AddBookmark("https://slate.local/page", "Updated Title");
    Equal("Updated Title", b1Updated!.Title);
    Equal(1, session.State.Bookmarks.Count);

    // Toggle removes existing bookmark
    bool toggledOff = session.ToggleBookmark("https://slate.local/page", "Updated Title");
    Assert(!toggledOff);
    Assert(!session.IsBookmarked("https://slate.local/page"));

    // Toggle adds if missing
    bool toggledOn = session.ToggleBookmark("https://slate.local/page", "Re-added");
    Assert(toggledOn);
    Assert(session.IsBookmarked("https://slate.local/page"));

    // Remove by Guid
    var b2 = session.AddBookmark("https://slate.local/page2", "Page 2");
    Assert(session.RemoveBookmark(b2!.Id));
    Assert(!session.IsBookmarked("https://slate.local/page2"));

    // Invalid URL cannot be bookmarked
    Assert(session.AddBookmark("about:blank", "Blank") is null);
});

Check("BrowserSession history deletion and granular clearing", () =>
{
    var session = new BrowserSession();
    var now = DateTimeOffset.UtcNow;
    session.State.History.Add(new HistoryEntry { Url = "https://site1.example/", Title = "Site 1", VisitedAt = now });
    session.State.History.Add(new HistoryEntry { Url = "https://site2.example/", Title = "Site 2", VisitedAt = now.AddHours(-2) });
    session.State.History.Add(new HistoryEntry { Url = "https://site3.example/sub", Title = "Site 3", VisitedAt = now.AddDays(-2) });

    // Remove single entry
    Assert(session.RemoveHistoryEntry("https://site1.example/"));
    Assert(!session.State.History.Any(h => h.Url == "https://site1.example/"));

    // Filtered clear by query
    session.ClearHistory(query: "sub");
    Assert(!session.State.History.Any(h => h.Url.Contains("sub")));

    // Clear since time
    session.ClearHistory(since: now.AddHours(-3));
    Equal(0, session.State.History.Count);
});

Check("OmniboxService matches bookmarks and ranks properly", () =>
{
    var session = new BrowserSession();
    session.AddBookmark("https://docs.microsoft.com/en-us/dotnet/", ".NET Documentation");
    session.State.History.Add(new HistoryEntry { Url = "https://dotnet.microsoft.com/", Title = ".NET Home" });

    var suggestions = OmniboxService.GetSuggestions(
        "dotnet",
        session.State.Tabs,
        session.State.History,
        "DuckDuckGo",
        session.State.Bookmarks);

    Assert(suggestions.Any(s => s.Kind == OmniboxSuggestionKind.Bookmark && s.Title.Contains(".NET Documentation")));
    Equal(OmniboxSuggestionKind.Search, suggestions[0].Kind);
    var bookmarkIndex = suggestions.FindIndex(s => s.Kind == OmniboxSuggestionKind.Bookmark);
    var historyIndex = suggestions.FindIndex(s => s.Kind == OmniboxSuggestionKind.History);
    Assert(bookmarkIndex > 0 && bookmarkIndex < historyIndex);
});

Check("BrowserCommandRegistry matches Batch C shortcuts", () =>
{
    Assert(BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyD, ShortcutModifiers.Control, out var cmdBookmark));
    Equal(BrowserCommand.BookmarkPage, cmdBookmark);

    Assert(BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyO, ShortcutModifiers.Control | ShortcutModifiers.Shift, out var cmdBookmarks));
    Equal(BrowserCommand.OpenBookmarks, cmdBookmarks);
});

Check("Private tabs are excluded from session serialization and recently closed", () =>
{
    var session = new BrowserSession();
    var privTab = session.AddTab("https://example.com/private", isPrivate: true);
    privTab.IsPinned = true; // Even if pinned, private tabs must not persist
    Assert(privTab.IsPrivate);

    // Normalize excludes private tabs
    var state = session.State;
    var reloadedSession = new BrowserSession(state);
    Assert(!reloadedSession.State.Tabs.Any(t => t.Id == privTab.Id));

    // Closing private tab never adds to RecentlyClosed
    var privTab2 = session.AddTab("https://example.com/secret", isPrivate: true);
    session.CloseTab(privTab2.Id);
    Assert(!session.State.RecentlyClosed.Any(t => t.Id == privTab2.Id));
});

Check("Private tab visits are never recorded to history", () =>
{
    var session = new BrowserSession();
    var privTab = session.AddTab("https://example.com/stealth", isPrivate: true);
    session.RecordVisit(privTab);
    Assert(!session.State.History.Any(h => h.Url.Contains("stealth")));
});

Check("Navigation rejects dangerous local file extensions and UNC paths", () =>
{
    Assert(!Navigation.IsLocalFileUrl("file://server/share/page.html")); // UNC share
    Assert(!Navigation.IsLocalFileUrl("file:///C:/malware.exe")); // Executable
    Assert(!Navigation.IsLocalFileUrl("file:///C:/script.ps1")); // Script
    Assert(!Navigation.IsLocalFileUrl("file:///C:/batch.bat")); // Batch
    Assert(!Navigation.IsLocalFileUrl("file:///C:/virus.scr")); // Screen saver executable
    Assert(!Navigation.IsLocalFileUrl("file:///C:/launcher.js")); // Windows Script Host launcher
    Assert(!Navigation.IsLocalFileUrl("file:///C:/shortcut.lnk")); // Shell shortcut
    Assert(!Navigation.IsLocalFileUrl("file:///%5C%5Cserver%5Cshare%5Cpage.html")); // Encoded UNC share
    Assert(!Navigation.IsLocalFileUrl("https://example.com/")); // Not a file
    Assert(Navigation.IsLocalFileUrl("file:///C:/Users/test/document.html")); // Safe HTML
    Assert(Navigation.IsLocalFileUrl("file:///C:/test.txt")); // Safe Text
});

Check("Navigation resolves view-source and safe local file paths", () =>
{
    Equal("view-source:https://example.com/", Navigation.Resolve("view-source:https://example.com/"));
    Equal("view-source:https://example.com/", Navigation.Resolve("view-source:example.com"));
    Assert(Navigation.IsViewSourceUrl("view-source:https://example.com/"));
    Assert(!Navigation.IsViewSourceUrl("https://example.com/"));
});

Check("BrowserCommandRegistry matches Batch D shortcuts", () =>
{
    Assert(BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyN, ShortcutModifiers.Control | ShortcutModifiers.Alt, out var cmdPriv));
    Equal(BrowserCommand.NewPrivateTab, cmdPriv);

    Assert(BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyO, ShortcutModifiers.Control, out var cmdOpen));
    Equal(BrowserCommand.OpenFile, cmdOpen);

    Assert(BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyS, ShortcutModifiers.Control, out var cmdSave));
    Equal(BrowserCommand.SavePage, cmdSave);

    Assert(BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyU, ShortcutModifiers.Control, out var cmdSource));
    Equal(BrowserCommand.ViewSource, cmdSource);
});

Check("LogSanitizer redacts credentials, authorization tokens, headers, JSON keys, and enforces size limit", () =>
{
    // URL userinfo credentials
    var sanitizedUrl = LogSanitizer.Sanitize("Error navigating to https://admin:SuperSecret123@example.com/api");
    Equal("Error navigating to https://admin:[REDACTED]@example.com/api", sanitizedUrl);

    // Headers
    var headerBlock = "GET / HTTP/1.1\r\nAuthorization: Bearer eyJhbGciOiJIUzI1NiJ9\r\nCookie: session_id=abc123xyz\r\nHost: api.example.com";
    var sanitizedHeaders = LogSanitizer.Sanitize(headerBlock);
    Assert(sanitizedHeaders.Contains("Authorization: [REDACTED]"), "Header authorization not redacted: " + sanitizedHeaders);
    Assert(sanitizedHeaders.Contains("Cookie: [REDACTED]"), "Header cookie not redacted: " + sanitizedHeaders);
    Assert(sanitizedHeaders.Contains("Host: api.example.com"), "Header host lost: " + sanitizedHeaders);

    // Bearer and Basic inline
    var inlineTokens = "Client tokens: Bearer abc-123_456.xyz== and Basic dXNlcjpwYXNzd29yZA==";
    var sanitizedInline = LogSanitizer.Sanitize(inlineTokens);
    Assert(sanitizedInline.Contains("Bearer [REDACTED]"), "Bearer inline not redacted: " + sanitizedInline);
    Assert(sanitizedInline.Contains("Basic [REDACTED]"), "Basic inline not redacted: " + sanitizedInline);
    Assert(!sanitizedInline.Contains("abc-123"), "Bearer token leaked: " + sanitizedInline);
    Assert(!sanitizedInline.Contains("dXNlcjpwYXNzd29yZA=="), "Basic token leaked: " + sanitizedInline);

    // JSON fields
    var jsonPayload = "{\"username\": \"john\", \"password\": \"P@ssword!\", \"access_token\": \"secret_tok\", \"client_secret\": \"cs_999\"}";
    var sanitizedJson = LogSanitizer.Sanitize(jsonPayload);
    Assert(sanitizedJson.Contains("\"password\": \"[REDACTED]\""), "JSON password not redacted: " + sanitizedJson);
    Assert(sanitizedJson.Contains("\"access_token\": \"[REDACTED]\""), "JSON access_token not redacted: " + sanitizedJson);
    Assert(sanitizedJson.Contains("\"client_secret\": \"[REDACTED]\""), "JSON client_secret not redacted: " + sanitizedJson);
    Assert(sanitizedJson.Contains("\"username\": \"john\""), "JSON username lost: " + sanitizedJson);

    // Key-value pairs
    var kvPairs = "https://example.com/?api_key=secretKey123&refresh_token=refresh456&page=2";
    var sanitizedKv = LogSanitizer.Sanitize(kvPairs);
    Assert(sanitizedKv.Contains("api_key=[REDACTED]"), "KV api_key not redacted: " + sanitizedKv);
    Assert(sanitizedKv.Contains("refresh_token=[REDACTED]"), "KV refresh_token not redacted: " + sanitizedKv);
    Assert(sanitizedKv.Contains("page=2"), "KV page=2 lost: " + sanitizedKv);

    // Private key block
    var pemKey = "Certificate check failed:\n-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEA0...\n-----END RSA PRIVATE KEY-----\nEnd of cert";
    var sanitizedPem = LogSanitizer.Sanitize(pemKey);
    Assert(sanitizedPem.Contains("[REDACTED PRIVATE KEY]"), "PEM key not redacted: " + sanitizedPem);
    Assert(!sanitizedPem.Contains("MIIEowIBAAKCAQEA0"), "PEM key content leaked: " + sanitizedPem);

    // Enormous message truncation
    var massiveLog = new string('E', 40_000);
    var sanitizedMassive = LogSanitizer.Sanitize(massiveLog);
    Assert(sanitizedMassive.Length <= LogSanitizer.MaximumLogCharacters + "\n[TRUNCATED]".Length, "Massive log length not clamped");
    Assert(sanitizedMassive.EndsWith("\n[TRUNCATED]"), "Massive log truncation suffix missing");
});

Check("DownloadSafety atomically reserves destinations and avoids pending collisions", () =>
{
    var tempDir = Path.Combine(Path.GetTempPath(), "slate-atomic-download-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDir);
    try
    {
        var first = DownloadSafety.GetSafeDestinationPath(tempDir, "concurrent-download.txt");
        var second = DownloadSafety.GetSafeDestinationPath(tempDir, "concurrent-download.txt");
        var third = DownloadSafety.ReserveSafeDestinationPath(tempDir, "concurrent-download.txt");

        Assert(File.Exists(first), "First destination file was not reserved on disk");
        Assert(File.Exists(second), "Second destination file was not reserved on disk");
        Assert(File.Exists(third), "Third destination file was not reserved on disk");

        Assert(first != second, "Pending downloads collided on the same filename");
        Assert(second != third, "Second and third downloads collided");

        Equal(Path.Combine(tempDir, "concurrent-download.txt"), first);
        Equal(Path.Combine(tempDir, "concurrent-download (1).txt"), second);
        Equal(Path.Combine(tempDir, "concurrent-download (2).txt"), third);
    }
    finally
    {
        if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
    }
});

Check("Page-controlled titles and labels are strictly bounded to 512 characters and sanitized", () =>
{
    var hostileTitle = new string('Z', 10_000);
    var tab = new BrowserTab { Title = hostileTitle };
    Equal(512, tab.Title.Length);
    Assert(tab.Title.All(c => c == 'Z'));

    var history = new HistoryEntry { Url = "https://example.com/test", Title = hostileTitle };
    Equal(512, history.Title.Length);

    var bookmark = new BookmarkEntry { Url = "https://example.com/test", Title = hostileTitle, Folder = hostileTitle };
    Equal(512, bookmark.Title.Length);
    Equal(128, bookmark.Folder?.Length);

    var workspace = new Workspace { Name = hostileTitle };
    Equal(40, workspace.Name.Length);

    // Whitespace and control character normalization
    tab.Title = "  Hello \r\n\t World \x00\x1f Tab  ";
    Equal("Hello World Tab", tab.Title);

    // Empty or whitespace fallback
    tab.Title = "   \t  ";
    Equal("New tab", tab.Title);
});

Check("Shortcut routing preserves text-editing shortcuts and matches browser commands", () =>
{
    // Text-editing shortcuts must NOT be treated as browser commands
    Assert(BrowserCommandRegistry.IsTextEditingShortcut(BrowserCommandRegistry.KeyC, ShortcutModifiers.Control));
    Assert(BrowserCommandRegistry.IsTextEditingShortcut(BrowserCommandRegistry.KeyV, ShortcutModifiers.Control));
    Assert(BrowserCommandRegistry.IsTextEditingShortcut(BrowserCommandRegistry.KeyX, ShortcutModifiers.Control));
    Assert(BrowserCommandRegistry.IsTextEditingShortcut(BrowserCommandRegistry.KeyA, ShortcutModifiers.Control));
    Assert(BrowserCommandRegistry.IsTextEditingShortcut(BrowserCommandRegistry.KeyZ, ShortcutModifiers.Control));
    Assert(BrowserCommandRegistry.IsTextEditingShortcut(BrowserCommandRegistry.KeyY, ShortcutModifiers.Control));
    Assert(BrowserCommandRegistry.IsTextEditingShortcut(BrowserCommandRegistry.KeyZ, ShortcutModifiers.Control | ShortcutModifiers.Shift));

    // Browser commands must NOT be flagged as text-editing shortcuts
    Assert(!BrowserCommandRegistry.IsTextEditingShortcut(BrowserCommandRegistry.KeyT, ShortcutModifiers.Control));
    Assert(!BrowserCommandRegistry.IsTextEditingShortcut(BrowserCommandRegistry.KeyW, ShortcutModifiers.Control));
    Assert(!BrowserCommandRegistry.IsTextEditingShortcut(BrowserCommandRegistry.KeyL, ShortcutModifiers.Control));
    Assert(!BrowserCommandRegistry.IsTextEditingShortcut(BrowserCommandRegistry.KeyK, ShortcutModifiers.Control));
    Assert(!BrowserCommandRegistry.IsTextEditingShortcut(BrowserCommandRegistry.KeyJ, ShortcutModifiers.Control));
    Assert(!BrowserCommandRegistry.IsTextEditingShortcut(BrowserCommandRegistry.KeyH, ShortcutModifiers.Control));
    Assert(!BrowserCommandRegistry.IsTextEditingShortcut(BrowserCommandRegistry.KeyR, ShortcutModifiers.Control));

    // Browser commands match expected commands
    Assert(BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyT, ShortcutModifiers.Control, out var cmdT) && cmdT == BrowserCommand.NewTab);
    Assert(BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyW, ShortcutModifiers.Control, out var cmdW) && cmdW == BrowserCommand.CloseTab);
    Assert(BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyL, ShortcutModifiers.Control, out var cmdL) && cmdL == BrowserCommand.FocusAddress);
    Assert(BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyK, ShortcutModifiers.Control, out var cmdK) && cmdK == BrowserCommand.CommandPalette);
    Assert(BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyJ, ShortcutModifiers.Control, out var cmdJ) && cmdJ == BrowserCommand.Downloads);
    Assert(BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyH, ShortcutModifiers.Control, out var cmdH) && cmdH == BrowserCommand.History);
    Assert(BrowserCommandRegistry.TryMatch(BrowserCommandRegistry.KeyR, ShortcutModifiers.Control, out var cmdR) && cmdR == BrowserCommand.Reload);
});

Check("Password submission capture policy enforces privacy and canonical origins", () =>
{
    // Private and temporary contexts are strictly ineligible
    Assert(!PasswordCapturePolicy.TryNormalizeSubmission("https://example.com/login", "alice", "Secret123!", true, false, out _));
    Assert(!PasswordCapturePolicy.TryNormalizeSubmission("https://example.com/login", "alice", "Secret123!", false, true, out _));

    // Invalid credentials (empty password, invalid origin) are rejected
    Assert(!PasswordCapturePolicy.TryNormalizeSubmission("not-a-url", "alice", "Secret123!", false, false, out _));
    Assert(!PasswordCapturePolicy.TryNormalizeSubmission("https://example.com", "alice", "", false, false, out _));

    // Valid submission normalizes origin
    Assert(PasswordCapturePolicy.TryNormalizeSubmission("https://example.com/login?step=2", "alice", "Secret123!", false, false, out var origin));
    Equal("https://example.com", origin);
});

Slate.Tests.BrowserDataImportArchitectureTests.Run(Check);
Slate.Tests.PasswordCsvParsingTests.Run(Check);
Slate.Tests.PasswordTests.Run(Check);
Console.WriteLine($"\n{passed} checks passed.");
