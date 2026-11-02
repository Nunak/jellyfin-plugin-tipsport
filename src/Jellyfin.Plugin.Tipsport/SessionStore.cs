using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.DataProtection;

namespace Jellyfin.Plugin.Tipsport;

public sealed class SessionStore
{
    private readonly string _path;
    private readonly IDataProtector _protector;
    private readonly IDataProtector _credentialProtector;
    private readonly object _gate = new();
    internal string BrowserProfilePath => Path.Combine(Path.GetDirectoryName(_path)!, "browser-profile");
    public event Action? Changed;
    private long _revision;
    internal long Revision => Interlocked.Read(ref _revision);
    internal void SaveBrowserSession(string value, string region, long? expectedRevision = null)
    {
        lock (_gate)
        {
            if (expectedRevision.HasValue && Revision != expectedRevision.Value)
                throw new TipsportException("Přihlášení se mezitím změnilo. Novější relace zůstala zachovaná; pokud chcete použít toto okno, spusťte přihlášení znovu.");
            Save(value);
            var binding = Path.Combine(BrowserProfilePath, "session-binding.sha256");
            var temp = binding + ".tmp";
            try
            {
                File.WriteAllText(temp, BrowserBinding(value, region));
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.Move(temp, binding, true);
            }
            finally { try { File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
        }
    }
    internal bool IsBrowserEnrolled(string region)
    {
        try
        {
            var path = Path.Combine(BrowserProfilePath, "session-binding.sha256");
            return File.Exists(path) && File.ReadAllText(path) == BrowserBinding(Load(), region);
        }
        catch (Exception e) when (e is TipsportException or IOException or UnauthorizedAccessException) { return false; }
    }
    private static string BrowserBinding(string session, string region) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(BrowserLogin.Region(region) + ":" + session)));
    public SessionStore(IApplicationPaths paths)
    {
        var directory = Path.Combine(paths.PluginConfigurationsPath, "Tipsport.private");
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var provider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(directory, "keys")), builder =>
        {
            builder.SetApplicationName(Plugin.PluginId.ToString());
            if (OperatingSystem.IsWindows()) builder.ProtectKeysWithDpapi();
        });
        _protector = provider.CreateProtector("Tipsport.JSESSIONID.v1");
        _credentialProtector = provider.CreateProtector("Tipsport.Credentials.v1");
        _path = Path.Combine(directory, "session.protected");
    }
    private string CredentialsPath => Path.Combine(Path.GetDirectoryName(_path)!, "credentials.protected");
    internal bool HasSavedPassword => File.Exists(CredentialsPath);
    internal LoginCredentials? Credentials(string region)
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(CredentialsPath)) return null;
                var value = JsonSerializer.Deserialize<LoginCredentials>(_credentialProtector.Unprotect(File.ReadAllText(CredentialsPath)));
                return value != null && value.Region == region && value.Binding == BrowserBinding(Load(), region) ? value : null;
            }
            catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException or JsonException or TipsportException) { return null; }
        }
    }
    internal void SaveCredentials(LoginCredentials value, string session)
    {
        lock (_gate)
        {
            value.Binding = BrowserBinding(session, value.Region);
            if (Load() != session) throw new TipsportException("Relace se mezitím změnila. Přihlašovací údaje nebyly uloženy.");
            WriteCredentials(value);
        }
    }
    internal void PauseCredentials(string region)
    {
        lock (_gate)
        {
            var value = Credentials(region);
            if (value == null) return;
            value.Paused = true;
            WriteCredentials(value);
        }
    }
    internal void ForgetCredentials() { lock (_gate) File.Delete(CredentialsPath); }
    private void WriteCredentials(LoginCredentials value)
    {
        var temp = CredentialsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, _credentialProtector.Protect(JsonSerializer.Serialize(value)));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temp, CredentialsPath, true);
        }
        catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException)
        { throw new TipsportException("Údaje pro automatické přihlášení nelze bezpečně uložit. Ověřte práva účtu serveru ke složce Tipsport.private."); }
        finally { try { File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
    }
    public bool HasSession { get { lock (_gate) return File.Exists(_path); } }
    public string Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) throw new TipsportException("V nastavení pluginu nejprve použijte Přihlásit k Tipsportu.", true);
            try { return _protector.Unprotect(File.ReadAllText(_path)); }
            catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException)
            { throw new TipsportException("Uložené přihlášení nelze načíst pod tímto účtem serveru. Zkontrolujte přístup ke složce Tipsport.private a přihlášení obnovte v nastavení pluginu.", true); }
        }
    }
    public void Save(string value)
    {
        Validate(value);
        lock (_gate)
        {
            var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, _protector.Protect(value));
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.Move(temp, _path, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                // Keep the previous session intact and do not expose paths or encrypted data.
                // Never change ACLs automatically: the server's account must own its store.
                throw new TipsportException("Přihlášení nelze uložit. Účet, pod kterým běží Jellyfin, potřebuje právo měnit soubor relace ve složce Tipsport.private. Původní přihlášení zůstalo zachované.");
            }
            finally
            {
                try { File.Delete(temp); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            Interlocked.Increment(ref _revision);
        }
        Changed?.Invoke();
    }
    public static void Validate(string value)
    {
        if (value.Length is < 8 or > 4096 || value.Any(c => c < 33 || c > 126 || c is ';' or ','))
            throw new TipsportException("Zadejte pouze celou hodnotu cookie JSESSIONID, bez názvu cookie a bez mezer.");
    }
    public void ImportKodi()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kodi", "userdata", "addon_data", "plugin.video.tipsport", "session.txt");
        if (!File.Exists(path)) throw new TipsportException("Relace Kodi nebyla pod účtem serveru nalezena. Předejte cookie ručně.");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var cookie in document.RootElement.EnumerateArray())
            if (Catalogue.Text(cookie, "name") == "JSESSIONID") { Save(Catalogue.Text(cookie, "value")); return; }
        throw new TipsportException("Soubor Kodi neobsahuje JSESSIONID.");
    }
}

// Never expose this object from a controller or use a record-generated ToString containing the password.
internal sealed class LoginCredentials
{
    public string Region { get; set; } = "CZ";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string Binding { get; set; } = "";
    public bool Paused { get; set; }
    internal static LoginCredentials Create(string region, string username, string password)
    {
        BrowserLogin.Region(region);
        if (string.IsNullOrWhiteSpace(username) || username.Length > 254 || username.Any(char.IsControl) ||
            string.IsNullOrEmpty(password) || password.Length > 1024 || password.Any(char.IsControl))
            throw new TipsportException("Vyplňte platné přihlašovací jméno a heslo k Tipsportu.");
        return new() { Region = region, Username = username.Trim(), Password = password };
    }
}
