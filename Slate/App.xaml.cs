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
    internal static string ProfileDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Slate");
    internal static bool HasHardenedIsolationArg { get; } =
        Environment.GetCommandLineArgs().Any(a => a.Equals("--hardened-isolation", StringComparison.OrdinalIgnoreCase) ||
                                                 a.Equals("-hardened-isolation", StringComparison.OrdinalIgnoreCase) ||
                                                 a.Equals("/hardened-isolation", StringComparison.OrdinalIgnoreCase));
    internal static string? MemoryBenchmarkOutputPath { get; } =
        Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith("--memory-benchmark=", StringComparison.OrdinalIgnoreCase) ||
                                 a.StartsWith("-memory-benchmark=", StringComparison.OrdinalIgnoreCase) ||
                                 a.StartsWith("/memory-benchmark=", StringComparison.OrdinalIgnoreCase))
            ?.Split('=', 2)[1]?.Trim('"', '\'') ??
        (Environment.GetCommandLineArgs().Any(a => a.Equals("--memory-benchmark", StringComparison.OrdinalIgnoreCase) ||
                                                   a.Equals("-memory-benchmark", StringComparison.OrdinalIgnoreCase) ||
                                                   a.Equals("/memory-benchmark", StringComparison.OrdinalIgnoreCase))
            ? Path.Combine(ProfileDirectory, "memory-benchmark.json")
            : null);
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
        // 1. Scrub external environment variables that could inject unsafe flags, alter executable paths,
        // or attach debugging pipes to WebView2.
        foreach (var envVar in StartupSecurity.DangerousWebView2EnvironmentVariables)
        {
            try { Environment.SetEnvironmentVariable(envVar, null); } catch { }
        }

        // 2. Validate all command-line arguments passed to Slate
        var args = Environment.GetCommandLineArgs();
        if (!StartupSecurity.ValidateCommandLine(args, out var rejectionReason))
        {
            Environment.FailFast(rejectionReason);
        }

        // 3. Inspect Windows Registry policy overrides to prevent enterprise/group-policy injection of dangerous switches
        AuditRegistryPolicies();

        // 4. Ensure profile directory has secure permissions
        EnsureSecureProfileDirectory(ProfileDirectory);
    }

    private static void AuditRegistryPolicies()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            string[] subKeys =
            [
                @"SOFTWARE\Policies\Microsoft\Edge\WebView2",
                @"SOFTWARE\Policies\Microsoft\Edge\WebView2\AdditionalBrowserArguments"
            ];

            foreach (var root in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
            {
                foreach (var subKeyPath in subKeys)
                {
                    using var key = root.OpenSubKey(subKeyPath);
                    if (key is null) continue;

                    foreach (var valName in key.GetValueNames())
                    {
                        var val = key.GetValue(valName)?.ToString();
                        if (string.IsNullOrWhiteSpace(val)) continue;

                        // Reject custom runtime binary override policies
                        if (valName.Equals("browserExecutableFolder", StringComparison.OrdinalIgnoreCase))
                        {
                            Environment.FailFast($"Unexpected WebView2 browserExecutableFolder policy detected in registry ({subKeyPath}): {val}");
                        }

                        // Reject custom profile location overrides that evade Slate's ACL-protected profile
                        if (valName.Equals("userDataFolder", StringComparison.OrdinalIgnoreCase))
                        {
                            Environment.FailFast($"Unexpected WebView2 userDataFolder policy detected in registry ({subKeyPath}): {val}");
                        }

                        var tokens = val.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        foreach (var token in tokens)
                        {
                            if (StartupSecurity.IsDangerousArgument(token))
                            {
                                Environment.FailFast($"Dangerous WebView2 policy detected in registry ({subKeyPath}): {token}");
                            }
                        }
                    }
                }
            }
        }
        catch
        {
        }
    }

    private static void EnsureSecureProfileDirectory(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            Directory.CreateDirectory(path);
            var dirInfo = new DirectoryInfo(path);
            var security = dirInfo.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            var currentUser = System.Security.Principal.WindowsIdentity.GetCurrent().User;
            if (currentUser is not null)
            {
                security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    currentUser,
                    System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                    System.Security.AccessControl.PropagationFlags.None,
                    System.Security.AccessControl.AccessControlType.Allow));
            }
            var systemSid = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null);
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                systemSid,
                System.Security.AccessControl.FileSystemRights.FullControl,
                System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                System.Security.AccessControl.PropagationFlags.None,
                System.Security.AccessControl.AccessControlType.Allow));

            dirInfo.SetAccessControl(security);
        }
        catch { }
    }

    internal static bool IsDangerousBrowserArgument(string? argument) => StartupSecurity.IsDangerousArgument(argument);

    internal static string SanitizeLogMessage(string? message) => LogSanitizer.Sanitize(message);
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _instanceLock = new Mutex(true, "Local\\Slate.Browser." + Environment.UserName, out var firstInstance);
        if (!firstInstance)
        {
            foreach (var process in Process.GetProcessesByName("Slate"))
                if (process.Id != Environment.ProcessId && process.MainWindowHandle != IntPtr.Zero)
                { ShowWindow(process.MainWindowHandle, 9); SetForegroundWindow(process.MainWindowHandle); break; }
            _instanceLock.Dispose(); _instanceLock = null; Exit(); return;
        }
        _window = new MainWindow();
        _window.Closed += (_, _) => { _instanceLock?.ReleaseMutex(); _instanceLock?.Dispose(); _instanceLock = null; };
        _window.Activate();
    }
}
