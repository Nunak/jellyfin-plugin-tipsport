using System.Text.Json;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.Tipsport;

public sealed record BrowserLoginStatus(bool Available, bool Active, string State, string Message, DateTimeOffset? LastConfirmed, bool AutomaticLogin = false, bool AutomaticLoginPaused = false, bool SavedPassword = false, bool RecoveryAvailable = false);

internal interface ILoginBrowser : IAsyncDisposable
{
    Task<string?> ReadSession(string region, CancellationToken token);
    Task PrepareCredentials(string region, CancellationToken token) => throw new TipsportException("Prohlížeč nepodporuje přihlášení z tohoto zařízení.");
    Task<string> SubmitCredentials(LoginCredentials credentials, CancellationToken token) => throw new TipsportException("Prohlížeč nepodporuje přihlášení z tohoto zařízení.");
}

internal interface ILoginBrowserFactory
{
    bool Available { get; }
    Task<ILoginBrowser> Open(string profile, string region, bool interactive, CancellationToken token);
}

internal interface ISessionVerifier
{
    Task<bool> Verify(string session, string region, CancellationToken token);
}

internal sealed class ProviderSessionVerifier : ISessionVerifier
{
    public async Task<bool> Verify(string session, string region, CancellationToken token)
    {
        var curl = ProviderHttp.CurlPath ?? throw new TipsportException("Přihlášení přes prohlížeč vyžaduje systémový curl.");
        try
        {
            using var result = await ProviderHttp.Fetch(curl, new(TipsportClient.Origin(region) + "/rest/articles/v1/tv/program?day=0&articleId="), session, token).ConfigureAwait(false);
            Catalogue.Parse(result.RootElement);
            return true;
        }
        catch (TipsportException e) when (e.AuthenticationRequired) { return false; }
    }
}

/// <summary>Only the server's dedicated browser profile is used. Optional credentials are encrypted separately.</summary>
public sealed class BrowserLogin : IHostedService, IDisposable
{
    private readonly SessionStore _sessions;
    private readonly ILoginBrowserFactory _factory;
    private readonly ISessionVerifier _verifier;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Task<bool>? _operation;
    private DateTimeOffset _nextRecovery;
    private DateTimeOffset _nextCredentialAttempt;
    private string _region = "CZ";
    private string _state = "idle";
    private string _message = "Přihlaste se v běžném okně Tipsportu na serveru. Plugin přihlášení převezme automaticky.";
    private DateTimeOffset? _confirmed;
    private readonly TimeSpan _poll;
    private int _disposed;

    internal BrowserLogin(SessionStore sessions, ILoginBrowserFactory factory, ISessionVerifier verifier, TimeSpan? poll = null)
    { _sessions = sessions; _factory = factory; _verifier = verifier; _poll = poll ?? TimeSpan.FromSeconds(5); _region = Plugin.Instance?.Configuration.Region ?? "CZ"; }

    public BrowserLoginStatus Status
    {
        get
        {
            lock (_gate)
            {
                var available = _factory.Available && ProviderHttp.CurlPath != null;
                var message = !available ? "Přihlašovací okno není na tomto serveru dostupné. Je potřeba Chrome nebo Edge, systémový curl a přihlášená plocha serveru."
                    : _state == "idle" && _sessions.HasSession ? "Přihlášení je uložené. Tlačítkem jej můžete obnovit bez hledání cookie." : _message;
                var saved = _sessions.Credentials(_region);
                return new(available, _operation is { IsCompleted: false }, _state, message, _confirmed, saved != null, saved?.Paused == true, _sessions.HasSavedPassword, saved is { Paused: false } || _sessions.IsBrowserEnrolled(_region));
            }
        }
    }

    public BrowserLoginStatus Begin(string region)
    {
        region = Region(region);
        lock (_gate)
        {
            if (_operation is { IsCompleted: false }) return Status;
            _region = region;
            if (!Status.Available) throw new TipsportException("Na serveru není dostupný Chrome/Edge nebo systémový curl. Přihlášení v okně vyžaduje přihlášenou plochu serveru; v kontejneru použijte pokročilou správu relace.");
            SetState("waiting", "Na serveru se otevírá samostatné okno Tipsportu. Přihlaste se v něm; heslo zadáváte pouze Tipsportu. Na dokončení máte 10 minut.");
            var revision = _sessions.Revision;
            _operation = Task.Run(() => Run(region, true, expectedRevision: revision), _shutdown.Token);
            return Status;
        }
    }

    public BrowserLoginStatus BeginCredentials(string region, string username, string password, bool remember, long? expectedRevision = null)
    {
        var credentials = LoginCredentials.Create(region, username, password);
        lock (_gate)
        {
            if (_operation is { IsCompleted: false }) throw new TipsportException("Přihlášení už probíhá. Počkejte na jeho dokončení.");
            if (!Status.Available) throw new TipsportException("Na serveru je potřeba podporovaný Chrome/Edge a systémový curl.");
            if (DateTimeOffset.UtcNow < _nextCredentialAttempt) throw new TipsportException("Před dalším pokusem chvíli počkejte. Heslo se automaticky neopakuje.");
            _nextCredentialAttempt = DateTimeOffset.UtcNow.AddSeconds(30);
            _region = region;
            SetState("signing-in", "Přihlašuji k Tipsportu na serveru. Toto zařízení může zůstat na mobilních datech; okno serveru otevírat nemusíte.");
            var revision = expectedRevision ?? _sessions.Revision;
            _operation = Task.Run(() => Run(region, false, credentials, remember, true, revision), _shutdown.Token);
            return Status;
        }
    }

    public BrowserLoginStatus BeginSavedCredentials(string region)
    {
        var revision = _sessions.Revision;
        var saved = _sessions.Credentials(region) ?? throw new TipsportException("Pro aktuální relaci nejsou uložené přihlašovací údaje. Zadejte je znovu v nastavení pluginu.");
        // Explicit administrator action permits one bounded retry; background recovery never resumes paused passwords.
        try { return BeginCredentials(region, saved.Username, saved.Password, true, revision); }
        finally { saved.Password = ""; }
    }

    public void ForgetCredentials()
    {
        lock (_gate)
        {
            if (_operation is { IsCompleted: false }) throw new TipsportException("Nejprve počkejte na dokončení přihlášení.");
            try { _sessions.ForgetCredentials(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new TipsportException("Uložené přihlašovací údaje nelze odebrat. Ověřte práva účtu serveru."); }
        }
    }

    public async Task<bool> TryRecover(string region, CancellationToken token)
    {
        region = Region(region);
        Task<bool> recovery;
        lock (_gate)
        {
            // Never replace or compete with an interactive login. Do not repeatedly open a browser on every request.
            var revision = _sessions.Revision;
            var saved = _sessions.Credentials(region);
            if (!Status.Available || (saved == null && (!_sessions.IsBrowserEnrolled(region) || !File.Exists(Path.Combine(_sessions.BrowserProfilePath, "Local State"))))) return false;
            if (_operation is { IsCompleted: false })
            {
                if (_state is "waiting" or "signing-in") return false;
                recovery = _operation;
            }
            else
            {
                if (DateTimeOffset.UtcNow < _nextRecovery) return false;
                _nextRecovery = DateTimeOffset.UtcNow.AddMinutes(5);
                _region = region;
                SetState("recovering", "Obnovuji přihlášení z vyhrazeného profilu Tipsportu…");
                recovery = _operation = Task.Run(() => Run(region, false, saved?.Paused == false ? saved : null, saved?.Paused == false, expectedRevision: revision), _shutdown.Token);
            }
        }
        // A cancelled viewer must not cancel a recovery shared by other viewers.
        return await recovery.WaitAsync(token).ConfigureAwait(false);
    }

    private async Task<bool> Run(string region, bool interactive, LoginCredentials? credentials = null, bool remember = false, bool explicitCredentials = false, long? expectedRevision = null)
    {
        var revision = expectedRevision ?? _sessions.Revision;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        lifetime.CancelAfter(interactive ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(credentials != null ? 75 : 35));
        var attemptedPassword = false;
        var usedCredentialFlow = false;
        var lastStep = "waiting";
        try
        {
            await using var browser = await _factory.Open(_sessions.BrowserProfilePath, region, interactive, lifetime.Token).ConfigureAwait(false);
            // A requested account switch must not accidentally accept the previous account's cookie.
            if (explicitCredentials) await browser.PrepareCredentials(region, lifetime.Token).ConfigureAwait(false);
            while (true)
            {
                lifetime.Token.ThrowIfCancellationRequested();
                var candidate = !explicitCredentials || attemptedPassword ? await browser.ReadSession(region, lifetime.Token).ConfigureAwait(false) : null;
                if (candidate != null && await _verifier.Verify(candidate, region, lifetime.Token).ConfigureAwait(false))
                {
                    // An anonymous cookie is never allowed to replace the existing working session.
                    _sessions.SaveBrowserSession(candidate, region, revision);
                    if (credentials != null && remember) _sessions.SaveCredentials(credentials, candidate);
                    lock (_gate) { _confirmed = DateTimeOffset.UtcNow; SetState("signed-in", "Přihlášení k Tipsportu je ověřené a uložené. Nabídku zápasů lze načíst."); }
                    return true;
                }
                if (credentials != null)
                {
                    usedCredentialFlow = true;
                    var result = lastStep = await browser.SubmitCredentials(credentials, lifetime.Token).ConfigureAwait(false);
                    attemptedPassword |= result == "submitted";
                    if (result == "provider-error") throw new TipsportException("Tipsport na serveru místo přihlašovacího formuláře zobrazuje vlastní chybovou stránku. Heslo nebylo odesláno. Nejde o blokování vyskakovacích oken v mobilu.");
                    if (result == "wrong-origin") throw new TipsportException("Přihlašovací stránka opustila očekávanou doménu Tipsportu. Heslo nebylo odesláno; správce musí ověřit změnu přihlašování.");
                    if (result == "manual-required") throw new TipsportException("Tipsport vyžaduje CAPTCHA nebo další ověření. Automatické zadávání se zastavilo; dokončete ověření přímo v okně Tipsportu na serveru. Z mobilu to zatím tento plugin neumí.");
                    if (result == "failed") throw new TipsportException("Tipsport přihlášení nepřijal. Ověřte jméno a heslo; pokus se automaticky neopakuje.");
                    lock (_gate) SetState(interactive ? "waiting" : explicitCredentials ? "signing-in" : "recovering", result switch {
                        "submitted" => "Přihlašovací formulář byl odeslán. Ověřuji přihlášenou relaci u Tipsportu…",
                        "opening-form" => "Otevírám přihlašovací formulář Tipsportu na serveru…",
                        "notice" => "Tipsport před přihlášením zobrazuje upozornění. Čekám na zpřístupnění formuláře…",
                        "form-disabled" => "Formulář Tipsportu zatím nepovolil odeslání. Heslo nebylo odesláno…",
                        "form-changed" => "Formulář Tipsportu se změnil: nelze najít přihlašovací tlačítko…",
                        _ => "Čekám na načtení přihlašovací stránky Tipsportu na serveru…" });
                }
                await Task.Delay(_poll, lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            lock (_gate) SetState("login-required", credentials == null ? "Přihlášení se nedokončilo. Použijte přihlášení v nastavení pluginu." : lastStep switch {
                "submitted" => "Tipsport po odeslání formuláře neposkytl ověřenou relaci. Heslo se znovu automaticky neodesílá.",
                "notice" => "Tipsport vyžaduje potvrzení upozornění v okně na serveru; z mobilního formuláře jej zatím dokončit nelze. Heslo nebylo odesláno.",
                "form-disabled" or "form-changed" => "Přihlašovací formulář Tipsportu nedovolil odeslání. Heslo nebylo odesláno; správce musí ověřit změnu formuláře.",
                _ => "Přihlašovací stránka Tipsportu na serveru se nenačetla včas. Heslo nebylo odesláno. Nejde o blokování oken v mobilu." });
            return false;
        }
        catch (TipsportException e)
        {
            lock (_gate) SetState("error", e.Message);
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Net.WebSockets.WebSocketException or JsonException or System.ComponentModel.Win32Exception or InvalidOperationException or KeyNotFoundException)
        {
            // Raw browser errors can contain private profile paths or protocol data.
            lock (_gate) SetState("error", "Přihlašovací okno se nepodařilo použít. Zkontrolujte přihlášenou plochu serveru, dostupnost prohlížeče a práva ke složce Tipsport.private.");
            return false;
        }
        finally
        {
            // Failed saved-password attempts remain paused across server restarts, avoiding an account lockout.
            if (!explicitCredentials && usedCredentialFlow && _state != "signed-in")
                try { _sessions.PauseCredentials(region); } catch (TipsportException) { }
            if (credentials != null) credentials.Password = "";
        }
    }

    private void SetState(string state, string message) { _state = state; _message = message; }
    internal static string Region(string value) => value is "CZ" or "SK" ? value : throw new TipsportException("Neplatná země Tipsportu.");
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _shutdown.Cancel();
        Task<bool>? operation;
        lock (_gate) operation = _operation;
        if (operation != null) { try { await operation.WaitAsync(cancellationToken).ConfigureAwait(false); } catch (OperationCanceledException) { } }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel(); _shutdown.Dispose();
    }
}
