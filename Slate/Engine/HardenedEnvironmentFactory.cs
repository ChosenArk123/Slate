using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace Slate.Engine;

public static class HardenedEnvironmentFactory
{
    /// <summary>
    /// Creates a hardened CoreWebView2Environment enforcing strict process isolation,
    /// aggressive background throttling, and privacy protections without undermining
    /// Chromium/WebView2 security mechanisms.
    /// </summary>
    public static async Task<CoreWebView2Environment> CreateHardenedEnvironmentAsync(string userDataFolder, bool jitless = false)
    {
        var argsList = new System.Collections.Generic.List<string>
        {
            // =========================================================================
            // Taxonomy: security-critical
            // =========================================================================
            // Enforces physical OS process boundaries per origin (out-of-process iframes / Spectre mitigation)
            "--site-per-process",

            // =========================================================================
            // Taxonomy: privacy-oriented
            // =========================================================================
            // Prevents WebRTC from enumerating and leaking private LAN IPv4/IPv6 interfaces to remote sites
            "--force-webrtc-ip-handling-policy=default_public_interface_only",

            // Restricts cross-tab shared state and background communication channels
            "--disable-shared-workers",

            // =========================================================================
            // Taxonomy: gaming-performance-oriented
            // =========================================================================
            // Throttles JavaScript timer wakeups in background/hidden tabs to at most 1 per minute
            "--enable-features=IntensiveWakeUpThrottling,QuickIntensiveWakeUpThrottlingAfterLoading",

            // Explicitly ensures background timer throttling remains active
            "--disable-background-timer-throttling=false",

            // Allows the OS scheduler to deprioritize hidden renderer processes
            "--disable-renderer-backgrounding=false",

            // Disables profile synchronization infrastructure not utilized by Slate
            "--disable-sync",

            // Disables Chromium's external crash reporting subprocess (Slate handles crash logs locally)
            "--disable-breakpad",

            // Disables external network health telemetry reporting
            "--disable-domain-reliability"

            // NOTE ON REMOVED / UNSUPPORTED FLAGS:
            // 1. '--disable-background-networking' was REMOVED because it disables Chromium Safe Browsing
            //    updates, CRLSet certificate revocation checks, and captive portal detection.
            // 2. '--disable-component-update' was REMOVED because it halts emergency CRLSet revocation
            //    lists and security component updates.
            // 3. '--https-only-mode' was REMOVED from the command line because WebView2 lacks Chrome's
            //    native interstitial fallback UI; Slate enforces its own coherent HTTPS-First policy instead.
            // 4. '--disable-reading-from-canvas' was REMOVED because it is not a valid or supported Chromium switch.
        };

        // Optional V8 Attack Surface Reduction (JIT-less Mode)
        // Disables TurboFan and Maglev JIT compilers to eliminate JIT-specific vulnerability classes.
        // Kept optional because complex SPAs, web games, and video-heavy sites will experience lower JS performance.
        if (jitless)
        {
            argsList.Add("--js-flags=\"--jitless\"");
        }

        var browserArgs = string.Join(" ", argsList);

        var options = new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = browserArgs,
            ExclusiveUserDataFolderAccess = true,
            IsCustomCrashReportingEnabled = false,
            EnableTrackingPrevention = true
        };

        return await CoreWebView2Environment.CreateWithOptionsAsync(
            browserExecutableFolder: null, // Resolves installed Edge/WebView2 Evergreen runtime
            userDataFolder: userDataFolder,
            options: options
        );
    }
}
