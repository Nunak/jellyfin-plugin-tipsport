using System.Text.Json;

namespace Jellyfin.Plugin.Tipsport;

internal static class ProviderLoginForm
{
    internal const string HealthScript = "(/Tipsport.*(?:Chyba|Error)/i.test(document.title) && /došlo k chybě|požadavku na server|request.*error/i.test(document.body?.innerText || ''))";
    internal static string AuthenticatedExpression(string region) => "(" + AuthenticatedScript + ")(" + JsonSerializer.Serialize(TipsportClient.Origin(BrowserLogin.Region(region))) + ")";
    internal const string AuthenticatedScript = """
        function (origin) {
            if (window.top !== window || location.origin !== origin) return false;
            const visible = e => !!e && [...e.getClientRects()].some(r => r.width > 0 && r.height > 0) && getComputedStyle(e).visibility !== 'hidden' && getComputedStyle(e).display !== 'none';
            const text = e => (e.textContent || '').trim().replace(/\s+/g, ' ');
            const controls = [...document.querySelectorAll('a, button')].filter(visible);
            if (controls.some(e => /^(Přihlásit|Přihlásit se|Prihlásiť|Prihlásiť sa|Log in|Sign in)$/i.test(text(e)))) return false;
            return controls.some(e => /^(Vložit peníze|Vložiť peniaze|Odhlásit|Odhlásit se|Odhlásiť|Odhlásiť sa)$/i.test(text(e)));
        }
        """;
    // The script can only fill the provider's own top-level page. It never returns page text, credentials or cookies.
    internal static string Expression(LoginCredentials credentials, bool submitted) => "(" + Script + ")(" +
        JsonSerializer.Serialize(new { origin = TipsportClient.Origin(credentials.Region), username = submitted ? "" : credentials.Username,
            password = submitted ? "" : credentials.Password, submitted }) + ")";

    internal const string Script = """
        function (args) {
            // A restored Chromium tab can announce its destination before its new document exists.
            // Wait without touching any fields; origin checks still gate every credential submission.
            if (document.readyState === 'loading' || location.href === 'about:blank') return 'waiting';
            if (window.top !== window || location.origin !== args.origin) return 'wrong-origin';
            if (/Tipsport.*(?:Chyba|Error)/i.test(document.title) && /došlo k chybě|požadavku na server|request.*error/i.test(document.body?.innerText || '')) return 'provider-error';
            const visible = e => !!e && [...e.getClientRects()].some(r => r.width > 0 && r.height > 0) && getComputedStyle(e).visibility !== 'hidden' && getComputedStyle(e).display !== 'none';
            if ([...document.querySelectorAll('iframe[src*="captcha"], iframe[src*="challenges.cloudflare.com"], #challenge-running, #cf-challenge-running')].some(visible)) return 'manual-required';
            const text = e => (e.textContent || '').trim().replace(/\s+/g, ' ');
            const errors = [...document.querySelectorAll('[role="alert"], [class*="ErrorMessage"], [class*="errorMessage"]')].filter(visible);
            if (args.submitted) {
                if (errors.some(e => /nesprávn|neplatn|nesprávne|incorrect|invalid|špatné|chybn/i.test(text(e)))) return 'failed';
                return 'submitted';
            }
            if ([...document.querySelectorAll('[class*="GamblingWarning-styled__ShadowOverlay"]')].some(visible)) return 'notice';
            const password = [...document.querySelectorAll('input[type="password"]')].find(visible);
            const username = [...document.querySelectorAll('input[name="username"], input[autocomplete="username"], input[type="email"]')].find(visible);
            if (password && username) {
                const scope = password.closest('form') || password.closest('[role="dialog"]') || document;
                const submit = [...scope.querySelectorAll('button, input[type="submit"]')].find(e => visible(e) && /^(Přihlásit se|Prihlásiť sa|Log in|Sign in)$/i.test(text(e) || e.value || ''));
                if (!submit) return 'form-changed';
                const set = (field, value) => {
                    Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(field, value);
                    field.dispatchEvent(new Event('input', {bubbles: true}));
                    field.dispatchEvent(new Event('change', {bubbles: true}));
                };
                set(username, args.username); set(password, args.password);
                if (submit.disabled) return 'form-disabled';
                submit.click();
                return 'submitted';
            }
            const login = [...document.querySelectorAll('button, a')].find(e => visible(e) && /^(Přihlásit|Prihlásiť|Log in|Sign in)$/i.test(text(e)));
            if (login) { login.click(); return 'opening-form'; }
            return 'waiting';
        }
        """;
}
