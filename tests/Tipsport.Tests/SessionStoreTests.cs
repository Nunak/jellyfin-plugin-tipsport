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
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    public class Paths : DispatchProxy
    {
        public string Root { get; set; } = "";
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.ReturnType == typeof(string) ? Root : null;
    }
}
