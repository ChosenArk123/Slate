using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Slate.Core;

namespace Slate;

public sealed partial class MainWindow
{
    private const string SyntheticPassword = "Slate-Synthetic-Only!123";
    private sealed class PausingCredentialProtector : ICredentialProtector, IDisposable
    {
        private readonly WindowsCredentialProtector _inner = new();
        private readonly ManualResetEventSlim _release = new(false);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public byte[] Protect(byte[] value) => _inner.Protect(value);
        public byte[] Unprotect(byte[] value)
        {
            Entered.TrySetResult();
            if (!_release.Wait(TimeSpan.FromSeconds(15))) throw new InvalidOperationException("Synthetic decryption barrier expired.");
            return _inner.Unprotect(value);
        }
        public void Release() => _release.Set();
        public void Dispose() { _release.Set(); _release.Dispose(); }
    }
    private async Task RunPasswordSmokeTestsAsync(string origin, Action<string, bool> check)
    {
        async Task Until(Func<bool> condition)
        {
            var end = DateTimeOffset.UtcNow.AddSeconds(10);
            while (!condition()) { if (DateTimeOffset.UtcNow > end) throw new InvalidOperationException("Password fixture did not reach its expected state."); await Task.Delay(40); }
        }
        PasswordController Controller() => _runtimes[FocusedTab.Id].Passwords!;
        async Task Open(string path)
        {
            await NavigateAsync(origin + path);
            await Until(() => !FocusedTab.IsLoading && CurrentCore()?.Source == origin + path && Controller().Ready);
            // Ready is set just before isolated-world installation finishes.
            await Task.Delay(100);
        }
        async Task<bool> Page(string expression) => await CurrentCore()!.ExecuteScriptAsync(expression) == "true";
        var protector = new WindowsCredentialProtector();
        var plain = Encoding.UTF8.GetBytes(SyntheticPassword);
        var encrypted = protector.Protect(plain);
        var decrypted = protector.Unprotect(encrypted);
        check("DPAPI user-bound protection round trips synthetic bytes", plain.SequenceEqual(decrypted) && !plain.SequenceEqual(encrypted));
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain); System.Security.Cryptography.CryptographicOperations.ZeroMemory(decrypted);
        encrypted[encrypted.Length / 2] ^= 1;
        bool rejected = false; try { protector.Unprotect(encrypted); } catch { rejected = true; }
        check("DPAPI rejects tampered ciphertext", rejected);
        var alice = await _credentialVault.SaveAsync(origin, "synthetic-alice", SyntheticPassword, null);
        var bob = await _credentialVault.SaveAsync(origin, "synthetic-bob", SyntheticPassword + "2", null);
        await Open("/password-login");
        var core = CurrentCore()!;
        var controller = Controller();
        check("Password controller attaches an isolated top-level document", controller.CaptureTicket() is not null);
        check("Password integration keeps host objects, web messaging and Chromium password autosave disabled",
            !core.Settings.AreHostObjectsAllowed && !core.Settings.IsWebMessageEnabled && !core.Settings.IsPasswordAutosaveEnabled);
        check("Password binding and controller state are absent from the page world", await Page("typeof globalThis.__slateCredentialSubmit === 'undefined' && typeof globalThis.__slatePassword === 'undefined'"));
        var ticket = controller.CaptureTicket()!;
        check("Multiple accounts are returned without arbitrary autofill", (await controller.AccountsAsync(ticket)).Count == 2 && await Page("document.querySelector('[type=password]').value === ''"));
        check("Explicit account selection fills a same-origin form", await controller.FillAsync(ticket, alice));
        check("Fill never submits and updates expected fields", await Page("document.querySelector('[autocomplete=username]').value === 'synthetic-alice' && document.querySelector('[type=password]').value.length > 0 && !window.submitted"));
        check("A document cannot be repeatedly filled", !await controller.FillAsync(ticket, bob));

        await Open("/password-login"); ticket = controller.CaptureTicket()!;
        await core.ExecuteScriptAsync("history.pushState({}, '', '/password-login#route')"); await Task.Delay(80);
        check("SPA source changes invalidate fill tickets", !await controller.FillAsync(ticket, alice));
        await Open("/password-login"); ticket = controller.CaptureTicket()!;
        await NavigateAsync(origin.Replace("127.0.0.1", "localhost") + "/password-login");
        await Until(() => !FocusedTab.IsLoading && controller.Ready);
        check("Wrong-origin navigation rejects a previously selected account", !await controller.FillAsync(ticket, alice));
        check("Wrong-origin lookup does not expose saved accounts", (await controller.AccountsAsync(controller.CaptureTicket()!)).Count == 0);

        await Open("/password-login"); ticket = controller.CaptureTicket()!;
        // Start decryption and immediately begin navigation. The operation must not land in the next document.
        var racingFill = controller.FillAsync(ticket, alice);
        await NavigateAsync(origin + "/password-login?newdocument");
        await racingFill; await Until(() => !FocusedTab.IsLoading && controller.Ready);
        check("Pending fill cannot enter a replacement document at the same origin", await Page("document.querySelector('[type=password]').value === ''"));
        check("Old unique-context ticket is rejected after same-origin replacement", !await controller.FillAsync(ticket, alice));

        using (var paused = new PausingCredentialProtector())
        {
            var raceVault = new CredentialVault(Path.Combine(App.ProfileDirectory, "race-vault"), paused);
            var raceAccount = await raceVault.SaveAsync(origin, "synthetic-race", SyntheticPassword, null);
            using var raceController = new PasswordController(core, raceVault, () => !FocusedTab.IsLoading, () => false);
            await raceController.InitializeAsync(); await Open("/password-login"); await Until(() => raceController.Ready); await Task.Delay(100);
            var blockedFill = raceController.FillAsync(raceController.CaptureTicket()!, raceAccount);
            try
            {
                await Until(() => paused.Entered.Task.IsCompleted);
                await NavigateAsync(origin.Replace("127.0.0.1", "localhost") + "/password-login");
                await Until(() => !FocusedTab.IsLoading && raceController.Ready);
            }
            finally { paused.Release(); }
            check("A deterministic decryption/navigation race aborts before disclosing the secret", !await blockedFill && await Page("document.querySelector('[type=password]').value === ''"));
        }

        await Open("/password-login");
        await core.ExecuteScriptAsync("globalThis.__slatePassword={fill:()=>true}; Object.defineProperty(HTMLInputElement.prototype,'value',{get(){return 'page-forged'},set(v){window.intercepted=true}})");
        check("Page-world controller and accessor forgery cannot replace the isolated fill implementation", await controller.FillAsync(controller.CaptureTicket()!, alice) && await Page("window.intercepted !== true"));

        await Open("/password-login"); ticket = controller.CaptureTicket()!;
        var sleepingTab = FocusedTab.Id;
        await NewTabAsync(); await SleepTabAsync(sleepingTab, true);
        check("Sleep invalidates a selected credential ticket", !await controller.FillAsync(ticket, alice));
        var placeholderTab = FocusedTab.Id; await ActivateTabAsync(sleepingTab); await CloseTabAsync(placeholderTab);
        check("Wake does not revive a pre-sleep ticket", !await controller.FillAsync(ticket, alice));
        check("Fresh explicit selection works after wake", await controller.FillAsync(controller.CaptureTicket()!, alice));

        foreach (var path in new[] { "/password-frame", "/password-same-frame", "/password-cross-action", "/password-ambiguous" })
        {
            await Open(path);
            check("Conservative fill denies fixture " + path, !await controller.FillAsync(controller.CaptureTicket()!, alice));
        }
        await Open("/password-dynamic");
        await core.ExecuteScriptAsync("document.body.insertAdjacentHTML('beforeend', document.querySelector('template').innerHTML)");
        check("Forms added dynamically can be explicitly filled", await controller.FillAsync(controller.CaptureTicket()!, alice));
        await Open("/password-only");
        check("Explicit saved-account selection supports password-only steps", await controller.FillAsync(controller.CaptureTicket()!, alice));

        await Open("/password-spa");
        await core.ExecuteScriptAsync("document.querySelector('[autocomplete=username]').value='synthetic-new'; document.querySelector('[type=password]').value='Synthetic-New-Only!123'; document.querySelector('form').requestSubmit()");
        await Until(() => controller.HasCandidate);
        check("Submission captures a candidate without persistence or a premature offer", controller.OfferOrigin is null && (await _credentialVault.ListAsync(origin)).Count == 2);
        await core.ExecuteScriptAsync("document.querySelector('form').remove()");
        await Until(() => controller.OfferOrigin is not null);
        check("SPA form disappearance produces a compact native save offer", _passwordNotice.IsOpen && !controller.IsUpdate);
        await Task.Delay(200);
        await CaptureElementAsync(_passwordNotice, Path.Combine(Path.GetDirectoryName(App.SmokeOutput!)!, "password-save-offer.png"));
        check("An explicitly confirmed offer is saved", await controller.SaveOfferAsync());
        check("Candidate cleared after save", !controller.HasCandidate && (await _credentialVault.ListAsync(origin)).Count == 3);

        await Open("/password-spa");
        check("Existing account can be filled before a login", await controller.FillAsync(controller.CaptureTicket()!, alice));
        await core.ExecuteScriptAsync("document.querySelector('form').requestSubmit()"); await Until(() => controller.HasCandidate);
        await core.ExecuteScriptAsync("document.querySelector('form').remove()"); await Until(() => !controller.HasCandidate);
        check("Unchanged credentials do not prompt", controller.OfferOrigin is null);

        await Open("/password-spa");
        await core.ExecuteScriptAsync("document.querySelector('[autocomplete=username]').value='synthetic-alice'; document.querySelector('[type=password]').value='Synthetic-Updated-Only!456'; document.querySelector('form').requestSubmit()");
        await Until(() => controller.HasCandidate); await core.ExecuteScriptAsync("document.querySelector('form').remove()"); await Until(() => controller.OfferOrigin is not null);
        check("Changed exact-account password offers update", controller.IsUpdate); controller.Dismiss();

        await Open("/password-spa");
        await core.ExecuteScriptAsync("document.querySelector('[autocomplete=username]').value='synthetic-forged'; document.querySelector('[type=password]').value='Synthetic-Forged-Only!123'; document.querySelector('form').dispatchEvent(new Event('submit',{bubbles:true,cancelable:true})); document.querySelector('form').remove()");
        await Task.Delay(1100);
        check("Untrusted synthetic submit events cannot capture credentials", !controller.HasCandidate);

        await Open("/password-unidentified-create");
        check("Generation declines creation forms without an identifiable account", !await controller.GenerateAsync(controller.CaptureTicket()!));
        await Open("/password-create");
        check("Password generation fills creation/confirmation fields", await controller.GenerateAsync(controller.CaptureTicket()!));
        check("Generated password meets default length and confirmation equality", await Page("(()=>{const p=[...document.querySelectorAll('[type=password]')];return p[0].value.length===24 && p[0].value===p[1].value})()"));
        await core.ExecuteScriptAsync("document.querySelector('[autocomplete=username]').value='synthetic-generated'; document.querySelector('form').requestSubmit()"); await Until(() => controller.HasCandidate);
        await core.ExecuteScriptAsync("document.querySelector('form').remove()"); await Until(() => controller.OfferOrigin is not null);
        check("Generated passwords enter the same explicit save pipeline", !controller.IsUpdate); controller.Dismiss();

        // A regular POST navigation provides evidence only after successful same-origin completion.
        await Open("/password-login");
        await core.ExecuteScriptAsync("document.querySelector('[autocomplete=username]').value='synthetic-post'; document.querySelector('[type=password]').value='Synthetic-Post-Only!123'; document.querySelector('form').requestSubmit()");
        await Until(() => controller.OfferOrigin is not null);
        check("Successful same-origin POST navigation can offer save", core.Source.EndsWith("/password-success", StringComparison.Ordinal)); controller.Dismiss();

        await Open("/password-slow-login");
        await core.ExecuteScriptAsync("document.querySelector('[autocomplete=username]').value='synthetic-slow'; document.querySelector('[type=password]').value='Synthetic-Slow-Only!123'; document.querySelector('form').requestSubmit()");
        await Until(() => controller.OfferOrigin is not null);
        check("Candidates survive an in-progress navigation until success evidence arrives", core.Source.EndsWith("/password-success-slow", StringComparison.Ordinal)); controller.Dismiss();

        var oldTab = FocusedTab.Id;
        await NewTabAsync(origin + "/password-spa", true); await Until(() => Controller().Ready); await Task.Delay(100);
        var temporaryController = Controller(); var beforeUse = (await _credentialVault.ListAsync(origin)).Single(e => e.Id == alice.Id).LastUsed;
        check("Temporary tabs allow explicit fill", await temporaryController.FillAsync(temporaryController.CaptureTicket()!, alice));
        check("Temporary fill does not persist last-used metadata", (await _credentialVault.ListAsync(origin)).Single(e => e.Id == alice.Id).LastUsed == beforeUse);
        await CurrentCore()!.ExecuteScriptAsync("document.querySelector('form').requestSubmit();document.querySelector('form').remove()"); await Task.Delay(1100);
        check("Temporary submission never captures or offers a save", !temporaryController.HasCandidate && temporaryController.OfferOrigin is null);
        await CloseTabAsync(FocusedTab.Id); await ActivateTabAsync(oldTab);
        check("Closed runtime rejects all credential work", temporaryController.CaptureTicket() is null);

        Save();
        string vaultText = await File.ReadAllTextAsync(Path.Combine(App.ProfileDirectory, "credentials.v1.json"));
        string sessionText = await File.ReadAllTextAsync(Path.Combine(App.ProfileDirectory, "session.json"));
        check("DPAPI vault and session contain no synthetic plaintext passwords", !vaultText.Contains(SyntheticPassword, StringComparison.Ordinal) && !sessionText.Contains(SyntheticPassword, StringComparison.Ordinal));
        var reloaded = new CredentialVault(App.ProfileDirectory, new WindowsCredentialProtector());
        check("Windows vault reload decrypts a single requested entry", await reloaded.RevealAsync(alice.Id, alice.Revision) == SyntheticPassword);
        var manager = ShowPasswordManagerAsync(); await Until(() => _activeDialog?.IsLoaded == true); await Task.Delay(200);
        check("Password settings list renders masked without plaintext passwords", !Descendants(_activeDialog!).OfType<TextBlock>().Any(t => t.Text.Contains(SyntheticPassword, StringComparison.Ordinal)));
        await CaptureElementAsync(_activeDialog!, Path.Combine(Path.GetDirectoryName(App.SmokeOutput!)!, "password-manager-masked.png"));
        var managerPanel = (StackPanel)_activeDialog!.Content;
        var managerList = managerPanel.Children.OfType<ListView>().Single(); managerList.SelectedIndex = 0;
        var revealButton = Descendants(_activeDialog!).OfType<Button>().Single(b => b.Content as string == "Reveal");
        ((IInvokeProvider)new ButtonAutomationPeer(revealButton).GetPattern(PatternInterface.Invoke)).Invoke();
        await Until(() => Descendants(_activeDialog!).OfType<TextBlock>().Any(t => t.Text == SyntheticPassword));
        check("Deliberate reveal decrypts only the selected password", revealButton.Content as string == "Hide");
        managerList.SelectedIndex = 1;
        check("Changing account selection immediately remasks a revealed password", !Descendants(_activeDialog!).OfType<TextBlock>().Any(t => t.Text == SyntheticPassword));
        _activeDialog!.Hide(); await manager;

        await Open("/password-spa");
        await core.ExecuteScriptAsync("document.querySelector('[autocomplete=username]').value='synthetic-close'; document.querySelector('[type=password]').value='Synthetic-Close-Only!123'; document.querySelector('form').requestSubmit()"); await Until(() => controller.HasCandidate);
        await core.ExecuteScriptAsync("document.querySelector('form').remove()"); await Until(() => controller.OfferOrigin is not null);
        var beforeClosure = (await _credentialVault.ListAsync()).Count;
        DisposeRuntime(FocusedTab.Id); await RefreshAsync(); await Until(() => Controller().Ready);
        check("Runtime destruction dismisses candidates and prevents stale save approval", !controller.HasCandidate && !await controller.SaveOfferAsync() && (await _credentialVault.ListAsync()).Count == beforeClosure);
        check("Recreated WebView receives a new document controller", !ReferenceEquals(controller, Controller()) && Controller().CaptureTicket() is not null);
    }

    private static string? PasswordFixture(string request, int port)
    {
        string path = request.Split(' ').ElementAtOrDefault(1)?.Split('?')[0] ?? "";
        if (!path.StartsWith("/password-", StringComparison.Ordinal)) return null;
        string inputs = "<input autocomplete='username' name='username'><input type='password' autocomplete='current-password' name='password'>";
        if (path == "/password-only") inputs = "<input type='password' autocomplete='current-password' name='password'>";
        if (path == "/password-create") inputs = "<input autocomplete='username' name='username'><input type='password' autocomplete='new-password' name='password'><input type='password' autocomplete='new-password' name='confirmation'>";
        if (path == "/password-unidentified-create") inputs = "<input type='password' autocomplete='new-password' name='password'>";
        if (path == "/password-ambiguous") inputs += "<input type='password' name='confirmation'>";
        string action = path == "/password-cross-action" ? "http://localhost:" + port + "/password-success" : "/password-success";
        if (path == "/password-slow-login") action = "/password-success-slow";
        string prevent = path is "/password-spa" or "/password-create" ? "event.preventDefault();" : "";
        string form = "<form method='post' action='" + action + "' onsubmit='window.submitted=true;" + prevent + "'>" + inputs + "<button>Sign in</button></form>";
        string body = path switch
        {
            "/password-success" or "/password-success-slow" => "<p>Fixture success</p>",
            "/password-frame" => "<iframe src='http://localhost:" + port + "/password-login'></iframe>",
            "/password-same-frame" => "<iframe src='/password-login'></iframe>",
            "/password-dynamic" => "<template>" + form + "</template>",
            _ => form
        };
        return "<!doctype html><html><head><title>Slate Password Fixture</title></head><body>" + body + "</body></html>";
    }
}
