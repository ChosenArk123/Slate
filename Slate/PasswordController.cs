using System.Reflection;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using Slate.Core;

namespace Slate;

/// <summary>One controller per WebView lifetime. No credentials are sent through default-world evaluation.</summary>
internal sealed class PasswordController : IDisposable
{
    internal sealed record Ticket(long Generation, string Context, string Origin, string Url);
    private sealed class Candidate(string origin, string username, string password, long generation, string url)
    {
        public string Origin { get; } = origin;
        public string Username { get; } = username;
        public string Password { get; set; } = password;
        public long Generation { get; } = generation;
        public string Url { get; } = url;
        public DateTimeOffset Expires { get; } = DateTimeOffset.UtcNow.AddSeconds(60);
        public CredentialDecision? Decision { get; set; }
    }
    private readonly CoreWebView2 _core;
    private readonly CredentialVault _vault;
    private readonly Func<bool> _live, _temporary, _alive, _autofill, _savePrompts, _private;
    private readonly Func<string?> _pageUrl;
    private readonly string _world = "Slate.Password." + Guid.NewGuid().ToString("N");
    private const string Binding = "__slateCredentialSubmit";
    private readonly Dictionary<int, (string Unique, string Frame)> _contexts = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private CoreWebView2DevToolsProtocolEventReceiver? _created, _binding, _destroyed, _cleared;
    private string? _context, _origin, _nonce;
    private int _contextId;
    private long _generation;
    private bool _disposed, _busy, _ready, _operation;
    private Candidate? _candidate;
    public event Action? OfferChanged;
    public event Action? AvailabilityChanged;
    public string? OfferOrigin => _candidate?.Decision is not null ? _candidate.Origin : null;
    public string? OfferUsername => _candidate?.Decision is not null ? _candidate.Username : null;
    public bool IsUpdate => _candidate?.Decision?.Change == CredentialChange.Update;
    public int AccountCount { get; private set; }
    internal bool HasCandidate => _candidate is not null;
    public bool Ready => !_disposed && _ready && _live() && _context is not null;
    private static readonly string DocumentScript = ReadScript();
    private static string ReadScript()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Slate.PasswordDocument.js")!;
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
    public PasswordController(CoreWebView2 core, CredentialVault vault, Func<bool> live, Func<bool> temporary,
        Func<bool>? alive = null, Func<bool>? autofill = null, Func<bool>? savePrompts = null, Func<bool>? privateContext = null,
        Func<string?>? pageUrl = null)
    {
        _core = core; _vault = vault; _live = live; _temporary = temporary; _alive = alive ?? live;
        _autofill = autofill ?? (() => false); _savePrompts = savePrompts ?? (() => true); _private = privateContext ?? (() => false);
        _pageUrl = pageUrl ?? (() => _core.Source);
    }
    public async Task InitializeAsync()
    {
        try
        {
            _created = _core.GetDevToolsProtocolEventReceiver("Runtime.executionContextCreated"); _created.DevToolsProtocolEventReceived += ContextCreated;
            _binding = _core.GetDevToolsProtocolEventReceiver("Runtime.bindingCalled"); _binding.DevToolsProtocolEventReceived += Submitted;
            _destroyed = _core.GetDevToolsProtocolEventReceiver("Runtime.executionContextDestroyed"); _destroyed.DevToolsProtocolEventReceived += ContextDestroyed;
            _cleared = _core.GetDevToolsProtocolEventReceiver("Runtime.executionContextsCleared"); _cleared.DevToolsProtocolEventReceived += ContextsCleared;
            _core.NavigationStarting += NavigationStarting;
            _core.SourceChanged += SourceChanged;
            _core.NavigationCompleted += NavigationCompleted;
            _core.ProcessFailed += ProcessFailed;
            await Protocol("Runtime.enable", new { });
            if (_disposed) return;
            await Protocol("Runtime.addBinding", new { name = Binding, executionContextName = _world });
            if (_disposed) return;
            _timer.Tick += Tick;
        }
        catch { Dispose(); } // Unsupported CDP/runtime fails closed; ordinary browsing continues.
    }
    private async Task<JsonElement> Protocol(string method, object parameters)
    {
        if (_disposed) throw new InvalidOperationException();
        string result = await _core.CallDevToolsProtocolMethodAsync(method, JsonSerializer.Serialize(parameters));
        using var json = JsonDocument.Parse(result); return json.RootElement.Clone();
    }
    private void ContextCreated(CoreWebView2 sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
    {
        try
        {
            using var json = JsonDocument.Parse(args.ParameterObjectAsJson);
            var c = json.RootElement.GetProperty("context");
            if (c.GetProperty("name").GetString() != _world) return;
            _contexts[c.GetProperty("id").GetInt32()] = (c.GetProperty("uniqueId").GetString()!, c.GetProperty("auxData").GetProperty("frameId").GetString()!);
        }
        catch { /* Malformed protocol events cannot establish a context. */ }
    }
    private void ContextDestroyed(CoreWebView2 sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
    {
        try
        {
            using var json = JsonDocument.Parse(args.ParameterObjectAsJson);
            int id = json.RootElement.GetProperty("executionContextId").GetInt32();
            _contexts.Remove(id);
            if (_contextId == id) { _context = null; _ready = false; }
        }
        catch { _context = null; _ready = false; }
    }
    private void ContextsCleared(CoreWebView2 sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
    { _contexts.Clear(); _context = null; _ready = false; }
    private void NavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        SetAccountCount(0);
        _generation++; _ready = false; _context = null; _nonce = null;
        if (_candidate is { } c && (c.Decision is not null || CredentialOrigin.Normalize(args.Uri) != c.Origin || _generation > c.Generation + 1)) Dismiss();
    }
    private void SourceChanged(CoreWebView2 sender, CoreWebView2SourceChangedEventArgs args)
    {
        // A same-document route change invalidates tickets, but retains the isolated world.
        if (!args.IsNewDocument) { _generation++; if (_candidate?.Decision is not null) Dismiss(); }
        if (_candidate is { } c && CredentialOrigin.Normalize(_core.Source) != c.Origin) Dismiss();
    }
    private async void NavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (!args.IsSuccess) { Dismiss(); return; }
        try { await AttachDocumentAsync(); }
        catch { _ready = false; Dismiss(); }
    }
    private void ProcessFailed(CoreWebView2 sender, CoreWebView2ProcessFailedEventArgs args)
    {
        if (args.ProcessFailedKind is CoreWebView2ProcessFailedKind.BrowserProcessExited or CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.FrameRenderProcessExited) Dispose();
        else InvalidateForSleep();
    }
    private async Task AttachDocumentAsync()
    {
        long generation = _generation;
        string? origin = CredentialOrigin.Normalize(_core.Source);
        if (_disposed || !_live() || origin is null) return;
        var tree = await Protocol("Page.getFrameTree", new { });
        if (_disposed || !_live() || generation != _generation || CredentialOrigin.Normalize(_core.Source) != origin) return;
        var frame = tree.GetProperty("frameTree").GetProperty("frame");
        if (CredentialOrigin.Normalize(frame.GetProperty("url").GetString()) != origin) return;
        string frameId = frame.GetProperty("id").GetString()!;
        var world = await Protocol("Page.createIsolatedWorld", new { frameId, worldName = _world, grantUniveralAccess = false });
        int id = world.GetProperty("executionContextId").GetInt32();
        if (_disposed || !_live() || generation != _generation || CredentialOrigin.Normalize(_core.Source) != origin ||
            !_contexts.TryGetValue(id, out var context) || context.Frame != frameId) return;
        _contextId = id; _context = context.Unique; _origin = origin; _nonce = Guid.NewGuid().ToString("N"); _ready = true;
        var ticket = CaptureTicket();
        if (ticket is null) { _ready = false; return; }
        await Call(ticket, DocumentScript, Binding, _nonce, !_temporary() && !_private());
        if (!Valid(ticket)) { _ready = false; return; }
        IReadOnlyList<CredentialMetadata> accounts = [];
        try
        {
            accounts = await AccountsAsync(ticket);
            if (Valid(ticket)) SetAccountCount(accounts.Count);
        }
        catch { SetAccountCount(0); }
        if (accounts.Count == 1 && Valid(ticket) && !_private() && _autofill() && !_temporary() && Navigation.IsHttpsOrigin(ticket.Origin))
            await FillCoreAsync(ticket, accounts[0], true);
    }
    internal Ticket? CaptureTicket() => Ready && CredentialOrigin.Normalize(_core.Source) == _origin &&
        CredentialOrigin.Normalize(_pageUrl()) == _origin ? new(_generation, _context!, _origin!, _core.Source) : null;
    private bool Valid(Ticket? ticket) => ticket is not null && Ready && ticket.Generation == _generation && ticket.Context == _context &&
        ticket.Origin == _origin && _core.Source == ticket.Url && CredentialOrigin.Normalize(_core.Source) == ticket.Origin &&
        CredentialOrigin.Normalize(_pageUrl()) == ticket.Origin;
    private async Task<JsonElement?> Call(Ticket ticket, string function, params object?[] arguments)
    {
        if (!Valid(ticket)) return null;
        // Only audited static function source is composed here. Values (including secrets and URL) are CDP arguments.
        string guardedFunction = "function(expectedUrl,...args){if(location.href!==expectedUrl)return null;return (" + function + ")(...args);}";
        var result = await Protocol("Runtime.callFunctionOn", new { functionDeclaration = guardedFunction,
            uniqueContextId = ticket.Context, arguments = new object?[] { ticket.Url }.Concat(arguments).Select(value => new { value }).ToArray(),
            returnByValue = true, silent = true });
        if (!Valid(ticket) || result.TryGetProperty("exceptionDetails", out _)) return null;
        return result.GetProperty("result").TryGetProperty("value", out var value) ? value.Clone() : null;
    }
    public async Task<IReadOnlyList<CredentialMetadata>> AccountsAsync(Ticket ticket)
    {
        // Private and stale/restricted contexts must not even initiate a vault query.
        if (_private() || !Valid(ticket)) return [];
        var accounts = await _vault.ListAsync(ticket.Origin);
        if (_private() || !Valid(ticket)) return [];
        return accounts.OrderBy(account => account.Username, StringComparer.Ordinal)
            .ThenBy(account => account.Id).ToArray();
    }
    private void SetAccountCount(int count)
    {
        if (AccountCount == count) return;
        AccountCount = count;
        AvailabilityChanged?.Invoke();
    }
    public Task<bool> FillAsync(Ticket ticket, CredentialMetadata account) => FillCoreAsync(ticket, account, false);
    private async Task<bool> FillCoreAsync(Ticket ticket, CredentialMetadata account, bool automatic)
    {
        if (_operation || _private() || !Valid(ticket) || account.Origin != ticket.Origin ||
            (automatic && (!_autofill() || _temporary() || !Navigation.IsHttpsOrigin(ticket.Origin)))) return false;
        _operation = true;
        string? password = null;
        try
        {
            password = await _vault.RevealAsync(account.Id, account.Revision);
            if (_private() || !Valid(ticket) ||
                (automatic && (!_autofill() || _temporary() || !Navigation.IsHttpsOrigin(ticket.Origin)))) return false;
            var result = await Call(ticket, "function(u,p,a){return globalThis.__slatePassword?.fill(u,p,false,a) ?? false;}", account.Username, password, automatic);
            password = "";
            bool filled = result?.ValueKind == JsonValueKind.True;
            if (filled && Valid(ticket) && !_private() && !_temporary() && !_vault.RecoveredFromBackup)
            {
                // The secret has already been delivered. A bookkeeping failure must not report a failed fill.
                try { await _vault.MarkUsedAsync(account.Id, account.Revision); } catch { }
            }
            return filled;
        }
        catch { return false; }
        finally { password = null; _operation = false; }
    }
    public async Task<bool> GenerateAsync(Ticket ticket)
    {
        if (_operation || !Valid(ticket)) return false;
        _operation = true;
        try
        {
            var result = await Call(ticket, "function(p){return globalThis.__slatePassword?.fill('',p,true,false) ?? false;}", PasswordGenerator.Generate());
            return result?.ValueKind == JsonValueKind.True;
        }
        catch { return false; }
        finally { _operation = false; }
    }
    private void Submitted(CoreWebView2 sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
    {
        if (!Ready || !_savePrompts() || _temporary() || _private() || _candidate is not null) return;
        try
        {
            string raw = args.ParameterObjectAsJson;
            if (raw.Length > 40000) return;
            using var json = JsonDocument.Parse(raw);
            var root = json.RootElement;
            if (root.GetProperty("name").GetString() != Binding || root.GetProperty("executionContextId").GetInt32() != _contextId) return;
            using var message = JsonDocument.Parse(root.GetProperty("payload").GetString()!);
            var m = message.RootElement;
            if (m.EnumerateObject().Count() != 4 || m.GetProperty("v").GetInt32() != 1 || m.GetProperty("nonce").GetString() != _nonce) return;
            string? username = m.GetProperty("username").GetString(), password = m.GetProperty("password").GetString();
            var ticket = CaptureTicket();
            if (ticket is null || !PasswordCapturePolicy.TryNormalizeSubmission(ticket.Url, username, password,
                    _private(), _temporary(), out var origin) || origin != ticket.Origin) return;
            _candidate = new Candidate(origin, username!, password!, _generation, _core.Source);
            _timer.Start();
        }
        catch { /* Never log page-controlled messages or protocol errors. */ }
    }
    private async void Tick(object? sender, object args)
    {
        if (_candidate is { } expired && (expired.Expires < DateTimeOffset.UtcNow || !_alive() || !_savePrompts() || _temporary() || _private())) { Dismiss(); return; }
        if (_busy) return;
        if (_candidate is not { Decision: null } candidate || CaptureTicket() is not { } ticket) return;
        _busy = true;
        try
        {
            var result = await Call(ticket, "function(){return globalThis.__slatePassword?.status();}");
            if (!Valid(ticket) || result is null || _candidate != candidate) return;
            bool anyPassword = result.Value.GetProperty("anyPassword").GetBoolean();
            bool gone = result.Value.GetProperty("gone").GetBoolean() && !anyPassword;
            bool navigated = _generation == candidate.Generation + 1 && _core.Source != candidate.Url && !anyPassword;
            if (!gone && !navigated) return;
            var decision = await _vault.AssessAsync(candidate.Origin, candidate.Username, candidate.Password);
            if (!Valid(ticket) || _candidate != candidate || !_savePrompts() || _temporary() || _private()) return;
            if (decision.Change == CredentialChange.Unchanged) { Dismiss(); return; }
            candidate.Decision = decision; OfferChanged?.Invoke();
        }
        catch { Dismiss(); }
        finally { _busy = false; }
    }
    public async Task<bool> SaveOfferAsync()
    {
        var candidate = _candidate;
        if (candidate?.Decision is null || !_live() || !_savePrompts() || _temporary() || _private() || candidate.Expires < DateTimeOffset.UtcNow ||
            CaptureTicket() is not { } ticket || ticket.Origin != candidate.Origin) return false;
        // Copy only for this committed, explicitly approved write; tab closure no longer cancels atomic storage.
        string password = candidate.Password;
        Dismiss();
        try { await _vault.SaveAsync(candidate.Origin, candidate.Username, password, candidate.Decision.Existing); return true; }
        catch { return false; }
        finally { password = ""; }
    }
    public void Dismiss()
    {
        _timer.Stop();
        if (_candidate is not null) _candidate.Password = "";
        _candidate = null; OfferChanged?.Invoke();
    }
    public void InvalidateForSleep() { _generation++; Dismiss(); }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _ready = false; _generation++; _context = null; _nonce = null; _contexts.Clear();
        SetAccountCount(0);
        _timer.Stop(); _timer.Tick -= Tick; Dismiss();
        if (_created is not null) _created.DevToolsProtocolEventReceived -= ContextCreated;
        if (_binding is not null) _binding.DevToolsProtocolEventReceived -= Submitted;
        if (_destroyed is not null) _destroyed.DevToolsProtocolEventReceived -= ContextDestroyed;
        if (_cleared is not null) _cleared.DevToolsProtocolEventReceived -= ContextsCleared;
        _core.NavigationStarting -= NavigationStarting; _core.SourceChanged -= SourceChanged;
        _core.NavigationCompleted -= NavigationCompleted; _core.ProcessFailed -= ProcessFailed;
        OfferChanged = null;
        AvailabilityChanged = null;
    }
}
