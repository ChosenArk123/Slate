using Microsoft.UI.Xaml;
using Slate.Core;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Slate;

public partial class App : Application
{
    private Window? _window;
    private Mutex? _instanceLock;
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    internal static string? SmokeOutput { get; } = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--smoke-test="))?[13..];
    internal static string ProfileDirectory { get; } = SmokeOutput is { } output
        ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "smoke-profile-" + Guid.NewGuid().ToString("N"))
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Slate");
    public App()
    {
        AuditStartupEnvironment();
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            try
            {
                Directory.CreateDirectory(ProfileDirectory);
                var logEntry = $"{DateTimeOffset.Now:u} {SanitizeLogMessage(e.Exception?.ToString())}{Environment.NewLine}";
                File.AppendAllText(Path.Combine(ProfileDirectory, "crash.log"), logEntry);
            }
            catch (IOException) { }
        };
    }

    private static void AuditStartupEnvironment()
    {
        // Prevent external environment injection of unsafe browser flags into WebView2
        Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", null);

        // Disallow dangerous command-line flags that weaken sandboxing or expose debugging ports
        string[] dangerousFlags =
        [
            "remote-debugging-port",
            "remote-debugging-pipe",
            "no-sandbox",
            "disable-web-security",
            "ignore-certificate-errors",
            "disable-site-isolation-trials",
            "single-process"
        ];
        var args = Environment.GetCommandLineArgs();
        foreach (var arg in args)
        {
            foreach (var flag in dangerousFlags)
            {
                if (IsBrowserSwitch(arg, flag))
                {
                    Environment.FailFast($"Unsafe browser command-line flag rejected: {flag}");
                }
            }
        }
    }

    internal static bool IsDangerousBrowserArgument(string? argument)
    {
        if (string.IsNullOrEmpty(argument)) return false;
        foreach (var flag in new[] { "remote-debugging-port", "remote-debugging-pipe", "no-sandbox", "disable-web-security",
            "ignore-certificate-errors", "disable-site-isolation-trials", "single-process" })
            if (IsBrowserSwitch(argument, flag)) return true;
        return false;
    }

    private static bool IsBrowserSwitch(string argument, string name)
    {
        foreach (string prefix in new[] { "--", "-", "/" })
        {
            string value = prefix + name;
            if (argument.Equals(value, StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith(value + "=", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    internal static string SanitizeLogMessage(string? message) => LogSanitizer.Sanitize(message);
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (SmokeOutput is null)
        {
            _instanceLock = new Mutex(true, "Local\\Slate.Browser." + Environment.UserName, out var firstInstance);
            if (!firstInstance)
            {
                foreach (var process in Process.GetProcessesByName("Slate"))
                    if (process.Id != Environment.ProcessId && process.MainWindowHandle != IntPtr.Zero)
                    { ShowWindow(process.MainWindowHandle, 9); SetForegroundWindow(process.MainWindowHandle); break; }
                _instanceLock.Dispose(); _instanceLock = null; Exit(); return;
            }
        }
        _window = new MainWindow();
        _window.Closed += (_, _) => { _instanceLock?.ReleaseMutex(); _instanceLock?.Dispose(); _instanceLock = null; };
        _window.Activate();
    }
}
