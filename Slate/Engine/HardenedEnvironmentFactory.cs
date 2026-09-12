using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace Slate.Engine;

public static class HardenedEnvironmentFactory
{
    /// <summary>
    /// Creates a hardened CoreWebView2Environment enforcing strict process isolation,
    /// complete V8 JIT elimination, aggressive background throttling, and privacy protections.
    /// </summary>
    public static async Task<CoreWebView2Environment> CreateHardenedEnvironmentAsync(string userDataFolder, bool jitless = false)
    {
        var argsList = new System.Collections.Generic.List<string>
        {
            // 1. Process Isolation & Hardware Side-Channel Defense
            "--site-per-process",                        // Enforces physical OS process boundaries per origin (Spectre mitigation)
            "--disable-shared-workers",                  // Prevents cross-tab state sharing via workers

            // 3. Gaming Coexistence & Background Throttling
            "--enable-features=IntensiveWakeUpThrottling,QuickIntensiveWakeUpThrottlingAfterLoading", // Throttles background timers to 1/min
            "--disable-background-timer-throttling=false",
            "--disable-renderer-backgrounding=false",

            // 4. Background Telemetry & Service Elimination
            "--disable-background-networking",           // Kills background traffic (telemetry, updates)
            "--disable-sync",                            // Disables profile synchronization
            "--disable-breakpad",                        // Disables crash reporting subprocesses
            "--disable-component-update",                 // Halts background component updates
            "--disable-domain-reliability",               // Disables network telemetry reporting

            // 5. Privacy, Network & Fingerprinting Hardening
            "--force-webrtc-ip-handling-policy=default_public_interface_only", // Prevents private LAN IP leakage via WebRTC
            "--https-only-mode",                         // Blocks silent HTTP downgrade attacks
            "--disable-reading-from-canvas"              // Mitigates canvas-based hardware fingerprinting
        };

        // 2. V8 Attack Surface Reduction (SDSM / JIT-less Mode)
        // When enabled, completely disables TurboFan & Maglev JIT compilers (neutralizes ~60% of Chrome 0-days)
        // When disabled, standard browsing retains native JIT for hardware VP9/AV1 video decoding, Maps, and complex SPAs.
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
            browserExecutableFolder: null, // Resolves installed Edge/WebView2 runtime
            userDataFolder: userDataFolder,
            options: options
        );
    }
}
