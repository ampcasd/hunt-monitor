using System.Text.Json;
using System.Text.Json.Nodes;
using TibiaSquare.HuntMonitor.Infrastructure;

namespace TibiaSquare.HuntMonitor.Obs;

/// <summary>
/// Keeps our scene aligned with the raw Game Capture source, not the monitor.
/// The caller holds the OBS WebSocket lock for the entire synchronization.
/// </summary>
internal sealed class ObsCaptureGeometry(
    Func<string, JsonObject?, Task<JsonNode?>> request,
    ILogger logger,
    string sceneName,
    string sourceName)
{
    private DateTime _retryCanvasAfter;

    public async Task SynchronizeAsync()
    {
        var item = await request("GetSceneItemId", new JsonObject
        {
            ["sceneName"] = sceneName,
            ["sourceName"] = sourceName,
        });
        var itemId = item?["responseData"]?["sceneItemId"]?.GetValue<long>();
        if (itemId == null) return;

        var result = await request("GetSceneItemTransform", new JsonObject
        {
            ["sceneName"] = sceneName,
            ["sceneItemId"] = itemId.Value,
        });
        var transform = result?["responseData"]?["sceneItemTransform"];
        var width = (int)(transform?["sourceWidth"]?.GetValue<double>() ?? 0);
        var height = (int)(transform?["sourceHeight"]?.GetValue<double>() ?? 0);
        // An unhooked/minimized source can temporarily have no image. Keep the
        // last valid canvas until OBS has real pixels again.
        if (width < 2 || height < 2) return;

        var video = await request("GetVideoSettings", null);
        var settings = video?["responseData"];
        if (settings == null) return;
        var canvasWidth = settings["baseWidth"]!.GetValue<int>();
        var canvasHeight = settings["baseHeight"]!.GetValue<int>();
        // OBS aligns output width to four pixels and height to two. Match that
        // rounding or odd-sized windows would trigger a video reset every frame.
        // The base canvas and OCR image stay pixel exact.
        var outputWidth = Math.Max(4, width & ~3);
        var outputHeight = height & ~1;
        if ((canvasWidth != width || canvasHeight != height ||
             settings["outputWidth"]!.GetValue<int>() != outputWidth ||
             settings["outputHeight"]!.GetValue<int>() != outputHeight) &&
            DateTime.UtcNow >= _retryCanvasAfter)
        {
            if (await ResizeCanvasAsync(width, height, outputWidth, outputHeight))
            {
                canvasWidth = width;
                canvasHeight = height;
                logger.Info($"OBS canvas matched to game capture: {width}×{height}");
            }
            else
            {
                _retryCanvasAfter = DateTime.UtcNow.AddSeconds(10);
            }
        }

        // If another output prevents resizing, fit the whole source inside the
        // existing canvas. OCR still reads native pixels directly from the input.
        var desired = new JsonObject
        {
            ["positionX"] = 0.0,
            ["positionY"] = 0.0,
            ["rotation"] = 0.0,
            ["alignment"] = 5, // top left
            ["scaleX"] = 1.0,
            ["scaleY"] = 1.0,
            ["cropLeft"] = 0,
            ["cropRight"] = 0,
            ["cropTop"] = 0,
            ["cropBottom"] = 0,
            ["cropToBounds"] = false,
            ["boundsType"] = "OBS_BOUNDS_SCALE_INNER",
            ["boundsAlignment"] = 5,
            ["boundsWidth"] = (double)canvasWidth,
            ["boundsHeight"] = (double)canvasHeight,
        };
        if (desired.All(pair => Matches(transform?[pair.Key], pair.Value))) return;

        var updated = await request("SetSceneItemTransform", new JsonObject
        {
            ["sceneName"] = sceneName,
            ["sceneItemId"] = itemId.Value,
            ["sceneItemTransform"] = desired,
        });
        if (Succeeded(updated))
            logger.Info($"OBS source fitted without cropping: {width}×{height}");
    }

    private async Task<bool> ResizeCanvasAsync(int width, int height, int outputWidth, int outputHeight)
    {
        var camera = await request("GetVirtualCamStatus", null);
        var restartCamera = camera?["responseData"]?["outputActive"]?.GetValue<bool>() == true;
        if (restartCamera && !Succeeded(await request("StopVirtualCam", null)))
            return false;

        try
        {
            return Succeeded(await request("SetVideoSettings", new JsonObject
            {
                ["baseWidth"] = width,
                ["baseHeight"] = height,
                ["outputWidth"] = outputWidth,
                ["outputHeight"] = outputHeight,
            }));
        }
        finally
        {
            if (restartCamera)
                await request("StartVirtualCam", null);
        }
    }

    private static bool Succeeded(JsonNode? response) =>
        response?["requestStatus"]?["result"]?.GetValue<bool>() == true;

    private static bool Matches(JsonNode? actual, JsonNode? expected)
    {
        // OBS serializes float fields as 1.0 while JsonValue<double> may emit 1.
        // Compare their values so unchanged transforms do not get reapplied.
        if (actual?.GetValueKind() == JsonValueKind.Number &&
            expected?.GetValueKind() == JsonValueKind.Number)
            return Math.Abs(actual.Deserialize<double>() - expected.Deserialize<double>()) < 0.001;
        return JsonNode.DeepEquals(actual, expected);
    }
}
