using System.Text.Json.Nodes;
using TibiaSquare.HuntMonitor.Infrastructure;
using TibiaSquare.HuntMonitor.Obs;
using Xunit;

namespace TibiaSquare.HuntMonitor.Tests.Obs;

public sealed class ObsCaptureGeometryTests
{
    [Fact]
    public async Task FourKWindow_RemovesOldMonitorScaleAndCrop_UsingNativeSourceSize()
    {
        var obs = new FakeObs(3838, 1473);

        await obs.Geometry.SynchronizeAsync();

        Assert.Equal(3838, obs.Video["baseWidth"]!.GetValue<int>());
        Assert.Equal(1473, obs.Video["baseHeight"]!.GetValue<int>());
        Assert.Equal(1472, obs.Video["outputHeight"]!.GetValue<int>());
        Assert.Equal(3836, obs.Video["outputWidth"]!.GetValue<int>());
        Assert.Equal(1, obs.Transform["scaleX"]!.GetValue<double>());
        Assert.Equal(1, obs.Transform["scaleY"]!.GetValue<double>());
        Assert.Equal(0, obs.Transform["cropRight"]!.GetValue<int>());
        Assert.Equal(0, obs.Transform["positionX"]!.GetValue<double>());
        Assert.False(obs.Transform["cropToBounds"]!.GetValue<bool>());
        Assert.Equal("OBS_BOUNDS_SCALE_INNER", obs.Transform["boundsType"]!.GetValue<string>());
        Assert.Equal(3838, obs.Transform["boundsWidth"]!.GetValue<double>());
        Assert.Equal(1473, obs.Transform["boundsHeight"]!.GetValue<double>());
        Assert.True(obs.CameraActive);
        Assert.True(obs.Calls.IndexOf("StopVirtualCam") < obs.Calls.IndexOf("SetVideoSettings"));
        Assert.True(obs.Calls.IndexOf("SetVideoSettings") < obs.Calls.IndexOf("StartVirtualCam"));

        obs.Calls.Clear();
        await obs.Geometry.SynchronizeAsync();
        Assert.DoesNotContain("SetVideoSettings", obs.Calls);
        Assert.DoesNotContain("SetSceneItemTransform", obs.Calls);
        Assert.DoesNotContain("StopVirtualCam", obs.Calls);
    }

    [Fact]
    public async Task ObsFloatSerialization_DoesNotReapplyUnchangedTransform()
    {
        var obs = new FakeObs(3838, 1473);
        await obs.Geometry.SynchronizeAsync();
        // Real OBS JSON uses decimal notation even for whole-valued floats.
        foreach (var field in new[] { "positionX", "positionY", "rotation", "scaleX", "scaleY", "boundsWidth", "boundsHeight" })
            obs.Transform[field] = JsonNode.Parse(obs.Transform[field]!.ToJsonString() + ".0");
        obs.Calls.Clear();

        await obs.Geometry.SynchronizeAsync();

        Assert.DoesNotContain("SetSceneItemTransform", obs.Calls);
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(3840, 2160)]
    [InlineData(3440, 1440)]
    public async Task WindowResize_UpdatesOnNextFrame(int width, int height)
    {
        var obs = new FakeObs(3838, 1473);
        await obs.Geometry.SynchronizeAsync();
        obs.Transform["sourceWidth"] = (double)width;
        obs.Transform["sourceHeight"] = (double)height;

        await obs.Geometry.SynchronizeAsync();

        Assert.Equal(width, obs.Video["baseWidth"]!.GetValue<int>());
        Assert.Equal(height, obs.Video["baseHeight"]!.GetValue<int>());
        Assert.Equal(width, obs.Transform["boundsWidth"]!.GetValue<double>());
        Assert.Equal(height, obs.Transform["boundsHeight"]!.GetValue<double>());
    }

    [Fact]
    public async Task UnavailableSource_PreservesCanvasAndRetriesWhenSourceAppears()
    {
        var obs = new FakeObs(0, 0);
        await obs.Geometry.SynchronizeAsync();
        Assert.DoesNotContain("SetVideoSettings", obs.Calls);
        Assert.DoesNotContain("SetSceneItemTransform", obs.Calls);
        obs.Transform["sourceWidth"] = 1920.0;
        obs.Transform["sourceHeight"] = 1080.0;

        await obs.Geometry.SynchronizeAsync();

        Assert.Equal(1920, obs.Video["baseWidth"]!.GetValue<int>());
    }

    [Fact]
    public async Task BusyOutput_FitsInsideExistingCanvas_AndRestoresCamera()
    {
        var obs = new FakeObs(5120, 1440) { RejectResize = true };

        await obs.Geometry.SynchronizeAsync();

        Assert.Equal(3840, obs.Video["baseWidth"]!.GetValue<int>());
        Assert.Equal(3840, obs.Transform["boundsWidth"]!.GetValue<double>());
        Assert.Equal(2160, obs.Transform["boundsHeight"]!.GetValue<double>());
        Assert.Equal("OBS_BOUNDS_SCALE_INNER", obs.Transform["boundsType"]!.GetValue<string>());
        Assert.True(obs.CameraActive);
        obs.Calls.Clear();
        await obs.Geometry.SynchronizeAsync();
        Assert.DoesNotContain("StopVirtualCam", obs.Calls);
        Assert.DoesNotContain("SetVideoSettings", obs.Calls);
    }

    [Fact]
    public async Task ResizeException_RestoresCamera()
    {
        var obs = new FakeObs(1920, 1080) { ThrowOnResize = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => obs.Geometry.SynchronizeAsync());
        Assert.True(obs.CameraActive);
    }

    [Fact]
    public async Task InactiveCamera_IsNotStartedByResize()
    {
        var obs = new FakeObs(1920, 1080) { CameraActive = false };
        await obs.Geometry.SynchronizeAsync();
        Assert.DoesNotContain("StopVirtualCam", obs.Calls);
        Assert.DoesNotContain("StartVirtualCam", obs.Calls);
    }

    private sealed class FakeObs
    {
        public JsonObject Video { get; } = new()
        {
            ["baseWidth"] = 3840, ["baseHeight"] = 2160,
            ["outputWidth"] = 3840, ["outputHeight"] = 2160,
        };
        public JsonObject Transform { get; }
        public List<string> Calls { get; } = [];
        public bool CameraActive { get; set; } = true;
        public bool RejectResize { get; init; }
        public bool ThrowOnResize { get; init; }
        public ObsCaptureGeometry Geometry { get; }

        public FakeObs(int width, int height)
        {
            Transform = new JsonObject
            {
                ["sourceWidth"] = (double)width, ["sourceHeight"] = (double)height,
                ["scaleX"] = 1.5, ["scaleY"] = 1.5,
                ["cropRight"] = 100, ["positionX"] = -50.0,
            };
            Geometry = new ObsCaptureGeometry(Request, new NullLogger(), "Hunt Monitor", "Tibia Game Capture");
        }

        private Task<JsonNode?> Request(string type, JsonObject? data)
        {
            Calls.Add(type);
            JsonObject? responseData = null;
            var success = true;
            switch (type)
            {
                case "GetSceneItemId": responseData = new() { ["sceneItemId"] = 1L }; break;
                case "GetSceneItemTransform": responseData = new() { ["sceneItemTransform"] = Transform.DeepClone() }; break;
                case "GetVideoSettings": responseData = (JsonObject)Video.DeepClone(); break;
                case "GetVirtualCamStatus": responseData = new() { ["outputActive"] = CameraActive }; break;
                case "StopVirtualCam": CameraActive = false; break;
                case "StartVirtualCam": CameraActive = true; break;
                case "SetVideoSettings":
                    if (ThrowOnResize) throw new InvalidOperationException("Connection interrupted");
                    success = !RejectResize;
                    if (success)
                    {
                        foreach (var field in data!) Video[field.Key] = field.Value!.DeepClone();
                        Video["outputWidth"] = Video["outputWidth"]!.GetValue<int>() & ~3;
                        Video["outputHeight"] = Video["outputHeight"]!.GetValue<int>() & ~1;
                    }
                    break;
                case "SetSceneItemTransform":
                    foreach (var field in data!["sceneItemTransform"]!.AsObject())
                        Transform[field.Key] = field.Value!.DeepClone();
                    break;
                default: throw new InvalidOperationException(type);
            }
            return Task.FromResult<JsonNode?>(new JsonObject
            {
                ["requestStatus"] = new JsonObject { ["result"] = success },
                ["responseData"] = responseData,
            });
        }
    }

    private sealed class NullLogger : ILogger
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
        public void Error(string message, Exception ex) { }
    }
}
