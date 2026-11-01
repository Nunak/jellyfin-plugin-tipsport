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
    [Fact] public void MissingExplicitEncoderDoesNotSilentlySelectAnother()
    {
        Assert.Throws<TipsportException>(() => FfmpegSourceFactory.ResolveExecutable(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "ffmpeg"), AppContext.BaseDirectory, null, false));
    }
}
