using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.Tipsport;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace Tipsport.Tests;

public sealed class BrowserLoginTests
{
    [Fact]
    public async Task VerifiedBrowserLoginStoresSessionWithoutReturningItAndClosesItsWindow()
    {
        using var setup = new Setup();
        const string session = "synthetic-authenticated-browser-session";
        var browser = new FakeBrowser(_ => Task.FromResult<string?>(session));
        var factory = new FakeFactory(browser);
        using var login = new BrowserLogin(setup.Store, factory, new Verifier(_ => true), TimeSpan.FromMilliseconds(2));
        var started = login.Begin("CZ");
        Assert.True(started.Active);
        await WaitUntil(() => !login.Status.Active);
        Assert.Equal("signed-in", login.Status.State);
        Assert.Equal(session, setup.Store.Load());
        Assert.True(browser.Disposed);
        Assert.True(factory.Interactive);
        Assert.DoesNotContain(session, JsonSerializer.Serialize(login.Status));
        Assert.DoesNotContain(setup.Root, JsonSerializer.Serialize(login.Status));
    }

    [Fact]
    public async Task AnonymousOrUnverifiedCookieNeverReplacesExistingSessionAndDuplicateClicksShareOneWindow()
    {
        using var setup = new Setup();
        setup.Store.Save("synthetic-working-original-session");
        var browser = new FakeBrowser(_ => Task.FromResult<string?>("synthetic-anonymous-session"));
        var factory = new FakeFactory(browser);
        var verifier = new Verifier(_ => false);
        using var login = new BrowserLogin(setup.Store, factory, verifier, TimeSpan.FromMilliseconds(2));
        login.Begin("CZ");
        login.Begin("CZ");
        await WaitUntil(() => verifier.Calls > 0);
        Assert.Equal(1, factory.Calls);
        Assert.Equal("synthetic-working-original-session", setup.Store.Load());
        await login.StopAsync(CancellationToken.None);
        Assert.True(browser.Disposed);
        Assert.Equal("login-required", login.Status.State);
    }

    [Fact]
    public async Task ConcurrentExpiredRequestsShareOneRecoveryAndCancellationOfOneViewerDoesNotCancelTheOthers()
    {
        using var setup = new Setup();
        Directory.CreateDirectory(setup.Store.BrowserProfilePath);
        File.WriteAllText(Path.Combine(setup.Store.BrowserProfilePath, "Local State"), "{}");
        setup.Store.SaveBrowserSession("synthetic-previous-browser-session", "CZ");
        var candidate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeFactory(new FakeBrowser(ct => candidate.Task.WaitAsync(ct)));
        using var login = new BrowserLogin(setup.Store, factory, new Verifier(_ => true));
        using var cancelled = new CancellationTokenSource();
        var first = login.TryRecover("CZ", cancelled.Token);
        var second = login.TryRecover("CZ", CancellationToken.None);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        candidate.SetResult("synthetic-renewed-browser-session");
        Assert.True(await second);
        Assert.Equal(1, factory.Calls);
        Assert.False(factory.Interactive);
        Assert.Equal("synthetic-renewed-browser-session", setup.Store.Load());
    }

    [Fact]
    public async Task FailedAutomaticRecoveryIsThrottledAndPreservesTheOldSession()
    {
        using var setup = new Setup();
        setup.Store.Save("synthetic-original-session");
        Directory.CreateDirectory(setup.Store.BrowserProfilePath);
        File.WriteAllText(Path.Combine(setup.Store.BrowserProfilePath, "Local State"), "{}");
        setup.Store.SaveBrowserSession("synthetic-original-session", "CZ");
        var factory = new FakeFactory(new FakeBrowser(_ => throw new IOException("sensitive-profile-path-and-cookie")));
        using var login = new BrowserLogin(setup.Store, factory, new Verifier(_ => true));
        Assert.False(await login.TryRecover("CZ", CancellationToken.None));
        Assert.False(await login.TryRecover("CZ", CancellationToken.None));
        Assert.Equal(1, factory.Calls);
        Assert.Equal("synthetic-original-session", setup.Store.Load());
        Assert.DoesNotContain("sensitive-profile-path-and-cookie", JsonSerializer.Serialize(login.Status));
        Assert.Equal("error", login.Status.State);
    }

    [Fact]
    public async Task LegacyCookieInstallDoesNotLaunchBrowserWithoutAnEnrolledProfile()
    {
        using var setup = new Setup();
        var factory = new FakeFactory(new FakeBrowser(_ => Task.FromResult<string?>(null)));
        using var login = new BrowserLogin(setup.Store, factory, new Verifier(_ => true));
        Assert.False(await login.TryRecover("CZ", CancellationToken.None));
        Assert.Equal(0, factory.Calls);
    }

    [Fact]
    public async Task ImportingAnotherSessionOrChangingRegionCannotSilentlyRestoreAPreviouslyEnrolledAccount()
    {
        using var setup = new Setup();
        Directory.CreateDirectory(setup.Store.BrowserProfilePath);
        File.WriteAllText(Path.Combine(setup.Store.BrowserProfilePath, "Local State"), "{}");
        setup.Store.SaveBrowserSession("synthetic-first-account-session", "CZ");
        Assert.True(setup.Store.IsBrowserEnrolled("CZ"));
        Assert.False(setup.Store.IsBrowserEnrolled("SK"));
        setup.Store.Save("synthetic-different-account-session");
        var factory = new FakeFactory(new FakeBrowser(_ => Task.FromResult<string?>(null)));
        using var login = new BrowserLogin(setup.Store, factory, new Verifier(_ => true));
        Assert.False(await login.TryRecover("CZ", CancellationToken.None));
        Assert.Equal(0, factory.Calls);
        Assert.Equal("synthetic-different-account-session", setup.Store.Load());
    }

    [Fact]
    public async Task ASlowBrowserLoginCannotOverwriteANewerManualSession()
    {
        using var setup = new Setup();
        var candidate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeFactory(new FakeBrowser(ct => candidate.Task.WaitAsync(ct)));
        using var login = new BrowserLogin(setup.Store, factory, new Verifier(_ => true));
        login.Begin("CZ");
        await WaitUntil(() => factory.Calls > 0);
        setup.Store.Save("synthetic-newer-manual-session");
        candidate.SetResult("synthetic-older-browser-session");
        await WaitUntil(() => !login.Status.Active);
        Assert.Equal("synthetic-newer-manual-session", setup.Store.Load());
        Assert.Equal("error", login.Status.State);
        Assert.Contains("mezitím změnilo", login.Status.Message);
    }

    [Theory]
    [InlineData("", "CZ")]
    [InlineData("www.tipsport.cz.evil.test", "CZ")]
    [InlineData("evil.tipsport.cz", "CZ")]
    [InlineData("tipsport.sk", "CZ")]
    [InlineData("tipsport.cz", "SK")]
    public void CookiesFromOtherOriginsAreNotAccepted(string domain, string region)
    {
        using var result = Cookie(domain);
        Assert.Null(ChromiumLoginBrowser.SessionCookie(result.RootElement, region));
    }

    [Theory]
    [InlineData("tipsport.cz", "CZ")]
    [InlineData(".tipsport.cz", "CZ")]
    [InlineData("www.tipsport.cz", "CZ")]
    [InlineData(".tipsport.sk", "SK")]
    public void OwnRegionSecureRootCookieCanBeCaptured(string domain, string region)
    {
        using var result = Cookie(domain);
        Assert.Equal("synthetic-browser-session", ChromiumLoginBrowser.SessionCookie(result.RootElement, region));
    }

    [Fact]
    public void InsecureExpiredWrongPathAndMalformedCookiesAreRejected()
    {
        foreach (var cookie in new[] { Cookie("tipsport.cz", secure: false), Cookie("tipsport.cz", expires: 1), Cookie("tipsport.cz", path: "/other"), Cookie("tipsport.cz", value: "invalid;injection") })
            using (cookie) Assert.Null(ChromiumLoginBrowser.SessionCookie(cookie.RootElement, "CZ"));
    }

    [Theory]
    [InlineData("0", "/devtools/browser/example")]
    [InlineData("65536", "/devtools/browser/example")]
    [InlineData("9222", "ws://evil.test/devtools/browser/example")]
    [InlineData("9222", "/devtools/browser/example?redirect=evil")]
    [InlineData("9222", "/devtools/browser/../secret")]
    public void DebuggerCannotBeRedirectedAwayFromOwnedLocalBrowser(string port, string path) =>
        Assert.Throws<TipsportException>(() => ChromiumLoginBrowser.DebuggerEndpoint([port, path]));

    [Fact]
    public void BrowserIsIsolatedAndNeverDisablesItsSecurityOrAcceptsArbitraryOrigins()
    {
        var args = ChromiumLoginBrowser.Arguments("/private/owned/profile", "CZ", false).ToArray();
        Assert.Contains("--remote-debugging-address=127.0.0.1", args);
        Assert.Contains("--remote-debugging-port=0", args);
        Assert.Contains("--user-data-dir=" + Path.GetFullPath("/private/owned/profile"), args);
        Assert.DoesNotContain(args, a => a.Contains("no-sandbox") || a.Contains("ignore-certificate-errors") || a.Contains("disable-web-security") || a.Contains("remote-allow-origins"));
        Assert.Equal("https://www.tipsport.cz/", args[^1]);
        Assert.Throws<TipsportException>(() => ChromiumLoginBrowser.Arguments("/private/owned/profile", "https://evil.test", true).ToArray());
        Assert.Equal("127.0.0.1", ChromiumLoginBrowser.DebuggerEndpoint(["9222", "/devtools/browser/abc-123"]).Host);
    }

    [Theory]
    [InlineData("DevTools listening on ws://evil.test:9222/devtools/browser/abc")]
    [InlineData("DevTools listening on ws://127.0.0.1:9223/devtools/browser/abc")]
    [InlineData("DevTools listening on ws://127.0.0.1:9222/devtools/browser/abc?redirect=evil")]
    [InlineData("DevTools listening on ws://user@127.0.0.1:9222/devtools/browser/abc")]
    [InlineData("DevTools listening on ws://127.0.0.1:9222/devtools/page/abc")]
    public void BrowserStderrCannotRedirectCredentialsToAnotherEndpoint(string line) => Assert.Null(ChromiumLoginBrowser.OwnedEndpoint(line, 9222));

    [Fact]
    public void OwnedBrowserAnnouncementUsesOnlyTheSelectedLoopbackPort()
    {
        Assert.Equal("ws://127.0.0.1:54321/devtools/browser/abc-123", ChromiumLoginBrowser.OwnedEndpoint("DevTools listening on ws://127.0.0.1:54321/devtools/browser/abc-123", 54321)!.AbsoluteUri);
        Assert.Contains("--remote-debugging-port=54321", ChromiumLoginBrowser.Arguments("/private/owned/profile", "CZ", true, 54321));
        Assert.DoesNotContain("--headless=new", ChromiumLoginBrowser.Arguments("/private/owned/profile", "CZ", true, 54321));
    }

    [Theory]
    [InlineData(nameof(BrowserLoginController.Begin))]
    [InlineData(nameof(BrowserLoginController.Status))]
    [InlineData(nameof(BrowserLoginController.Credentials))]
    [InlineData(nameof(BrowserLoginController.ForgetCredentials))]
    [InlineData(nameof(BrowserLoginController.Renew))]
    public void BrowserManagementRequiresAdministrator(string method)
    {
        var auth = Assert.Single(typeof(BrowserLoginController).GetMethod(method)!.GetCustomAttributes<AuthorizeAttribute>());
        Assert.Equal(MediaBrowser.Common.Api.Policies.RequiresElevation, auth.Policy);
    }

    [Fact]
    public void PasswordEndpointRejectsPlainHttpBeforeStartingAnyBrowser()
    {
        using var setup = new Setup();
        var factory = new FakeFactory(new FakeBrowser(_ => Task.FromResult<string?>(null)));
        using var login = new BrowserLogin(setup.Store, factory, new Verifier(_ => false));
        var controller = new BrowserLoginController(login) { ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() } };
        controller.Request.Scheme = "http";
        Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(controller.Credentials(new PasswordLoginRequest { Username = "synthetic-user", Password = "synthetic-password" }));
        Assert.Equal(0, factory.Calls);
        Assert.False(setup.Store.HasSavedPassword);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MobileLoginResetsOldBrowserAccountAndOnlySavesPasswordAfterVerifiedOptIn(bool remember)
    {
        using var setup = new Setup();
        setup.Store.Save("synthetic-previous-account-session");
        var browser = new FakeBrowser(_ => Task.FromResult<string?>("synthetic-new-account-session"));
        var verifier = new Verifier(_ => true);
        using var login = new BrowserLogin(setup.Store, new FakeFactory(browser), verifier, TimeSpan.FromMilliseconds(2));
        login.BeginCredentials("CZ", "synthetic-user", "synthetic-test-password", remember);
        await WaitUntil(() => !login.Status.Active);
        Assert.True(browser.Prepared);
        Assert.Equal(1, browser.PasswordSubmissions);
        Assert.Equal("signed-in", login.Status.State);
        Assert.Equal("synthetic-new-account-session", setup.Store.Load());
        Assert.Equal(remember, setup.Store.HasSavedPassword);
        Assert.Equal(remember, login.Status.AutomaticLogin);
        Assert.DoesNotContain("synthetic-test-password", JsonSerializer.Serialize(login.Status));
        if (remember) Assert.Equal("synthetic-test-password", setup.Store.Credentials("CZ")!.Password);
    }

    [Fact]
    public async Task ChallengeDuringMobileLoginPreservesWorkingSessionAndDoesNotSavePassword()
    {
        using var setup = new Setup();
        setup.Store.Save("synthetic-working-session");
        var browser = new FakeBrowser(_ => Task.FromResult<string?>("synthetic-anonymous")) { FormResult = "manual-required" };
        using var login = new BrowserLogin(setup.Store, new FakeFactory(browser), new Verifier(_ => true), TimeSpan.FromMilliseconds(2));
        login.BeginCredentials("CZ", "synthetic-user", "synthetic-password", true);
        await WaitUntil(() => !login.Status.Active);
        Assert.Equal("error", login.Status.State);
        Assert.Contains("další ověření", login.Status.Message);
        Assert.Equal("synthetic-working-session", setup.Store.Load());
        Assert.False(setup.Store.HasSavedPassword);
        Assert.True(browser.Disposed);
        Assert.Throws<TipsportException>(() => login.BeginCredentials("CZ", "synthetic-user", "synthetic-password", true));
    }

    [Fact]
    public async Task ProviderErrorStopsBeforeAnyCandidateCanBeVerifiedAndPreservesTheWorkingSession()
    {
        using var setup = new Setup();
        setup.Store.Save("synthetic-working-session");
        var browser = new FakeBrowser(_ => Task.FromResult<string?>("synthetic-anonymous")) { FormResult = "provider-error" };
        var verifier = new Verifier(_ => true);
        using var login = new BrowserLogin(setup.Store, new FakeFactory(browser), verifier, TimeSpan.FromMilliseconds(2));
        login.BeginCredentials("CZ", "synthetic-user", "synthetic-password", true);
        await WaitUntil(() => !login.Status.Active);
        Assert.Equal("error", login.Status.State);
        Assert.Contains("vlastní chybovou stránku", login.Status.Message);
        Assert.Equal(0, verifier.Calls);
        Assert.Equal("synthetic-working-session", setup.Store.Load());
        Assert.False(setup.Store.HasSavedPassword);
    }

    [Fact]
    public async Task FailedSavedPasswordIsPausedPersistentlyAndNotRetriedDuringNextRecovery()
    {
        using var setup = new Setup();
        const string session = "synthetic-original-session";
        setup.Store.Save(session);
        setup.Store.SaveCredentials(LoginCredentials.Create("CZ", "synthetic-user", "synthetic-password"), session);
        var browser = new FakeBrowser(_ => Task.FromResult<string?>(null));
        using (var login = new BrowserLogin(setup.Store, new FakeFactory(browser), new Verifier(_ => false), TimeSpan.FromMilliseconds(2)))
            Assert.False(await login.TryRecover("CZ", CancellationToken.None));
        Assert.True(setup.Store.Credentials("CZ")!.Paused);
        Assert.Equal(session, setup.Store.Load());
        var another = new FakeBrowser(_ => throw new IOException("not-used"));
        using var restarted = new BrowserLogin(setup.Store, new FakeFactory(another), new Verifier(_ => false), TimeSpan.FromMilliseconds(2));
        Assert.False(await restarted.TryRecover("CZ", CancellationToken.None));
        Assert.Equal(0, another.PasswordSubmissions);
    }

    [Fact]
    public async Task AdministratorCanRetryPausedPasswordOnceAndSuccessResumesAutomaticRecovery()
    {
        using var setup = new Setup();
        const string session = "synthetic-original-session";
        setup.Store.Save(session);
        setup.Store.SaveCredentials(LoginCredentials.Create("CZ", "synthetic-user", "synthetic-password"), session);
        setup.Store.PauseCredentials("CZ");
        var browser = new FakeBrowser(_ => Task.FromResult<string?>("synthetic-renewed-session"));
        using var login = new BrowserLogin(setup.Store, new FakeFactory(browser), new Verifier(_ => true), TimeSpan.FromMilliseconds(2));
        login.BeginSavedCredentials("CZ");
        await WaitUntil(() => !login.Status.Active);
        Assert.Equal("signed-in", login.Status.State);
        Assert.Equal(1, browser.PasswordSubmissions);
        Assert.False(setup.Store.Credentials("CZ")!.Paused);
        Assert.Equal("synthetic-password", setup.Store.Credentials("CZ")!.Password);
        Assert.Throws<TipsportException>(() => login.BeginSavedCredentials("CZ"));
    }

    [Fact]
    public void SavedPasswordRetryCannotUseCredentialsFromAnotherAccount()
    {
        using var setup = new Setup();
        setup.Store.Save("synthetic-first-session");
        setup.Store.SaveCredentials(LoginCredentials.Create("CZ", "synthetic-user", "synthetic-password"), "synthetic-first-session");
        setup.Store.Save("synthetic-other-account-session");
        var factory = new FakeFactory(new FakeBrowser(_ => Task.FromResult<string?>(null)));
        using var login = new BrowserLogin(setup.Store, factory, new Verifier(_ => true));
        Assert.Throws<TipsportException>(() => login.BeginSavedCredentials("CZ"));
        Assert.Equal(0, factory.Calls);
    }

    [Theory]
    [InlineData("http://www.tipsport.cz/", "CZ")]
    [InlineData("https://www.tipsport.cz.evil.test/", "CZ")]
    [InlineData("https://www.tipsport.cz:444/", "CZ")]
    [InlineData("https://user@www.tipsport.cz/", "CZ")]
    [InlineData("https://www.tipsport.sk/", "CZ")]
    public void PasswordsCannotBeTypedIntoOtherOrigins(string url, string region) => Assert.False(ChromiumLoginBrowser.IsLoginOrigin(url, region));

    [Fact]
    public void RepeatedFormPollsDoNotRetransmitPasswordAndPayloadIsJsonEscaped()
    {
        var credentials = LoginCredentials.Create("CZ", "synthetic-user", "quote\"\\');alert(1)//");
        var expression = ProviderLoginForm.Expression(credentials, false);
        var json = expression[(expression.LastIndexOf(")(", StringComparison.Ordinal) + 2)..^1];
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal(credentials.Password, parsed.RootElement.GetProperty("password").GetString());
        var afterSubmission = ProviderLoginForm.Expression(credentials, true);
        Assert.DoesNotContain("alert(1)//", afterSubmission);
        Assert.True(ChromiumLoginBrowser.IsLoginOrigin("https://www.tipsport.cz/", "CZ"));
    }

    private static JsonDocument Cookie(string domain, bool secure = true, double expires = -1, string path = "/", string value = "synthetic-browser-session") =>
        JsonDocument.Parse(JsonSerializer.Serialize(new { result = new { cookies = new[] { new { name = "JSESSIONID", domain, secure, expires, path, value } } } }));

    private static async Task WaitUntil(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!predicate()) await Task.Delay(5, timeout.Token);
    }

    private sealed class Setup : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "tipsport-browser-test-" + Guid.NewGuid());
        public SessionStore Store { get; }
        public Setup()
        {
            var paths = DispatchProxy.Create<IApplicationPaths, SessionStoreTests.Paths>();
            ((SessionStoreTests.Paths)(object)paths).Root = Root;
            Store = new SessionStore(paths);
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    private sealed class FakeFactory(ILoginBrowser browser) : ILoginBrowserFactory
    {
        public bool Available => true;
        public int Calls;
        public bool Interactive;
        public Task<ILoginBrowser> Open(string profile, string region, bool interactive, CancellationToken token)
        { Directory.CreateDirectory(profile); Interlocked.Increment(ref Calls); Interactive = interactive; return Task.FromResult(browser); }
    }
    private sealed class FakeBrowser(Func<CancellationToken, Task<string?>> read) : ILoginBrowser
    {
        public bool Disposed;
        public bool Prepared;
        public int PasswordSubmissions;
        public string FormResult = "submitted";
        public Task PrepareCredentials(string region, CancellationToken token) { Prepared = true; return Task.CompletedTask; }
        public Task<string> SubmitCredentials(LoginCredentials credentials, CancellationToken token)
        {
            PasswordSubmissions++;
            if (PasswordSubmissions > 1) throw new TipsportException("failed");
            return Task.FromResult(FormResult);
        }
        public Task<string?> ReadSession(string region, CancellationToken token) => read(token);
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class Verifier(Func<string, bool> verify) : ISessionVerifier
    {
        public int Calls;
        public Task<bool> Verify(string session, string region, CancellationToken token) { Interlocked.Increment(ref Calls); return Task.FromResult(verify(session)); }
    }
}
