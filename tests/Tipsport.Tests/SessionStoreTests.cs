using System.Reflection;
using Jellyfin.Plugin.Tipsport;
using MediaBrowser.Common.Configuration;
using Xunit;

namespace Tipsport.Tests;

public sealed class SessionStoreTests
{
    [Fact]
    public void SessionPersistsEncryptedAndCanBeReopenedWithoutReturningItThroughConfiguration()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tipsport-test-" + Guid.NewGuid());
        try
        {
            var paths = DispatchProxy.Create<IApplicationPaths, Paths>();
            ((Paths)(object)paths).Root = directory;
            var store = new SessionStore(paths);
            const string session = "synthetic-p-wt-session-for-unit-test";
            store.Save(session);
            Assert.True(store.HasSession);
            Assert.Equal(session, new SessionStore(paths).Load());
            Assert.DoesNotContain(session, File.ReadAllText(Path.Combine(directory, "Tipsport.private", "session.protected")));
            Assert.DoesNotContain(typeof(PluginConfiguration).GetProperties(), p => p.Name.Contains("session", StringComparison.OrdinalIgnoreCase));
            Assert.Throws<TipsportException>(() => store.Save("invalid;cookie"));
            Assert.Equal(session, store.Load());
            const string renewed = "synthetic-renewed-session-for-unit-test";
            store.Save(renewed);
            Assert.Equal(renewed, new SessionStore(paths).Load());
            Assert.DoesNotContain(renewed, File.ReadAllText(Path.Combine(directory, "Tipsport.private", "session.protected")));
            Assert.Empty(Directory.GetFiles(Path.Combine(directory, "Tipsport.private"), "*.tmp"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Fact]
    public void FailedReplacementReportsStorageFailureWithoutLeakingDataOrLeavingTemporaryCredentials()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tipsport-test-" + Guid.NewGuid());
        try
        {
            var paths = DispatchProxy.Create<IApplicationPaths, Paths>();
            ((Paths)(object)paths).Root = directory;
            var store = new SessionStore(paths);
            Directory.CreateDirectory(Path.Combine(directory, "Tipsport.private", "session.protected"));
            var error = Assert.Throws<TipsportException>(() => store.Save("synthetic-private-session-for-test"));
            Assert.Contains("Přihlášení nelze uložit", error.Message);
            Assert.DoesNotContain(directory, error.Message);
            Assert.DoesNotContain("synthetic-private-session-for-test", error.Message);
            Assert.Empty(Directory.GetFiles(Path.Combine(directory, "Tipsport.private"), "*.tmp"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Fact]
    public void SavedPasswordIsEncryptedBoundToAccountAndCanBePausedAcrossRestarts()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tipsport-vault-test-" + Guid.NewGuid());
        try
        {
            var paths = DispatchProxy.Create<IApplicationPaths, Paths>();
            ((Paths)(object)paths).Root = directory;
            var store = new SessionStore(paths);
            const string session = "synthetic-first-account-session";
            store.Save(session);
            var credentials = LoginCredentials.Create("CZ", "synthetic-user", "synthetic-PrivatePassword42!");
            store.SaveCredentials(credentials, session);
            var encrypted = File.ReadAllText(Path.Combine(directory, "Tipsport.private", "credentials.protected"));
            Assert.DoesNotContain(credentials.Username, encrypted);
            Assert.DoesNotContain(credentials.Password, encrypted);
            Assert.DoesNotContain(session, encrypted);
            var reopened = new SessionStore(paths);
            Assert.Equal(credentials.Password, reopened.Credentials("CZ")!.Password);
            Assert.Null(reopened.Credentials("SK"));
            reopened.PauseCredentials("CZ");
            Assert.True(new SessionStore(paths).Credentials("CZ")!.Paused);
            reopened.Save("synthetic-second-account-session");
            Assert.Null(reopened.Credentials("CZ"));
            Assert.True(reopened.HasSavedPassword);
            reopened.ForgetCredentials();
            Assert.False(reopened.HasSavedPassword);
            Assert.Equal("synthetic-second-account-session", reopened.Load());
            Assert.Empty(Directory.GetFiles(Path.Combine(directory, "Tipsport.private"), "*.tmp"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("", "password")]
    [InlineData("user", "")]
    [InlineData("user\nother", "password")]
    [InlineData("user", "password\n")]
    public void InvalidCredentialsAreRejectedWithoutIncludingTheirValue(string username, string password)
    {
        var error = Assert.Throws<TipsportException>(() => LoginCredentials.Create("CZ", username, password));
        Assert.Equal("Vyplňte platné přihlašovací jméno a heslo k Tipsportu.", error.Message);
    }

    public class Paths : DispatchProxy
    {
        public string Root { get; set; } = "";
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.ReturnType == typeof(string) ? Root : null;
    }
}
