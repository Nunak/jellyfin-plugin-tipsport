using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.DataProtection;

namespace Jellyfin.Plugin.Tipsport;

public sealed class SessionStore
{
    private readonly string _path;
    private readonly IDataProtector _protector;
    private readonly object _gate = new();
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
        _path = Path.Combine(directory, "session.protected");
    }
    public bool HasSession { get { lock (_gate) return File.Exists(_path); } }
    public string Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) throw new TipsportException("V nastavení pluginu nejprve předejte přihlášenou relaci Tipsportu.");
            try { return _protector.Unprotect(File.ReadAllText(_path)); }
            catch (System.Security.Cryptography.CryptographicException) { throw new TipsportException("Uloženou relaci nelze otevřít pod tímto účtem serveru. Předejte ji znovu."); }
        }
    }
    public void Save(string value)
    {
        Validate(value);
        lock (_gate)
        {
            var temp = _path + ".tmp";
            File.WriteAllText(temp, _protector.Protect(value));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temp, _path, true);
        }
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
