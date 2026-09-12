using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;

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
    public Action? Teardown { get; set; }
}

public sealed class TabManagerService
{
    private readonly Dictionary<Guid, TabRuntime> _runtimes = [];

    public Guid? FocusedTabId { get; set; }
    public Func<Guid?>? FocusedTabIdProvider { get; set; }
    public Guid? SplitTabId { get; set; }

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
            try { runtime.Teardown?.Invoke(); } catch { }
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

    public void Clear()
    {
        foreach (var id in _runtimes.Keys.ToList())
        {
            DisposeRuntime(id);
        }
    }
}
