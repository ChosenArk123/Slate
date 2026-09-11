// Runs only in a named isolated world in the top-level document. No page globals or frame traversal.
function (bindingName, nonce, allowCapture) {
    if (globalThis.__slatePassword) return false;
    let filled = false, submitted = false, formAtSubmit = null, filledUsername = '';
    const visible = e => e.isConnected && !e.disabled && !e.readOnly && e.getClientRects().length > 0 &&
        getComputedStyle(e).visibility === 'visible' && getComputedStyle(e).display !== 'none';
    const hint = e => (e.autocomplete || '').toLowerCase().split(/\s+/).pop();
    const fieldText = e => [e.autocomplete, e.name, e.id, e.getAttribute('aria-label'), e.placeholder]
        .filter(Boolean).join(' ').toLowerCase().replace(/[^a-z0-9]+/g, ' ');
    const oneTimeCode = e => {
        const text = fieldText(e);
        return hint(e) === 'one-time-code' || /(^| )(otp|totp|2fa|mfa)( |$)/.test(text) ||
            /one time|verification ?code|authentication ?code|security ?code/.test(text);
    };
    function discover(mode, submittedForm = null) {
        const passwords = [...document.querySelectorAll('input[type="password"]')].filter(e => visible(e) && !oneTimeCode(e));
        const forms = submittedForm instanceof HTMLFormElement
            ? [submittedForm]
            : [...new Set(passwords.map(p => p.form).filter(Boolean))];
        const found = [];
        for (const form of forms) {
            // No cross-origin actions, GET credentials, detached controls, or password fields outside forms.
            if (new URL(form.action, location.href).origin !== location.origin || form.method.toLowerCase() !== 'post') continue;
            const all = [...form.elements].filter(e => e instanceof HTMLInputElement);
            const pass = passwords.filter(p => p.form === form);
            const fresh = pass.filter(p => hint(p) === 'new-password');
            const current = pass.filter(p => hint(p) !== 'new-password');
            let targets;
            if (mode === 'generate') {
                if (fresh.length < 1 || fresh.length > 2 || current.length > 1) continue;
                targets = fresh;
            } else if (mode === 'capture' && fresh.length) {
                if (fresh.length > 2 || current.length > 1 || fresh.some(p => p.value !== fresh[0].value)) continue;
                targets = [fresh[0]];
            } else {
                if (fresh.length || pass.length !== 1 || !['', 'on', 'off', 'current-password'].includes(hint(pass[0]))) continue;
                if (mode === 'auto' && hint(pass[0]) !== 'current-password') continue;
                targets = pass;
            }
            let names = all.filter(e => visible(e) && ['text', 'email', 'tel'].includes(e.type) && hint(e) === 'username');
            if (!names.length) names = all.filter(e => visible(e) && ['text', 'email'].includes(e.type) &&
                !['new-password', 'one-time-code'].includes(hint(e)) && !oneTimeCode(e));
            if (names.length > 1) continue;
            if (mode === 'generate' && !names.length) continue; // Do not generate a password we cannot associate with an account for saving.
            if (mode === 'auto' && (!names.length || hint(names[0]) !== 'username')) continue;
            found.push({ form, password: targets[0], targets, username: names[0] || null });
        }
        return found.length === 1 ? found[0] : null;
    }
    document.addEventListener('submit', e => {
        if (!allowCapture || !e.isTrusted || submitted || !(e.target instanceof HTMLFormElement)) return;
        // The trusted event identifies the submitted form. Other login forms elsewhere on the page
        // must not make this otherwise eligible submission ambiguous.
        const f = discover('capture', e.target);
        if (!f || e.target !== f.form || !f.password.value || f.password.value.length > 4096) return;
        if (!f.username && !filledUsername) return; // A password-only step does not identify an account to update.
        const username = f.username ? f.username.value : filledUsername;
        if (username.length > 1024 || /[\x00-\x1f\x7f]/.test(username)) return;
        submitted = true; formAtSubmit = f.form;
        globalThis[bindingName](JSON.stringify({ v: 1, nonce, username, password: f.password.value }));
    }, true);
    globalThis.__slatePassword = Object.freeze({
        status: () => ({ login: !!discover('login'), generate: !!discover('generate'),
            gone: submitted && (!formAtSubmit.isConnected || ![...formAtSubmit.querySelectorAll('input[type="password"]')].some(visible)),
            anyPassword: [...document.querySelectorAll('input[type="password"]')].some(visible) }),
        fill: (username, password, generate, automatic) => {
            if (filled || submitted) return false;
            const f = discover(generate ? 'generate' : automatic ? 'auto' : 'login');
            if (!f || (!generate && (f.password.value || (f.username && f.username.value))) || (generate && f.targets.some(p => p.value))) return false;
            filled = true;
            if (!generate) filledUsername = username;
            // Native setters from the isolated world avoid page-overridden JavaScript accessors.
            const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
            if (!generate && f.username) setter.call(f.username, username);
            for (const p of f.targets) setter.call(p, password);
            // All writes precede page event handlers, which may synchronously mutate the DOM.
            for (const e of [f.username, ...f.targets].filter(Boolean)) {
                e.dispatchEvent(new Event('input', { bubbles: true }));
                e.dispatchEvent(new Event('change', { bubbles: true }));
            }
            return true;
        }
    });
    return true;
}
