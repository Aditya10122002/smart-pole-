// ============================================================
// HardwareAudio.cs — Audio I/O & Microphone Interface
// BluAI Pvt. Ltd. | VitalsChair™ Backend
//
// Manages audio input/output hardware detection, microphone
// streaming, audio playback, and real-time device switching.
// Supports HDMI, USB headsets, and dedicated mic input.
// ============================================================

using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Audio hardware interface for VitalsChair covering microphone
/// recording, audio playback, and device detection/switching.
/// </summary>
public static class HardwareAudio
{
    // ────────────────────────────────────────────────────────
    // CONFIGURATION
    // ────────────────────────────────────────────────────────
    private static IConfiguration? _configuration;

    // ────────────────────────────────────────────────────────
    // AUDIO DEVICE STATE
    // ────────────────────────────────────────────────────────
    private static volatile string _audioOut = "plughw:0,0";           // Default HDMI output
    private static volatile string _audioMic = "";                     // Mic input device
    private static volatile string _audioMode = "HdmiOnly";            // Current mode
    private static int _chunkssent = 0;                                // Chunks sent counter
    private static string _currentRecordingFilePath = "";              // Current recording file

    // Software playback volume (0–100). The HDMI/I2S speaker (card 1) exposes no
    // ALSA volume control, so volume is applied as software gain on the live PCM
    // stream in GeminiLiveSession. This covers BOTH the speaker and the USB headset
    // because all live audio flows through the same playback pipe.
    private static volatile int _volumePercent = 75;                   // GUI default
    private static volatile bool _muted = false;

    // Microphone state tracking
    private static CancellationTokenSource? _microphoneCts;
    private static Task? _microphoneTask;
    private static DateTime? _microphoneStartTime = null;

    // ────────────────────────────────────────────────────────
    // INITIALIZE
    // Sets the configuration object for API access.
    // Call this once during application startup.
    // ────────────────────────────────────────────────────────
    public static void Initialize(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    // ────────────────────────────────────────────────────────
    // START AUDIO DEVICE MONITOR
    // Initiates periodic detection of audio hardware changes.
    // Runs in background every 5 seconds.
    // ────────────────────────────────────────────────────────
    public static void StartAudioDeviceMonitor()
    {
        // Run detection immediately
        DetectAudioDevices();

        // Schedule periodic detection
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(5000);
                DetectAudioDevices();
            }
        });
    }

    // ────────────────────────────────────────────────────────
    // DETECT AUDIO DEVICES
    // Queries ALSA to identify connected audio hardware.
    // Determines if running in HDMI-only, USB-headset, or
    // USB-microphone mode, then updates output/input routing.
    // ────────────────────────────────────────────────────────
private static void DetectAudioDevices()
{
    try
    {
        if (!File.Exists("/proc/asound/cards")) return;

        string cards = File.ReadAllText("/proc/asound/cards");

        int hdmiCard = -1;
        int usbCard  = -1;
        bool usbIsHeadset = false;

        var lines = cards.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var match = System.Text.RegularExpressions.Regex.Match(line, @"^\s*(\d+)\s+\[");
            if (!match.Success) continue;

            int cardNum = int.Parse(match.Groups[1].Value);
            string desc = line;
            if (i + 1 < lines.Length) desc += " " + lines[i + 1];
            desc = desc.ToLower();

            if (desc.Contains("hdmi"))
            {
                hdmiCard = cardNum;
            }
            else if (desc.Contains("usb"))
            {
                usbCard = cardNum;
                usbIsHeadset =
                    desc.Contains("plantronics") || desc.Contains("blackwire") ||
                    desc.Contains("headset")     || desc.Contains("headphone") ||
                    desc.Contains("jabra")       || desc.Contains("logitech")  ||
                    desc.Contains("sennheiser")  || desc.Contains("bose")      ||
                    desc.Contains("sony")        || desc.Contains("poly");
            }
        }

        bool usbHasCapture = usbCard >= 0 && File.Exists($"/dev/snd/pcmC{usbCard}D0c");
        string hdmiOut = hdmiCard >= 0 ? $"plughw:{hdmiCard},0" : "plughw:0,0";

        string newOut, newMic, newMode;

        if (usbCard < 0 || !usbHasCapture)
        {
            newMode = "HdmiOnly";
            newOut  = hdmiOut;
            newMic  = "";
        }
        else if (usbIsHeadset)
        {
            newMode = "UsbHeadset";
            newOut  = $"plughw:{usbCard},0";
            newMic  = $"plughw:{usbCard},0";
        }
        else
        {
            newMode = "HdmiWithUsbMic";
            newOut  = hdmiOut;
            newMic  = $"plughw:{usbCard},0";
        }

        if (newMode != _audioMode)
        {
            _audioMode = newMode;
            _audioOut  = newOut;
            _audioMic  = newMic;
            //Log($"🔊 [Audio] Mode → {newMode} | Out={newOut} | Mic={(_audioMic == "" ? "none" : _audioMic)}");
        }
     }
     catch (Exception)
     {
         // Log($"⚠️ [Audio] Detection error");
     }
}

    // ────────────────────────────────────────────────────────
    // PLAY WAV AUDIO
    // Plays WAV audio via configured output device using aplay.
    // ────────────────────────────────────────────────────────
    public static async Task PlayWavAsync(byte[] audioBytes)
    {
        try
        {
            string tmpMp3 = Path.Combine(Path.GetTempPath(), $"audio_{Guid.NewGuid()}.mp3");
            await File.WriteAllBytesAsync(tmpMp3, audioBytes);

            string outDevice = _audioOut;

            var psi = new ProcessStartInfo
            {
                FileName = "ffplay",
                Arguments = $"-nodisp -autoexit -D {outDevice} {tmpMp3}",
                UseShellExecute = false,
                RedirectStandardOutput = true
            };

            using var proc = Process.Start(psi);
            proc.WaitForExit();

            try { File.Delete(tmpMp3); } catch { }
        }
        catch (Exception ex)
        {
            ProductionLogger.Error(LogCategory.Audio, "Audio playback error", ex);
        }
    }

    // ────────────────────────────────────────────────────────
    // START MICROPHONE STREAMING
    // Begins real-time microphone recording and sends audio
    // chunks to the BluNote API for transcription.
    // ────────────────────────────────────────────────────────
    public static async Task StartMicrophoneStreamingAsync()
    {
        if (_microphoneTask != null && !_microphoneTask.IsCompleted)
        {
            ProductionLogger.Warning(LogCategory.Audio, "Microphone already recording");
            return;
        }

        string micDevice = _audioMic;
        if (string.IsNullOrEmpty(micDevice))
        {
            ProductionLogger.Warning(LogCategory.Audio, "No microphone detected - cannot record");
            return;
        }

        ProductionLogger.Info(LogCategory.Audio, "Microphone device selected", new System.Collections.Generic.Dictionary<string, object> { ["device"] = micDevice });

        _microphoneCts = new CancellationTokenSource();
        ProductionLogger.Info(LogCategory.Audio, "Starting audio streaming");

        _microphoneTask = Task.Run(async () =>
        {
            await RecordAndSendChunksAsync(_microphoneCts.Token, micDevice);
        });

        _microphoneStartTime = DateTime.Now;
    }

    // ────────────────────────────────────────────────────────
    // RECORD AND SEND CHUNKS
    // Records audio via arecord and streams to BluNote API
    // in real-time using chunks (PCM frames).
    // ────────────────────────────────────────────────────────
    private static async Task RecordAndSendChunksAsync(CancellationToken cancellationToken, string micDevice)
    {
        const int SAMPLE_RATE = 16000;
        const int CHANNELS = 1;
        const int BITS = 16;
        const int CHUNK_SIZE_MS = 500;
        int bytesPerSample = BITS / 8;
        int bytesPerSecond = SAMPLE_RATE * CHANNELS * bytesPerSample;
        int chunkBytes = (bytesPerSecond * CHUNK_SIZE_MS) / 1000;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "arecord",
                Arguments = $"-D {micDevice} -f S16_LE -r {SAMPLE_RATE} -c {CHANNELS}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            using var audioStream = proc.StandardOutput.BaseStream;

            ProductionLogger.Info(LogCategory.Audio, "Recording started - streaming chunks to API");

            var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            byte[] buffer = new byte[chunkBytes];
            int bytesRead;

            while (!cancellationToken.IsCancellationRequested &&
                   (bytesRead = await audioStream.ReadAsync(buffer, 0, chunkBytes, cancellationToken)) > 0)
            {
                if (bytesRead > 0)
                {
                    byte[] chunk = new byte[bytesRead];
                    Buffer.BlockCopy(buffer, 0, chunk, 0, bytesRead);

                    ProductionLogger.Debug(LogCategory.Audio, "Sending microphone chunk",
                        new System.Collections.Generic.Dictionary<string, object>
                        {
                            ["chunk_number"] = _chunkssent,
                            ["bytes"] = bytesRead,
                            ["sample_rate"] = SAMPLE_RATE
                        });
                    await SendAudioChunkAsync(httpClient, chunk, chunk.Length);
                }
            }

            ProductionLogger.Info(LogCategory.Audio, "Recording stopped", new System.Collections.Generic.Dictionary<string, object> { ["chunks_sent"] = _chunkssent });
        }
        catch (Exception ex)
        {
            ProductionLogger.Error(LogCategory.Audio, "Microphone streaming error", ex);
        }
    }

    // ────────────────────────────────────────────────────────
    // SEND AUDIO CHUNK
    // Uploads a single audio chunk to the BluNote API.
    // ────────────────────────────────────────────────────────
    private static async Task SendAudioChunkAsync(HttpClient client, byte[] buffer, int length)
    {
        try
        {
            var content = new ByteArrayContent(buffer);
            content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");

            // Load BluNote API URL from config
            string blunoteUrl = ConfigManager.GetBluHealthApiUrl("BluNoteUrl");
            if (string.IsNullOrWhiteSpace(blunoteUrl))
            {
                blunoteUrl = _configuration?["BluHealthApi:BluNoteUrl"] ?? string.Empty;
            }
            
            if (string.IsNullOrWhiteSpace(blunoteUrl))
            {
                ProductionLogger.Warning(LogCategory.Audio, "BluNoteUrl not configured in ConfigManager or appsettings");
                return;
            }

            var response = await client.PostAsync(blunoteUrl, content);
            string resp = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                _chunkssent++;
                ProductionLogger.Info(LogCategory.Audio, "Microphone chunk sent successfully", new System.Collections.Generic.Dictionary<string, object> { ["chunk_number"] = _chunkssent, ["bytes"] = length });
            }
            else
            {
                ProductionLogger.Warning(LogCategory.Audio, "Microphone chunk send failed", new System.Collections.Generic.Dictionary<string, object> { ["chunk_number"] = _chunkssent, ["status_code"] = (int)response.StatusCode });
            }
        }
        catch (Exception ex)
        {
            ProductionLogger.Error(LogCategory.Audio, "Microphone send error", ex);
        }
    }

    // ────────────────────────────────────────────────────────
    // STOP MICROPHONE STREAMING
    // Gracefully terminates microphone recording.
    // ────────────────────────────────────────────────────────
    public static async Task StopMicrophoneStreamingAsync()
    {
        if (_microphoneCts == null)
        {
            ProductionLogger.Warning(LogCategory.Audio, "No active microphone recording");
            return;
        }

        try
        {
            ProductionLogger.Info(LogCategory.Audio, "Stopping microphone streaming");
            _microphoneCts.Cancel();

            if (_microphoneTask != null)
                await _microphoneTask;
        }
        catch (Exception ex)
        {
            ProductionLogger.Error(LogCategory.Audio, "Error stopping microphone", ex);
        }
        finally
        {
            _microphoneCts = null;
            _microphoneTask = null;
        }
    }

    // ────────────────────────────────────────────────────────
    // UTILITY: Get Current Audio Mode
    // Returns the detected audio routing mode.
    // ────────────────────────────────────────────────────────
    public static string GetAudioMode() => _audioMode;

    // ────────────────────────────────────────────────────────
    // UTILITY: Get Current Audio Output Device
    // Returns the active ALSA output device identifier.
    // ────────────────────────────────────────────────────────
    public static string GetAudioOutputDevice() => _audioOut;

    // ────────────────────────────────────────────────────────
    // UTILITY: Get Current Microphone Device
    // Returns the active ALSA microphone device identifier.
    // ────────────────────────────────────────────────────────
    public static string GetMicrophoneDevice() => _audioMic;

    // ────────────────────────────────────────────────────────
    // SET VOLUME
    // Sets the software playback volume (0–100). Applied as gain on
    // the live PCM stream, so it works on the HDMI/I2S speaker (which
    // has no ALSA volume control) and the USB headset alike.
    // ────────────────────────────────────────────────────────
    public static void SetVolume(int percent)
    {
        _volumePercent = Math.Clamp(percent, 0, 100);
        if (_volumePercent > 0)
            _muted = false;                 // dragging the slider up clears mute

        ProductionLogger.Info(LogCategory.Audio, "Volume set",
            new System.Collections.Generic.Dictionary<string, object> { ["percent"] = _volumePercent, ["muted"] = _muted });
    }

    // ────────────────────────────────────────────────────────
    // CURRENT VOLUME / PLAYBACK GAIN
    // GetPlaybackGain returns linear gain 0.0–1.0 for PCM scaling
    // (0 when muted). Consumed by GeminiLiveSession playback loop.
    // ────────────────────────────────────────────────────────
    public static int GetVolume() => _volumePercent;
    public static bool IsMuted() => _muted;
    public static double GetPlaybackGain() => _muted ? 0.0 : _volumePercent / 100.0;

    // ────────────────────────────────────────────────────────
    // MUTE / UNMUTE
    // Mute silences playback without losing the volume setting;
    // Unmute restores it (or restorePercent if it was left at 0).
    // ────────────────────────────────────────────────────────
    public static void Mute()
    {
        _muted = true;
        ProductionLogger.Info(LogCategory.Audio, "Audio muted");
    }

    public static void Unmute(int restorePercent = 75)
    {
        _muted = false;
        if (_volumePercent == 0)
            _volumePercent = Math.Clamp(restorePercent, 1, 100);

        ProductionLogger.Info(LogCategory.Audio, "Audio unmuted",
            new System.Collections.Generic.Dictionary<string, object> { ["percent"] = _volumePercent });
    }
}
