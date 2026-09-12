using System;
using System.Collections.Generic;
using System.Linq;

namespace Slate.Core;

/// <summary>
/// Validates process startup environment and command-line arguments to prevent
/// hostile parent-process injection or security boundary bypasses.
/// </summary>
public static class StartupSecurity
{
    /// <summary>
    /// Environment variables that can override WebView2 runtime behavior, executable paths,
    /// <summary>
    /// Security-sensitive environment variables documented by Microsoft WebView2 that could
    /// weaken the browser sandbox, inject arbitrary switches, redirect user data outside protected
    /// storage, or force an untrusted browser runtime executable.
    /// Non-security UI settings (such as WEBVIEW2_DEFAULT_BACKGROUND_COLOR) are deliberately excluded.
    /// </summary>
    public static readonly string[] DangerousWebView2EnvironmentVariables =
    [
        // Injects arbitrary Chromium command-line switches (e.g. --no-sandbox, --disable-web-security)
        "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS",

        // Overrides runtime binary folder to an untrusted custom browser executable directory
        "WEBVIEW2_BROWSER_EXECUTABLE_FOLDER",

        // Redirects user profile data outside Slate's protected profile directory
        "WEBVIEW2_USER_DATA_FOLDER",

        // Forces an unstable preview channel (Beta, Dev, Canary) with experimental/weakened security flags
        "WEBVIEW2_RELEASE_CHANNEL_PREFERENCE",

        // Attaches external diagnostic communication pipes for blocked scripts
        "WEBVIEW2_PIPE_FOR_BLOCKED_SCRIPT",

        // Legacy/alternate Edge switch variable capable of injecting browser arguments
        "EDGE_ADDITIONAL_BROWSER_ARGUMENTS"
    ];

    /// <summary>
    /// Dangerous Chromium/Edge command-line switches that weaken security boundaries,
    /// disable sandboxing, bypass TLS verification, or expose debugging endpoints.
    /// </summary>
    public static readonly string[] DangerousBrowserSwitches =
    [
        "no-sandbox",
        "disable-web-security",
        "ignore-certificate-errors",
        "remote-debugging-port",
        "remote-debugging-pipe",
        "disable-site-isolation-trials",
        "disable-site-isolation-for-policy",
        "single-process",
        "renderer-process-limit",
        "allow-running-insecure-content",
        "disable-gpu-sandbox",
        "allow-file-access-from-files",
        "disable-extensions",
        "test-type"
    ];

    private static readonly string[] SwitchPrefixes = ["--", "-", "/"];

    /// <summary>
    /// Determines whether the specified command-line argument matches a dangerous switch name.
    /// Supports '--', '-', and '/' prefixes, with exact match or '=value' syntax.
    /// Correctly strips surrounding quotes and catches site-isolation disabling switches.
    /// </summary>
    public static bool IsDangerousArgument(string? argument)
    {
        if (string.IsNullOrWhiteSpace(argument)) return false;

        string trimmed = argument.Trim().Trim('"', '\'');
        foreach (var prefix in SwitchPrefixes)
        {
            if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            string withoutPrefix = trimmed[prefix.Length..];
            int equalsIndex = withoutPrefix.IndexOf('=');
            string switchName = equalsIndex >= 0 ? withoutPrefix[..equalsIndex] : withoutPrefix;
            string switchValue = equalsIndex >= 0 ? withoutPrefix[(equalsIndex + 1)..].Trim('"', '\'') : "";

            // Catch site-isolation disabling via --disable-features
            if (switchName.Equals("disable-features", StringComparison.OrdinalIgnoreCase) &&
                (switchValue.Contains("IsolateOrigins", StringComparison.OrdinalIgnoreCase) ||
                 switchValue.Contains("site-per-process", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            foreach (var dangerous in DangerousBrowserSwitches)
            {
                if (switchName.Equals(dangerous, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Validates all command-line arguments passed to Slate.
    /// Only allows known Slate flags (--hardened-isolation, --smoke-test=...) or valid navigation URLs.
    /// Rejects any dangerous or unrecognized browser switches.
    /// </summary>
    public static bool ValidateCommandLine(IEnumerable<string> args, out string? rejectedReason)
    {
        rejectedReason = null;
        bool isFirst = true;

        foreach (var arg in args)
        {
            // Skip the executable path itself
            if (isFirst)
            {
                isFirst = false;
                continue;
            }

            if (string.IsNullOrWhiteSpace(arg)) continue;

            if (IsDangerousArgument(arg))
            {
                rejectedReason = $"Dangerous command-line switch rejected: {arg}";
                return false;
            }

            // If it starts with a switch prefix, verify it's a recognized Slate option
            if (arg.StartsWith("--", StringComparison.Ordinal) ||
                arg.StartsWith("-", StringComparison.Ordinal) ||
                arg.StartsWith("/", StringComparison.Ordinal))
            {
                if (IsRecognizedSlateSwitch(arg))
                {
                    // Disallow UNC network shares in switch paths to prevent NTLM hash leaks
                    int equalsIdx = arg.IndexOf('=');
                    if (equalsIdx >= 0)
                    {
                        string pathVal = arg[(equalsIdx + 1)..].Trim('"', '\'');
                        if (pathVal.StartsWith(@"\\") || pathVal.StartsWith("//"))
                        {
                            rejectedReason = $"UNC path rejected in switch for security reasons: {arg}";
                            return false;
                        }
                    }
                    continue;
                }

                // Any switch that resembles a browser flag or Chromium parameter is rejected
                rejectedReason = $"Unrecognized command-line switch rejected: {arg}";
                return false;
            }
        }

        return true;
    }

    private static bool IsRecognizedSlateSwitch(string arg)
    {
        foreach (var prefix in SwitchPrefixes)
        {
            string flag = arg;
            if (!flag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            string name = flag[prefix.Length..];
            if (name.Equals("hardened-isolation", StringComparison.OrdinalIgnoreCase))
                return true;

            if (name.StartsWith("smoke-test=", StringComparison.OrdinalIgnoreCase))
                return true;

            if (name.Equals("memory-benchmark", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("memory-benchmark=", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
