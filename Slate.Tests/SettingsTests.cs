using System.Text.Json;
using Slate.Core;

namespace Slate.Tests;

public static class SettingsTests
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
        // -------------------------------------------------------------
        // Group 1: Settings persistence and JSON round-tripping
        // -------------------------------------------------------------
        check("Settings: Clean defaults serialize and deserialize without changes", () =>
        {
            var initial = new BrowserSettings();
            Equal("DuckDuckGo", initial.SearchEngine);
            Equal("Dark", initial.Theme);
            Equal("Slate", initial.AccentTheme);
            Equal(15, initial.SleepAfterMinutes);
            Equal(true, initial.RestoreSession);
            Equal("Restore previous session", initial.StartupBehavior);
            Equal(false, initial.SidebarCollapsed);
            Equal(false, initial.CompactSidebarDensity);
            Equal(false, initial.ReduceMotion);
            Equal(100, initial.DefaultZoomPercent);
            Equal(false, initial.DeveloperToolsEnabled);
            Equal(true, initial.ShowBookmarksBar);
            Equal(null, initial.DownloadPath);
            Equal(false, initial.AskDownloadLocation);
            Equal(true, initial.AutofillPasswords);

            var state = new BrowserState { Settings = initial };
            var json = JsonSerializer.Serialize(state);
            var restored = JsonSerializer.Deserialize<BrowserState>(json);
            Assert(restored is not null);
            var s = restored.Settings;
            Equal("DuckDuckGo", s.SearchEngine);
            Equal("Dark", s.Theme);
            Equal("Slate", s.AccentTheme);
            Equal(15, s.SleepAfterMinutes);
            Equal(true, s.RestoreSession);
            Equal("Restore previous session", s.StartupBehavior);
            Equal(false, s.SidebarCollapsed);
            Equal(false, s.CompactSidebarDensity);
            Equal(false, s.ReduceMotion);
            Equal(100, s.DefaultZoomPercent);
            Equal(false, s.DeveloperToolsEnabled);
            Equal(true, s.ShowBookmarksBar);
            Equal(null, s.DownloadPath);
            Equal(false, s.AskDownloadLocation);
            Equal(true, s.AutofillPasswords);
        });

        check("Settings: Custom settings survive JSON serialization round-trip", () =>
        {
            var custom = new BrowserSettings
            {
                SearchEngine = "Google",
                Theme = "Light",
                AccentTheme = "Cobalt",
                SleepAfterMinutes = 60,
                StartupBehavior = "Open new tab page",
                SidebarCollapsed = true,
                CompactSidebarDensity = true,
                ReduceMotion = true,
                DefaultZoomPercent = 125,
                DeveloperToolsEnabled = true,
                ShowBookmarksBar = false,
                DownloadPath = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
                AskDownloadLocation = true,
                AutofillPasswords = false
            };

            Equal(false, custom.RestoreSession);
            Equal("Open new tab page", custom.StartupBehavior);

            var state = new BrowserState { Settings = custom };
            var json = JsonSerializer.Serialize(state);
            var loaded = JsonSerializer.Deserialize<BrowserState>(json);
            Assert(loaded is not null);
            var s = loaded.Settings;

            Equal("Google", s.SearchEngine);
            Equal("Light", s.Theme);
            Equal("Cobalt", s.AccentTheme);
            Equal(60, s.SleepAfterMinutes);
            Equal(false, s.RestoreSession);
            Equal("Open new tab page", s.StartupBehavior);
            Equal(true, s.SidebarCollapsed);
            Equal(true, s.CompactSidebarDensity);
            Equal(true, s.ReduceMotion);
            Equal(125, s.DefaultZoomPercent);
            Equal(true, s.DeveloperToolsEnabled);
            Equal(false, s.ShowBookmarksBar);
            Equal(custom.DownloadPath, s.DownloadPath);
            Equal(true, s.AskDownloadLocation);
            Equal(false, s.AutofillPasswords);
        });

        check("Settings: StateStore atomic persistence survives disk restart", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "slate-settings-persist-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var store = new StateStore(directory);
                var session = new BrowserSession();
                session.State.Settings.Theme = "System";
                session.State.Settings.AccentTheme = "Plum";
                session.State.Settings.SearchEngine = "Bing";
                session.State.Settings.SleepAfterMinutes = 30;
                session.State.Settings.StartupBehavior = "Open new tab page";
                session.State.Settings.CompactSidebarDensity = true;
                session.State.Settings.ReduceMotion = true;
                session.State.Settings.ShowBookmarksBar = false;
                session.State.Settings.AskDownloadLocation = true;
                session.State.Settings.AutofillPasswords = false;
                session.State.Settings.DownloadPath = directory;

                Assert(store.Save(session.State), "Failed first save");
                Assert(store.Save(session.State), "Failed second save (backup generation)");

                var primaryJson = Path.Combine(directory, "session.json");
                var backupJson = Path.Combine(directory, "session.json.bak");
                Assert(File.Exists(primaryJson), "Primary session.json missing");
                Assert(File.Exists(backupJson), "Backup session.json.bak missing");

                var loadedState = store.Load();
                Assert(loadedState is not null, "Loaded state is null");
                var s = loadedState.Settings;
                Equal("System", s.Theme);
                Equal("Plum", s.AccentTheme);
                Equal("Bing", s.SearchEngine);
                Equal(30, s.SleepAfterMinutes);
                Equal(false, s.RestoreSession);
                Equal("Open new tab page", s.StartupBehavior);
                Equal(true, s.CompactSidebarDensity);
                Equal(true, s.ReduceMotion);
                Equal(false, s.ShowBookmarksBar);
                Equal(true, s.AskDownloadLocation);
                Equal(false, s.AutofillPasswords);
                Equal(directory, s.DownloadPath);
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        });

        check("Settings: Backward compatibility with legacy JSON without new properties", () =>
        {
            string legacyJson = """
            {
              "Version": 1,
              "Settings": {
                "SearchEngine": "DuckDuckGo",
                "Theme": "Dark",
                "AccentTheme": "Clay",
                "RestoreSession": false
              }
            }
            """;
            var loaded = JsonSerializer.Deserialize<BrowserState>(legacyJson);
            Assert(loaded is not null);
            var s = loaded.Settings;
            Equal("Clay", s.AccentTheme);
            Equal(false, s.RestoreSession);
            Equal("Open new tab page", s.StartupBehavior);
            Equal(false, s.CompactSidebarDensity);
            Equal(true, s.ShowBookmarksBar);
        });

        // -------------------------------------------------------------
        // Group 2: Tab sleep ComboBox string <-> minute integer two-way mapping
        // -------------------------------------------------------------
        check("Tab sleep: Standard options map cleanly two-way with minute integers", () =>
        {
            var expectedPairs = new (string Option, int Minutes)[]
            {
                ("Never", 0),
                ("5 minutes", 5),
                ("15 minutes", 15),
                ("30 minutes", 30),
                ("1 hour", 60),
                ("2 hours", 120)
            };

            foreach (var (option, minutes) in expectedPairs)
            {
                Equal(minutes, BrowserSettings.SleepOptionToMinutes(option));
                Equal(option, BrowserSettings.MinutesToSleepOption(minutes));
            }
        });

        check("Tab sleep: Case-insensitive, trimmed, and abbreviated string forms resolve accurately", () =>
        {
            // "Never" forms
            Equal(0, BrowserSettings.SleepOptionToMinutes("never"));
            Equal(0, BrowserSettings.SleepOptionToMinutes("NEVER"));
            Equal(0, BrowserSettings.SleepOptionToMinutes("  Never  "));
            Equal(0, BrowserSettings.SleepOptionToMinutes("0"));

            // "5 minutes" forms
            Equal(5, BrowserSettings.SleepOptionToMinutes("5 min"));
            Equal(5, BrowserSettings.SleepOptionToMinutes("5m"));
            Equal(5, BrowserSettings.SleepOptionToMinutes("5"));
            Equal(5, BrowserSettings.SleepOptionToMinutes("  5 minutes  "));

            // "15 minutes" forms
            Equal(15, BrowserSettings.SleepOptionToMinutes("15 min"));
            Equal(15, BrowserSettings.SleepOptionToMinutes("15m"));
            Equal(15, BrowserSettings.SleepOptionToMinutes("15"));
            Equal(15, BrowserSettings.SleepOptionToMinutes("15 MINUTES"));

            // "30 minutes" forms
            Equal(30, BrowserSettings.SleepOptionToMinutes("30 min"));
            Equal(30, BrowserSettings.SleepOptionToMinutes("30m"));
            Equal(30, BrowserSettings.SleepOptionToMinutes("30"));

            // "1 hour" forms
            Equal(60, BrowserSettings.SleepOptionToMinutes("1 hr"));
            Equal(60, BrowserSettings.SleepOptionToMinutes("1 HOUR"));
            Equal(60, BrowserSettings.SleepOptionToMinutes("60 minutes"));
            Equal(60, BrowserSettings.SleepOptionToMinutes("60m"));
            Equal(60, BrowserSettings.SleepOptionToMinutes("60"));

            // "2 hours" forms
            Equal(120, BrowserSettings.SleepOptionToMinutes("2 hrs"));
            Equal(120, BrowserSettings.SleepOptionToMinutes("2 hours"));
            Equal(120, BrowserSettings.SleepOptionToMinutes("120 minutes"));
            Equal(120, BrowserSettings.SleepOptionToMinutes("120m"));
            Equal(120, BrowserSettings.SleepOptionToMinutes("120"));
        });

        check("Tab sleep: Unknown strings and null values normalize safely without throwing", () =>
        {
            Equal(15, BrowserSettings.SleepOptionToMinutes(null));
            Equal(15, BrowserSettings.SleepOptionToMinutes(""));
            Equal(15, BrowserSettings.SleepOptionToMinutes("   "));
            Equal(15, BrowserSettings.SleepOptionToMinutes("random string"));
            Equal(15, BrowserSettings.SleepOptionToMinutes("not an option"));
            Equal(15, BrowserSettings.SleepOptionToMinutes("invalid-value"));
        });

        check("Tab sleep: Arbitrary numeric strings and boundary minutes normalize safely", () =>
        {
            // Numeric strings parse and normalize
            Equal(0, BrowserSettings.SleepOptionToMinutes("-5"));
            Equal(5, BrowserSettings.SleepOptionToMinutes("7"));
            Equal(15, BrowserSettings.SleepOptionToMinutes("18"));
            Equal(30, BrowserSettings.SleepOptionToMinutes("40"));
            Equal(60, BrowserSettings.SleepOptionToMinutes("80"));
            Equal(120, BrowserSettings.SleepOptionToMinutes("200"));

            // Minutes to options for in-between and extreme values
            Equal("Never", BrowserSettings.MinutesToSleepOption(-10));
            Equal("Never", BrowserSettings.MinutesToSleepOption(0));
            Equal("5 minutes", BrowserSettings.MinutesToSleepOption(3));
            Equal("5 minutes", BrowserSettings.MinutesToSleepOption(10));
            Equal("15 minutes", BrowserSettings.MinutesToSleepOption(12));
            Equal("15 minutes", BrowserSettings.MinutesToSleepOption(20));
            Equal("30 minutes", BrowserSettings.MinutesToSleepOption(25));
            Equal("30 minutes", BrowserSettings.MinutesToSleepOption(45));
            Equal("1 hour", BrowserSettings.MinutesToSleepOption(50));
            Equal("1 hour", BrowserSettings.MinutesToSleepOption(90));
            Equal("2 hours", BrowserSettings.MinutesToSleepOption(95));
            Equal("2 hours", BrowserSettings.MinutesToSleepOption(1000));
        });

        // -------------------------------------------------------------
        // Group 3: Download path safety validation and normalization
        // -------------------------------------------------------------
        check("Download safety: Safe local directories pass validation", () =>
        {
            var temp = Path.GetTempPath();
            Assert(DownloadSafety.IsSafeLocalDirectory(temp), $"Temp path was rejected: {temp}");

            var sysDrive = Path.GetPathRoot(Environment.SystemDirectory);
            if (!string.IsNullOrEmpty(sysDrive) && Directory.Exists(sysDrive))
            {
                Assert(DownloadSafety.IsSafeLocalDirectory(sysDrive), $"System root was rejected: {sysDrive}");
            }
        });

        check("Download safety: UNC paths and network shares are strictly rejected", () =>
        {
            Assert(!DownloadSafety.IsSafeLocalDirectory(@"\\server\share"));
            Assert(!DownloadSafety.IsSafeLocalDirectory(@"\\127.0.0.1\c$"));
            Assert(!DownloadSafety.IsSafeLocalDirectory(@"\\localhost\share"));
            Assert(!DownloadSafety.IsSafeLocalDirectory(@"//server/share"));
            Assert(!DownloadSafety.IsSafeLocalDirectory(@"\\?\UNC\server\share"));
        });

        check("Download safety: Relative paths, device paths, and invalid inputs are rejected", () =>
        {
            Assert(!DownloadSafety.IsSafeLocalDirectory(null));
            Assert(!DownloadSafety.IsSafeLocalDirectory(""));
            Assert(!DownloadSafety.IsSafeLocalDirectory("   "));
            Assert(!DownloadSafety.IsSafeLocalDirectory(@"relative\folder"));
            Assert(!DownloadSafety.IsSafeLocalDirectory(@".\downloads"));
            Assert(!DownloadSafety.IsSafeLocalDirectory(@"..\downloads"));
            Assert(!DownloadSafety.IsSafeLocalDirectory(@"CON:"));
            Assert(!DownloadSafety.IsSafeLocalDirectory(@"PRN:"));
            Assert(!DownloadSafety.IsSafeLocalDirectory(@"NUL:"));
            Assert(!DownloadSafety.IsSafeLocalDirectory(@"X:\nonexistent_drive\folder"));

            // An existing file must be rejected
            var tempFile = Path.GetTempFileName();
            try
            {
                Assert(!DownloadSafety.IsSafeLocalDirectory(tempFile), "Existing file was not rejected");
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        });

        check("Download safety: BrowserSession.Normalize strips unsafe download paths", () =>
        {
            var state = new BrowserState
            {
                Settings = new BrowserSettings
                {
                    DownloadPath = @"\\rogue-server\payloads"
                }
            };
            var session = new BrowserSession(state);
            Equal(null, session.State.Settings.DownloadPath);

            var safeState = new BrowserState
            {
                Settings = new BrowserSettings
                {
                    DownloadPath = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)
                }
            };
            var safeSession = new BrowserSession(safeState);
            Equal(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), safeSession.State.Settings.DownloadPath);
        });

        // -------------------------------------------------------------
        // Group 4: New settings properties default and persistence behavior
        // -------------------------------------------------------------
        check("New properties: CompactSidebarDensity defaults to false and preserves toggle", () =>
        {
            var s = new BrowserSettings();
            Equal(false, s.CompactSidebarDensity);
            s.CompactSidebarDensity = true;
            Equal(true, s.CompactSidebarDensity);

            var json = JsonSerializer.Serialize(s);
            var roundtrip = JsonSerializer.Deserialize<BrowserSettings>(json);
            Assert(roundtrip is not null);
            Equal(true, roundtrip.CompactSidebarDensity);
        });

        check("New properties: StartupBehavior and RestoreSession maintain two-way synchronization", () =>
        {
            var s = new BrowserSettings();
            Equal(true, s.RestoreSession);
            Equal("Restore previous session", s.StartupBehavior);

            // Change RestoreSession to false -> StartupBehavior becomes "Open new tab page"
            s.RestoreSession = false;
            Equal(false, s.RestoreSession);
            Equal("Open new tab page", s.StartupBehavior);

            // Change RestoreSession to true -> StartupBehavior becomes "Restore previous session"
            s.RestoreSession = true;
            Equal(true, s.RestoreSession);
            Equal("Restore previous session", s.StartupBehavior);

            // Change StartupBehavior to "Open new tab page" -> RestoreSession becomes false
            s.StartupBehavior = "Open new tab page";
            Equal(false, s.RestoreSession);
            Equal("Open new tab page", s.StartupBehavior);

            // Change StartupBehavior to "Restore previous session" -> RestoreSession becomes true
            s.StartupBehavior = "Restore previous session";
            Equal(true, s.RestoreSession);
            Equal("Restore previous session", s.StartupBehavior);

            // Unknown string falls back safely to "Restore previous session" and true
            s.StartupBehavior = "Unknown";
            Equal(true, s.RestoreSession);
            Equal("Restore previous session", s.StartupBehavior);
        });

        check("New properties: SleepOptions and StartupBehaviorOptions arrays are non-empty and valid", () =>
        {
            Assert(BrowserSettings.SleepOptions.Length == 6, "Expected 6 sleep options");
            Equal("Never", BrowserSettings.SleepOptions[0]);
            Equal("2 hours", BrowserSettings.SleepOptions[^1]);

            Assert(BrowserSettings.StartupBehaviorOptions.Length == 2, "Expected 2 startup behavior options");
            Equal("Restore previous session", BrowserSettings.StartupBehaviorOptions[0]);
            Equal("Open new tab page", BrowserSettings.StartupBehaviorOptions[1]);
        });
    }
}
