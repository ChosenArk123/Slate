using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Slate.Core;

namespace Slate.Services;

public sealed class TabRuntime(WebView2 view)
{
    public WebView2 View { get; } = view;
    public Task? Initialization { get; set; }
    public bool Suspended { get; set; }
    public bool Sleeping { get; set; }
    public string? Error { get; set; }
    public bool Failed { get; set; }
    public bool SecureNavigation { get; set; }
    public bool CertificateError { get; set; }
    public CoreWebView2Certificate? LastCertificate { get; set; }
    public CoreWebView2WebErrorStatus? LastCertificateError { get; set; }
    public double ZoomFactor { get; set; } = 1.0;
    public BitmapImage? FaviconImage { get; set; }
    public long FaviconRevision { get; set; }
    public HashSet<Guid> Downloads { get; } = [];
    public bool HasActivePermissionPrompt { get; set; }
    internal PasswordController? Passwords { get; set; }
    public Action? Teardown { get; set; }
}

public sealed class TabManagerService
{
    private readonly Dictionary<Guid, TabRuntime> _runtimes = [];

    public Guid? FocusedTabId { get; set; }
    public Func<Guid?>? FocusedTabIdProvider { get; set; }
    public Guid? SplitTabId { get; set; }
    public Action<TabRuntime>? OnRuntimeDisposing { get; set; }

    public Dictionary<Guid, TabRuntime> Runtimes => _runtimes;
    public int RuntimeCount => _runtimes.Count;

    public TabRuntime? GetRuntime(Guid tabId) => _runtimes.GetValueOrDefault(tabId);

    public bool TryGetRuntime(Guid tabId, out TabRuntime? runtime) => _runtimes.TryGetValue(tabId, out runtime);

    public void RegisterRuntime(Guid tabId, TabRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtimes[tabId] = runtime;
    }

    public bool HasRuntime(Guid tabId) => _runtimes.ContainsKey(tabId);

    public IEnumerable<TabRuntime> AllRuntimes => _runtimes.Values;

    public IEnumerable<CoreWebView2> GetActiveCoreViews()
    {
        foreach (var runtime in _runtimes.Values)
        {
            var core = runtime.View.CoreWebView2;
            if (core is not null) yield return core;
        }
    }

    public CoreWebView2? GetFocusedCoreView()
    {
        Guid? focusedId = FocusedTabIdProvider?.Invoke() ?? FocusedTabId;
        if (focusedId is { } id && _runtimes.TryGetValue(id, out var runtime))
        {
            return runtime.View.CoreWebView2;
        }
        return null;
    }

    public void DisposeRuntime(Guid tabId)
    {
        if (_runtimes.Remove(tabId, out var runtime))
        {
            try { OnRuntimeDisposing?.Invoke(runtime); } catch { }
            try { runtime.Passwords?.Dispose(); runtime.Passwords = null; } catch { }
            try { runtime.Teardown?.Invoke(); } catch { }
            runtime.Teardown = null;
            try
            {
                if (runtime.View.Parent is Panel parent)
                {
                    parent.Children.Remove(runtime.View);
                }
                runtime.View.Close();
            }
            catch { }
        }
    }

    /// <summary>
    /// Discards WebView2 runtimes for inactive background tabs that are not protected:
    /// - Not currently focused or visible in split-view
    /// - Not playing audio
    /// - Not performing active downloads
    /// - Not involved in active permission prompts or critical dialogs
    /// - Not actively loading
    /// - Not accessed within the recency threshold (default 30 seconds)
    /// Discarded tabs release full OS renderer processes and memory back to the system.
    /// Candidates are discarded in Least Recently Used (LRU) order.
    /// </summary>
    public List<Guid> DiscardInactiveRuntimes(
        ISet<Guid> protectedTabIds,
        int maxRetainedBackgroundViews = 1,
        Func<Guid, DateTimeOffset?>? lastAccessedProvider = null,
        Func<Guid, bool>? isLoadingProvider = null,
        TimeSpan? minInactiveDuration = null)
    {
        var discarded = new List<Guid>();
        var now = DateTimeOffset.UtcNow;
        var inactiveThreshold = minInactiveDuration ?? TimeSpan.FromSeconds(30);

        var candidateIds = TabLifecyclePolicy.SelectDiscardCandidates(
            _runtimes.Keys,
            id => protectedTabIds.Contains(id),
            id => _runtimes.TryGetValue(id, out var r) && r.HasActivePermissionPrompt,
            id => _runtimes.TryGetValue(id, out var r) && r.Downloads.Count > 0,
            id => _runtimes.TryGetValue(id, out var r) && r.View.CoreWebView2?.IsDocumentPlayingAudio == true,
            id => isLoadingProvider?.Invoke(id) == true,
            id => lastAccessedProvider?.Invoke(id) ?? DateTimeOffset.MinValue,
            inactiveThreshold,
            maxRetainedBackgroundViews,
            now);

        foreach (var tabId in candidateIds)
        {
            DisposeRuntime(tabId);
            discarded.Add(tabId);
        }

        return discarded;
    }

    public void Clear()
    {
        foreach (var id in _runtimes.Keys.ToList())
        {
            DisposeRuntime(id);
        }
    }
}
