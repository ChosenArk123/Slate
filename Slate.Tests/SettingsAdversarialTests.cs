using System.Text.Json;
using Slate.Core;

namespace Slate.Tests;

public static class SettingsAdversarialTests
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
        // =========================================================================
        // Battery 1: Tab sleep bidirectional mapping across boundary inputs
        // =========================================================================
        check("Adversarial: Tab sleep 'Never' and '0' boundary string variants normalize to 0", () =>
        {
            var zeroVariants = new[]
            {
                "Never", "never", "NEVER", "  Never  ", "\tnever\r\n",
                "0", " 0 ", "00", "+0", "-0"
            };

            foreach (var variant in zeroVariants)
            {
                int minutes = BrowserSettings.SleepOptionToMinutes(variant);
                Equal(0, minutes);
                Equal("Never", BrowserSettings.MinutesToSleepOption(minutes));
            }
        });

        check("Adversarial: Tab sleep negative numeric strings and integers normalize to 0 / 'Never'", () =>
        {
            var negativeStrings = new[] { "-1", "-5", "-15", "-30", "-60", "-120", "-9999", "-2147483648" };
            foreach (var neg in negativeStrings)
            {
                int minutes = BrowserSettings.SleepOptionToMinutes(neg);
                Equal(0, minutes);
                Equal("Never", BrowserSettings.MinutesToSleepOption(minutes));
            }

            var negativeInts = new[] { -1, -2, -5, -15, -100, -9999, int.MinValue };
            foreach (var neg in negativeInts)
            {
                Equal(0, BrowserSettings.NormalizeSleepMinutes(neg));
                Equal("Never", BrowserSettings.MinutesToSleepOption(neg));
            }
        });

        check("Adversarial: Tab sleep unknown strings, null, whitespace, and overflow normalize safely", () =>
        {
            var unknownStrings = new[]
            {
                null, "", "   ", "\t\r\n",
                "Nevermind", "forever", "infinity", "-infinity", "NaN",
                "xyz", "not_an_option", "invalid", "!@#$%^&*()",
                "0 minutes", "5 hours", "10 days",
                "2147483648", // int.MaxValue + 1 (overflows int.TryParse)
                "-2147483649", // int.MinValue - 1 (underflows int.TryParse)
                "99999999999999999999999999999999999999999999999999",
                "-99999999999999999999999999999999999999999999999999"
            };

            foreach (var str in unknownStrings)
            {
                int minutes = BrowserSettings.SleepOptionToMinutes(str);
                Equal(15, minutes); // fallback default
                Equal("15 minutes", BrowserSettings.MinutesToSleepOption(minutes));
            }
        });

        check("Adversarial: Tab sleep extreme and boundary minute counts normalize cleanly", () =>
        {
            // Boundary thresholds: <=0 -> 0, <=10 -> 5, <=20 -> 15, <=45 -> 30, <=90 -> 60, >90 -> 120
            var boundaryChecks = new (int Input, int ExpectedNorm, string ExpectedOption)[]
            {
                (int.MinValue, 0, "Never"),
                (-1, 0, "Never"),
                (0, 0, "Never"),
                (1, 5, "5 minutes"),
                (5, 5, "5 minutes"),
                (10, 5, "5 minutes"),
                (11, 15, "15 minutes"),
                (15, 15, "15 minutes"),
                (20, 15, "15 minutes"),
                (21, 30, "30 minutes"),
                (30, 30, "30 minutes"),
                (45, 30, "30 minutes"),
                (46, 60, "1 hour"),
                (60, 60, "1 hour"),
                (90, 60, "1 hour"),
                (91, 120, "2 hours"),
                (120, 120, "2 hours"),
                (500, 120, "2 hours"),
                (100_000, 120, "2 hours"),
                (int.MaxValue, 120, "2 hours")
            };

            foreach (var (input, expectedNorm, expectedOption) in boundaryChecks)
            {
                Equal(expectedNorm, BrowserSettings.NormalizeSleepMinutes(input));
                Equal(expectedOption, BrowserSettings.MinutesToSleepOption(input));
                Equal(expectedNorm, BrowserSettings.SleepOptionToMinutes(expectedOption));
            }
        });

        check("Adversarial: Tab sleep generative idempotence and consistency across range [-5000, 5000]", () =>
        {
            var canonicalOptions = new HashSet<string>(BrowserSettings.SleepOptions);
            var canonicalMinutes = new HashSet<int> { 0, 5, 15, 30, 60, 120 };

            for (int minutes = -5000; minutes <= 5000; minutes++)
            {
                var option = BrowserSettings.MinutesToSleepOption(minutes);
                Assert(canonicalOptions.Contains(option), $"Option '{option}' not in canonical SleepOptions for minutes {minutes}");

                var normMinutes = BrowserSettings.NormalizeSleepMinutes(minutes);
                Assert(canonicalMinutes.Contains(normMinutes), $"NormMinutes {normMinutes} not canonical for minutes {minutes}");

                var mappedMinutes = BrowserSettings.SleepOptionToMinutes(option);
                Equal(normMinutes, mappedMinutes);

                // Idempotence: mapping back and forth is stable
                var remappedOption = BrowserSettings.MinutesToSleepOption(mappedMinutes);
                Equal(option, remappedOption);
            }
        });

        // =========================================================================
        // Battery 2: Settings JSON serialization, schema compatibility, and recovery
        // =========================================================================
        check("Adversarial: Settings JSON round-trip with all properties populated with non-defaults", () =>
        {
            var nonDefaults = new BrowserSettings
            {
                SearchEngine = "Bing",
                Theme = "Light",
                AccentTheme = "Amber",
                SleepAfterMinutes = 120,
                StartupBehavior = "Open new tab page",
                SidebarCollapsed = true,
                CompactSidebarDensity = true,
                ReduceMotion = true,
                DefaultZoomPercent = 250,
                DeveloperToolsEnabled = true,
                ShowBookmarksBar = false,
                DownloadPath = @"C:\SlateDownloads\Custom",
                AskDownloadLocation = true,
                AutofillPasswords = false
            };

            var state = new BrowserState { Settings = nonDefaults };
            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            var deserialized = JsonSerializer.Deserialize<BrowserState>(json);
            Assert(deserialized is not null);

            var s = deserialized.Settings;
            Equal("Bing", s.SearchEngine);
            Equal("Light", s.Theme);
            Equal("Amber", s.AccentTheme);
            Equal(120, s.SleepAfterMinutes);
            Equal("Open new tab page", s.StartupBehavior);
            Equal(false, s.RestoreSession);
            Equal(true, s.SidebarCollapsed);
            Equal(true, s.CompactSidebarDensity);
            Equal(true, s.ReduceMotion);
            Equal(250, s.DefaultZoomPercent);
            Equal(true, s.DeveloperToolsEnabled);
            Equal(false, s.ShowBookmarksBar);
            Equal(@"C:\SlateDownloads\Custom", s.DownloadPath);
            Equal(true, s.AskDownloadLocation);
            Equal(false, s.AutofillPasswords);
        });

        check("Adversarial: Schema compatibility with legacy and partially missing JSON files", () =>
        {
            // Case A: Bare minimum session without Settings object
            string noSettingsJson = """{ "Version": 1 }""";
            var stateNoSettings = JsonSerializer.Deserialize<BrowserState>(noSettingsJson);
            Assert(stateNoSettings is not null);
            var sessionA = new BrowserSession(stateNoSettings);
            Equal("DuckDuckGo", sessionA.State.Settings.SearchEngine);
            Equal("Dark", sessionA.State.Settings.Theme);
            Equal("Slate", sessionA.State.Settings.AccentTheme);
            Equal(15, sessionA.State.Settings.SleepAfterMinutes);
            Equal("Restore previous session", sessionA.State.Settings.StartupBehavior);
            Equal(true, sessionA.State.Settings.RestoreSession);
            Equal(false, sessionA.State.Settings.CompactSidebarDensity);
            Equal(true, sessionA.State.Settings.ShowBookmarksBar);

            // Case B: Settings object explicitly null
            string nullSettingsJson = """{ "Version": 1, "Settings": null }""";
            var stateNullSettings = JsonSerializer.Deserialize<BrowserState>(nullSettingsJson);
            Assert(stateNullSettings is not null);
            var sessionB = new BrowserSession(stateNullSettings);
            Assert(sessionB.State.Settings is not null);
            Equal("DuckDuckGo", sessionB.State.Settings.SearchEngine);

            // Case C: Settings with all string fields set to null
            string nullFieldsJson = """
            {
                "Version": 1,
                "Settings": {
                    "SearchEngine": null,
                    "Theme": null,
                    "AccentTheme": null,
                    "StartupBehavior": null,
                    "DownloadPath": null
                }
            }
            """;
            var stateNullFields = JsonSerializer.Deserialize<BrowserState>(nullFieldsJson);
            Assert(stateNullFields is not null);
            var sessionC = new BrowserSession(stateNullFields);
            // BrowserSession.Normalize ensures Theme and AccentTheme fall back cleanly
            Equal("Dark", sessionC.State.Settings.Theme);
            Equal("Slate", sessionC.State.Settings.AccentTheme);
            Equal("Restore previous session", sessionC.State.Settings.StartupBehavior);
            Equal(true, sessionC.State.Settings.RestoreSession);
            Equal(null, sessionC.State.Settings.DownloadPath);

            // Case D: Unknown extra properties injected into Settings
            string extraPropsJson = """
            {
                "Version": 1,
                "Settings": {
                    "SearchEngine": "Google",
                    "UnknownFutureProperty": "future_value",
                    "HackerPayload": { "nested": 123 }
                }
            }
            """;
            var stateExtraProps = JsonSerializer.Deserialize<BrowserState>(extraPropsJson);
            Assert(stateExtraProps is not null);
            Equal("Google", stateExtraProps.Settings.SearchEngine);
        });

        check("Adversarial: Conflicting StartupBehavior and RestoreSession ordering resolves consistently", () =>
        {
            // Order 1: StartupBehavior precedes RestoreSession
            string jsonOrder1 = """
            {
                "Version": 1,
                "Settings": {
                    "StartupBehavior": "Open new tab page",
                    "RestoreSession": true
                }
            }
            """;
            var state1 = JsonSerializer.Deserialize<BrowserState>(jsonOrder1);
            Assert(state1 is not null);
            Equal(true, state1.Settings.RestoreSession);
            Equal("Restore previous session", state1.Settings.StartupBehavior);

            // Order 2: RestoreSession precedes StartupBehavior
            string jsonOrder2 = """
            {
                "Version": 1,
                "Settings": {
                    "RestoreSession": true,
                    "StartupBehavior": "Open new tab page"
                }
            }
            """;
            var state2 = JsonSerializer.Deserialize<BrowserState>(jsonOrder2);
            Assert(state2 is not null);
            Equal(false, state2.Settings.RestoreSession);
            Equal("Open new tab page", state2.Settings.StartupBehavior);
        });

        check("Adversarial: BrowserSession.Normalize clamps out-of-range persisted values", () =>
        {
            var rawState = new BrowserState
            {
                Settings = new BrowserSettings
                {
                    SleepAfterMinutes = -999,
                    DefaultZoomPercent = 9999,
                    Theme = "NeonPunk",
                    AccentTheme = "Psychedelic"
                }
            };

            var session = new BrowserSession(rawState);
            Equal(0, session.State.Settings.SleepAfterMinutes);
            Equal(500, session.State.Settings.DefaultZoomPercent);
            Equal("Dark", session.State.Settings.Theme);
            Equal("Slate", session.State.Settings.AccentTheme);

            var rawState2 = new BrowserState
            {
                Settings = new BrowserSettings
                {
                    SleepAfterMinutes = 10000,
                    DefaultZoomPercent = -50
                }
            };
            var session2 = new BrowserSession(rawState2);
            Equal(120, session2.State.Settings.SleepAfterMinutes);
            Equal(25, session2.State.Settings.DefaultZoomPercent);
        });

        // =========================================================================
        // Battery 3: StateStore atomic persistence, backup rotation, and corruption recovery
        // =========================================================================
        check("Adversarial: StateStore atomic backup rotation and recovery when primary is corrupt", () =>
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "slate-adversarial-store-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var store = new StateStore(tempDir);
                var primaryFile = Path.Combine(tempDir, "session.json");
                var backupFile = Path.Combine(tempDir, "session.json.bak");

                // Save 1: Creates primary and backup
                var session1 = new BrowserSession();
                session1.State.Settings.SearchEngine = "Google";
                session1.State.Settings.SleepAfterMinutes = 30;
                Assert(store.Save(session1.State), "Save 1 failed");
                Assert(File.Exists(primaryFile), "Primary file missing after Save 1");
                Assert(File.Exists(backupFile), "Backup file missing after Save 1");

                // Save 2: Updates with new settings
                session1.State.Settings.SearchEngine = "Bing";
                session1.State.Settings.SleepAfterMinutes = 60;
                Assert(store.Save(session1.State), "Save 2 failed");

                // Corrupt primary file with invalid JSON syntax
                File.WriteAllText(primaryFile, "{ corrupted json !@#$%^");

                // Load must recover cleanly from backup
                var recoveredState = store.Load();
                Assert(recoveredState is not null, "Recovered state was null");
                Assert(store.LastError is not null, "LastError was not set on corruption recovery");
                Equal("Bing", recoveredState.Settings.SearchEngine);
                Equal(60, recoveredState.Settings.SleepAfterMinutes);

                // Now corrupt BOTH primary and backup
                File.WriteAllText(backupFile, "also corrupt");
                var defaultState = store.Load();
                Assert(defaultState is not null, "Default state was null");
                Equal(1, defaultState.Version);
                Equal("DuckDuckGo", defaultState.Settings.SearchEngine); // Clean fallback
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        });

        check("Adversarial: StateStore rejects unsupported session Version and recovers", () =>
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "slate-adversarial-ver-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var store = new StateStore(tempDir);
                var primaryFile = Path.Combine(tempDir, "session.json");

                // Write session with unsupported future version
                File.WriteAllText(primaryFile, """{ "Version": 99, "Settings": { "SearchEngine": "FutureEngine" } }""");

                var loaded = store.Load();
                Assert(loaded is not null);
                Assert(store.LastError is not null);
                Equal(1, loaded.Version);
                Equal("DuckDuckGo", loaded.Settings.SearchEngine);
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        });

        // =========================================================================
        // Battery 4: Download path safety validation against UNC, relative, and device paths
        // =========================================================================
        check("Adversarial: DownloadSafety rejects exhaustive matrix of UNC and network shares", () =>
        {
            var uncPaths = new[]
            {
                @"\\server\share",
                @"\\server\share\sub",
                @"\\127.0.0.1\c$",
                @"\\127.0.0.1\share",
                @"\\192.168.1.100\downloads",
                @"\\localhost\share",
                @"\\localhost\c$",
                @"//server/share",
                @"//server/share/sub",
                @"//127.0.0.1/share",
                @"//localhost/share",
                @"\\server/share",
                @"//server\share",
                @"\\?\UNC\server\share",
                @"\\?\UNC\127.0.0.1\c$",
                @"  \\server\share  ",
                @"  //server/share  "
            };

            foreach (var unc in uncPaths)
            {
                Assert(!DownloadSafety.IsSafeLocalDirectory(unc), $"Failed to reject UNC path: '{unc}'");
            }
        });

        check("Adversarial: DownloadSafety rejects relative paths, path traversal, and empty inputs", () =>
        {
            var relativePaths = new[]
            {
                null,
                "",
                "   ",
                "\t\r\n",
                "downloads",
                @"downloads\sub",
                @"downloads/sub",
                @".\downloads",
                @"..\downloads",
                @"..\..\Windows\System32",
                @".",
                @"..",
                @"folder",
                @"folder\..\sub"
            };

            foreach (var rel in relativePaths)
            {
                Assert(!DownloadSafety.IsSafeLocalDirectory(rel), $"Failed to reject relative path: '{rel}'");
            }
        });

        check("Adversarial: DownloadSafety rejects Windows reserved device names and device namespaces", () =>
        {
            var devicePaths = new[]
            {
                "CON:", "PRN:", "AUX:", "NUL:",
                "COM1:", "COM2:", "COM3:", "COM4:", "COM5:", "COM6:", "COM7:", "COM8:", "COM9:",
                "LPT1:", "LPT2:", "LPT3:", "LPT4:", "LPT5:", "LPT6:", "LPT7:", "LPT8:", "LPT9:",
                @"\\.\CON", @"\\.\NUL", @"\\.\PRN",
                @"\\.\C:\", @"\\?\C:\"
            };

            foreach (var dev in devicePaths)
            {
                Assert(!DownloadSafety.IsSafeLocalDirectory(dev), $"Failed to reject device path: '{dev}'");
            }
        });

        check("Adversarial: DownloadSafety rejects non-existent drives and existing regular files", () =>
        {
            // Non-existent drive
            Assert(!DownloadSafety.IsSafeLocalDirectory(@"Z:\DefinitelyNonExistentDriveXYZ\Folder"));
            Assert(!DownloadSafety.IsSafeLocalDirectory(@"1:\InvalidDriveLetter\Folder"));

            // Regular file must be rejected (only directories are valid download locations)
            var tempFile = Path.GetTempFileName();
            try
            {
                Assert(File.Exists(tempFile));
                Assert(!DownloadSafety.IsSafeLocalDirectory(tempFile), $"Failed to reject existing file as download path: '{tempFile}'");
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        });

        check("Adversarial: DownloadSafety accepts valid rooted local directories and non-existent local subfolders", () =>
        {
            var tempDir = Path.GetTempPath();
            Assert(DownloadSafety.IsSafeLocalDirectory(tempDir), $"Valid temp directory was rejected: '{tempDir}'");

            var nonExistentSubfolder = Path.Combine(tempDir, "subfolder-not-yet-created-" + Guid.NewGuid().ToString("N"));
            Assert(!Directory.Exists(nonExistentSubfolder));
            Assert(DownloadSafety.IsSafeLocalDirectory(nonExistentSubfolder), $"Valid planned subfolder was rejected: '{nonExistentSubfolder}'");
        });

        check("Adversarial: BrowserSession.Normalize strips unsafe download paths and preserves valid ones", () =>
        {
            var unsafePaths = new[]
            {
                @"\\rogue\payload",
                @"//evil.com/share",
                @"..\..\Windows",
                "CON:",
                @"Z:\NonExistentDrive\Downloads"
            };

            foreach (var badPath in unsafePaths)
            {
                var state = new BrowserState { Settings = new BrowserSettings { DownloadPath = badPath } };
                var session = new BrowserSession(state);
                Equal(null, session.State.Settings.DownloadPath);
            }

            var safePath = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
            var safeState = new BrowserState { Settings = new BrowserSettings { DownloadPath = safePath } };
            var safeSession = new BrowserSession(safeState);
            Equal(safePath, safeSession.State.Settings.DownloadPath);
        });
    }
}
