using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.Tipsport;

/// <summary>Bodyless HTTP/1.1 requests with Content-Type, matching the existing provider integration.</summary>
internal static class ProviderHttp
{
    internal static string? CurlPath => OperatingSystem.IsWindows()
        ? Existing(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "curl.exe"))
        : Existing("/usr/bin/curl") ?? Existing("/bin/curl");
    private static string? Existing(string path) => File.Exists(path) ? path : null;

    internal static async Task<JsonDocument> Fetch(string executable, Uri uri, string? session, CancellationToken token)
    {
        var body = await FetchText(executable, uri, session, token).ConfigureAwait(false);
        try { return JsonDocument.Parse(body); }
        catch (JsonException) { throw new TipsportException("Poskytovatel nevrátil rozpoznanou odpověď."); }
    }

    internal static async Task<string> FetchText(string executable, Uri uri, string? session, CancellationToken token)
    {
        TipsportClient.CheckedUri(uri.AbsoluteUri);
        if (session != null && uri.Host is not ("www.tipsport.cz" or "www.tipsport.sk"))
            throw new TipsportException("Relace smí být poslána pouze Tipsportu.");
        if (session != null) SessionStore.Validate(session);
        var addresses = await Dns.GetHostAddressesAsync(uri.Host, token).ConfigureAwait(false);
        if (addresses.Length == 0 || addresses.Any(ip => !TipsportClient.IsPublicAddress(ip)))
            throw new TipsportException("Interní adresy poskytovatelů nejsou povolené.");
        var pinned = addresses.OrderBy(ip => ip.AddressFamily != AddressFamily.InterNetwork).Take(8)
            .Select(ip => ip.AddressFamily == AddressFamily.InterNetworkV6 ? "[" + ip + "]" : ip.ToString());
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        // --disable is first so a user's .curlrc cannot change TLS validation or destinations.
        foreach (var argument in new[] {
            "--disable", "--globoff", "--silent", "--show-error", "--http1.1", "--proto", "=https",
            "--connect-timeout", "8", "--max-time", "15", "--max-filesize", "8388608",
            "--resolve", uri.Host + ":443:" + string.Join(',', pinned),
            "--user-agent", TipsportClient.UserAgent,
            "--header", "Accept: application/json", "--header", "Content-Type: application/json",
            "--header", "Accept-Encoding: identity", "--header", "Connection: close",
            "--write-out", "\n%{http_code}", "--config", "-"
        }) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new TipsportException("Systémový HTTP klient se nespustil.");
            using var cancellation = token.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
            var drainError = process.StandardError.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None);
            // Cookie stays off the command line and is sent only to the fixed HTTPS origin.
            var config = "url = \"" + Escape(uri.AbsoluteUri) + "\"\n";
            if (session != null) config += "cookie = \"JSESSIONID=" + Escape(session) + "\"\n";
            await process.StandardInput.WriteAsync(config.AsMemory(), token).ConfigureAwait(false);
            process.StandardInput.Close();
            using var output = new MemoryStream();
            var buffer = new byte[16384];
            int count;
            while ((count = await process.StandardOutput.BaseStream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                if (output.Length + count > 8 * 1024 * 1024 + 4) throw new TipsportException("Odpověď poskytovatele je příliš velká.");
                output.Write(buffer, 0, count);
            }
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            await drainError.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new TipsportException("Systémový HTTP klient přenos nedokončil (kód " + process.ExitCode + ").");
            return ParseText(output.ToArray());
        }
        finally
        {
            try
            {
                if (process.Id != 0 && !process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
            }
            catch (InvalidOperationException) { }
        }
    }

    internal static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    internal static JsonDocument Parse(byte[] output)
    {
        try { return JsonDocument.Parse(ParseText(output)); }
        catch (JsonException) { throw new TipsportException("Poskytovatel nevrátil rozpoznanou odpověď."); }
    }
    internal static string ParseText(byte[] output)
    {
        if (output.Length < 4 || output[^4] != (byte)'\n' || !int.TryParse(Encoding.ASCII.GetString(output[^3..]), out var status))
            throw new TipsportException("HTTP klient nevrátil stav odpovědi.");
        if (status is < 200 or >= 300)
        {
            var body = Encoding.UTF8.GetString(output, 0, Math.Min(output.Length - 4, 32768));
            if (body.Contains("cf-chl", StringComparison.OrdinalIgnoreCase) || body.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase))
                throw new TipsportException("Webová ochrana Tipsportu vyžaduje ověření v prohlížeči. Relace samotná pro tento HTTP klient nestačí.");
            throw new TipsportException("Poskytovatel vrátil HTTP " + status + ". Ověřte přihlášenou relaci a dostupnost služby.");
        }
        return Encoding.UTF8.GetString(output, 0, output.Length - 4);
    }
}
