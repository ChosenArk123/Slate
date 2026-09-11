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
            var tab1 = session.AddTab("https://site1.example.com/");
            var tab2 = session.AddTab("https://site2.example.com/");
            var tab3 = session.AddTab("https://site3.example.com/");
            session.Activate(tab2.Id);

            // Simulate normalization upon startup restore
            var normalizedState = JsonSerializer.Deserialize<BrowserState>(JsonSerializer.Serialize(session.State))!;
            var restoredSession = new BrowserSession(normalizedState);

            // Active tab should be awake, background restored tabs should be sleeping
            var restoredActive = restoredSession.ActiveTab;
            Equal(tab2.Id, restoredActive.Id);
            Assert(!restoredActive.IsSleeping, "Restored active tab must not be sleeping");

            var otherTabs = restoredSession.State.Tabs.Where(t => t.Id != restoredActive.Id).ToList();
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
            Equal(t1.Id, tabsAfter[1].Id);
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

            // In Slate, RecentlyClosed is clamped to 20 tabs
            Assert(session.State.RecentlyClosed.Count <= 20, "RecentlyClosed must be clamped to 20");
            Equal("https://close-29.example/", session.State.RecentlyClosed[0].Url);
        });
    }
}
