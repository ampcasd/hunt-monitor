using TibiaSquare.HuntMonitor.Infrastructure;
using TibiaSquare.HuntMonitor.Private;
using Xunit;

namespace TibiaSquare.HuntMonitor.Tests.Infrastructure;

public sealed class PeriodicCaptureLoopTests
{
    [Theory]
    [InlineData(PanelLocateStatus.NotAttempted, false)]
    [InlineData(PanelLocateStatus.Found, false)]
    [InlineData(PanelLocateStatus.InvalidCrop, false)]
    [InlineData(PanelLocateStatus.NotFound, true)]
    public void VisibilityWarning_OnlyCountsConfirmedFullFrameAbsence(
        PanelLocateStatus status,
        bool expected)
    {
        Assert.Equal(expected, PeriodicCaptureLoop.IsConfirmedPanelMissing(status));
    }
}
