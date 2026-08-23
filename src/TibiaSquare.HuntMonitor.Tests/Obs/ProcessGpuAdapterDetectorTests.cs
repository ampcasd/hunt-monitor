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
    [InlineData(null, 0, 1, 2, 3)]
    [InlineData(0, 0, 1, 2, 3)]
    [InlineData(1, 1, 0, 2, 3)]
    [InlineData(3, 3, 0, 1, 2)]
    [InlineData(9, 0, 1, 2, 3)]
    public void BuildProbeOrder_TriesConfiguredAdapterThenEveryAvailableIndex(
        int? current,
        int first,
        int second,
        int third,
        int fourth)
    {
        var currentAdapterIndex = current.HasValue ? (uint?)current.Value : null;

        var order = ObsAdapterProbe.BuildProbeOrder(currentAdapterIndex, maxAdapterCount: 4);

        Assert.Equal(
            new uint[] { (uint)first, (uint)second, (uint)third, (uint)fourth },
            order);
    }

    [Fact]
    public void BuildProbeOrder_ReturnsEmptyWhenNoAdaptersCanBeProbed()
    {
        var order = ObsAdapterProbe.BuildProbeOrder(currentAdapterIndex: 0, maxAdapterCount: 0);

        Assert.Empty(order);
    }
}
