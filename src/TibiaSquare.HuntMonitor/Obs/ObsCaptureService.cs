using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TibiaSquare.HuntMonitor.Capture;
using TibiaSquare.HuntMonitor.Infrastructure;
using Windows.Graphics.Imaging;

namespace TibiaSquare.HuntMonitor.Obs;

/// <summary>
/// Captures frames from OBS via the WebSocket 5.x protocol using GetSourceScreenshot.
/// Implements ICaptureService for drop-in replacement of DXGI capture.
/// Also handles scene/source creation and Game Capture configuration via WebSocket.
/// </summary>
public sealed class ObsCaptureService : ICaptureService
{
    private readonly ObsProcessManager _processManager;
    private readonly ILogger _logger;

    private ClientWebSocket? _ws;
    private bool _identified;
    private bool _capturing;
    private bool _sceneConfigured;
    private bool _audioDisabled;
    private int _requestId;
    private string? _windowTarget;
    private readonly SemaphoreSlim _wsLock = new(1, 1);

    private const string SceneName = "Hunt Monitor";
    private const string SourceName = "Tibia Game Capture";
    private const string ImageFormat = "png";
    private static readonly (int W, int H) DefaultResolution = (1920, 1080);

    /// <summary>Current screenshot request dimensions — set dynamically to match the game window.</summary>
    private int _screenshotWidth = DefaultResolution.W;
    private int _screenshotHeight = DefaultResolution.H;

    /// <summary>Currently configured game resolution (0×0 = not yet set).</summary>
    private (int W, int H) _gameResolution;

    public bool IsCapturing => _capturing && _identified;

    /// <summary>The game resolution this service is configured for (0×0 if not yet set).</summary>
    public (int W, int H) GameResolution => _gameResolution;

    /// <summary>
    /// Sets the screenshot dimensions to match the detected game resolution.
    /// Called after detecting the Tibia window's client area.
    /// </summary>
    public void SetGameResolution(int width, int height)
    {
        _screenshotWidth = width;
        _screenshotHeight = height;
        _gameResolution = (width, height);
    }

    public ObsCaptureService(ObsProcessManager processManager, ILogger logger)
    {
        _processManager = processManager;
        _logger = logger;
    }

    public void StartCapture(IntPtr hwnd)
    {
        _capturing = true;
    }

    public void StopCapture()
    {
        _capturing = false;
        _sceneConfigured = false;
        _windowTarget = null;
        DisconnectWebSocket();
    }

    public async Task<SoftwareBitmap?> TryGetFrameAsync()
    {
        if (!_capturing || !_processManager.IsRunning)
            return null;

        try
        {
            if (!await EnsureConnectedAsync())
                return null;

            var screenshot = await RequestScreenshotAsync();
            if (screenshot == null)
                return null;

            return await DecodePngToSoftwareBitmapAsync(screenshot);
        }
        catch (Exception ex)
        {
            _logger.Error($"OBS capture error: {ex.Message}");
            DisconnectWebSocket();
            return null;
        }
    }

    /// <summary>
    /// Ensures the OBS scene and Game Capture source exist, then configures Game Capture
    /// to target the specific Tibia window using the OBS "Title:Class:Exe" format.
    /// </summary>
    public async Task ConfigureForTibiaAsync(TibiaWindowInfo info)
    {
        if (!await EnsureConnectedAsync())
        {
            _logger.Error("Cannot configure OBS: WebSocket not connected");
            return;
        }

        await _wsLock.WaitAsync();
        try
        {
            // 0. Disable audio ASAP to prevent Bluetooth HFP switch
            await EnsureAudioDisabledAsync();

            // 1. Ensure our scene exists
            await EnsureSceneExistsAsync();

            // 2. Ensure Game Capture source exists in the scene
            await EnsureGameCaptureExistsAsync();

            // 3. Set current scene to ours
            await SendRequestInternalAsync("SetCurrentProgramScene", new JsonObject
            {
                ["sceneName"] = SceneName,
            });

            // 4. Configure Game Capture to target the specific Tibia window
            // OBS Game Capture "window" value format: "Title:Class:executable.exe"
            var obsWindow = info.ObsWindowString;
            _windowTarget = obsWindow; // Store for recycle recovery
            _logger.Info($"Setting Game Capture window to: {obsWindow}");

            await SendRequestInternalAsync("SetInputSettings", new JsonObject
            {
                ["inputName"] = SourceName,
                ["inputSettings"] = new JsonObject
                {
                    ["capture_mode"] = "window",
                    ["window"] = obsWindow,
                    ["priority"] = 2, // 2 = match by executable name (most reliable)
                    ["capture_cursor"] = false,
                    ["capture_audio"] = false,
                    ["anti_cheat_hook"] = true,
                },
                ["overlay"] = false, // full replace, not overlay
            });

            // 5. Start virtual camera so the feed is available
            await StartVirtualCameraAsync();

            _sceneConfigured = true;
            _logger.Info("OBS Game Capture configured for Tibia");
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to configure OBS: {ex.Message}");
        }
        finally
        {
            _wsLock.Release();
        }
    }

    #region WebSocket Connection

    private async Task<bool> EnsureConnectedAsync()
    {
        if (_ws?.State == WebSocketState.Open && _identified)
            return true;

        DisconnectWebSocket();

        try
        {
            _ws = new ClientWebSocket();
            var uri = new Uri($"ws://127.0.0.1:{_processManager.WebSocketPort}");
            using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _ws.ConnectAsync(uri, connectCts.Token);

            // OBS WebSocket 5.x handshake: Hello -> Identify -> Identified
            var hello = await ReceiveMessageAsync();
            if (hello == null) return false;

            var helloData = JsonNode.Parse(hello);
            var opCode = helloData?["op"]?.GetValue<int>() ?? -1;
            if (opCode != 0)
            {
                _logger.Error($"Expected Hello (op=0), got op={opCode}");
                return false;
            }

            var identifyData = new JsonObject
            {
                ["rpcVersion"] = 1,
            };

            // Handle authentication if required
            var authNode = helloData?["d"]?["authentication"];
            if (authNode != null)
            {
                var challenge = authNode["challenge"]?.GetValue<string>() ?? "";
                var salt = authNode["salt"]?.GetValue<string>() ?? "";
                identifyData["authentication"] = GenerateAuthResponse(
                    _processManager.WebSocketPassword, salt, challenge);
            }

            await SendMessageAsync(new JsonObject
            {
                ["op"] = 1,
                ["d"] = identifyData,
            });

            var identifiedMsg = await ReceiveMessageAsync();
            if (identifiedMsg == null) return false;

            var identifiedOp = JsonNode.Parse(identifiedMsg)?["op"]?.GetValue<int>() ?? -1;
            if (identifiedOp != 2)
            {
                _logger.Error($"Expected Identified (op=2), got op={identifiedOp}");
                return false;
            }

            _identified = true;
            _audioDisabled = false; // Reset so audio gets disabled on this connection
            _logger.Info("Connected to OBS WebSocket");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error($"OBS WebSocket connection failed: {ex.Message}");
            DisconnectWebSocket();
            return false;
        }
    }

    #endregion

    #region Scene & Source Setup

    /// <summary>Must be called while holding _wsLock.</summary>
    private async Task EnsureSceneExistsAsync()
    {
        var response = await SendRequestInternalAsync("GetSceneList");
        if (response == null) return;

        var scenes = response["responseData"]?["scenes"]?.AsArray();
        bool exists = false;
        if (scenes != null)
        {
            foreach (var scene in scenes)
            {
                if (scene?["sceneName"]?.GetValue<string>() == SceneName)
                {
                    exists = true;
                    break;
                }
            }
        }

        if (!exists)
        {
            _logger.Info($"Creating OBS scene: {SceneName}");
            await SendRequestInternalAsync("CreateScene", new JsonObject
            {
                ["sceneName"] = SceneName,
            });
        }
        else
        {
            _logger.Info($"OBS scene already exists: {SceneName}");
        }
    }

    /// <summary>Must be called while holding _wsLock.</summary>
    private async Task EnsureGameCaptureExistsAsync()
    {
        var response = await SendRequestInternalAsync("GetInputList");
        if (response == null) return;

        var inputs = response["responseData"]?["inputs"]?.AsArray();
        bool exists = false;
        if (inputs != null)
        {
            foreach (var input in inputs)
            {
                if (input?["inputName"]?.GetValue<string>() == SourceName)
                {
                    exists = true;
                    break;
                }
            }
        }

        if (!exists)
        {
            _logger.Info($"Creating OBS Game Capture input: {SourceName}");
            await SendRequestInternalAsync("CreateInput", new JsonObject
            {
                ["sceneName"] = SceneName,
                ["inputName"] = SourceName,
                ["inputKind"] = "game_capture",
                ["inputSettings"] = new JsonObject
                {
                    ["capture_mode"] = "window",
                    ["capture_cursor"] = false,
                    ["capture_audio"] = false,
                    ["anti_cheat_hook"] = true,
                },
                ["sceneItemEnabled"] = true,
            });
        }
        else
        {
            _logger.Info($"OBS Game Capture input already exists: {SourceName}");
        }

        // Ensure audio capture is disabled even if the source already existed
        await SendRequestInternalAsync("SetInputSettings", new JsonObject
        {
            ["inputName"] = SourceName,
            ["inputSettings"] = new JsonObject
            {
                ["capture_audio"] = false,
            },
            ["overlay"] = true,
        });
    }

    /// <summary>
    /// Ensures audio is disabled once per WebSocket connection.
    /// Must be called while holding _wsLock.
    /// </summary>
    private async Task EnsureAudioDisabledAsync()
    {
        if (_audioDisabled) return;
        _audioDisabled = true;
        await DisableAllAudioAsync();
    }

    /// <summary>
    /// Disables all audio inputs by setting their device to "disabled" and muting them.
    /// Just muting isn't enough — OBS still opens the audio device when muted, which causes
    /// Windows to switch Bluetooth headphones from A2DP (high quality stereo) to HFP
    /// (headset profile with mic support, much lower audio quality).
    /// Setting device_id to "disabled" prevents OBS from opening the hardware at all.
    /// Must be called while holding _wsLock.
    /// </summary>
    private async Task DisableAllAudioAsync()
    {
        var response = await SendRequestInternalAsync("GetInputList");
        if (response == null) return;

        var inputs = response["responseData"]?["inputs"]?.AsArray();
        if (inputs == null) return;

        foreach (var input in inputs)
        {
            var kind = input?["inputKind"]?.GetValue<string>() ?? "";
            if (kind.Contains("wasapi", StringComparison.OrdinalIgnoreCase)
                || kind.Contains("audio", StringComparison.OrdinalIgnoreCase))
            {
                var inputName = input?["inputName"]?.GetValue<string>();
                if (inputName == null) continue;

                _logger.Info($"Disabling OBS audio input: {inputName} ({kind})");

                // Set device to "disabled" so OBS doesn't open the audio hardware
                await SendRequestInternalAsync("SetInputSettings", new JsonObject
                {
                    ["inputName"] = inputName,
                    ["inputSettings"] = new JsonObject
                    {
                        ["device_id"] = "disabled",
                    },
                    ["overlay"] = true,
                });

                // Also mute as a safety net
                await SendRequestInternalAsync("SetInputMute", new JsonObject
                {
                    ["inputName"] = inputName,
                    ["inputMuted"] = true,
                });
            }
        }
    }

    /// <summary>Must be called while holding _wsLock.</summary>
    private async Task StartVirtualCameraAsync()
    {
        // Check if virtual camera is already active
        var response = await SendRequestInternalAsync("GetVirtualCamStatus");
        var isActive = response?["responseData"]?["outputActive"]?.GetValue<bool>() ?? false;

        if (!isActive)
        {
            _logger.Info("Starting OBS Virtual Camera");
            await SendRequestInternalAsync("StartVirtualCam");
        }
        else
        {
            _logger.Info("OBS Virtual Camera already running");
        }
    }

    /// <summary>
    /// Re-pokes the Game Capture source to recover from stale/black frames.
    /// Escalates strategy based on attempt number:
    ///   1 = disable/re-enable source
    ///   2 = re-apply Game Capture window settings
    ///   3+ = switch scenes and back (most aggressive, mirrors manual fix)
    /// </summary>
    public async Task RecycleGameCaptureAsync(int attempt = 1)
    {
        if (!await EnsureConnectedAsync())
            return;

        await _wsLock.WaitAsync();
        try
        {
            if (attempt >= 3)
            {
                _logger.Info($"Recycling OBS Game Capture attempt {attempt}: scene switch");
                await RecycleViaSceneSwitchAsync();
            }
            else if (attempt == 2)
            {
                _logger.Info($"Recycling OBS Game Capture attempt {attempt}: re-apply settings");
                await RecycleViaReapplySettingsAsync();
            }
            else
            {
                _logger.Info($"Recycling OBS Game Capture attempt {attempt}: disable/re-enable");
                await RecycleViaToggleAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to recycle Game Capture: {ex.Message}");
        }
        finally
        {
            _wsLock.Release();
        }
    }

    /// <summary>Must be called while holding _wsLock.</summary>
    private async Task RecycleViaToggleAsync()
    {
        var itemId = await GetSceneItemIdAsync();
        if (itemId == null) return;

        await SendRequestInternalAsync("SetSceneItemEnabled", new JsonObject
        {
            ["sceneName"] = SceneName,
            ["sceneItemId"] = itemId.Value,
            ["sceneItemEnabled"] = false,
        });

        await Task.Delay(500);

        await SendRequestInternalAsync("SetSceneItemEnabled", new JsonObject
        {
            ["sceneName"] = SceneName,
            ["sceneItemId"] = itemId.Value,
            ["sceneItemEnabled"] = true,
        });

        _logger.Info("OBS Game Capture recycled (toggle)");
    }

    /// <summary>Must be called while holding _wsLock.</summary>
    private async Task RecycleViaReapplySettingsAsync()
    {
        // Re-apply the Game Capture window target — forces OBS to re-initialize capture.
        // overlay=false replaces ALL settings, so we must include window + priority
        // or OBS loses the capture target and shows black permanently.
        var settings = new JsonObject
        {
            ["capture_mode"] = "window",
            ["capture_cursor"] = false,
            ["capture_audio"] = false,
            ["anti_cheat_hook"] = true,
        };

        if (_windowTarget != null)
        {
            settings["window"] = _windowTarget;
            settings["priority"] = 2; // match by executable name
        }

        await SendRequestInternalAsync("SetInputSettings", new JsonObject
        {
            ["inputName"] = SourceName,
            ["inputSettings"] = settings,
            ["overlay"] = false,
        });

        _logger.Info($"OBS Game Capture recycled (re-apply settings, window={_windowTarget ?? "null"})");
    }

    /// <summary>Must be called while holding _wsLock.</summary>
    private async Task RecycleViaSceneSwitchAsync()
    {
        // Get the list of scenes to find a different one to switch to
        var response = await SendRequestInternalAsync("GetSceneList");
        var scenes = response?["responseData"]?["scenes"]?.AsArray();
        string? otherScene = null;
        if (scenes != null)
        {
            foreach (var scene in scenes)
            {
                var name = scene?["sceneName"]?.GetValue<string>();
                if (name != null && name != SceneName)
                {
                    otherScene = name;
                    break;
                }
            }
        }

        if (otherScene == null)
        {
            // No other scene exists — create a temporary one
            otherScene = "TibiaSquare_Temp";
            await SendRequestInternalAsync("CreateScene", new JsonObject
            {
                ["sceneName"] = otherScene,
            });
        }

        // Switch away
        await SendRequestInternalAsync("SetCurrentProgramScene", new JsonObject
        {
            ["sceneName"] = otherScene,
        });

        await Task.Delay(1000);

        // Switch back
        await SendRequestInternalAsync("SetCurrentProgramScene", new JsonObject
        {
            ["sceneName"] = SceneName,
        });

        _logger.Info("OBS Game Capture recycled (scene switch)");
    }

    /// <summary>Must be called while holding _wsLock.</summary>
    private async Task<long?> GetSceneItemIdAsync()
    {
        var response = await SendRequestInternalAsync("GetSceneItemId", new JsonObject
        {
            ["sceneName"] = SceneName,
            ["sourceName"] = SourceName,
        });
        var itemId = response?["responseData"]?["sceneItemId"]?.GetValue<long>();
        if (itemId == null)
            _logger.Error("Could not find scene item ID for Game Capture");
        return itemId;
    }

    #endregion

    #region Screenshot Capture

    private async Task<byte[]?> RequestScreenshotAsync()
    {
        if (!_sceneConfigured)
            return null;

        await _wsLock.WaitAsync();
        try
        {
            // Disable audio on first frame request after connection
            await EnsureAudioDisabledAsync();

            var response = await SendRequestInternalAsync("GetSourceScreenshot", new JsonObject
            {
                // Capture the raw input rather than the rendered scene. Scene-item
                // transforms and stale overlay inputs can otherwise cover or scale
                // the game even while Game Capture itself is healthy.
                ["sourceName"] = SourceName,
                ["imageFormat"] = ImageFormat,
                ["imageWidth"] = _screenshotWidth,
                ["imageHeight"] = _screenshotHeight,
            });

            if (response == null) return null;

            var statusCode = response["requestStatus"]?["code"]?.GetValue<int>() ?? 0;
            if (statusCode != 100)
            {
                var comment = response["requestStatus"]?["comment"]?.GetValue<string>() ?? "unknown";
                _logger.Error($"GetSourceScreenshot failed: {comment} (code {statusCode})");
                return null;
            }

            var imageData = response["responseData"]?["imageData"]?.GetValue<string>();
            if (imageData == null) return null;

            // Strip data URI prefix: "data:image/png;base64,"
            var commaIdx = imageData.IndexOf(',');
            var base64 = commaIdx >= 0 ? imageData[(commaIdx + 1)..] : imageData;
            return Convert.FromBase64String(base64);
        }
        finally
        {
            _wsLock.Release();
        }
    }

    private static async Task<SoftwareBitmap?> DecodePngToSoftwareBitmapAsync(byte[] pngBytes)
    {
        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        await stream.WriteAsync(
            System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(pngBytes));
        stream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(stream);
        return await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore);
    }

    #endregion

    #region WebSocket Messaging

    /// <summary>
    /// Sends a request and returns the response "d" node.
    /// Acquires the WebSocket lock — call from unlocked context.
    /// </summary>
    private async Task<JsonNode?> SendRequestAsync(string requestType, JsonObject? requestData = null)
    {
        await _wsLock.WaitAsync();
        try
        {
            return await SendRequestInternalAsync(requestType, requestData);
        }
        finally
        {
            _wsLock.Release();
        }
    }

    /// <summary>
    /// Sends a request and returns the response "d" node.
    /// Must be called while holding _wsLock.
    /// </summary>
    private async Task<JsonNode?> SendRequestInternalAsync(string requestType, JsonObject? requestData = null)
    {
        var requestId = Interlocked.Increment(ref _requestId).ToString();

        var d = new JsonObject
        {
            ["requestType"] = requestType,
            ["requestId"] = requestId,
        };
        if (requestData != null)
            d["requestData"] = requestData;

        await SendMessageAsync(new JsonObject
        {
            ["op"] = 6,
            ["d"] = d,
        });

        // Read responses until we get our RequestResponse
        for (int i = 0; i < 30; i++)
        {
            var raw = await ReceiveMessageAsync();
            if (raw == null) return null;

            var msg = JsonNode.Parse(raw);
            var op = msg?["op"]?.GetValue<int>() ?? -1;

            if (op == 5) continue; // Skip events

            if (op == 7) // RequestResponse
            {
                var respId = msg?["d"]?["requestId"]?.GetValue<string>();
                if (respId != requestId) continue;

                var statusCode = msg?["d"]?["requestStatus"]?["code"]?.GetValue<int>() ?? 0;
                if (statusCode != 100)
                {
                    var comment = msg?["d"]?["requestStatus"]?["comment"]?.GetValue<string>() ?? "";
                    _logger.Info($"OBS {requestType}: code={statusCode} {comment}");
                }
                return msg?["d"];
            }
        }

        _logger.Error($"No response for OBS request: {requestType}");
        return null;
    }

    private async Task SendMessageAsync(JsonObject message)
    {
        if (_ws?.State != WebSocketState.Open)
            throw new InvalidOperationException("WebSocket is not connected");

        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private async Task<string?> ReceiveMessageAsync()
    {
        if (_ws?.State != WebSocketState.Open)
            return null;

        var buffer = new byte[16 * 1024 * 1024]; // 16MB for base64 screenshots
        var result = new StringBuilder();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        while (true)
        {
            WebSocketReceiveResult received;
            try
            {
                received = await _ws.ReceiveAsync(buffer, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            if (received.MessageType == WebSocketMessageType.Close)
            {
                _identified = false;
                return null;
            }

            result.Append(Encoding.UTF8.GetString(buffer, 0, received.Count));

            if (received.EndOfMessage)
                return result.ToString();
        }
    }

    #endregion

    #region Auth

    private static string GenerateAuthResponse(string password, string salt, string challenge)
    {
        var hash1 = SHA256.HashData(Encoding.UTF8.GetBytes(password + salt));
        var base64Secret = Convert.ToBase64String(hash1);
        var hash2 = SHA256.HashData(Encoding.UTF8.GetBytes(base64Secret + challenge));
        return Convert.ToBase64String(hash2);
    }

    #endregion

    private void DisconnectWebSocket()
    {
        _identified = false;
        // Don't reset _sceneConfigured — the OBS scene/source persist across
        // reconnections. Resetting it causes frames to silently stop after
        // transient WebSocket errors because ConfigureForTibiaAsync is only
        // called on initial Tibia window detection, not on reconnect.

        if (_ws == null) return;

        try
        {
            _ws.Dispose();
        }
        catch { }

        _ws = null;
    }

    public void Dispose()
    {
        _capturing = false;
        DisconnectWebSocket();
        _wsLock.Dispose();
    }
}
