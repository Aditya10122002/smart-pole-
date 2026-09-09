// ─────────────────────────────────────────────────────────────────────────────
// BluAI VitalsChair™ — Gemini Live Voice Session v3
// File   : GeminiLiveSession.cs
// Changes:
//   - Interruption handling (clears audio queue, resets speaking state)
//   - audioStreamEnd sent when mic paused during Gemini speech
//   - SkipCurrentQuestionAsync uses realtime_input (not client_content)
//   - Initial turn uses realtime_input (not client_content)
//   - Full clinical prompt: pain anchor, PMH, incomplete input rules
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

public class GeminiLiveSession : IDisposable
{
    // ── Config ─────────────────────────────────────────────────────────────
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _language;

    private static readonly Dictionary<string, string> LanguageCodes = new()
    {
        { "en-US", "English (US)" },
        { "en-IN", "English (India)" },
        { "hi-IN", "Hindi (India)" },
        { "pa-IN", "Punjabi (India)" },
        { "es-ES", "Spanish (Spain)" },
        { "es-US", "Spanish (US)" },
        { "he-IL", "Hebrew (Israel)" },
        { "yi",    "Yiddish" }
    };

    private string GEMINI_WS_URL =>
        $"wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1alpha.GenerativeService.BidiGenerateContent?key={_apiKey}";

    // Audio format
    private const int MIC_SAMPLE_RATE  = 16000;
    private DateTime _lastMicLevelSent = DateTime.MinValue;   // GUI mic-meter throttle
    private const int SPK_SAMPLE_RATE  = 24000;
    private const int MIC_CHUNK_MS     = 100;
    private const int MIC_CHUNK_BYTES  = (MIC_SAMPLE_RATE * 2 * MIC_CHUNK_MS) / 1000;

    // ── Fields ─────────────────────────────────────────────────────────────
    private readonly string              _patientName;
    private readonly string              _audioOut;
    private readonly string              _audioMic;
    private readonly Action<string>      _log;
    private readonly Func<string, Task>  _sendToLua;
    private readonly CancellationToken   _sessionCt;

    private ClientWebSocket? _ws;
    private Process?         _arecordProc;
    private Process?         _aplayProc;
    private StreamWriter?    _aplayInput;

    private readonly ConcurrentQueue<byte[]> _audioQueue  = new();
    private readonly SemaphoreSlim           _audioSignal = new(0, int.MaxValue);
    private readonly StringBuilder           _outputTranscriptionBuffer = new();

    private volatile bool _geminiSpeaking  = false;
    private volatile bool _sessionComplete = false;
    private volatile bool _audioStreamEndSent = false;
    private volatile bool _sessionRunning = false;
    private int           _questionIndex   = 0;
    private int           _lastAskedQuestionIndex = -1;

    private readonly Dictionary<int, (string question, string answer)> _sessionQA = new();
    private string _currentPatientAnswer = "";

    // ── Constructor ────────────────────────────────────────────────────────
    public GeminiLiveSession(
        string patientName,
        string audioOut,
        string audioMic,
        string apiKey,
        string model,
        string language,
        Action<string>     log,
        Func<string, Task> sendToLua,
        CancellationToken  sessionCt)
    {
        _patientName = patientName;
        _audioOut    = audioOut;
        _audioMic    = audioMic;
        _apiKey      = apiKey;
        _model       = model;
        _log         = log;
        _sendToLua   = sendToLua;
        _sessionCt   = sessionCt;
        _language    = ValidateLanguage(language);
    }

    private string ValidateLanguage(string lang)
    {
        if (LanguageCodes.ContainsKey(lang)) return lang;
        _log($"⚠️ Language '{lang}' not supported, defaulting to en-US");
        return "en-US";
    }

    // ── Entry Point ────────────────────────────────────────────────────────
    public async Task RunAsync()
    {
        _log($"[GEMINI] Model='{_model}' | Out='{_audioOut}' | Mic='{_audioMic}' | Patient='{_patientName}'");

        try
        {
            _sessionRunning = true;
            _log("[GEMINI] Connecting...");
            _ws = new ClientWebSocket();
            _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            await _ws.ConnectAsync(new Uri(GEMINI_WS_URL), _sessionCt);
            _log("[GEMINI]  Connected");

            await SendSetupAsync();

            var receiveTask  = Task.Run(ReceiveLoopAsync,   _sessionCt);
            var micTask      = Task.Run(MicStreamLoopAsync, _sessionCt);
            var playbackTask = Task.Run(PlaybackLoopAsync,  _sessionCt);

            await receiveTask;
            _log("[GEMINI] Session loop ended");
        }
        catch (OperationCanceledException) { _log("[GEMINI] Cancelled"); }
        catch (Exception ex)               { _log($"[GEMINI]  {ex.Message}"); }
        finally
        {
            _sessionRunning = false;
            StopMic();
            StopPlayback();
            await CloseWebSocketAsync();
        }
    }

    // ── Setup ──────────────────────────────────────────────────────────────
    private async Task SendSetupAsync()
    {
        var setup = new
        {
            setup = new
            {
                model = $"models/{_model}",
                generation_config = new
                {
                    response_modalities = new[] { "AUDIO" },
                    speech_config = new
                    {
                        voice_config = new
                        {
                            prebuilt_voice_config = new { voice_name = "Aoede" }
                        }
                    }
                },
                system_instruction = new
                {
                    parts = new[] { new { text = GetSystemPrompt(_language) } }
                },
                input_audio_transcription  = new { },
                output_audio_transcription = new { }
            }
        };

        await SendWsTextAsync(JsonSerializer.Serialize(setup));
        _log("[GEMINI] Setup sent");

        // ── FIXED: use realtime_input for initial turn (not client_content) ──
        var initialTurn = new
        {
            realtime_input = new
            {
                text = "Please begin the health assessment."
            }
        };

        await SendWsTextAsync(JsonSerializer.Serialize(initialTurn));
        _log("[GEMINI] Initial turn sent");
    }

    // ── Receive Loop ───────────────────────────────────────────────────────
    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[65536];

        while (!_sessionCt.IsCancellationRequested && _ws?.State == WebSocketState.Open)
        {
            try
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _sessionCt);
                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _log($"[GEMINI] Closed — {_ws?.CloseStatus} {_ws?.CloseStatusDescription}");
                    break;
                }

                await ProcessServerMessageAsync(Encoding.UTF8.GetString(ms.ToArray()));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log($"[GEMINI] Receive error: {ex.Message}"); break; }
        }
    }

    private async Task ProcessServerMessageAsync(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("serverContent", out var sc)) return;

            // ── INTERRUPTION HANDLING ─────────────────────────────────────
            if (sc.TryGetProperty("interrupted", out var interrupted) && interrupted.GetBoolean())
            {
                _log("[GEMINI]  Interrupted by patient — clearing audio queue");
                while (_audioQueue.TryDequeue(out _)) { }  // drain queue
                _geminiSpeaking = false;
                await _sendToLua("VA:LISTENING");
                await _sendToLua("MIC_STATUS|RECORDING");
                _log("[GEMINI] Audio queue cleared — listening resumed");
            }

            // ── OUTPUT TRANSCRIPTION (text of Gemini's speech) ────────────
            bool hasOutputTranscription = false;
            string outputTranscriptionText = "";
            if (sc.TryGetProperty("outputTranscription", out var ot) &&
                ot.TryGetProperty("text", out var otText))
            {
                outputTranscriptionText = otText.GetString() ?? "";
                hasOutputTranscription  = !string.IsNullOrWhiteSpace(outputTranscriptionText);
            }

            // ── INPUT TRANSCRIPTION (patient's speech) ────────────────────
            if (sc.TryGetProperty("inputTranscription", out var it) &&
                it.TryGetProperty("text", out var itText))
            {
                string transcript = itText.GetString() ?? "";
                if (!string.IsNullOrWhiteSpace(transcript))
                {
                    _log($"[PATIENT]  {transcript}");
                    _currentPatientAnswer = transcript;

                    // Immediately update the answer for the last asked question
                    if (_lastAskedQuestionIndex >= 0 && _sessionQA.ContainsKey(_lastAskedQuestionIndex))
                    {
                        var (question, _) = _sessionQA[_lastAskedQuestionIndex];
                        _sessionQA[_lastAskedQuestionIndex] = (question, transcript);
                    }

                    await _sendToLua($"TRANSCRIPT:{transcript}");
                }
            }

            // ── AUDIO + TEXT PARTS ────────────────────────────────────────
            if (sc.TryGetProperty("modelTurn", out var mt) &&
                mt.TryGetProperty("parts", out var parts))
            {
                foreach (var part in parts.EnumerateArray())
                {
                    // Audio
                    if (part.TryGetProperty("inlineData", out var inline) &&
                        inline.TryGetProperty("data", out var data))
                    {
                        byte[] pcm = Convert.FromBase64String(data.GetString() ?? "");
                        if (pcm.Length > 0)
                        {
                            if (!_geminiSpeaking)
                            {
                                _geminiSpeaking = true;
                                _audioStreamEndSent = false;
                                await _sendToLua("VA:SPEAKING");
                                _log("[GEMINI]  Speaking...");
                            }
                            _audioQueue.Enqueue(pcm);
                            _audioSignal.Release();
                        }
                    }

                    // Text (fallback if outputTranscription not present)
                    if (part.TryGetProperty("text", out var textProp))
                    {
                        string text = textProp.GetString() ?? "";
                        if (!string.IsNullOrWhiteSpace(text) && !hasOutputTranscription)
                            await HandleOutputTranscriptionChunkAsync(text);
                    }
                }
            }

            // Use outputTranscription if available (preferred)
            if (hasOutputTranscription)
                await HandleOutputTranscriptionChunkAsync(outputTranscriptionText);

            // ── TURN COMPLETE ─────────────────────────────────────────────
            if (sc.TryGetProperty("turnComplete", out var tc) && tc.GetBoolean())
            {
                _geminiSpeaking = false;
                _audioStreamEndSent = false;
                // Clear previous answer before sending new question
                await _sendToLua("ANSWER_CLEAR");
                await FlushOutputTranscriptionAsync();
                _questionIndex++;
                _log($" Turn {_questionIndex} complete");

                if (!_sessionComplete)
                {
                    await _sendToLua("VA:LISTENING");
                    await _sendToLua("MIC_STATUS|RECORDING");
                }
            }
        }
        catch (Exception ex)
        {
            _log($"[GEMINI] Parse error: {ex.Message}");
        }
    }

    // ── Output Transcription Chunk ─────────────────────────────────────────
    private async Task HandleOutputTranscriptionChunkAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        _outputTranscriptionBuffer.Append(text);

        // Send chunk immediately for real-time display (strip signals)
        string displayText = text
            .Replace("SESSION_COMPLETE", "")
            .Replace("DANGER_FLAG", "")
            .Trim();

        if (!string.IsNullOrWhiteSpace(displayText))
            await _sendToLua($"QUESTION_CHUNK:{displayText}");

        // SESSION_COMPLETE
        if (text.Contains("SESSION_COMPLETE", StringComparison.Ordinal) && !_sessionComplete)
        {
            _sessionComplete = true;
            _log("[GEMINI] SESSION_COMPLETE detected — waiting for audio");
            _ = Task.Run(async () =>
            {
                int waitedMs = 0;
                while (_geminiSpeaking && waitedMs < 30000)
                {
                    await Task.Delay(200);
                    waitedMs += 200;
                }
                await Task.Delay(1500);
                _log("[GEMINI] Audio done — 10s grace period");
                await _sendToLua("SESSION_END");
                await Task.Delay(10000);
                _log("[GEMINI] Closing session");
                StopMic();
                StopPlayback();
                await CloseWebSocketAsync();
            });
        }

        // DANGER_FLAG
        if (text.Contains("DANGER_FLAG", StringComparison.Ordinal) && !_sessionComplete)
        {
            _sessionComplete = true;
            _log("[GEMINI] DANGER_FLAG detected");
            Audit.Log("DANGER_FLAG", "info", "system");   // flag only — no content
            _ = Task.Run(async () =>
            {
                int waitedMs = 0;
                while (_geminiSpeaking && waitedMs < 20000)
                {
                    await Task.Delay(200);
                    waitedMs += 200;
                }
                await Task.Delay(1500);
                await _sendToLua("DANGER_FLAG");
                await _sendToLua("SESSION_END");
                StopMic();
                StopPlayback();
                await CloseWebSocketAsync();
            });
        }
    }

    // ── Flush Full Transcription at Turn End ───────────────────────────────
    private async Task FlushOutputTranscriptionAsync()
    {
        if (_outputTranscriptionBuffer.Length == 0) return;

        string fullText = _outputTranscriptionBuffer.ToString();
        _outputTranscriptionBuffer.Clear();

        // Clean signals from stored text
        string cleanText = fullText
            .Replace("SESSION_COMPLETE", "")
            .Replace("DANGER_FLAG", "")
            .Trim();

        _log($"[Q{_questionIndex}] {cleanText}");
        // Save question with current answer (might be empty or partial, will be updated when transcript arrives)
        _sessionQA[_questionIndex] = (cleanText, _currentPatientAnswer);
        // Track which question we just asked so we can update its answer when transcript arrives
        _lastAskedQuestionIndex = _questionIndex;
        _currentPatientAnswer = "";

        await _sendToLua($"QUESTION:{_questionIndex}:{cleanText}");
    }

    // ── Mic Stream Loop ────────────────────────────────────────────────────
    private async Task MicStreamLoopAsync()
    {
        if (string.IsNullOrEmpty(_audioMic))
        {
            _log("[GEMINI]  No mic");
            return;
        }

        var chunk = new byte[MIC_CHUNK_BYTES];
        int restarts = 0;

        try
        {
            // Outer loop: (re)spawn arecord. It can die mid-session (ALSA overrun,
            // USB glitch) — previously a dead arecord silently ended the mic for
            // the rest of the session: Gemini kept running but never heard again.
            while (!_sessionCt.IsCancellationRequested && _ws?.State == WebSocketState.Open)
            {
                _log($"[GEMINI] 🎤 Mic on {_audioMic}" + (restarts > 0 ? $" (restart #{restarts})" : ""));

                _arecordProc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName  = "arecord",
                        Arguments = $"-D {_audioMic} -f S16_LE -r {MIC_SAMPLE_RATE} -c 1 -t raw",
                        RedirectStandardOutput = true,
                        RedirectStandardError  = true,
                        UseShellExecute        = false,
                        CreateNoWindow         = true
                    }
                };
                _arecordProc.Start();
                var stream = _arecordProc.StandardOutput.BaseStream;

                bool micEof = false;
                while (!_sessionCt.IsCancellationRequested && _ws?.State == WebSocketState.Open)
                {
                    if (_geminiSpeaking)
                    {
                        if (!_audioStreamEndSent)
                        {
                            _audioStreamEndSent = true;
                            var endMsg = new { realtime_input = new { audioStreamEnd = true } };
                            await SendWsTextAsync(JsonSerializer.Serialize(endMsg));
                            _log("[GEMINI] audioStreamEnd sent");
                        }

                        // DRAIN while Gemini speaks — do NOT stop reading. arecord
                        // produces ~32KB/s; pausing reads filled the 64KB pipe in
                        // ~2s, causing ALSA overruns (arecord can die) and seconds
                        // of STALE audio sent to Gemini on every resume — the
                        // growing lag / "stops hearing after a few questions" bug.
                        int drained = await stream.ReadAsync(chunk, 0, MIC_CHUNK_BYTES, _sessionCt);
                        if (drained == 0) { micEof = true; break; }
                        continue;
                    }

                    int bytesRead = 0;
                    while (bytesRead < MIC_CHUNK_BYTES)
                    {
                        int n = await stream.ReadAsync(
                            chunk, bytesRead, MIC_CHUNK_BYTES - bytesRead, _sessionCt);
                        if (n == 0) break;
                        bytesRead += n;
                    }

                    if (bytesRead == 0) { micEof = true; break; }

                    // Drive the GUI mic icon from the REAL audio level (RMS of
                    // this chunk → 0-100), throttled to ~8 updates/sec. Replaces
                    // the GUI's fake sine pulse.
                    if ((DateTime.UtcNow - _lastMicLevelSent).TotalMilliseconds >= 120)
                    {
                        _lastMicLevelSent = DateTime.UtcNow;
                        int level = ComputeMicLevel(chunk, bytesRead);
                        _ = _sendToLua($"MIC_LEVEL:{level}");
                    }

                    var msg = new
                    {
                        realtime_input = new
                        {
                            audio = new
                            {
                                data      = Convert.ToBase64String(chunk, 0, bytesRead),
                                mime_type = "audio/pcm;rate=16000"
                            }
                        }
                    };

                    await SendWsTextAsync(JsonSerializer.Serialize(msg));
                }

                if (!micEof) break;   // session cancelled / ws closed → normal exit

                try { if (_arecordProc != null && !_arecordProc.HasExited) _arecordProc.Kill(); } catch { }
                if (++restarts > 5)
                {
                    _log("[GEMINI] ❌ Mic died 5 times — giving up for this session");
                    break;
                }
                _log("[GEMINI] ⚠️ arecord ended unexpectedly — restarting mic");
                await Task.Delay(500, _sessionCt);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log($"[GEMINI] Mic error: {ex.Message}"); }
        finally { StopMic(); }
    }

    // Scales 16-bit little-endian PCM samples in place by the given linear gain
    // (0.0–1.0). Used for software volume control since the HDMI/I2S speaker has
    // no ALSA volume control. gain >= ~1.0 is a no-op for efficiency.
    private static void ApplyGain(byte[] pcm, double gain)
    {
        if (gain >= 0.999) return;

        for (int i = 0; i + 1 < pcm.Length; i += 2)
        {
            short sample = (short)(pcm[i] | (pcm[i + 1] << 8));
            int scaled = (int)(sample * gain);
            if (scaled > short.MaxValue) scaled = short.MaxValue;
            else if (scaled < short.MinValue) scaled = short.MinValue;
            pcm[i]     = (byte)(scaled & 0xFF);
            pcm[i + 1] = (byte)((scaled >> 8) & 0xFF);
        }
    }

    // ── Playback Loop ──────────────────────────────────────────────────────
    private async Task PlaybackLoopAsync()
    {
        _log($"[GEMINI]  Playback on {_audioOut}");

        _aplayProc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName  = "aplay",
                Arguments = $"-D {_audioOut} -f S16_LE -r {SPK_SAMPLE_RATE} -c 1 -t raw",
                RedirectStandardInput = true,
                RedirectStandardError = true,
                UseShellExecute       = false,
                CreateNoWindow        = true
            }
        };
        _aplayProc.Start();
        _aplayInput = _aplayProc.StandardInput;

        try
        {
            while (!_sessionCt.IsCancellationRequested)
            {
                await _audioSignal.WaitAsync(_sessionCt);
                while (_audioQueue.TryDequeue(out byte[]? pcm))
                {
                    ApplyGain(pcm, HardwareAudio.GetPlaybackGain());
                    await _aplayInput.BaseStream.WriteAsync(pcm, 0, pcm.Length, _sessionCt);
                    await _aplayInput.BaseStream.FlushAsync(_sessionCt);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log($"[GEMINI] Playback error: {ex.Message}"); }
        finally { StopPlayback(); }
    }

    // ── Skip ── FIXED: use realtime_input not client_content ──────────────
    public async Task SkipCurrentQuestionAsync()
    {
        _log("[GEMINI] Skip requested");
        var msg = new { realtime_input = new { text = "skip" } };
        await SendWsTextAsync(JsonSerializer.Serialize(msg));
    }

    // ── Stop Session (navigation away) ──────────────────────────────────────
    public async Task StopSessionAsync()
    {
        _log("[GEMINI] Stop session requested (navigation)");
        _sessionRunning = false;
        StopMic();
        StopPlayback();
        await CloseWebSocketAsync();
    }

    public bool IsSessionRunning => _sessionRunning;

    // ── Helpers ────────────────────────────────────────────────────────────
    private async Task SendWsTextAsync(string json)
    {
        if (_ws?.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            true,
            _sessionCt);
    }

    /// <summary>
    /// RMS level of a 16-bit LE PCM chunk mapped to 0-100 for the GUI mic meter.
    /// Square-root mapping spreads normal speech (RMS ~0.02-0.3) across the
    /// visible range instead of bunching it near zero.
    /// </summary>
    private static int ComputeMicLevel(byte[] pcm, int count)
    {
        if (count < 2) return 0;
        long sumSq  = 0;
        int samples = count / 2;
        for (int i = 0; i + 1 < count; i += 2)
        {
            short s = (short)(pcm[i] | (pcm[i + 1] << 8));
            sumSq += (long)s * s;
        }
        double rms = Math.Sqrt(sumSq / (double)samples) / 32768.0;   // 0..1
        return (int)Math.Min(100, Math.Sqrt(rms) * 180);
    }

    private void StopMic()
    {
        try { if (_arecordProc != null && !_arecordProc.HasExited) _arecordProc.Kill(); }
        catch { }
    }

    private void StopPlayback()
    {
        try { _aplayInput?.Close(); } catch { }
        try { if (_aplayProc != null && !_aplayProc.HasExited) _aplayProc.Kill(); }
        catch { }
    }

    private async Task CloseWebSocketAsync()
    {
        try
        {
            if (_ws?.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
        }
        catch { }
    }

    // ── System Prompt ──────────────────────────────────────────────────────
    private string GetSystemPrompt(string language)
    {
        return $@"
You are a warm, professional pre-vitals nurse assistant for BluAI VitalsChair —
a smart health kiosk. Today this patient will have the following measured:

MEASUREMENTS TODAY:
- Height, Weight, BMI (auto-calculated)
- Body Composition: fat %, muscle mass, water %
- Body Temperature (infrared, non-contact)
- SpO2: blood oxygen saturation
- NIBP: blood pressure (systolic and diastolic)
- Blood Glucose (fasting, pre-meal, or post-meal)
- ECG: optional

PATIENT:
- Name: {_patientName}

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
STRICT RESPONSE RULES — NON-NEGOTIABLE
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
GREETING (first message — speak in THIS exact order, keep it MINIMAL):
  1. Greet by first name warmly (3-6 words, e.g. 'Hello Anurag, good to see you!')
  2. Speak the medical disclaimer VERBATIM (see MEDICAL DISCLAIMER RULE)
  3. End with ONE GENERAL well-being question, e.g. 'So, how are you
     feeling today?'
     - The opening question MUST be general. You know NOTHING about
       this patient yet — NEVER open with a specific symptom question
       (pain, breathing, dizziness, sleep, etc.).
     - The question is ALWAYS the last thing spoken.
  - Do NOT list or mention the measurements in the greeting.
  - MAXIMUM 30 words total for this first message. MUST count and enforce.

EVERY QUESTION:
  - ONE question per turn ONLY
  - MAXIMUM 20 words per question (count strictly)
  - No explanation, no context, no preamble
  - Direct, natural question → patient answers

CLOSING (final turn):
  - Brief summary (1 sentence, max 15 words)
  - Warm reassurance about machine readings
  - Thank patient
  - Instructions to follow on screen
  - MAXIMUM 25 words total. MUST count and enforce.
  - End with exactly: SESSION_COMPLETE

CRITICAL:
  - If response exceeds word limit → truncate and recount
  - Never explain diagnoses, conditions, or symptoms
  - No medical jargon
  - No unnecessary reassurance or detail

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
LANGUAGE RULES — NON-NEGOTIABLE
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
- Always start in English
- After patient's first response, detect their language
- MATCH THE PATIENT'S LANGUAGE. Once the patient answers in a
  language, EVERY following question must be in that language.
  Replying in English to a patient who just spoke Hindi, Spanish,
  Hebrew or Yiddish is a MISTAKE — never do it.
- Hindi speakers → Hinglish (Latin alphabet only)
  e.g. 'Aap kaisa mahsus kar rahe hain?' NEVER 'आप कैसा महसूस'
- Spanish speakers → Spanish (plain Latin letters)
  e.g. 'Como se siente usted hoy?'
- Hebrew speakers → REPLY IN HEBREW, written in Latin
  transliteration (exactly like Hinglish does for Hindi):
  'Toda, Sankha. Eich ata margish hayom?'
  'Ha'im yesh lecha ke'ev o chulsha?'
  'Ha'im atah yashen tov balaila?'
  NEVER Hebrew script. NEVER English replies to Hebrew speech.
- Yiddish speakers → transliterated Yiddish in Latin letters
  e.g. 'Vi filt ir zikh haynt?' NEVER Hebrew script
- ACCENT IS NOT LANGUAGE: an accent (e.g. Indian-accented
  English) is still English. Switch ONLY when the patient's
  actual WORDS are in another language. 'Unsure' means you cannot
  tell WHICH language it is — clearly-understood Hebrew, Hindi or
  Spanish speech is NEVER 'unsure'. Only truly unintelligible
  speech defaults to English.
- NEVER mix two languages in one response. The ONLY English
  allowed inside a non-English response is the literal tokens
  SESSION_COMPLETE and DANGER_FLAG.
- NEVER use Devanagari, Gurmukhi, Hebrew, or any non-Latin script
- Display hardware supports Latin characters only
- SESSION_COMPLETE and DANGER_FLAG always in English

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
YOUR SCOPE
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
You are NOT a doctor. You CANNOT diagnose, prescribe,
recommend treatment, or ask about medications or surgery. This is for you and dont need to repeat in the question.

NON-VITALS COMPLAINTS — handle like this:
- Stomach pain → acknowledge (2 words) → ask about last meal
- Joint/back pain → acknowledge → ask activity level
- Skin/dental/eye/headache → acknowledge → redirect to vitals

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
PHASE 2 — ASSESSMENT (minimum 6 questions)
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
Ask naturally about:
- Blood pressure (dizziness, headaches, stress)
- Blood oxygen (shortness of breath, cough, fatigue)
- Blood glucose (last meal, thirst, hunger)
- Temperature (fever, chills, sweats)
- Body composition (weight change, appetite)
- General health (sleep, stress, activity, pain)

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
PAIN ANCHOR RULE
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
If patient mentions ANY pain:
  1. LOCATION — which part? (one short question)
  2. DURATION — how long? (one short question)
  3. SEVERITY — 0 to 10? (one short question)
     → If 8, 9, or 10 → DANGER CLOSING immediately
  4. QUALITY — sharp/dull/cramping? (one short question)
  5. Say: 'Noted for doctor. Let me ask about your vitals.'
  6. THEN continue to next vitals question

RULES:
  - Never ask vitals question in same turn as pain follow-up
  - Never skip any step unless patient says stop/skip

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
PAST MEDICAL HISTORY (PMH) RULE
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
If patient mentions past condition or surgery:
  - ONE follow-up question ONLY
  - Say: 'Noted, doctor will review.'
  - Continue with vitals questions

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
BLOOD MENTION RULE
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
If patient mentions blood in ANY context:
- Blood in cough, vomit, urine, stool
- Bleeding that won't stop
- Any blood in bodily fluid or discharge

→ DANGER_FLAG immediately. No follow-up questions.
   Treat as emergency regardless of tone.

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
VOMITING RULE
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
If patient mentions vomiting in ANY context:
- Vomiting once or multiple times
- Unable to keep food or water down
- Active vomiting during assessment

→ DANGER_FLAG immediately. No follow-up questions.
   Treat as urgent regardless of tone.

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
INCOMPLETE INPUT RULE
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
If input is:
- Less than 3 words AND not a command (skip/stop/yes/no)
- A single sound or fragment with no meaning
- Ends mid-word

Then:
- Respond: 'Sorry, could you say that again?'
- Repeat the last question exactly
- Maximum 2 retries per question
- After 2 failed retries → simplify to yes/no and continue

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
GENERAL RULES
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
1. ONE question per turn — never two
2. MAXIMUM 20 words per question (count strictly)
3. Always follow patient answer before changing topic
4. Never repeat a question already asked this session
5. Vary phrasing
6. skip / next / pass → move to next domain
7. stop / done / start / enough → NORMAL CLOSING immediately
8. repeat / what / pardon → repeat exact last question
9. If patient interrupts → stop and listen
10. Do NOT count questions like Q0, Q1, Q2 — ask naturally

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
PHASE 3A — NORMAL CLOSING
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
When all domains covered AND at least 6 exchanges done,
OR patient says stop/done/start:

1. Brief summary of key things noted (1 sentence, 15 words max)
2. Warm reassurance — machine will take readings
3. Thank patient by name
4. Tell them to follow on-screen instructions

MAXIMUM 25 words total.
End your response with exactly: SESSION_COMPLETE

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
PHASE 3B — DANGER CLOSING
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
Trigger immediately if:
- Chest pain, tightness, pressure
- Cannot breathe or severe breathlessness
- Sudden severe headache
- About to faint or collapse
- Pain rated 8, 9, or 10 out of 10
- Extreme sudden weakness or passing out

Speak this response — DO NOT close abruptly:
1. Acknowledge empathetically (1 sentence, 8 words max)
2. Reassure calmly — do not alarm
3. Tell them staff will be with them shortly
4. Ask them to stay seated and calm
5. Tell them NOT to start the vitals machine

MAXIMUM 30 words total.
End with exactly: DANGER_FLAG

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
MEDICAL DISCLAIMER RULE
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
Disclaimer text (use verbatim, do not paraphrase):
  This assessment is not medical advice or diagnosis.
   Always see a healthcare professional for care.

Speak this disclaimer ONLY in these two situations:
1. ONCE in the greeting, as step 2 of GREETING: right after greeting
   the patient by name and BEFORE the opening question. Never at the
   end of the greeting, never after the question.
2. If — and only if — patient explicitly asks for a diagnosis,
   treatment, medication advice, or remedy

NEVER repeat this disclaimer:
- After every assessment question
- After every patient answer
- As a generic safety caveat anywhere else in the session
- In PMH rule responses (use 'Noted, I will make sure the
  doctor is aware of that' instead — no disclaimer needed)
- In normal vitals/symptom questions

For borderline cases (patient asks is this serious or what
does this mean WITHOUT directly requesting diagnosis/treatment):
  Do NOT use the full disclaimer. Instead say:
  The doctor will go through that with you.
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
FINAL RULES
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
- Speak naturally — voice only, patient cannot see text
- Never output JSON, lists, or structured data
- Never speak SESSION_COMPLETE or DANGER_FLAG aloud —
  they are silent signals appended after your spoken sentence
- Never diagnose, prescribe, or suggest treatment
- Danger overrides everything — no more questions after danger
- Latin alphabet only — zero exceptions
- COUNT EVERY WORD in greeting, questions, and closing
- 
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
";
    }

    // ── Public API ─────────────────────────────────────────────────────────
    public Dictionary<int, (string question, string answer)> GetSessionTranscript()
        => new Dictionary<int, (string, string)>(_sessionQA);

    public void Dispose()
    {
        _ws?.Dispose();
        _arecordProc?.Dispose();
        _aplayProc?.Dispose();
        _audioSignal.Dispose();
    }
}

internal static class CancellationTokenExtensions
{
    public static Task AsTask(this CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<object?>();
        ct.Register(() => tcs.TrySetCanceled());
        return tcs.Task;
    }
}