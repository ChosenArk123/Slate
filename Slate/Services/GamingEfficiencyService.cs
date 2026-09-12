using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace Slate.Services;

public sealed class GamingEfficiencyService : IDisposable
{
    #region Win32 P/Invoke Definitions

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(IntPtr hProcess, int processInformationClass, ref PROCESS_POWER_THROTTLING_STATE processInformation, uint processInformationSize);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint MONITOR_DEFAULTTOPRIMARY = 1;
    private const int ProcessPowerThrottling = 4;
    private const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    private const uint PROCESS_SET_INFORMATION = 0x0200;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateMemoryResourceNotification(int notificationType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryMemoryResourceNotification(IntPtr resourceNotificationHandle, [MarshalAs(UnmanagedType.Bool)] out bool resourceState);

    private const int LowMemoryResourceNotification = 0;

    #endregion

    private readonly IntPtr _slateWindowHandle;
    private readonly uint _currentProcessId;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue;
    private readonly Func<IEnumerable<CoreWebView2>> _activeViewsProvider;
    private readonly Func<CoreWebView2?> _focusedViewProvider;
    private readonly Func<IReadOnlyList<CoreWebView2ProcessInfo>?> _processInfosProvider;
    private readonly WinEventDelegate _winEventDelegate;
    private IntPtr _hookHandle;
    private IntPtr _lowMemoryHandle;
    private bool _isGamingModeActive;
    private bool _disposed;

    public event Action<bool>? GamingModeChanged;
    public event Action? InactiveTabDiscardRequested;

    public GamingEfficiencyService(
        IntPtr slateWindowHandle,
        Microsoft.UI.Dispatching.DispatcherQueue dispatcherQueue,
        Func<IEnumerable<CoreWebView2>> activeViewsProvider,
        Func<CoreWebView2?> focusedViewProvider,
        Func<IReadOnlyList<CoreWebView2ProcessInfo>?> processInfosProvider)
    {
        _slateWindowHandle = slateWindowHandle;
        _dispatcherQueue = dispatcherQueue;
        _currentProcessId = (uint)Process.GetCurrentProcess().Id;
        _activeViewsProvider = activeViewsProvider;
        _focusedViewProvider = focusedViewProvider;
        _processInfosProvider = processInfosProvider;

        try
        {
            _lowMemoryHandle = CreateMemoryResourceNotification(LowMemoryResourceNotification);
        }
        catch { }

        _winEventDelegate = OnForegroundWindowChanged;
        _hookHandle = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND,
            EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _winEventDelegate,
            0,
            0,
            WINEVENT_OUTOFCONTEXT
        );
    }

    private void OnForegroundWindowChanged(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (_disposed || hwnd == IntPtr.Zero) return;

        GetWindowThreadProcessId(hwnd, out uint fgProcessId);
        bool isSlate = fgProcessId == _currentProcessId || hwnd == _slateWindowHandle;
        bool isFullscreen = !isSlate && IsFullscreenWindow(hwnd);

        // Marshal to WinUI DispatcherQueue to prevent cross-thread COM exceptions (RPC_E_WRONG_THREAD)
        // when interacting with CoreWebView2 objects.
        _dispatcherQueue.TryEnqueue(async () =>
        {
            if (_disposed) return;

            if (isSlate)
            {
                if (_isGamingModeActive)
                {
                    await ExitGamingModeAsync();
                }
                return;
            }

            if (isFullscreen)
            {
                if (!_isGamingModeActive)
                {
                    await EnterGamingModeAsync();
                }
            }
            else if (_isGamingModeActive)
            {
                await ExitGamingModeAsync();
            }
        });
    }

    private static bool IsFullscreenWindow(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out RECT windowRect)) return false;

        IntPtr hMonitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTOPRIMARY);
        var monitorInfo = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(hMonitor, ref monitorInfo)) return false;

        // Check if the foreground window bounds match or exceed the monitor surface
        return windowRect.Left <= monitorInfo.rcMonitor.Left &&
               windowRect.Top <= monitorInfo.rcMonitor.Top &&
               windowRect.Right >= monitorInfo.rcMonitor.Right &&
               windowRect.Bottom >= monitorInfo.rcMonitor.Bottom;
    }

    public async Task EnterGamingModeAsync()
    {
        if (_isGamingModeActive) return;
        _isGamingModeActive = true;
        GamingModeChanged?.Invoke(true);

        // 1. Enforce Windows EcoQoS (Efficiency Mode / E-Cores) strictly on Slate's own process tree
        ApplyEcoQoSToProcessTree(true);

        // 2. Clamp CoreWebView2 Memory & Suspend Inactive Tabs (Audio-Aware)
        var focusedView = _focusedViewProvider();
        foreach (var view in _activeViewsProvider())
        {
            try
            {
                // Instruct Chromium memory manager to purge font, image, and V8 caches
                view.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;

                // Suspend any tab that is not actively focused and is NOT playing audio.
                // Keeping audio-playing tabs awake ensures podcasts, music, and streams remain uninterrupted.
                if (view != focusedView && !view.IsDocumentPlayingAudio)
                {
                    await view.TrySuspendAsync();
                }
            }
            catch { /* View may be navigating or closing */ }
        }

        // 3. Signal tab manager to discard older inactive background tab runtimes
        InactiveTabDiscardRequested?.Invoke();

        // 4. Purge Physical Working Set from RAM for Slate and its own child processes
        TrimWorkingSet();
    }

    public async Task ExitGamingModeAsync()
    {
        if (!_isGamingModeActive) return;
        _isGamingModeActive = false;
        GamingModeChanged?.Invoke(false);

        // 1. Remove EcoQoS Throttling from Process Tree
        ApplyEcoQoSToProcessTree(false);

        // 2. Restore Normal Memory Target across active views & Resume Focused View
        foreach (var view in _activeViewsProvider())
        {
            try
            {
                view.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
            }
            catch { }
        }

        var focusedView = _focusedViewProvider();
        if (focusedView is not null)
        {
            try
            {
                focusedView.Resume();
            }
            catch { }
        }

        await Task.CompletedTask;
    }

    private void ApplyEcoQoSToProcessTree(bool enable)
    {
        var currentProcess = Process.GetCurrentProcess();
        ApplyEcoQoSToProcess(currentProcess.Handle, enable);

        // Enumerate ONLY Slate's own child processes (renderers, utility, and GPU processes)
        // using CoreWebView2Environment.GetProcessInfos() to prevent throttling external apps
        // like Discord, Steam, Spotify, or Windows Widgets.
        try
        {
            var childInfos = _processInfosProvider?.Invoke();
            if (childInfos is not null)
            {
                foreach (var info in childInfos)
                {
                    if (info.ProcessId == currentProcess.Id) continue;
                    try
                    {
                        IntPtr hProc = OpenProcess(PROCESS_SET_INFORMATION, false, (uint)info.ProcessId);
                        if (hProc != IntPtr.Zero)
                        {
                            ApplyEcoQoSToProcess(hProc, enable);
                            CloseHandle(hProc);
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    private static void ApplyEcoQoSToProcess(IntPtr hProcess, bool enable)
    {
        var throttling = new PROCESS_POWER_THROTTLING_STATE
        {
            Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
            ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
            StateMask = enable ? PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0
        };

        SetProcessInformation(hProcess, ProcessPowerThrottling, ref throttling, (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
    }

    public void TrimWorkingSet()
    {
        try
        {
            var currentProcess = Process.GetCurrentProcess();
            EmptyWorkingSet(currentProcess.Handle);

            // Target ONLY Slate's own child processes
            var childInfos = _processInfosProvider?.Invoke();
            if (childInfos is not null)
            {
                foreach (var info in childInfos)
                {
                    if (info.ProcessId == currentProcess.Id) continue;
                    try
                    {
                        IntPtr hProc = OpenProcess(PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)info.ProcessId);
                        if (hProc != IntPtr.Zero)
                        {
                            EmptyWorkingSet(hProc);
                            CloseHandle(hProc);
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    public void CheckMemoryPressure()
    {
        if (_disposed || _lowMemoryHandle == IntPtr.Zero) return;
        try
        {
            if (QueryMemoryResourceNotification(_lowMemoryHandle, out bool isLowMemory) && isLowMemory)
            {
                InactiveTabDiscardRequested?.Invoke();
                TrimWorkingSet();
            }
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWinEvent(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }

        if (_lowMemoryHandle != IntPtr.Zero)
        {
            CloseHandle(_lowMemoryHandle);
            _lowMemoryHandle = IntPtr.Zero;
        }

        if (_isGamingModeActive)
        {
            ApplyEcoQoSToProcessTree(false);
        }
    }
}
