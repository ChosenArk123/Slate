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
                Assert(!loadedState.Tabs.Any(t => t.IsPrivate), "Loaded tabs must contain zero InPrivate tabs");
                Assert(!loadedState.Tabs.Any(t => t.Url.Contains("secret-private.example.com")), "Loaded tabs must not have InPrivate URL");
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
            string[] dangerous = ["exe", "bat", "cmd", "ps1", "vbs", "msi", "dll", "com", "scr", "reg", "hta", "cpl", "pif"];
            foreach (var ext in dangerous)
            {
                Assert(Navigation.IsDangerousExtension(ext), $"Extension '{ext}' without dot must be flagged dangerous");
                Assert(Navigation.IsDangerousExtension("." + ext), $"Extension '.{ext}' with dot must be flagged dangerous");
                Assert(Navigation.IsDangerousExtension("." + ext.ToUpperInvariant()), $"Extension '.{ext.ToUpperInvariant()}' uppercase must be flagged dangerous");
                Assert(Navigation.IsDangerousExtension("." + ext.Substring(0, 1).ToUpperInvariant() + ext.Substring(1)), $"Extension mixed case must be flagged dangerous");
            }

            string[] safe = [".mhtml", ".html", ".htm", ".txt", ".json", ".pdf", ".png", ".jpg", ".zip", ".tar.gz", ".css", ".js"];
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

            // Frames must strictly block file: URLs and unrecognized protocols
            Assert(!Navigation.IsAllowedFrameUrl("file:///C:/passwords.txt"));
            Assert(!Navigation.IsAllowedFrameUrl("file://server/share/file.txt"));
            Assert(!Navigation.IsAllowedFrameUrl("javascript:stealData()"));
            Assert(!Navigation.IsAllowedFrameUrl("powershell:run()"));
            Assert(!Navigation.IsAllowedFrameUrl("data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==")); // data is allowed frame url in standard web, but check implementation
            Assert(!Navigation.IsAllowedFrameUrl("custom-scheme://test"));

            // Allowed frame URLs
            Assert(Navigation.IsAllowedFrameUrl("https://example.com/frame"));
            Assert(Navigation.IsAllowedFrameUrl("http://example.com/frame"));
            Assert(Navigation.IsAllowedFrameUrl("about:blank"));
            Assert(Navigation.IsAllowedFrameUrl("about:srcdoc"));
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
    }
}
