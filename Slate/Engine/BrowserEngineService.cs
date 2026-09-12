using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Slate.Core;

namespace Slate.Engine;

public sealed class BrowserEngineService
{
    private readonly string _userDataFolder;
    private Task<CoreWebView2Environment>? _environmentTask;

    private readonly Dictionary<string, Queue<DateTimeOffset>> _popupAttempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<DateTimeOffset> _globalPopupAttempts = [];
    private readonly Dictionary<string, Queue<DateTimeOffset>> _permissionAttempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<DateTimeOffset> _globalPermissionAttempts = [];
    private readonly Dictionary<(string Origin, CoreWebView2PermissionKind Kind), DateTimeOffset> _lastPermissionPrompt = [];

    private static readonly TimeSpan PopupBurstWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PermissionBurstWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan PermissionRepeatWindow = TimeSpan.FromSeconds(30);
    public const int MaximumPopupsPerBurst = 5;
    public const int MaximumGlobalPopupsPerBurst = 10;
    public const int MaximumPermissionPromptsPerBurst = 3;
    public const int MaximumGlobalPermissionPromptsPerBurst = 6;
    private readonly bool _hardenedIsolation;

    public BrowserEngineService(string userDataFolder, bool hardenedIsolation = false)
    {
        _userDataFolder = userDataFolder;
        _hardenedIsolation = hardenedIsolation;
    }

    public Task<CoreWebView2Environment> GetEnvironmentAsync()
    {
        return _environmentTask ??= HardenedEnvironmentFactory.CreateHardenedEnvironmentAsync(_userDataFolder, _hardenedIsolation);
    }

    public CoreWebView2Environment? Environment => _environmentTask?.IsCompletedSuccessfully == true ? _environmentTask.Result : null;

    public IReadOnlyList<CoreWebView2ProcessInfo>? GetProcessInfos()
    {
        try { return Environment?.GetProcessInfos(); } catch { return null; }
    }

    public void ResetEnvironment()
    {
        _environmentTask = null;
    }

    public bool IsHardenedIsolation => _hardenedIsolation;

    public void ConfigureHardenedSettings(CoreWebView2Settings settings, bool developerToolsEnabled = false)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // Enforce strict security baseline: host objects and web messaging disabled
        settings.AreHostObjectsAllowed = false;
        settings.IsWebMessageEnabled = false;
        // Edge's internal password autosave is disabled so Slate owns credential storage exclusively
        settings.IsPasswordAutosaveEnabled = false;
        // General autofill stores non-password form data (for example addresses) profile-wide.
        // It is not the WebAuthn API and is not needed for navigator.credentials.create/get.
        settings.IsGeneralAutofillEnabled = false;
        // In hardened isolation mode, developer tools are strictly blocked.
        settings.AreDevToolsEnabled = developerToolsEnabled && !_hardenedIsolation;
        settings.IsStatusBarEnabled = false;
    }

    public bool AllowPopup(string? uri, DateTimeOffset now)
    {
        string host = Navigation.DisplayHost(uri ?? "");
        CleanOldAttempts(_globalPopupAttempts, now, PopupBurstWindow);
        if (_globalPopupAttempts.Count >= MaximumGlobalPopupsPerBurst) return false;

        if (!_popupAttempts.TryGetValue(host, out var attempts))
        {
            attempts = new Queue<DateTimeOffset>();
            _popupAttempts[host] = attempts;
        }

        CleanOldAttempts(attempts, now, PopupBurstWindow);
        if (attempts.Count >= MaximumPopupsPerBurst) return false;

        attempts.Enqueue(now);
        _globalPopupAttempts.Enqueue(now);
        return true;
    }

    public bool AllowPermissionPrompt(string origin, CoreWebView2PermissionKind kind, DateTimeOffset now)
    {
        string host = Navigation.DisplayHost(origin);
        var key = (origin, kind);
        if (_lastPermissionPrompt.TryGetValue(key, out var last) && now - last < PermissionRepeatWindow)
            return false;

        CleanOldAttempts(_globalPermissionAttempts, now, PermissionBurstWindow);
        if (_globalPermissionAttempts.Count >= MaximumGlobalPermissionPromptsPerBurst) return false;

        if (!_permissionAttempts.TryGetValue(host, out var attempts))
        {
            attempts = new Queue<DateTimeOffset>();
            _permissionAttempts[host] = attempts;
        }

        CleanOldAttempts(attempts, now, PermissionBurstWindow);
        if (attempts.Count >= MaximumPermissionPromptsPerBurst) return false;

        attempts.Enqueue(now);
        _globalPermissionAttempts.Enqueue(now);
        _lastPermissionPrompt[key] = now;
        return true;
    }

    public void ClearRateLimits()
    {
        _popupAttempts.Clear();
        _globalPopupAttempts.Clear();
        _permissionAttempts.Clear();
        _globalPermissionAttempts.Clear();
        _lastPermissionPrompt.Clear();
    }

    private static void CleanOldAttempts(Queue<DateTimeOffset> queue, DateTimeOffset now, TimeSpan window)
    {
        while (queue.Count > 0 && now - queue.Peek() > window)
            queue.Dequeue();
    }
}
