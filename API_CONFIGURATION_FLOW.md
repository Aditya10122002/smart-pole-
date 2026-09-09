# VitalsChair API Configuration Flow

## Overview
All APIs are now managed through a **ConfigManager** that loads from cache first (offline-capable), then checks the server for updates. This ensures the application works offline while still supporting remote configuration updates.

---

## Architecture

### 1. **Fallback Hierarchy** (in order of precedence)
1. **Config API** (server) → `/api/vitalchair-admin/device/config/fetch`
2. **Cache** → `/data/config_cache.json` (saved from last server response)
3. **Defaults** → `appsettings.json` (initial/development values)

---

## Configuration Sections

### ✅ BluHealthAPI URLs (from config API or appsettings)
```json
{
  "BluHealthApi": {
    "SendOtpUrl": "https://bluhealthapi.bluai.ai/api/bluhealth/send-Otp-Patient",
    "VerifyOtpUrl": "https://bluhealthapi.bluai.ai/api/bluhealth/verify-otp-patient",
    "DeviceUrl": "https://bluhealthapi.bluai.ai/api/bluhealth/createDevice",
    "VitalsUrl": "https://bluhealthapi.bluai.ai/api/bluhealth/addVitals",
    "VoiceQuestionUrl": "https://bluhealthapi.bluai.ai/api/bluhealth/medical-chat",
    "BluNoteUrl": "https://bluhealthapi.bluai.ai/api/bluhealth/speech-to-texts"
  }
}
```

**Getter:** `ConfigManager.GetBluHealthApiUrl(string key)`

**Used in:** OTP verification, device registration, vitals upload, voice Q&A, audio transcription

---

### 🎬 GeminiAPI Configuration (AI Voice Assistant)
```json
{
  "GeminiAPI": {
    "ApiKey": "<GEMINI_API_KEY — supplied at runtime from server config, never hardcode>",
    "Model": "gemini-3.1-flash-live-preview"
  }
}
```

**Getters:**
- `ConfigManager.GetGeminiApiKey()` → WebSocket authentication
- `ConfigManager.GetGeminiModel()` → Model selection (e.g., gemini-3.1-flash-live)

**Flow:**
1. `Program.cs` reads config via `ConfigManager.InitializeAsync()`
2. When voice session starts: `new GeminiLiveSession(..., apiKey, model, ...)`
3. Session connects to Gemini Live API with provided credentials

**⚠️ IMPORTANT:** API key should come from config API server in production, NOT hardcoded

---

### 🎵 TTS Configuration (Text-to-Speech)
```json
{
  "Tts": {
    "ServiceUrl": "http://tts-service:5050/tts",
    "OpenRouterApiKey": "sk-or-v1-your-key",
    "OpenRouterTtsUrl": "https://openrouter.ai/api/v1/audio/speech",
    "OpenRouterTtsModel": "openai/gpt-4o-mini-tts-2025-12-15",
    "OpenRouterTtsVoice": "nova"
  }
}
```

**Getters:**
- `ConfigManager.GetTtsServiceUrl()`
- `ConfigManager.GetOpenRouterApiKey()`
- `ConfigManager.GetOpenRouterTtsUrl()`
- `ConfigManager.GetOpenRouterTtsModel()`
- `ConfigManager.GetOpenRouterTtsVoice()`

**Used in:** `FetchTtsWavAsync()` for pre-generating question audio

---

### 🗣️ Azure Speech Configuration (Transcription)
```json
{
  "AzureSpeech": {
    "Key": "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx",
    "Region": "centralindia"
  }
}
```

**Getters:**
- `ConfigManager.GetAzureSpeechKey()`
- `ConfigManager.GetAzureSpeechRegion()`

**Used in:** Live speech transcription (if enabled as fallback)

---

### 🔌 HL7 Configuration (Hospital Integration)
```json
{
  "HL7": {
    "Enabled": "true",
    "HisHost": "192.168.8.155",
    "HisPort": "2575"
  }
}
```

**Getters:**
- `ConfigManager.GetHl7Enabled()`
- `ConfigManager.GetHl7Host()`
- `ConfigManager.GetHl7Port()`

**Used in:** `hl7.cs` for sending vitals to Hospital Information System

---

## Startup Flow

### 1. **Program.Main()**
```csharp
await ConfigManager.InitializeAsync();  // Load cache + check server
RefreshBluHealthApiUrlsFromConfig();    // Apply config to static fields
```

### 2. **ConfigManager.InitializeAsync()**
```
├─ LoadFromCache()
│  ├─ LoadAppSettingsDefaults()     // appsettings.json
│  └─ Parse /data/config_cache.json // Last known good config
│
└─ CheckForUpdateAsync()
   ├─ GET /api/vitalchair-admin/device/config/version
   └─ If update available:
      └─ FetchFullConfigAsync()      // GET /config/fetch
         └─ Save to /data/config_cache.json
```

### 3. **RefreshBluHealthApiUrlsFromConfig()**
Copies config from `ConfigManager` static fields to `Program.cs` static fields:
- SEND_OTP_URL
- VERIFY_OTP_URL
- BLUHEALTH_DEVICE_URL
- BLUHEALTH_VITALS_URL
- VOICE_QUESTION_API_URL
- BLUNOTE_API_URL
- TTS_URL, OPENROUTER_API_KEY, etc.
- AZURE_SPEECH_KEY, AZURE_SPEECH_REGION

---

## Offline Behavior

✅ **Application works completely offline:**
- If config cache exists (`/data/config_cache.json`), app uses it
- If server is unreachable, app continues with cached or appsettings defaults
- All API calls fail gracefully with queuing for later retry

---

## Adding New API Endpoints

### Step 1: Add to `appsettings.json`
```json
{
  "YourSection": {
    "ApiKey": "...",
    "Url": "..."
  }
}
```

### Step 2: Add static fields in `ConfigManager.cs`
```csharp
private static string _yourApiKey = string.Empty;
private static string _yourUrl = string.Empty;
```

### Step 3: Add loading in `LoadAppSettingsDefaults()`
```csharp
var your = cfg.GetSection("YourSection");
if (your.Exists())
{
    _yourApiKey = your["ApiKey"] ?? _yourApiKey;
    _yourUrl = your["Url"] ?? _yourUrl;
}
```

### Step 4: Add server parsing in `ApplyYourConfig()`
```csharp
private static void ApplyYourConfig(JsonElement section)
{
    if (section.TryGetProperty("ApiKey", out var k))
        _yourApiKey = k.GetString() ?? _yourApiKey;
    if (section.TryGetProperty("Url", out var u))
        _yourUrl = u.GetString() ?? _yourUrl;
}
```

### Step 5: Call in `FetchFullConfigAsync()`
```csharp
if (root.TryGetProperty("YourSection", out var yourEl))
    ApplyYourConfig(yourEl);
```

### Step 6: Add public getters in `ConfigManager.cs`
```csharp
public static string GetYourApiKey() => _yourApiKey;
public static string GetYourUrl() => _yourUrl;
```

---

## Security Notes

⚠️ **DO NOT** store sensitive keys in appsettings.json for production
- Use environment variables or secure config servers
- API keys should only come from config API endpoint
- Ensure config API is properly authenticated with JWT token

---

## Testing Configuration

### Verify cache loading:
```bash
cat /data/config_cache.json
```

### Verify config version check (with device JWT):
```bash
curl -H "Authorization: Bearer $JWT" \
  http://192.168.1.21:5000/api/vitalchair-admin/device/config/version
```

### Monitor config loading in logs:
```
[startup] 🔧 ConfigManager initializing...
[startup] 📂 Config loaded from cache — version 2
[startup] 📡 Checking config version (device has v2)...
[startup] ✅ Config is up to date (v2) — no action needed
[startup] ✅ All API endpoints refreshed from runtime config
```

---

## Summary Table

| Component | Type | Source | Getter | Fallback |
|-----------|------|--------|--------|----------|
| **BluHealth APIs** | URLs | Config API / Cache | `GetBluHealthApiUrl()` | appsettings |
| **Gemini API** | Key + Model | Config API / Cache | `GetGeminiApiKey()`, `GetGeminiModel()` | appsettings |
| **TTS APIs** | URLs + Keys | Config API / Cache | `GetTtsServiceUrl()`, `GetOpenRouterApiKey()` | appsettings |
| **Azure Speech** | Key + Region | Config API / Cache | `GetAzureSpeechKey()`, `GetAzureSpeechRegion()` | appsettings |
| **HL7** | Host + Port | Config API / Cache | `GetHl7Host()`, `GetHl7Port()` | appsettings |

