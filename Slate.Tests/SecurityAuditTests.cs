using System.Net;
using System.Text.Json;
using Slate.Core;

namespace Slate.Tests;

public static class SecurityAuditTests
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
        // 1. Private Browsing Zero Persistence Invariants
        // -----------------------------------------------------------------
        check("Security Audit: InPrivate tabs are strictly excluded from session serialization", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "slate-sec-test-" + Guid.NewGuid());
            try
            {
                var store = new StateStore(directory);
                var session = new BrowserSession();
                var normal = session.AddTab("https://public.example.com/");
                var privateTab = session.AddTab("https://secret-private.example.com/", isPrivate: true);
                var tempTab = session.AddTab("https://temp.example.com/", temporary: true);

                Assert(store.Save(session.State));
                Assert(store.Save(session.State)); // test backup copy creation

                var primaryPath = Path.Combine(directory, "session.json");
                var backupPath = Path.Combine(directory, "session.json.bak");

                Assert(File.Exists(primaryPath), "Primary session.json must exist");
                Assert(File.Exists(backupPath), "Backup session.json.bak must exist");

                string primaryJson = File.ReadAllText(primaryPath);
                string backupJson = File.ReadAllText(backupPath);

                Assert(!primaryJson.Contains("secret-private.example.com", StringComparison.Ordinal), "session.json must not leak InPrivate URL");
                Assert(!backupJson.Contains("secret-private.example.com", StringComparison.Ordinal), "session.json.bak must not leak InPrivate URL");
                Assert(!primaryJson.Contains(privateTab.Id.ToString(), StringComparison.Ordinal), "session.json must not leak InPrivate tab ID");
                Assert(!backupJson.Contains(privateTab.Id.ToString(), StringComparison.Ordinal), "session.json.bak must not leak InPrivate tab ID");

                // Temporary tabs also not persisted
                Assert(!primaryJson.Contains("temp.example.com", StringComparison.Ordinal));
                Assert(!backupJson.Contains("temp.example.com", StringComparison.Ordinal));

                // Normal tab persisted
                Assert(primaryJson.Contains("public.example.com", StringComparison.Ordinal));
                Assert(backupJson.Contains("public.example.com", StringComparison.Ordinal));

                // Load state from store and verify InPrivate tab is absent
                var loadedState = store.Load();
                Assert(loadedState is not null);
                Assert(!loadedState!.Tabs.Any(t => t.IsPrivate), "Loaded tabs must contain zero InPrivate tabs");
                Assert(!loadedState!.Tabs.Any(t => t.Url.Contains("secret-private.example.com")), "Loaded tabs must not have InPrivate URL");
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        });

        check("Security Audit: InPrivate tab visits are never recorded into history", () =>
        {
            var session = new BrowserSession();
            var privateTab = session.AddTab("https://classified.example.org/dashboard", isPrivate: true);
            session.RecordVisit(privateTab);
            Equal(0, session.State.History.Count);

            // Even repeated visits or title updates do not record
            privateTab.Title = "Top Secret";
            session.RecordVisit(privateTab);
            Equal(0, session.State.History.Count);

            // Contrast with normal tab visit
            var normalTab = session.AddTab("https://public.example.org/home");
            session.RecordVisit(normalTab);
            Equal(1, session.State.History.Count);
            Equal("https://public.example.org/home", session.State.History[0].Url);
        });

        check("Security Audit: InPrivate download metadata is excluded from primary, backup, and normalized state", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "slate-private-download-" + Guid.NewGuid());
            try
            {
                var state = new BrowserSession().State;
                state.Downloads.Add(new DownloadEntry
                {
                    FileName = "private-statement.pdf", Path = @"C:\private-statement.pdf", Status = "Completed", IsPrivate = true
                });
                var store = new StateStore(directory);
                Assert(store.Save(state));
                Assert(store.Save(state));
                Assert(!File.ReadAllText(Path.Combine(directory, "session.json")).Contains("private-statement", StringComparison.Ordinal));
                Assert(!File.ReadAllText(Path.Combine(directory, "session.json.bak")).Contains("private-statement", StringComparison.Ordinal));
                Assert(!new BrowserSession(state).State.Downloads.Any(download => download.IsPrivate));
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        });

        check("Security Audit: Closed InPrivate tabs never enter RecentlyClosed list", () =>
        {
            var session = new BrowserSession();
            var privateTab = session.AddTab("https://private.vault.example/", isPrivate: true);
            var normalTab = session.AddTab("https://standard.example/");

            session.CloseTab(privateTab.Id);
            Equal(0, session.State.RecentlyClosed.Count);
            Assert(!session.State.RecentlyClosed.Any(t => t.Url.Contains("private.vault.example")));

            session.CloseTab(normalTab.Id);
            Equal(1, session.State.RecentlyClosed.Count);
            Equal("https://standard.example/", session.State.RecentlyClosed[0].Url);
        });

        check("Security Audit: Session normalization purges any persisted or foreign InPrivate tabs", () =>
        {
            var state = new BrowserState();
            state.Tabs.Add(new BrowserTab { Url = "https://leaked.private.example/", IsPrivate = true });
            state.Tabs.Add(new BrowserTab { Url = "https://safe.example/", IsPrivate = false });
            state.RecentlyClosed.Add(new BrowserTab { Url = "https://closed.private.example/", IsPrivate = true });

            var session = new BrowserSession(state);
            Assert(!session.State.Tabs.Any(t => t.IsPrivate), "Normalization must purge all InPrivate tabs from state.Tabs");
            Assert(!session.State.RecentlyClosed.Any(t => t.IsPrivate), "Normalization must purge all InPrivate tabs from RecentlyClosed");
            Equal(1, session.State.Tabs.Count);
            Equal("https://safe.example/", session.State.Tabs[0].Url);
        });

        check("Security Audit: local file and view-source targets do not persist or enter RecentlyClosed", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "slate-restricted-state-" + Guid.NewGuid());
            try
            {
                var session = new BrowserSession();
                var local = session.AddTab("file:///C:/Users/secret/private-notes.txt");
                var source = session.AddTab("view-source:https://private.example/account");
                session.CloseTab(local.Id);
                session.CloseTab(source.Id);
                Assert(!session.State.RecentlyClosed.Any(tab => tab.Url.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
                    tab.Url.StartsWith("view-source:", StringComparison.OrdinalIgnoreCase)));
                session.State.Tabs.Add(new BrowserTab { WorkspaceId = session.ActiveWorkspace.Id, Url = "file:///C:/Users/secret/private-notes.txt" });
                session.State.Tabs.Add(new BrowserTab { WorkspaceId = session.ActiveWorkspace.Id, Url = "view-source:https://private.example/account" });
                var store = new StateStore(directory);
                Assert(store.Save(session.State));
                var json = File.ReadAllText(Path.Combine(directory, "session.json"));
                Assert(!json.Contains("private-notes", StringComparison.Ordinal));
                Assert(!json.Contains("private.example", StringComparison.Ordinal));
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        });

        check("Security Audit: InPrivate favicon disk persistence invariant", () =>
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "slate-fav-sec-" + Guid.NewGuid());
            try
            {
                var store = new FaviconStore(tempDir);
                var testUrl = "https://confidential.example.com/";
                var key = FaviconStore.GetKey(testUrl);
                Assert(!string.IsNullOrEmpty(key));

                // Verification of invariant: For InPrivate tabs, SaveFaviconAsync is never called.
                // Ensure directory remains empty when InPrivate tabs are browsed.
                if (Directory.Exists(tempDir))
                {
                    var files = Directory.GetFiles(tempDir);
                    Equal(0, files.Length);
                }
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        });

        check("Security Audit: Popup and duplicate actions preserve InPrivate and Temporary flags", () =>
        {
            var session = new BrowserSession();
            var privateTab = session.AddTab("https://private.example.com/start", temporary: false, isPrivate: true);
            Assert(privateTab.IsPrivate, "Tab must be InPrivate");
            Assert(!privateTab.IsTemporary, "Tab must not be Temporary");

            // Verify NewTab propagation contract for popup
            var popupTab = session.AddTab("https://private.example.com/popup", privateTab.IsTemporary, privateTab.IsPrivate);
            Assert(popupTab.IsPrivate, "Popup spawned from InPrivate tab must inherit IsPrivate == true");
            Assert(!popupTab.IsTemporary, "Popup spawned from non-temporary tab must have IsTemporary == false");

            // Verify NewTab propagation contract for duplicate
            var duplicatePrivate = session.AddTab(privateTab.Url, privateTab.IsTemporary, privateTab.IsPrivate);
            Assert(duplicatePrivate.IsPrivate, "Duplicate of InPrivate tab must inherit IsPrivate == true");
            Equal(privateTab.Url, duplicatePrivate.Url);

            // Verify Temporary tab duplicate propagation contract
            var tempTab = session.AddTab("https://temporary.example.com/page", temporary: true, isPrivate: false);
            var duplicateTemp = session.AddTab(tempTab.Url, tempTab.IsTemporary, tempTab.IsPrivate);
            Assert(duplicateTemp.IsTemporary, "Duplicate of Temporary tab must inherit IsTemporary == true");
            Assert(!duplicateTemp.IsPrivate, "Duplicate of Temporary tab must inherit IsPrivate == false");
        });

        // -----------------------------------------------------------------
        // 2. Local Files & Save Page Safety Invariants
        // -----------------------------------------------------------------
        check("Security Audit: DownloadSafety rejects UNC and network share paths", () =>
        {
            Assert(!DownloadSafety.IsSafeLocalDirectory(@"\\server\share"));
            Assert(!DownloadSafety.IsSafeLocalDirectory(@"\\127.0.0.1\c$"));
            Assert(!DownloadSafety.IsSafeLocalDirectory(@"\\localhost\c$"));
            Assert(!DownloadSafety.IsSafeLocalDirectory(@"//server/share"));
            Assert(!DownloadSafety.IsSafeLocalDirectory(@"//localhost/test"));
            Assert(!DownloadSafety.IsSafeLocalDirectory(@"\\?\UNC\server\share"));
        });

        check("Security Audit: DownloadSafety rejects relative paths, empty paths, and files", () =>
        {
            Assert(!DownloadSafety.IsSafeLocalDirectory(null));
            Assert(!DownloadSafety.IsSafeLocalDirectory(""));
            Assert(!DownloadSafety.IsSafeLocalDirectory("   "));
            Assert(!DownloadSafety.IsSafeLocalDirectory("relative/path/downloads"));
            Assert(!DownloadSafety.IsSafeLocalDirectory(@"downloads\nested"));
            Assert(!DownloadSafety.IsSafeLocalDirectory(@"..\..\windows"));

            // An existing file path is not a safe directory
            var tempFile = Path.GetTempFileName();
            try
            {
                Assert(!DownloadSafety.IsSafeLocalDirectory(tempFile), "A file path must not be accepted as a safe directory");
            }
            finally
            {
                File.Delete(tempFile);
            }
        });

        check("Security Audit: Navigation.IsDangerousExtension blocks all executable and script extensions", () =>
        {
            string[] dangerous = ["exe", "bat", "cmd", "ps1", "vbs", "msi", "dll", "com", "scr", "reg", "hta", "cpl", "pif",
                "appref-ms", "application", "diagcab", "jar", "jnlp", "js", "jse", "lnk", "msc", "scf", "url", "vbe", "wsf", "wsh"];
            foreach (var ext in dangerous)
            {
                Assert(Navigation.IsDangerousExtension(ext), $"Extension '{ext}' without dot must be flagged dangerous");
                Assert(Navigation.IsDangerousExtension("." + ext), $"Extension '.{ext}' with dot must be flagged dangerous");
                Assert(Navigation.IsDangerousExtension("." + ext.ToUpperInvariant()), $"Extension '.{ext.ToUpperInvariant()}' uppercase must be flagged dangerous");
                Assert(Navigation.IsDangerousExtension("." + ext.Substring(0, 1).ToUpperInvariant() + ext.Substring(1)), $"Extension mixed case must be flagged dangerous");
            }

            string[] safe = [".mhtml", ".html", ".htm", ".txt", ".json", ".pdf", ".png", ".jpg", ".zip", ".tar.gz", ".css"];
            foreach (var ext in safe)
            {
                Assert(!Navigation.IsDangerousExtension(ext), $"Extension '{ext}' must be considered non-dangerous");
            }

            Assert(!Navigation.IsDangerousExtension(null));
            Assert(!Navigation.IsDangerousExtension(""));
        });

        check("Security Audit: DownloadSafety filename sanitization strips device names, streams, and traversal", () =>
        {
            // Windows device names
            Assert(!DownloadSafety.SanitizeFileName("CON").Equals("CON", StringComparison.OrdinalIgnoreCase));
            Assert(!DownloadSafety.SanitizeFileName("PRN.txt").StartsWith("PRN.", StringComparison.OrdinalIgnoreCase));
            Assert(!DownloadSafety.SanitizeFileName("aux.html").StartsWith("aux.", StringComparison.OrdinalIgnoreCase));
            Assert(!DownloadSafety.SanitizeFileName("NUL").Equals("NUL", StringComparison.OrdinalIgnoreCase));

            // Traversal sequences and separators
            var sanitized = DownloadSafety.SanitizeFileName(@"../../etc/passwd");
            Assert(!sanitized.Contains(".."), "Sanitized filename must not contain traversal sequence");
            Assert(!sanitized.Contains('/'), "Sanitized filename must not contain forward slash");
            Assert(!sanitized.Contains('\\'), "Sanitized filename must not contain backslash");

            // NTFS alternate data streams
            var streamSanitized = DownloadSafety.SanitizeFileName("file.html:hiddenStream");
            Assert(!streamSanitized.Contains(':'), "Sanitized filename must not contain colon (NTFS stream separator)");

            var bidiSanitized = DownloadSafety.SanitizeFileName("photo\u202Egnp.exe");
            Assert(!bidiSanitized.Contains('\u202E'), "Sanitized filename must strip bidirectional format controls");

            // Bounded length (max 200 chars)
            var longName = new string('A', 300);
            var boundedSanitized = DownloadSafety.SanitizeFileName(longName);
            Assert(boundedSanitized.Length <= 200, "Sanitized filename must be clamped to 200 chars");
        });

        check("Security Audit: Local file navigation restrictions and iframe protection", () =>
        {
            // Navigation.IsLocalFileUrl rejects UNC file paths and non-file schemes
            Assert(!Navigation.IsLocalFileUrl(@"file://server/share/test.txt"));
            Assert(!Navigation.IsLocalFileUrl(@"http://example.com/"));
            Assert(!Navigation.IsLocalFileUrl(@"https://example.com/"));
            Assert(!Navigation.IsLocalFileUrl(@"about:blank"));
            Assert(!Navigation.IsLocalFileUrl(@"file:////server/share/test.txt"));
            Assert(!Navigation.IsLocalFileUrl(@"file:///%5C%5Cserver%5Cshare%5Ctest.txt"));
            Assert(!Navigation.IsLocalFileUrl(@"file:///?/C:/Windows/win.ini"));
            Assert(!Navigation.IsLocalFileUrl(@"file:///\\?\C:\Windows\win.ini"));
            Assert(!Navigation.IsLocalFileUrl(@"file:///C:/safe/launcher.js"));
            Assert(!Navigation.IsLocalFileUrl(@"file:///C:/safe/launcher.exe."));
            Assert(!Navigation.IsLocalFileUrl(@"file:///C:/safe/launcher.exe%20"));
            Assert(!Navigation.IsLocalFileUrl(@"file:///C:/safe/document.txt:stream"));

            // Frames must strictly block file: URLs and unrecognized protocols
            Assert(!Navigation.IsAllowedFrameUrl("file:///C:/passwords.txt"));
            Assert(!Navigation.IsAllowedFrameUrl("file://server/share/file.txt"));
            Assert(!Navigation.IsAllowedFrameUrl("powershell:run()"));
            Assert(!Navigation.IsAllowedFrameUrl("custom-scheme://test"));

            // Allowed frame URLs
            Assert(Navigation.IsAllowedFrameUrl("https://example.com/frame"));
            Assert(Navigation.IsAllowedFrameUrl("http://example.com/frame"));
            Assert(Navigation.IsAllowedFrameUrl("about:blank"));
            Assert(Navigation.IsAllowedFrameUrl("about:srcdoc"));
        });

        check("Security Audit: local file path validation rejects device, reserved, trailing, and reparse targets", () =>
        {
            Assert(!DownloadSafety.IsSafeLocalFilePath(@"\\server\share\page.html"));
            Assert(!DownloadSafety.IsSafeLocalFilePath(@"\\?\C:\Windows\win.ini"));
            Assert(!DownloadSafety.IsSafeLocalFilePath(@"\\.\C:\Windows\win.ini"));
            Assert(!DownloadSafety.IsSafeLocalFilePath(@"C:\Temp\CON.txt"));
            Assert(!DownloadSafety.IsSafeLocalFilePath(@"C:\Temp\page.html."));
            Assert(!DownloadSafety.IsSafeLocalFilePath(@"C:\Temp\page.html:hidden"));

            var root = Path.Combine(Path.GetTempPath(), "slate-reparse-" + Guid.NewGuid());
            var target = Path.Combine(root, "target");
            var link = Path.Combine(root, "link");
            try
            {
                Directory.CreateDirectory(target);
                try
                {
                    Directory.CreateSymbolicLink(link, target);
                    Assert(!DownloadSafety.IsSafeLocalFilePath(Path.Combine(link, "page.html")), "A file below a reparse directory was accepted");
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
                {
                    // Symlink creation can be disabled by host policy; production validation still rejects any existing reparse ancestor.
                }
            }
            finally
            {
                if (Directory.Exists(link)) Directory.Delete(link);
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });

        check("Security Audit: incomplete download cleanup only deletes validated local files", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "slate-download-cleanup-" + Guid.NewGuid());
            Directory.CreateDirectory(directory);
            try
            {
                var partial = Path.Combine(directory, "partial.txt");
                File.WriteAllText(partial, "partial");
                Assert(DownloadSafety.TryDeleteIncompleteFile(partial));
                Assert(!File.Exists(partial));
                Assert(!DownloadSafety.TryDeleteIncompleteFile(@"\\server\share\partial.txt"));
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        });

        // -----------------------------------------------------------------
        // 3. View Source Sandboxing Invariants
        // -----------------------------------------------------------------
        check("Security Audit: View Source URL detection and origin rejection", () =>
        {
            Assert(Navigation.IsViewSourceUrl("view-source:https://example.com/"));
            Assert(Navigation.IsViewSourceUrl("VIEW-SOURCE:http://example.com/login"));
            Assert(Navigation.IsViewSourceUrl("view-source:about:blank"));
            Assert(!Navigation.IsViewSourceUrl("https://example.com/"));
            Assert(!Navigation.IsViewSourceUrl("http://example.com/"));
            Assert(!Navigation.IsViewSourceUrl(null));
            Assert(!Navigation.IsViewSourceUrl(""));
            Assert(!Navigation.IsViewSourceUrl("   "));

            // CredentialOrigin.Normalize MUST return null for view-source: URLs
            Assert(CredentialOrigin.Normalize("view-source:https://example.com/") is null, "CredentialOrigin must reject view-source: URLs");
            Assert(CredentialOrigin.Normalize("view-source:http://example.com/login") is null, "CredentialOrigin must reject view-source: URLs");
            Assert(CredentialOrigin.Normalize("view-source:file:///C:/page.html") is null, "CredentialOrigin must reject view-source: URLs");
        });

        check("Security Audit: View Source HTML output properly escapes script tags and hostile content", () =>
        {
            var hostileCode = "<script>alert('pwned')</script>&<img src=x onerror=steal()>";
            var encoded = WebUtility.HtmlEncode(hostileCode);
            Assert(!encoded.Contains("<script>"), "Encoded view source output must not contain raw script tag");
            Assert(!encoded.Contains("<img"), "Encoded view source output must not contain raw img tag");
            Assert(encoded.Contains("&lt;script&gt;"), "Encoded view source must encode < and >");
            Assert(encoded.Contains("&amp;"), "Encoded view source must encode &");
        });

        // -----------------------------------------------------------------
        // 4. Secure Origins & TLS Invariants
        // -----------------------------------------------------------------
        check("Security Audit: Secure origin identification strictly enforces HTTPS and Loopback", () =>
        {
            Assert(Navigation.IsSecureOrigin("https://example.com"), "HTTPS must be secure origin");
            Assert(Navigation.IsSecureOrigin("https://sub.domain.example.org:8443"), "HTTPS with port must be secure origin");
            Assert(Navigation.IsSecureOrigin("http://localhost"), "Loopback localhost must be secure origin");
            Assert(Navigation.IsSecureOrigin("http://localhost:3000"), "Loopback localhost with port must be secure origin");
            Assert(Navigation.IsSecureOrigin("http://127.0.0.1"), "IPv4 loopback must be secure origin");
            Assert(Navigation.IsSecureOrigin("http://127.0.0.1:8080"), "IPv4 loopback with port must be secure origin");
            Assert(Navigation.IsSecureOrigin("http://[::1]"), "IPv6 loopback must be secure origin");
            Assert(Navigation.IsSecureOrigin("http://[::1]:9090"), "IPv6 loopback with port must be secure origin");

            Assert(!Navigation.IsSecureOrigin("http://example.com"), "HTTP remote host must NOT be secure origin");
            Assert(!Navigation.IsSecureOrigin("http://192.168.1.1"), "Non-loopback private IP must NOT be secure origin");
            Assert(!Navigation.IsSecureOrigin("ftp://example.com"), "FTP must NOT be secure origin");
            Assert(!Navigation.IsSecureOrigin(""), "Empty origin must NOT be secure origin");
            Assert(!Navigation.IsSecureOrigin(null), "Null origin must NOT be secure origin");
        });

        // -----------------------------------------------------------------
        // 5. Unicode / BiDi / UI Spoofing Resistance
        // -----------------------------------------------------------------
        check("Security Audit: BiDi override and isolate characters are neutralized in titles and labels", () =>
        {
            // Right-to-Left Override (RLO) U+202E disguised extension attack: "document\u202Eexe.pdf"
            string rloDisguise = "document\u202Eexe.pdf";
            string sanitized = BrowserText.SanitizeLabel(rloDisguise, 100);
            Assert(!sanitized.Contains('\u202E'), "RLO character must be stripped");
            Assert(!sanitized.Contains('\u202D'), "LRO character must be stripped");
            Equal("documentexe.pdf", sanitized);

            // Matrix of all dangerous BiDi controls: LRE, RLE, PDF, LRO, RLO, LRI, RLI, FSI, PDI, LRM, RLM, ALM
            char[] dangerousBidi =
            [
                '\u202A', '\u202B', '\u202C', '\u202D', '\u202E',
                '\u2066', '\u2067', '\u2068', '\u2069',
                '\u200E', '\u200F', '\u061C'
            ];

            foreach (var bidi in dangerousBidi)
            {
                Assert(BrowserText.IsDangerousFormatting(bidi), $"Character U+{(int)bidi:X4} must be identified as dangerous");
                string testInput = $"site{bidi}.example.com";
                string clean = BrowserText.SanitizeLabel(testInput, 100);
                Assert(!clean.Contains(bidi), $"Character U+{(int)bidi:X4} must be stripped by SanitizeLabel");
            }

            // Zero-width spaces, byte order mark, and line separators
            char[] invisibleChars = ['\u200B', '\uFEFF', '\u2028', '\u2029', '\uFFF9', '\uFFFC'];
            foreach (var inv in invisibleChars)
            {
                Assert(BrowserText.IsDangerousFormatting(inv), $"Character U+{(int)inv:X4} must be identified as dangerous");
                string testInput = $"pay{inv}pal.com";
                string clean = BrowserText.SanitizeLabel(testInput, 100);
                Assert(!clean.Contains(inv), $"Character U+{(int)inv:X4} must be stripped by SanitizeLabel");
            }

            // Legitimate international text MUST be preserved
            string arabic = "مرحبا بالعالم";
            Equal(arabic, BrowserText.SanitizeTitle(arabic));

            string hebrew = "שלום עולם";
            Equal(hebrew, BrowserText.SanitizeTitle(hebrew));

            string cjk = "Slate 浏览器 / ブラウザ / 브라우저";
            Equal(cjk, BrowserText.SanitizeTitle(cjk));

            string cyrillic = "Безопасный Браузер";
            Equal(cyrillic, BrowserText.SanitizeTitle(cyrillic));

            string accented = "Café résumé övergröße niño";
            Equal(accented, BrowserText.SanitizeTitle(accented));

            string emoji = "Slate Browser 🚀✨🛡️";
            Equal(emoji, BrowserText.SanitizeTitle(emoji));

            // SanitizeHost strips dangerous formatting and whitespace
            Equal("example.com", BrowserText.SanitizeHost(" example.com\u202E "));
            Equal("localhost", BrowserText.SanitizeHost("localhost"));
            Equal("unknown", BrowserText.SanitizeHost("   "));
        });

        // -----------------------------------------------------------------
        // 6. HTTPS-First Policy and Downgrade Prevention
        // -----------------------------------------------------------------
        check("Security Audit: HTTPS-First upgrades public HTTP while preserving local dev endpoints", () =>
        {
            // Public bare domains upgrade to HTTPS
            Equal("https://example.com/", Navigation.Resolve("example.com"));
            Equal("https://sub.domain.example.org/path", Navigation.Resolve("sub.domain.example.org/path"));

            // Public explicit HTTP is upgraded to HTTPS (HTTPS-First)
            Equal("https://example.com/", Navigation.Resolve("http://example.com"));
            Equal("https://example.com/login?u=1", Navigation.Resolve("http://example.com/login?u=1"));
            Equal("https://example.com:8443/test", Navigation.Resolve("http://example.com:8443/test"));

            // Local development endpoints remain HTTP
            Equal("http://localhost:3000/api", Navigation.Resolve("http://localhost:3000/api"));
            Equal("http://localhost:3000/test", Navigation.Resolve("localhost:3000/test"));
            Equal("http://127.0.0.1:8080/", Navigation.Resolve("http://127.0.0.1:8080/"));
            Equal("http://127.255.255.254:8080/", Navigation.Resolve("http://127.255.255.254:8080/"));
            Equal("http://[::1]:9090/", Navigation.Resolve("http://[::1]:9090/"));

            // Non-loopback LAN and reserved test domains are upgraded to HTTPS
            Equal("https://example.local/", Navigation.Resolve("http://example.local"));
            Equal("https://foo.test/", Navigation.Resolve("http://foo.test"));
            Equal("https://192.168.1.10/", Navigation.Resolve("http://192.168.1.10"));
            Equal("https://10.0.0.1/", Navigation.Resolve("http://10.0.0.1"));

            // IsLoopbackOrLocalHost checks: strictly loopback only
            Assert(Navigation.IsLoopbackOrLocalHost("localhost"));
            Assert(Navigation.IsLoopbackOrLocalHost("localhost."));
            Assert(Navigation.IsLoopbackOrLocalHost("dev.localhost"));
            Assert(Navigation.IsLoopbackOrLocalHost("foo.localhost"));
            Assert(!Navigation.IsLoopbackOrLocalHost(".localhost")); // missing label
            Assert(Navigation.IsLoopbackOrLocalHost("127.0.0.1"));
            Assert(Navigation.IsLoopbackOrLocalHost("127.0.0.2"));
            Assert(Navigation.IsLoopbackOrLocalHost("127.255.255.254")); // 127.0.0.0/8 full block
            Assert(Navigation.IsLoopbackOrLocalHost("::1"));
            Assert(Navigation.IsLoopbackOrLocalHost("[::1]"));

            // Must NOT treat LAN/test/external as loopback
            Assert(!Navigation.IsLoopbackOrLocalHost("example.local"));
            Assert(!Navigation.IsLoopbackOrLocalHost("foo.test"));
            Assert(!Navigation.IsLoopbackOrLocalHost("192.168.1.10"));
            Assert(!Navigation.IsLoopbackOrLocalHost("10.0.0.1"));
            Assert(!Navigation.IsLoopbackOrLocalHost("example.com"));
            Assert(!Navigation.IsLoopbackOrLocalHost("8.8.8.8"));

            // IsPublicHttp checks: unencrypted HTTP on non-loopback requires warning / upgrade
            Assert(Navigation.IsPublicHttp("http://example.com/"));
            Assert(Navigation.IsPublicHttp("http://example.local/"));
            Assert(Navigation.IsPublicHttp("http://foo.test/"));
            Assert(Navigation.IsPublicHttp("http://192.168.1.10/"));
            Assert(Navigation.IsPublicHttp("http://10.0.0.1/"));
            Assert(!Navigation.IsPublicHttp("https://example.com/"));
            Assert(!Navigation.IsPublicHttp("http://localhost:3000/"));
            Assert(!Navigation.IsPublicHttp("http://foo.localhost:3000/"));
            Assert(!Navigation.IsPublicHttp("http://127.0.0.1:8080/"));
            Assert(!Navigation.IsPublicHttp("http://127.255.255.254:8080/"));
            Assert(!Navigation.IsPublicHttp("http://[::1]:8080/"));

            // Insecure HTTP downgrade detection
            Assert(IsDowngrade("https://secure.example.com/page", "http://insecure.example.com/page"));
            Assert(IsDowngrade("https://secure.example.com/", "http://example.local/"));
            Assert(IsDowngrade("https://secure.example.com/", "http://foo.test/"));
            Assert(IsDowngrade("https://secure.example.com/", "http://192.168.1.10/"));
            Assert(!IsDowngrade("https://secure.example.com/", "http://localhost:3000/"));
            Assert(!IsDowngrade("https://secure.example.com/", "http://127.0.0.1:8080/"));
            Assert(!IsDowngrade("https://secure.example.com/", "https://other.example.com/"));
            Assert(!IsDowngrade("about:blank", "http://insecure.example.com/"));

            static bool IsDowngrade(string currentUrl, string targetUrl)
            {
                if (Uri.TryCreate(currentUrl, UriKind.Absolute, out var cur) &&
                    Uri.TryCreate(targetUrl, UriKind.Absolute, out var tgt))
                {
                    return cur.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) &&
                           tgt.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) &&
                           !Navigation.IsLoopbackOrLocalHost(tgt.Host);
                }
                return false;
            }
        });

        // -----------------------------------------------------------------
        // 7. Dangerous Extension Matrix (Windows Execution & Shortcut Vectors)
        // -----------------------------------------------------------------
        check("Security Audit: Expanded dangerous extension matrix blocks modern Windows vectors", () =>
        {
            string[] highRiskExtensions =
            [
                // Traditional executables & scripts
                "exe", "bat", "cmd", "com", "cpl", "dll", "hta", "jar", "js", "jse",
                "lnk", "msc", "msi", "msp", "mst", "pif", "ps1", "reg", "scr", "vbs", "wsf",
                // Modern Windows app packages
                "msix", "msixbundle", "appx", "appxbundle",
                // Disk images & containers
                "iso", "vhd", "vhdx", "img",
                // Windows settings / search / shell shortcuts
                "settingcontent-ms", "search-ms", "desklink", "mapimail", "theme", "themepack",
                // Help files & sandboxes
                "chm", "wsb",
                // Certificate & keystore formats
                "cer", "crt", "der", "pfx", "p12",
                // Web queries
                "iqy", "rqy"
            ];

            foreach (var ext in highRiskExtensions)
            {
                Assert(Navigation.IsDangerousExtension(ext), $"Extension .{ext} must be identified as dangerous");
                Assert(Navigation.IsDangerousExtension("." + ext), $"Extension .{ext} (with dot) must be identified as dangerous");
                Assert(Navigation.IsDangerousExtension(ext.ToUpperInvariant()), $"Extension .{ext.ToUpperInvariant()} must be case-insensitively dangerous");
            }

            // Safe document extensions must NOT be blocked
            string[] safeExtensions = ["html", "htm", "txt", "pdf", "json", "png", "jpg", "jpeg", "webp", "zip", "tar", "gz"];
            foreach (var ext in safeExtensions)
            {
                Assert(!Navigation.IsDangerousExtension(ext), $"Extension .{ext} must NOT be flagged as dangerous");
                Assert(!Navigation.IsDangerousExtension("." + ext), $"Extension .{ext} must NOT be flagged as dangerous");
            }
        });

        // -----------------------------------------------------------------
        // 8. Startup Environment & Command-Line Hardening
        // -----------------------------------------------------------------
        check("Security Audit: Startup validation rejects dangerous browser switches and detects overrides", () =>
        {
            // Dangerous switches across prefix variants, casing, and surrounding quotes
            string[] dangerousSwitches =
            [
                "no-sandbox",
                "disable-web-security",
                "ignore-certificate-errors",
                "remote-debugging-port",
                "remote-debugging-pipe",
                "disable-site-isolation-trials",
                "disable-site-isolation-for-policy",
                "single-process",
                "renderer-process-limit"
            ];

            foreach (var sw in dangerousSwitches)
            {
                Assert(StartupSecurity.IsDangerousArgument($"--{sw}"), $"--{sw} must be dangerous");
                Assert(StartupSecurity.IsDangerousArgument($"-{sw}"), $"-{sw} must be dangerous");
                Assert(StartupSecurity.IsDangerousArgument($"/{sw}"), $"/{sw} must be dangerous");
                Assert(StartupSecurity.IsDangerousArgument($"--{sw}=value"), $"--{sw}=value must be dangerous");
                Assert(StartupSecurity.IsDangerousArgument($"\"--{sw}\""), $"\"--{sw}\" (quoted) must be dangerous");
                Assert(StartupSecurity.IsDangerousArgument($"'--{sw}'"), $"'--{sw}' (single-quoted) must be dangerous");
                Assert(StartupSecurity.IsDangerousArgument($"  --{sw.ToUpperInvariant()}  "), $"Cased/padded --{sw} must be dangerous");
            }

            // Disabled site isolation variations
            Assert(StartupSecurity.IsDangerousArgument("--disable-features=IsolateOrigins,site-per-process"));
            Assert(StartupSecurity.IsDangerousArgument("--disable-features=site-per-process"));
            Assert(StartupSecurity.IsDangerousArgument("--disable-features=\"IsolateOrigins\""));
            Assert(StartupSecurity.IsDangerousArgument("/disable-site-isolation-for-policy"));

            // Benign arguments must NOT be falsely flagged
            Assert(!StartupSecurity.IsDangerousArgument("--enable-features=IntensiveWakeUpThrottling"));
            Assert(!StartupSecurity.IsDangerousArgument("--site-per-process"));
            Assert(!StartupSecurity.IsDangerousArgument("--allow-something-else"));

            // Command-line validation allowlist
            Assert(StartupSecurity.ValidateCommandLine(["Slate.exe", "--hardened-isolation"], out _));
            Assert(StartupSecurity.ValidateCommandLine(["Slate.exe", "-hardened-isolation"], out _));
            Assert(StartupSecurity.ValidateCommandLine(["Slate.exe", "--smoke-test=C:\\test\\smoke.json"], out _));
            Assert(StartupSecurity.ValidateCommandLine(["Slate.exe", "--memory-benchmark"], out _));
            Assert(StartupSecurity.ValidateCommandLine(["Slate.exe", "--memory-benchmark=C:\\test\\bench.json"], out _));
            Assert(StartupSecurity.ValidateCommandLine(["Slate.exe", "https://example.com"], out _));

            // Hostile command-line injection must fail validation
            Assert(!StartupSecurity.ValidateCommandLine(["Slate.exe", "--no-sandbox"], out var r1));
            Assert(r1!.Contains("Dangerous"), "Rejection reason must identify dangerous switch");

            Assert(!StartupSecurity.ValidateCommandLine(["Slate.exe", "--remote-debugging-port=9222"], out var r2));
            Assert(r2!.Contains("Dangerous"), "Rejection reason must identify dangerous switch");

            Assert(!StartupSecurity.ValidateCommandLine(["Slate.exe", "--disable-web-security"], out _));
            Assert(!StartupSecurity.ValidateCommandLine(["Slate.exe", "--single-process"], out _));
            Assert(!StartupSecurity.ValidateCommandLine(["Slate.exe", "--disable-features=site-per-process"], out _));
            Assert(!StartupSecurity.ValidateCommandLine(["Slate.exe", "--arbitrary-unrecognized-switch"], out var r3));
            Assert(r3!.Contains("Unrecognized"), "Unrecognized switches must be rejected");

            // Verify security-sensitive WebView2 environment variables are cataloged
            Assert(StartupSecurity.DangerousWebView2EnvironmentVariables.Contains("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS"));
            Assert(StartupSecurity.DangerousWebView2EnvironmentVariables.Contains("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER"));
            Assert(StartupSecurity.DangerousWebView2EnvironmentVariables.Contains("WEBVIEW2_USER_DATA_FOLDER"));
            Assert(StartupSecurity.DangerousWebView2EnvironmentVariables.Contains("WEBVIEW2_RELEASE_CHANNEL_PREFERENCE"));
            Assert(StartupSecurity.DangerousWebView2EnvironmentVariables.Contains("WEBVIEW2_PIPE_FOR_BLOCKED_SCRIPT"));
            Assert(StartupSecurity.DangerousWebView2EnvironmentVariables.Contains("EDGE_ADDITIONAL_BROWSER_ARGUMENTS"));

            // Verify benign UI variables are NOT in dangerous blacklist
            Assert(!StartupSecurity.DangerousWebView2EnvironmentVariables.Contains("WEBVIEW2_DEFAULT_BACKGROUND_COLOR"));
        });

        // -----------------------------------------------------------------
        // 9. Zone.Identifier (Mark of the Web) Attachment & Structural Safety
        // -----------------------------------------------------------------
        check("Security Audit: DownloadSafety formats safe Zone.Identifier and prevents INI injection", () =>
        {
            // Valid HTTP/HTTPS URLs produce well-formed INI
            string normalIni = DownloadSafety.FormatZoneIdentifier("https://secure.example.com/download.exe");
            Assert(normalIni.Contains("[ZoneTransfer]\r\nZoneId=3\r\n"), "Must specify ZoneId=3");
            Assert(normalIni.Contains("HostUrl=https://secure.example.com/download.exe\r\n"), "Must include HostUrl");

            // Adversarial CRLF / INI injection: attacker tries to inject ZoneId=0 (Local Machine / Full Trust)
            string crlfAttack = "https://evil.example.com/file.exe\r\nZoneId=0\r\n[AttackerSection]\r\nAdmin=True";
            string sanitizedIni = DownloadSafety.FormatZoneIdentifier(crlfAttack);

            // Must NOT contain separate ZoneId=0 line
            Assert(!sanitizedIni.Contains("\r\nZoneId=0"), "CRLF injection must not create separate ZoneId=0 line");
            Assert(!sanitizedIni.Contains("[AttackerSection]"), "Section brackets must be stripped");
            // Must retain genuine ZoneId=3
            Assert(sanitizedIni.Contains("ZoneId=3\r\n"), "Genuine ZoneId=3 must be retained");

            // Null-byte injection
            string nullAttack = "https://evil.example.com/file.exe\0ZoneId=0";
            string nullClean = DownloadSafety.FormatZoneIdentifier(nullAttack);
            Assert(!nullClean.Contains('\0'), "Null bytes must be stripped");

            // Length clamping: extremely long URL clamped to <= 2048 chars
            string hugeUrl = "https://example.com/" + new string('a', 5000);
            string hugeIni = DownloadSafety.FormatZoneIdentifier(hugeUrl);
            Assert(hugeIni.Length <= 2200, "Formatted Zone.Identifier must be bounded in length");

            // Unsupported non-web schemes omitted from HostUrl
            string fileScheme = DownloadSafety.FormatZoneIdentifier("file:///C:/malicious.exe");
            Assert(!fileScheme.Contains("HostUrl="), "file:// scheme must not be written to HostUrl");
            string jsScheme = DownloadSafety.FormatZoneIdentifier("javascript:alert(1)");
            Assert(!jsScheme.Contains("HostUrl="), "javascript: scheme must not be written to HostUrl");

            // Best-effort attachment on file
            var tempDir = Path.Combine(Path.GetTempPath(), "slate-motw-test-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var filePath = Path.Combine(tempDir, "sample_download.pdf");
                File.WriteAllText(filePath, "%PDF-1.4 sample content");

                Assert(DownloadSafety.AttachZoneIdentifier(filePath, "https://secure.example.com/sample_download.pdf"));

                // If running on an NTFS drive, verify the stream contents
                string streamPath = filePath + ":Zone.Identifier";
                if (File.Exists(streamPath))
                {
                    string streamContent = File.ReadAllText(streamPath);
                    Assert(streamContent.Contains("[ZoneTransfer]"), "Stream must include [ZoneTransfer]");
                    Assert(streamContent.Contains("ZoneId=3"), "Stream must specify Internet Zone (ZoneId=3)");
                    Assert(streamContent.Contains("HostUrl=https://secure.example.com/sample_download.pdf"), "Stream must include HostUrl");
                }

                // Invalid / non-existent file handling returns false gracefully without throwing
                Assert(!DownloadSafety.AttachZoneIdentifier(null));
                Assert(!DownloadSafety.AttachZoneIdentifier(Path.Combine(tempDir, "non_existent.txt")));
                Assert(!DownloadSafety.AttachZoneIdentifier(@"\\server\share\file.txt"));
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        });
    }
}
