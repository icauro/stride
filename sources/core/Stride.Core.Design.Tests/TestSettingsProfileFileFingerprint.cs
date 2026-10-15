using System.Security.Cryptography;
using Stride.Core.IO;
using Stride.Core.Settings;
using Xunit;

namespace Stride.Core.Design.Tests;

public class TestSettingsProfileFileFingerprint
{
    [Fact]
    public void NativeLoadAndReloadRetainTheHashOfSuccessfullyReadBytes()
    {
        var path = new UFile(Path.Combine(Path.GetTempPath(), "Stride-settings-" + Guid.NewGuid().ToString("N") + ".yaml"));
        var container = new SettingsContainer();
        var key = new SettingsKey<int>("Regression/Value", container, 0);
        using var authored = container.CreateSettingsProfile(false, registerInContainer: false);
        SettingsProfile? loaded = null;
        try
        {
            key.SetValue(17, authored);
            Assert.True(container.SaveSettingsProfile(authored, path));
            loaded = container.LoadSettingsProfile(path, false, registerInContainer: false);
            Assert.NotNull(loaded);
            Assert.Equal(17, key.GetValue(loaded, true));
            Assert.Equal(SHA256.HashData(File.ReadAllBytes(path.ToOSPath())), loaded.LoadedContentHash.ToArray());

            key.SetValue(29, authored);
            Assert.True(container.SaveSettingsProfile(authored, path));
            container.ReloadSettingsProfile(loaded);
            Assert.Equal(29, key.GetValue(loaded, true));
            Assert.Equal(SHA256.HashData(File.ReadAllBytes(path.ToOSPath())), loaded.LoadedContentHash.ToArray());
        }
        finally
        {
            loaded?.Dispose();
            if (File.Exists(path.ToOSPath())) File.Delete(path.ToOSPath());
        }
    }
}
