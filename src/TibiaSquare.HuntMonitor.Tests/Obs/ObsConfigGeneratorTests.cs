using System.IO;
using TibiaSquare.HuntMonitor.Obs;
using Xunit;

namespace TibiaSquare.HuntMonitor.Tests.Obs;

public sealed class ObsConfigGeneratorTests
{
    [Fact]
    public void EnsureConfig_WritesAndReadsExplicitAdapterIndex()
    {
        var obsDir = CreateTemporaryDirectory();
        try
        {
            ObsConfigGenerator.EnsureConfig(obsDir, 4456, "test-password", adapterIndex: 1);

            var adapterIndex = ObsConfigGenerator.ReadAdapterIndex(obsDir);
            var userConfig = File.ReadAllText(Path.Combine(
                obsDir,
                "config",
                "obs-studio",
                "user.ini"));

            Assert.Equal((uint)1, adapterIndex);
            Assert.Contains("AdapterIdx=1", userConfig);
        }
        finally
        {
            Directory.Delete(obsDir, recursive: true);
        }
    }

    [Fact]
    public void EnsureConfig_PreservesExistingAdapterIndexWhenNotOverridden()
    {
        var obsDir = CreateTemporaryDirectory();
        try
        {
            ObsConfigGenerator.EnsureConfig(obsDir, 4456, "test-password", adapterIndex: 2);

            ObsConfigGenerator.EnsureConfig(obsDir, 4456, "test-password");

            Assert.Equal((uint)2, ObsConfigGenerator.ReadAdapterIndex(obsDir));
        }
        finally
        {
            Directory.Delete(obsDir, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tibia-square-obs-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
