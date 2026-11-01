using System.Text;
using Jellyfin.Plugin.Tipsport;
using Xunit;

namespace Tipsport.Tests;

public class ProviderHttpTests
{
    [Fact] public void SuccessfulBodyIsParsedWithoutStatusSuffix()
    {
        using var json = ProviderHttp.Parse(Encoding.UTF8.GetBytes("{\"program\":[]}\n200"));
        Assert.Equal(0, json.RootElement.GetProperty("program").GetArrayLength());
    }
    [Theory]
    [InlineData("<html>cf-chl-secret-account-data</html>\n403", "ověření v prohlížeči")]
    [InlineData("private-error-body\n401", "HTTP 401")]
    [InlineData("private-error-body\n500", "HTTP 500")]
    [InlineData("private-error-body", "stav odpovědi")]
    [InlineData("private-error-body\n200", "rozpoznanou odpověď")]
    public void ErrorsAreUsefulWithoutLeakingResponseBody(string wire, string expected)
    {
        var error = Assert.Throws<TipsportException>(() => ProviderHttp.Parse(Encoding.UTF8.GetBytes(wire)));
        Assert.Contains(expected, error.Message);
        Assert.DoesNotContain("private-error-body", error.Message);
        Assert.DoesNotContain("secret-account-data", error.Message);
    }
    [Fact] public void CookieIsEscapedForStdinConfig()
    {
        Assert.Equal("abc\\\\def\\\"ghi", ProviderHttp.Escape("abc\\def\"ghi"));
    }
}
