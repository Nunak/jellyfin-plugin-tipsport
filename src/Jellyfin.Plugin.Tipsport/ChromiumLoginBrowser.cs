using System.Diagnostics;
using System.Net.WebSockets;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace Jellyfin.Plugin.Tipsport;

internal sealed class ChromiumLoginBrowserFactory : ILoginBrowserFactory
{
    public bool Available => ChromiumLoginBrowser.FindExecutable() != null && (!OperatingSystem.IsWindows() || Environment.UserInteractive);
    public Task<ILoginBrowser> Open(string profile, string region, bool interactive, CancellationToken token) => ChromiumLoginBrowser.Open(profile, region, interactive, token);
}

/// <summary>Chromium DevTools is bound to loopback for a new, plugin-owned profile, never the user's real Chrome profile.</summary>
internal sealed class ChromiumLoginBrowser : ILoginBrowser
{
    private readonly Process _process;
    private readonly ClientWebSocket _socket;
    private readonly FileStream _profileLock;
    private readonly Task _errorDrain;
    private readonly Task _outputDrain;
    private int _id;
    private string? _pageSession;
    private bool _submitted;

    private ChromiumLoginBrowser(Process process, ClientWebSocket socket, FileStream profileLock, Task errorDrain, Task outputDrain)
    { _process = process; _socket = socket; _profileLock = profileLock; _errorDrain = errorDrain; _outputDrain = outputDrain; }

    internal static string? FindExecutable()
    {
        var files = OperatingSystem.IsWindows()
            ? new[] {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe") }
            : OperatingSystem.IsMacOS() ? new[] { "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome", "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge" }
            : new[] { "/usr/bin/google-chrome", "/usr/bin/google-chrome-stable", "/usr/bin/chromium", "/usr/bin/chromium-browser", "/usr/bin/microsoft-edge" };
        return files.FirstOrDefault(path => Path.IsPathFullyQualified(path) && File.Exists(path));
    }

    internal static IEnumerable<string> Arguments(string profile, string region, bool interactive, int port = 0)
    {
        BrowserLogin.Region(region);
        if (port is < 0 or > 65535) throw new TipsportException("Neplatný místní port prohlížeče.");
        yield return "--user-data-dir=" + Path.GetFullPath(profile);
        yield return "--remote-debugging-address=127.0.0.1";
        yield return "--remote-debugging-port=" + port;
        yield return "--no-first-run";
        yield return "--no-default-browser-check";
        yield return "--new-window";
        if (!interactive) yield return "--headless=new";
        yield return TipsportClient.Origin(region) + "/";
    }

    internal static async Task<ILoginBrowser> Open(string profile, string region, bool interactive, CancellationToken token)
    {
        var executable = FindExecutable() ?? throw new TipsportException("Na serveru není dostupný podporovaný prohlížeč Chrome nebo Edge.");
        Directory.CreateDirectory(profile);
        if (OperatingSystem.IsWindows()) ProtectProfile(profile);
        else File.SetUnixFileMode(profile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var profileLock = new FileStream(Path.Combine(profile, "jellyfin-login.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Process? process = null;
        ClientWebSocket? socket = null;
        Task errorDrain = Task.CompletedTask, outputDrain = Task.CompletedTask;
        try
        {
            // Windows already requires a logged-in desktop. Use the regular desktop browser for login,
            // including requests initiated from a remote phone; no window is opened on that phone.
            var visible = interactive || OperatingSystem.IsWindows();
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var endpointReady = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = !visible, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var arg in Arguments(profile, region, visible, port)) start.ArgumentList.Add(arg);
            process = new Process { StartInfo = start };
            if (!process.Start()) throw new TipsportException("Přihlašovací prohlížeč se nespustil.");
            // Trust only the endpoint announced by our own child process, not an unrelated local HTTP service.
            errorDrain = ReadEndpoint(process.StandardError, port, endpointReady);
            outputDrain = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            using var startTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            startTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            Uri endpoint;
            while (true)
            {
                startTimeout.Token.ThrowIfCancellationRequested();
                if (process.HasExited) throw new TipsportException("Přihlašovací okno se zavřelo nebo už je tento profil otevřený. Zavřete předchozí okno pluginu a zkuste to znovu.");
                if (endpointReady.Task.IsCompleted) { endpoint = await endpointReady.Task.ConfigureAwait(false); break; }
                await Task.Delay(100, startTimeout.Token).ConfigureAwait(false);
            }
            socket = new ClientWebSocket();
            await socket.ConnectAsync(endpoint, startTimeout.Token).ConfigureAwait(false);
            return new ChromiumLoginBrowser(process, socket, profileLock, errorDrain, outputDrain);
        }
        catch
        {
            socket?.Dispose();
            if (process != null)
            {
                try { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } }
                catch (InvalidOperationException) { }
                try { await Task.WhenAll(errorDrain, outputDrain).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (Exception e) when (e is IOException or TimeoutException or InvalidOperationException) { }
                process.Dispose();
            }
            profileLock.Dispose();
            throw;
        }
    }

    private static async Task ReadEndpoint(StreamReader error, int port, TaskCompletionSource<Uri> ready)
    {
        while (await error.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            // Discard every other browser message; it can contain private profile paths or page data.
            if (OwnedEndpoint(line, port) is { } endpoint) ready.TrySetResult(endpoint);
        }
    }

    internal static Uri? OwnedEndpoint(string line, int expectedPort)
    {
        const string prefix = "DevTools listening on ";
        if (!line.StartsWith(prefix, StringComparison.Ordinal) || line.Length > 200 ||
            !Uri.TryCreate(line[prefix.Length..], UriKind.Absolute, out var uri) ||
            uri.Scheme != "ws" || uri.Host != "127.0.0.1" || uri.Port != expectedPort ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return null;
        try { return DebuggerEndpoint([expectedPort.ToString(System.Globalization.CultureInfo.InvariantCulture), uri.AbsolutePath]); }
        catch (TipsportException) { return null; }
    }

    internal static Uri DebuggerEndpoint(string[] lines)
    {
        const string prefix = "/devtools/browser/";
        if (lines.Length < 2 || !int.TryParse(lines[0], out var port) || port is <= 0 or > 65535 ||
            !lines[1].StartsWith(prefix, StringComparison.Ordinal) || lines[1].Length <= prefix.Length ||
            lines[1][prefix.Length..].Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new TipsportException("Prohlížeč neposkytl bezpečné místní spojení.");
        return new Uri("ws://127.0.0.1:" + port + lines[1]);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void ProtectProfile(string profile)
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new TipsportException("Účet serveru nelze ověřit pro vytvoření soukromého profilu.");
        var acl = new DirectorySecurity();
        acl.SetOwner(user);
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { user, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(profile).SetAccessControl(acl);
    }

    public async Task<string?> ReadSession(string region, CancellationToken token)
    {
        if (_process.HasExited) throw new TipsportException("Přihlašovací okno se zavřelo před dokončením. Otevřete jej znovu tlačítkem Přihlásit k Tipsportu.");
        var page = await PageSession(region, token).ConfigureAwait(false);
        if (page != null)
        {
            using var health = await Command("Runtime.evaluate", token, new { expression = ProviderLoginForm.HealthScript, returnByValue = true }, page).ConfigureAwait(false);
            if (health.RootElement.GetProperty("result").TryGetProperty("result", out var evaluation) && evaluation.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.True)
                throw new TipsportException("Tipsport na serveru místo přihlašovací stránky vrací chybu přístupu. Přihlášení se zastavilo bez změny uložené relace.");
            // A fresh anonymous cookie can load the public programme. Require the provider's signed-in UI too.
            using var identity = await Command("Runtime.evaluate", token, new { expression = ProviderLoginForm.AuthenticatedExpression(region), returnByValue = true }, page).ConfigureAwait(false);
            if (!identity.RootElement.GetProperty("result").TryGetProperty("result", out var status) || !status.TryGetProperty("value", out var signedIn) || signedIn.ValueKind != JsonValueKind.True) return null;
        }
        else return null;
        using var reply = await Command("Storage.getCookies", token).ConfigureAwait(false);
        return SessionCookie(reply.RootElement, region);
    }

    private async Task<string?> PageSession(string region, CancellationToken token)
    {
        if (_pageSession != null) return _pageSession;
        using var targets = await Command("Target.getTargets", token).ConfigureAwait(false);
        foreach (var target in targets.RootElement.GetProperty("result").GetProperty("targetInfos").EnumerateArray())
        {
            if (Catalogue.Text(target, "type") != "page" || !IsLoginOrigin(Catalogue.Text(target, "url"), region)) continue;
            using var attached = await Command("Target.attachToTarget", token, new { targetId = Catalogue.Text(target, "targetId"), flatten = true }).ConfigureAwait(false);
            return _pageSession = Catalogue.Text(attached.RootElement.GetProperty("result"), "sessionId");
        }
        return null;
    }

    internal static bool IsLoginOrigin(string url, string region) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) && uri.GetLeftPart(UriPartial.Authority) == TipsportClient.Origin(region);

    public async Task PrepareCredentials(string region, CancellationToken token)
    {
        // This is the dedicated plugin profile, never the user's regular browser or its cookies.
        using var cleared = await Command("Storage.clearCookies", token).ConfigureAwait(false);
        using var ready = CancellationTokenSource.CreateLinkedTokenSource(token);
        ready.CancelAfter(TimeSpan.FromSeconds(10));
        string? page;
        while ((page = await PageSession(region, ready.Token).ConfigureAwait(false)) == null) await Task.Delay(100, ready.Token).ConfigureAwait(false);
        using var navigated = await Command("Page.navigate", token, new { url = TipsportClient.Origin(region) + "/" }, page).ConfigureAwait(false);
    }

    public async Task<string> SubmitCredentials(LoginCredentials credentials, CancellationToken token)
    {
        var page = await PageSession(credentials.Region, token).ConfigureAwait(false);
        if (page == null) return "waiting";
        using var reply = await Command("Runtime.evaluate", token,
            new { expression = ProviderLoginForm.Expression(credentials, _submitted), returnByValue = true }, page).ConfigureAwait(false);
        var result = reply.RootElement.GetProperty("result");
        if (result.TryGetProperty("exceptionDetails", out _)) throw new TipsportException("Přihlašovací formulář Tipsportu se změnil. Použijte přihlášení v okně na serveru.");
        var value = result.TryGetProperty("result", out var evaluation) ? Catalogue.Text(evaluation, "value") : "waiting";
        _submitted |= value == "submitted";
        return value is "submitted" or "manual-required" or "wrong-origin" or "failed" or "provider-error" or "notice" or "form-changed" or "form-disabled" or "opening-form" ? value : "waiting";
    }

    internal static string? SessionCookie(JsonElement reply, string region)
    {
        BrowserLogin.Region(region);
        var host = region == "SK" ? "tipsport.sk" : "tipsport.cz";
        if (!reply.TryGetProperty("result", out var result) || !result.TryGetProperty("cookies", out var cookies) || cookies.ValueKind != JsonValueKind.Array) return null;
        foreach (var cookie in cookies.EnumerateArray())
        {
            if (Catalogue.Text(cookie, "name") != "JSESSIONID") continue;
            var domain = Catalogue.Text(cookie, "domain").TrimStart('.');
            if (domain != host && domain != "www." + host) continue;
            if (Catalogue.Text(cookie, "path") != "/") continue;
            if (!cookie.TryGetProperty("secure", out var secure) || secure.ValueKind != JsonValueKind.True) continue;
            if (cookie.TryGetProperty("expires", out var expiry) && expiry.TryGetDouble(out var expires) && expires > 0 && expires <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) continue;
            var value = Catalogue.Text(cookie, "value");
            try { SessionStore.Validate(value); return value; }
            catch (TipsportException) { }
        }
        return null;
    }

    private async Task<JsonDocument> Command(string method, CancellationToken token, object? parameters = null, string? sessionId = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        var id = ++_id;
        var command = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters ?? new { }, sessionId },
            new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        await _socket.SendAsync(command.AsMemory(), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
        var buffer = new byte[16384];
        while (true)
        {
            using var message = new MemoryStream();
            ValueWebSocketReceiveResult chunk;
            do
            {
                chunk = await _socket.ReceiveAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false);
                if (chunk.MessageType == WebSocketMessageType.Close) throw new TipsportException("Přihlašovací okno se zavřelo.");
                if (message.Length + chunk.Count > 1024 * 1024) throw new TipsportException("Přihlašovací prohlížeč vrátil příliš mnoho dat.");
                message.Write(buffer, 0, chunk.Count);
            } while (!chunk.EndOfMessage);
            var document = JsonDocument.Parse(message.ToArray());
            if (document.RootElement.TryGetProperty("id", out var responseId) && responseId.TryGetInt32(out var value) && value == id)
            {
                if (!document.RootElement.TryGetProperty("error", out _)) return document;
                document.Dispose();
                throw new TipsportException("Přihlašovací prohlížeč požadavek nepodporuje.");
            }
            document.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { using var reply = await Command("Browser.close", timeout.Token).ConfigureAwait(false); }
                catch (Exception e) when (e is OperationCanceledException or WebSocketException or TipsportException) { }
            }
        }
        finally
        {
            _socket.Dispose();
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { if (!_process.HasExited) { _process.Kill(entireProcessTree: true); await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } }
                await Task.WhenAll(_errorDrain, _outputDrain).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception e) when (e is InvalidOperationException or IOException or TimeoutException or System.ComponentModel.Win32Exception) { }
            _process.Dispose();
            _profileLock.Dispose();
        }
    }
}
