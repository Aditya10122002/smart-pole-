using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

public class ConfigManager
{
    private const string ConfigCachePath  = "/data/config_cache.json";
    private const string RegistrationPath = "/data/registration.json";

    // ── In-memory config ───────────────────────────────────────
    private static int    _configVersion  = 1;
    private static bool   _configLoaded   = false;
    public static bool IsPendingApproval() => !_configLoaded;
    // HL7 config (populated from cache or server)
    private static bool   _hl7Enabled     = false;
    private static string _hl7Host        = "127.0.0.1";
    private static int    _hl7Port        = 2575;

    private static string _sendOtpUrl         = string.Empty;
    private static string _verifyOtpUrl       = string.Empty;
    private static string _deviceUrl          = string.Empty;
    private static string _vitalsUrl          = string.Empty;
    private static string _voiceQuestionUrl   = string.Empty;
    private static string _bluNoteUrl         = string.Empty;

    // Voice Assistant Model config (from BluHealth device registration response)
    private static string _voiceAssistantApiKey    = string.Empty;
    private static string _voiceAssistantModel      = "gemini-3.1-flash-live-preview"; // fallback
    private static string _voiceAssistantLanguage   = "en-IN"; // fallback: Hindi (India)

    // ── Public: call after registration on every boot ─────────
    public static Task InitializeAsync()
    {
        ProductionLogger.Info(LogCategory.Config, "ConfigManager initializing");

        // Always load cache first — guarantees config available even offline
        LoadFromCache();
        return Task.CompletedTask;
    }

    // ── Load config from local cache ──────────────────────────
    private static void LoadFromCache()
    {
        try
        {
            if (!File.Exists(ConfigCachePath))
            {
                ProductionLogger.Warning(LogCategory.Config,
                    "No config cache found - device pending admin approval or first boot");
                return;
            }

            var doc  = JsonDocument.Parse(File.ReadAllText(ConfigCachePath));
            _configVersion = doc.RootElement
                               .TryGetProperty("config_version", out var v)
                               ? v.GetInt32() : 1;

            // Try to load HL7 settings from cache if present
            if (doc.RootElement.TryGetProperty("HL7", out var hl7El))
            {
                try
                {
                    if (hl7El.TryGetProperty("Enabled", out var e))
                        _hl7Enabled = string.Equals(e.GetString(), "true", StringComparison.OrdinalIgnoreCase);
                    if (hl7El.TryGetProperty("HisHost", out var h))
                        _hl7Host = h.GetString() ?? _hl7Host;
                    if (hl7El.TryGetProperty("HisPort", out var p) && p.ValueKind == JsonValueKind.Number)
                        _hl7Port = p.GetInt32();
                    else if (hl7El.TryGetProperty("HisPort", out var ps))
                        _hl7Port = int.TryParse(ps.GetString(), out var pv) ? pv : _hl7Port;
                }
                catch { /* ignore malformed cache HL7 block */ }
            }

            if (doc.RootElement.TryGetProperty("BluHealthApi", out var bluEl))
            {
                ApplyBluHealthApiConfig(bluEl);
            }

            if (doc.RootElement.TryGetProperty("VoiceAssistant", out var voiceEl))
            {
                ApplyVoiceAssistantConfig(voiceEl);
                ProductionLogger.Info(LogCategory.Config,
                    "VoiceAssistant configuration loaded from cache");
            }

            _configLoaded = true;
            ProductionLogger.Info(LogCategory.Config,
                "Configuration loaded from cache",
                new System.Collections.Generic.Dictionary<string, object>
                {
                    ["config_version"] = _configVersion
                });
        }
        catch (Exception ex)
        {
            ProductionLogger.Error(LogCategory.Config,
                "Failed to load configuration cache", ex,
                new System.Collections.Generic.Dictionary<string, object>
                {
                    ["using_defaults"] = true
                });
        }
    }

    private static void ApplyBluHealthApiConfig(JsonElement section)
    {
        if (section.ValueKind != JsonValueKind.Object) return;

        try
        {
            if (section.TryGetProperty("SendOtpUrl", out var x))
                _sendOtpUrl = x.GetString() ?? _sendOtpUrl;
            if (section.TryGetProperty("VerifyOtpUrl", out var x2))
                _verifyOtpUrl = x2.GetString() ?? _verifyOtpUrl;
            if (section.TryGetProperty("DeviceUrl", out var x3))
                _deviceUrl = x3.GetString() ?? _deviceUrl;
            if (section.TryGetProperty("VitalsUrl", out var x4))
                _vitalsUrl = x4.GetString() ?? _vitalsUrl;
            if (section.TryGetProperty("VoiceQuestionUrl", out var x5))
                _voiceQuestionUrl = x5.GetString() ?? _voiceQuestionUrl;
            if (section.TryGetProperty("BluNoteUrl", out var x6))
                _bluNoteUrl = x6.GetString() ?? _bluNoteUrl;

            ProductionLogger.Info(LogCategory.Config,
                "BluHealthApi configuration applied",
                new System.Collections.Generic.Dictionary<string, object>
                {
                    ["vitals_url"] = _vitalsUrl,
                    ["device_url"] = _deviceUrl
                });
        }
        catch { /* ignore malformed BluHealthApi block */ }
    }

    private static void ApplyVoiceAssistantConfig(JsonElement section)
    {
        if (section.ValueKind != JsonValueKind.Object) return;
        try
        {
            if (section.TryGetProperty("ApiKey", out var k))
                _voiceAssistantApiKey = k.GetString() ?? _voiceAssistantApiKey;
            if (section.TryGetProperty("Model", out var m))
                _voiceAssistantModel = m.GetString() ?? _voiceAssistantModel;
            if (section.TryGetProperty("Language", out var l))
                _voiceAssistantLanguage = l.GetString() ?? _voiceAssistantLanguage;

            ProductionLogger.Info(LogCategory.Config,
                "VoiceAssistant configuration applied",
                new System.Collections.Generic.Dictionary<string, object>
                {
                    ["model"] = _voiceAssistantModel,
                    ["language"] = _voiceAssistantLanguage
                });
        }
        catch { /* ignore malformed VoiceAssistantModel block */ }
    }

    // ── Public getters (used throughout app) ──────────────────
    public static int    GetConfigVersion()              => _configVersion;
    public static bool   IsConfigLoaded()                => _configLoaded;

    // HL7 accessors
    public static bool   GetHl7Enabled()                 => _hl7Enabled;
    public static string GetHl7Host()                    => _hl7Host;
    public static int    GetHl7Port()                    => _hl7Port;

    // BluHealth API accessors
    public static string GetBluHealthApiUrl(string key)
    {
        return key switch
        {
            "SendOtpUrl"       => _sendOtpUrl,
            "VerifyOtpUrl"     => _verifyOtpUrl,
            "DeviceUrl"        => _deviceUrl,
            "VitalsUrl"        => _vitalsUrl,
            "VoiceQuestionUrl" => _voiceQuestionUrl,
            "BluNoteUrl"       => _bluNoteUrl,
            _                   => string.Empty,
        };
    }

    public static string GetBluHealthApiUrl(string key, string fallback)
    {
        var value = GetBluHealthApiUrl(key);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    // Voice Assistant Model accessors
    public static string GetVoiceAssistantApiKey()          => _voiceAssistantApiKey;
    public static string GetVoiceAssistantModel()           => _voiceAssistantModel;
    public static string GetVoiceAssistantLanguage()        => _voiceAssistantLanguage;

    // ← Legacy: These will return real values once /config/fetch is wired
    public static string GetApiKey(string service)      => string.Empty; // Replaced by specific getters above
    public static bool   GetFeatureFlag(string flag)    => true;
    public static int    GetThreshold(string key, int defaultValue) => defaultValue;

    private static void Log(string message)
    {
        try
        {
            ProductionLogger.Info(LogCategory.Config, message);
        }
        catch
        {
            Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}");
        }
    }
}