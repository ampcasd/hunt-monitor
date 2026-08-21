using TibiaSquare.HuntMonitor.Obs;
using Xunit;

namespace TibiaSquare.HuntMonitor.Tests.Obs;

public sealed class ProcessGpuAdapterDetectorTests
{
    [Fact]
    public void TryParseInstance_ParsesMatchingProcessAndLuid()
    {
        const string instance = "pid_20224_luid_0x00000000_0x0000ed7c_phys_0_eng_0_engtype_3D";

        var parsed = ProcessGpuAdapterDetector.TryParseInstance(instance, 20224, out var adapter);

        Assert.True(parsed);
        Assert.Equal(new GpuAdapterId(0, 0x0000ed7c), adapter);
    }

    [Fact]
    public void TryParseInstance_RejectsAnotherProcess()
    {
        const string instance = "pid_2788_luid_0x00000000_0x4f559d42_phys_1_eng_0_engtype_3D";

        var parsed = ProcessGpuAdapterDetector.TryParseInstance(instance, 20224, out _);

        Assert.False(parsed);
    }

    [Theory]
    [InlineData(null, null, 1, 2)]
    [InlineData(1, 1, null, 2)]
    [InlineData(2, 2, null, 1)]
    public void BuildProbeOrder_TriesCurrentThenEveryWindowsPreference(
        int? current,
        int? first,
        int? second,
        int? third)
    {
        var order = ObsGpuPreferenceStore.BuildProbeOrder(current);

        Assert.Equal(new int?[] { first, second, third }, order);
    }
}
