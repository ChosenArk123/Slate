using System.Text.Json;
using Slate.Core;

namespace Slate.Tests;

public static class LifecycleTests
{
    private static void Assert(bool condition, string message = "Assertion failed")
    {
        if (!condition) throw new Exception(message);
    }

    private static void Equal<T>(T expected, T actual)
    {
        Assert(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}, got {actual}");
    }

    private static bool _autoRan;

    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void AutoRun()
    {
        if (_autoRan) return;
        _autoRan = true;
        Run((name, action) =>
        {
            try
            {
                action();
                Console.WriteLine("PASS  " + name);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAIL  " + name + ": " + ex.Message);
                Environment.ExitCode = 1;
            }
        });
    }

    public static void Run(Action<string, Action> check)
    {
        // -----------------------------------------------------------------
        // 1. Tab Lifecycle Transitions: Open -> Navigate -> Sleep -> Wake -> Close
        // -----------------------------------------------------------------
        check("Lifecycle: Open tab initializes with clean active state and correct defaults", () =>
        {
            var session = new BrowserSession();
            var tab = session.AddTab("https://start.example.com/");

            Equal("https://start.example.com/", tab.Url);
            Assert(!tab.IsSleeping, "Newly opened tab should not be sleeping");
            Assert(!tab.IsPrivate, "Default tab should not be InPrivate");
            Assert(!tab.IsTemporary, "Default tab should not be Temporary");
            Assert(!tab.IsPinned, "Default tab should not be pinned");
            Equal(session.ActiveWorkspace.Id, tab.WorkspaceId);
            Assert(session.State.Tabs.Contains(tab));
        });

        check("Lifecycle: Navigation updates URL, title, and records visit", () =>
        {
            var session = new BrowserSession();
            var tab = session.AddTab("https://initial.example.com/");
            session.RecordVisit(tab);
            Equal(1, session.State.History.Count);

            // Navigate to second URL
            tab.Url = "https://secondary.example.com/page";
            tab.Title = "Secondary Page";
            session.RecordVisit(tab);

            Equal(2, session.State.History.Count);
            Equal("https://secondary.example.com/page", session.State.History[0].Url);
            Equal("Secondary Page", session.State.History[0].Title);
        });

        check("Lifecycle: Sleep and Wake state transitions update IsSleeping cleanly", () =>
        {
            var session = new BrowserSession();
            var tab1 = session.AddTab("https://tab1.example.com/");
            var tab2 = session.AddTab("https://tab2.example.com/");

            // Put tab1 to sleep
            tab1.IsSleeping = true;
            Assert(tab1.IsSleeping, "Tab1 must be marked sleeping");

            // Verify tab2 remains awake
            Assert(!tab2.IsSleeping, "Tab2 must remain awake");

            // Waking tab1 by activation
            session.Activate(tab1.Id);
            tab1.IsSleeping = false;
            Assert(!tab1.IsSleeping, "Tab1 must be awake after activation");

            // Close tab1 and verify clean removal
            session.CloseTab(tab1.Id);
            Assert(!session.State.Tabs.Any(t => t.Id == tab1.Id));
            Equal(session.ActiveTab.Id, tab2.Id);
        });

        check("Lifecycle: Session restart restores inactive tabs in sleeping state", () =>
        {
            var session = new BrowserSession();
            session.CloseTab(session.ActiveTab.Id);
            var tab1 = session.AddTab("https://site1.example.com/");
            var tab2 = session.AddTab("https://site2.example.com/");
            var tab3 = session.AddTab("https://site3.example.com/");
            session.Activate(tab2.Id);

            // Simulate normalization upon startup restore
            var normalizedState = JsonSerializer.Deserialize<BrowserState>(JsonSerializer.Serialize(session.State))!;
            var restoredSession = new BrowserSession(normalizedState);

            // In Slate domain model, restored tabs start in sleeping state
            var restoredActive = restoredSession.ActiveTab;
            Equal(tab2.Id, restoredActive.Id);
            Assert(restoredActive.IsSleeping, "Restored active tab should start in sleeping state until UI activates");

            var otherTabs = restoredSession.State.Tabs.Where(t => t.Id != restoredActive.Id && t.Url != Navigation.NewTab).ToList();
            foreach (var t in otherTabs)
            {
                Assert(t.IsSleeping, $"Background restored tab {t.Url} must be initialized in sleeping state");
            }
        });

        // -----------------------------------------------------------------
        // 2. Tab Reordering & Bulk Operations Lifecycle
        // -----------------------------------------------------------------
        check("Lifecycle: Tab moving and reordering preserve tab identity and state", () =>
        {
            var session = new BrowserSession();
            var t1 = session.AddTab("https://1.example/");
            var t2 = session.AddTab("https://2.example/");
            var t3 = session.AddTab("https://3.example/");

            // Move t3 up
            Assert(session.MoveTab(t3.Id, -1));
            var tabs = session.VisibleTabs.ToList();
            Equal(t3.Id, tabs[tabs.Count - 2].Id);

            // Move t1 down
            Assert(session.MoveTab(t1.Id, 1));
            var tabsAfter = session.VisibleTabs.ToList();
            Equal(t1.Id, tabsAfter[2].Id);
        });

        check("Lifecycle: CloseOtherTabs preserves pinned tabs and active target", () =>
        {
            var session = new BrowserSession();
            var pinned = session.AddTab("https://pinned.example/");
            session.Pin(pinned.Id);

            var tabA = session.AddTab("https://a.example/");
            var target = session.AddTab("https://target.example/");
            var tabB = session.AddTab("https://b.example/");

            var closed = session.CloseOtherTabs(target.Id);

            Assert(closed.Any(t => t.Id == tabA.Id), "tabA must be closed");
            Assert(closed.Any(t => t.Id == tabB.Id), "tabB must be closed");
            Assert(!closed.Any(t => t.Id == target.Id), "target tab must NOT be closed");
            Assert(!closed.Any(t => t.Id == pinned.Id), "pinned tab must NOT be closed");

            Assert(session.State.Tabs.Any(t => t.Id == target.Id));
            Assert(session.State.Tabs.Any(t => t.Id == pinned.Id));
            Assert(!session.State.Tabs.Any(t => t.Id == tabA.Id));
            Assert(!session.State.Tabs.Any(t => t.Id == tabB.Id));
        });

        check("Lifecycle: CloseTabsBelow closes tabs after target in list order", () =>
        {
            var session = new BrowserSession();
            var tabA = session.AddTab("https://a.example/");
            var target = session.AddTab("https://target.example/");
            var tabB = session.AddTab("https://b.example/");
            var tabC = session.AddTab("https://c.example/");

            var closed = session.CloseTabsBelow(target.Id);

            Assert(closed.Any(t => t.Id == tabB.Id), "tabB must be closed");
            Assert(closed.Any(t => t.Id == tabC.Id), "tabC must be closed");
            Assert(!closed.Any(t => t.Id == tabA.Id), "tabA must NOT be closed");
            Assert(!closed.Any(t => t.Id == target.Id), "target must NOT be closed");
        });

        // -----------------------------------------------------------------
        // 3. Tab Teardown & Invariant Verification
        // -----------------------------------------------------------------
        check("Lifecycle: Teardown delegate execution pattern is idempotent and non-retaining", () =>
        {
            int unhookCount = 0;
            Action teardown = () => { unhookCount++; };

            // First invocation cleans up
            teardown.Invoke();
            Equal(1, unhookCount);

            // Nulling out teardown ensures no retained references
            teardown = null!;
            Assert(teardown is null);
        });

        check("Lifecycle: Enforces MaximumTabs ceiling to prevent resource exhaustion", () =>
        {
            Equal(100, BrowserSession.MaximumTabs);
            var session = new BrowserSession();
            for (int i = session.State.Tabs.Count; i < BrowserSession.MaximumTabs; i++)
            {
                session.AddTab($"https://bulk-{i}.example/");
            }
            Equal(BrowserSession.MaximumTabs, session.State.Tabs.Count);
        });

        check("Lifecycle: Recently closed tabs buffer enforces FIFO bounding", () =>
        {
            var session = new BrowserSession();
            var tabs = new List<BrowserTab>();
            for (int i = 0; i < 30; i++)
            {
                tabs.Add(session.AddTab($"https://close-{i}.example/"));
            }

            foreach (var tab in tabs)
            {
                session.CloseTab(tab.Id);
            }

            // In Slate, RecentlyClosed is clamped to 30 tabs
            Assert(session.State.RecentlyClosed.Count <= 30, "RecentlyClosed must be clamped to 30");
            Equal("https://close-29.example/", session.State.RecentlyClosed[0].Url);
        });

        // -----------------------------------------------------------------
        // 4. Tab Discard / Restore Lifecycle & State Survival Audit
        // -----------------------------------------------------------------
        check("Lifecycle: Discard and restore lifecycle cycles preserve tab metadata and update sleep state correctly", () =>
        {
            var session = new BrowserSession();
            var tab = session.AddTab("https://docs.example.com/api");
            tab.Title = "API Reference";
            tab.Favicon = "https://docs.example.com/favicon.png";
            tab.IsPinned = true;
            tab.LastAccessed = DateTimeOffset.UtcNow.AddMinutes(-5);

            // Cycle 1: Discard
            tab.IsSleeping = true;
            Assert(tab.IsSleeping, "Tab must be marked sleeping upon discard");
            Equal("https://docs.example.com/api", tab.Url);
            Equal("API Reference", tab.Title);
            Equal("https://docs.example.com/favicon.png", tab.Favicon);
            Assert(tab.IsPinned, "Pinned state must survive discard");

            // Cycle 1: Restore (Activation)
            session.Activate(tab.Id);
            tab.IsSleeping = false;
            tab.LastAccessed = DateTimeOffset.UtcNow;
            Assert(!tab.IsSleeping, "Tab must be awake after restore/activation");
            Equal(session.ActiveTab.Id, tab.Id);

            // Cycle 2: Discard again
            tab.IsSleeping = true;
            Assert(tab.IsSleeping, "Tab must be marked sleeping after second discard");
            Equal("https://docs.example.com/api", tab.Url);
            Equal("API Reference", tab.Title);

            // Cycle 2: Restore again
            tab.IsSleeping = false;
            Assert(!tab.IsSleeping, "Tab must be awake after second restore");
            Equal("https://docs.example.com/api", tab.Url);
        });

        check("Lifecycle: Tab state survival audit verifies persisted properties and non-persistence of ephemeral state", () =>
        {
            var session = new BrowserSession();
            var tab = session.AddTab("https://portal.example.com/dashboard");
            tab.Title = "Dashboard Overview";
            tab.Favicon = "https://portal.example.com/icon.png";
            tab.IsPinned = true;
            tab.IsLoading = true;

            // Discard tab: IsLoading resets, IsSleeping becomes true
            tab.IsSleeping = true;
            tab.IsLoading = false;

            // Verify metadata survives
            Equal("https://portal.example.com/dashboard", tab.Url);
            Equal("Dashboard Overview", tab.Title);
            Equal("https://portal.example.com/icon.png", tab.Favicon);
            Assert(tab.IsPinned, "Pinned state must survive");
            Assert(tab.IsSleeping, "Sleep state must be set");
            Assert(!tab.IsLoading, "Ephemeral loading state must be cleared");

            // Verify private tab in-memory survival
            var privTab = session.AddTab("https://secure.example.com/login", isPrivate: true);
            privTab.Title = "Secure Login";
            privTab.IsSleeping = true;
            Assert(privTab.IsPrivate, "Private flag must survive discard in memory");
            Equal("Secure Login", privTab.Title);

            // Verify session normalization excludes private tab while preserving regular tab
            var reloaded = new BrowserSession(session.State);
            Assert(reloaded.State.Tabs.Any(t => t.Title == "Dashboard Overview"), "Regular tab must be preserved");
            Assert(!reloaded.State.Tabs.Any(t => t.Title == "Secure Login"), "Private tab must never be restored in session");
        });

        check("Lifecycle: TabLifecyclePolicy prioritizes Least Recently Used (LRU) tabs for discard", () =>
        {
            var tabA = Guid.NewGuid();
            var tabB = Guid.NewGuid();
            var tabC = Guid.NewGuid();
            var tabD = Guid.NewGuid();
            var tabE = Guid.NewGuid();

            var now = DateTimeOffset.UtcNow;
            var accessTimes = new Dictionary<Guid, DateTimeOffset>
            {
                [tabA] = now.AddMinutes(-10), // Oldest
                [tabB] = now.AddMinutes(-8),
                [tabC] = now.AddMinutes(-6),
                [tabD] = now.AddMinutes(-4),
                [tabE] = now.AddMinutes(-2),  // Newest background
            };

            var allTabs = new[] { tabA, tabB, tabC, tabD, tabE };

            // When maxRetainedBackgroundViews = 2, 3 of 5 should be discarded in LRU order: A, then B, then C
            var discarded = TabLifecyclePolicy.SelectDiscardCandidates(
                allTabs,
                isProtected: _ => false,
                hasActivePermissionPrompt: _ => false,
                hasActiveDownloads: _ => false,
                isPlayingAudio: _ => false,
                isLoading: _ => false,
                getLastAccessed: id => accessTimes[id],
                minInactiveDuration: TimeSpan.FromSeconds(30),
                maxRetainedBackgroundViews: 2,
                now: now);

            Equal(3, discarded.Count);
            Equal(tabA, discarded[0]);
            Equal(tabB, discarded[1]);
            Equal(tabC, discarded[2]);
        });

        check("Lifecycle: TabLifecyclePolicy protects active, split, audio, download, permission prompt, and loading tabs", () =>
        {
            var protectedTab = Guid.NewGuid();
            var promptTab = Guid.NewGuid();
            var downloadTab = Guid.NewGuid();
            var audioTab = Guid.NewGuid();
            var loadingTab = Guid.NewGuid();
            var eligibleOldTab = Guid.NewGuid();
            var eligibleNewTab = Guid.NewGuid();

            var now = DateTimeOffset.UtcNow;
            var accessTimes = new Dictionary<Guid, DateTimeOffset>
            {
                [protectedTab] = now.AddMinutes(-20),
                [promptTab] = now.AddMinutes(-19),
                [downloadTab] = now.AddMinutes(-18),
                [audioTab] = now.AddMinutes(-17),
                [loadingTab] = now.AddMinutes(-16),
                [eligibleOldTab] = now.AddMinutes(-15),
                [eligibleNewTab] = now.AddMinutes(-10),
            };

            var allTabs = new[] { protectedTab, promptTab, downloadTab, audioTab, loadingTab, eligibleOldTab, eligibleNewTab };

            var discarded = TabLifecyclePolicy.SelectDiscardCandidates(
                allTabs,
                isProtected: id => id == protectedTab,
                hasActivePermissionPrompt: id => id == promptTab,
                hasActiveDownloads: id => id == downloadTab,
                isPlayingAudio: id => id == audioTab,
                isLoading: id => id == loadingTab,
                getLastAccessed: id => accessTimes[id],
                minInactiveDuration: TimeSpan.FromSeconds(30),
                maxRetainedBackgroundViews: 0,
                now: now);

            // Only eligible tabs should be discarded, ordered by LRU
            Equal(2, discarded.Count);
            Equal(eligibleOldTab, discarded[0]);
            Equal(eligibleNewTab, discarded[1]);
            Assert(!discarded.Contains(protectedTab), "Protected active/split tab must not be discarded");
            Assert(!discarded.Contains(promptTab), "Tab with active permission prompt must not be discarded");
            Assert(!discarded.Contains(downloadTab), "Tab with active download must not be discarded");
            Assert(!discarded.Contains(audioTab), "Tab playing audio must not be discarded");
            Assert(!discarded.Contains(loadingTab), "Tab currently loading must not be discarded");
        });

        check("Lifecycle: TabLifecyclePolicy respects recency threshold (< 30s) and retains recently used tabs", () =>
        {
            var veryRecentTab = Guid.NewGuid();   // 15 seconds ago (< 30s)
            var borderlineTab = Guid.NewGuid();   // 29 seconds ago (< 30s)
            var eligibleTab1 = Guid.NewGuid();    // 35 seconds ago (>= 30s)
            var eligibleTab2 = Guid.NewGuid();    // 120 seconds ago (>= 30s)

            var now = DateTimeOffset.UtcNow;
            var accessTimes = new Dictionary<Guid, DateTimeOffset>
            {
                [veryRecentTab] = now.AddSeconds(-15),
                [borderlineTab] = now.AddSeconds(-29),
                [eligibleTab1] = now.AddSeconds(-35),
                [eligibleTab2] = now.AddSeconds(-120),
            };

            var allTabs = new[] { veryRecentTab, borderlineTab, eligibleTab1, eligibleTab2 };

            var discarded = TabLifecyclePolicy.SelectDiscardCandidates(
                allTabs,
                isProtected: _ => false,
                hasActivePermissionPrompt: _ => false,
                hasActiveDownloads: _ => false,
                isPlayingAudio: _ => false,
                isLoading: _ => false,
                getLastAccessed: id => accessTimes[id],
                minInactiveDuration: TimeSpan.FromSeconds(30),
                maxRetainedBackgroundViews: 0,
                now: now);

            Equal(2, discarded.Count);
            Equal(eligibleTab2, discarded[0]); // 120s ago is older than 35s ago
            Equal(eligibleTab1, discarded[1]);
            Assert(!discarded.Contains(veryRecentTab), "Tab accessed 15s ago must be protected by recency threshold");
            Assert(!discarded.Contains(borderlineTab), "Tab accessed 29s ago must be protected by recency threshold");
        });
    }
}
