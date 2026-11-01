using Jellyfin.Plugin.Tipsport;
using Xunit;

namespace Tipsport.Tests;

public class EncoderPathTests
{
    [Fact] public void WindowsBareEncoderNameFindsBundledExecutable()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        try
        {
            var file = Path.Combine(directory, "ffmpeg.exe");
            File.WriteAllText(file, "test fixture, never executed");
            Assert.Equal(file, FfmpegSourceFactory.ResolveExecutable("ffmpeg", directory, null, true));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Theory]
    [InlineData("https://cdn.example.test/live.M3U8?signature=example", true)]
    [InlineData("https://cdn.example.test/live.ts?name=playlist.m3u8", false)]
    [InlineData("https://cdn.example.test/unknown", false)]
    public void NearLiveHlsOptionCannotBeAppliedToOtherInputFormats(string address, bool expected) =>
        Assert.Equal(expected, FfmpegSourceFactory.IsHlsPlaylist(new Uri(address)));

    [Fact] public void MissingExplicitEncoderDoesNotSilentlySelectAnother()
    {
        Assert.Throws<TipsportException>(() => FfmpegSourceFactory.ResolveExecutable(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "ffmpeg"), AppContext.BaseDirectory, null, false));
    }
}
