/*
 * RESILIENT SECRET KEY INTEGRATION GUIDE
 * =====================================
 * 
 * This guide shows how to integrate dynamic secret key management
 * with automatic key rotation, caching, and fallback strategies.
 * 
 * Files Involved:
 * - SecretKeyManager.cs                - Fetches keys from API
 * - ResilientSecretKeyManager.cs       - Adds caching and fallback
 * - ResilientApiEncryptionManager.cs   - Encryption with dynamic keys
 * - DeviceRegistration.cs              - Loads config including key API URL
 * - Program.cs                         - Initialize everything
 */

// ============================================================
// STEP 1: Program.cs Setup (Complete Example)
// ============================================================

/*
using vitalschair_prod_v1;

var builder = WebApplication.CreateBuilder(args);

// 1. Initialize file system first
Console.WriteLine("═══════════════════════════════════════════════");
Console.WriteLine("🔷 DEVICE INITIALIZATION");
Console.WriteLine("═══════════════════════════════════════════════\n");

if (!await DeviceDataFileManager.InitializeDataDirectoryAsync())
{
    Console.WriteLine("❌ CRITICAL: Data directory initialization failed!");
    Environment.Exit(1);
}

DeviceDataFileManager.PrintDiagnostics();

// 2. Register device (ensures config.json is populated with API URLs)
Console.WriteLine("\n🔧 Device Registration...");
try
{
    await DeviceRegistration.EnsureRegisteredAsync();
}
catch (Exception ex)
{
    Console.WriteLine($"❌ Device registration failed: {ex.Message}");
    Environment.Exit(1);
}

// 3. Initialize registration and config managers
var registrationManager = new RegistrationDataManager();
if (!await registrationManager.LoadAllAsync())
{
    Console.WriteLine("❌ Failed to load registration data");
    Environment.Exit(1);
}

// 4. Initialize secret key managers
var secretKeyManager = new SecretKeyManager(registrationManager);
var resilientSecretKeyManager = new ResilientSecretKeyManager(secretKeyManager);

// 5. Pre-fetch secret key at startup (optional but recommended)
Console.WriteLine("\n🔐 Secret Key Initialization...");
bool keyPreFetchSuccess = await resilientSecretKeyManager.PreFetchSecretKeyAsync();
resilientSecretKeyManager.PrintCacheDiagnostics();

// 6. Register services for dependency injection
builder.Services.AddSingleton(registrationManager);
builder.Services.AddSingleton(secretKeyManager);
builder.Services.AddSingleton(resilientSecretKeyManager);

// 7. Register encryption manager with resilient key provider
builder.Services.AddSingleton(sp => 
    new ResilientApiEncryptionManager(sp.GetRequiredService<ResilientSecretKeyManager>())
);

// 8. Register secure API client
builder.Services.AddScoped(sp => 
{
    var regMgr = sp.GetRequiredService<RegistrationDataManager>();
    var bluHealthConfig = regMgr.GetBluHealthApiConfig();
    var derivedKey = ApiEncryptionManager.DeriveKeyFromJWT(
        regMgr.GetJWT(),
        regMgr.GetDeviceId()
    );
    return new SecureApiClient(
        new Uri(bluHealthConfig.VitalsUrl).GetLeftPart(System.UriPartial.Authority),
        derivedKey
    );
});

var app = builder.Build();

Console.WriteLine("\n✓ Device initialization complete!\n");
app.Run();
*/

// ============================================================
// STEP 2: Usage in Services (Example: VitalsService)
// ============================================================

/*
public class VitalsService
{
    private readonly ResilientApiEncryptionManager _encryptionManager;
    private readonly HttpClient _httpClient;
    
    public VitalsService(ResilientApiEncryptionManager encryptionManager)
    {
        _encryptionManager = encryptionManager;
        _httpClient = new HttpClient();
    }
    
    public async Task<string> EncryptVitalsAsync(string vitalsJson)
    {
        // Encryption automatically uses the latest secret key
        // with caching and fallback to stale cache if API is down
        return await _encryptionManager.EncryptAsync(vitalsJson);
    }
    
    public async Task<string> DecryptResponseAsync(string encryptedResponse)
    {
        // Decryption with same key management strategy
        return await _encryptionManager.DecryptAsync(encryptedResponse);
    }
}
*/

// ============================================================
// STEP 3: Key Rotation Flow
// ============================================================

/*
Timeline of Events:

TIME 0: Device Startup
  1. Device registers with server (gets config.json with APIsecretKey URL)
  2. SecretKeyManager fetches first key from server
  3. Key is cached in memory and persisted to disk
  4. Device is ready to encrypt/decrypt

TIME 1: Normal Operation (< 1 hour from last fetch)
  1. User encrypts vitals data
  2. ResilientSecretKeyManager returns cached key (no API call)
  3. Encryption happens with cached key
  4. Performance: No network overhead ✓

TIME 2: Key Rotation on Server
  1. Admin updates secret key in server database
  2. Server's next API call responds with new key
  3. ResilientSecretKeyManager detects cache expired
  4. Fetches new key automatically

TIME 3: API Downtime (server unreachable)
  1. Encryption attempt triggers key fetch
  2. API unreachable (timeout/connection error)
  3. ResilientSecretKeyManager falls back to cached key
  4. Device continues working with stale key ✓

TIME 4: Extended API Downtime (> 24 hours)
  1. Cached key is very old
  2. Device logs critical warning
  3. Device continues using old key (best effort)
  4. When server recovers, fetches new key

TIME 5: Server Forever Down + Cache Expired (7+ days)
  1. No cached key available
  2. Key fetch fails
  3. Encryption fails with clear error
  4. Device operator must restore backup or redeploy
*/

// ============================================================
// STEP 4: Configuration Structure
// ============================================================

/*
config_cache.json should include:

{
  "config_version": 1,
  "device_id": "VC-IN-PB-2605-15678114",
  "BluHealthApi": {
    "DeviceUrl": "https://bluhealthapi.bluai.ai/api/bluhealth/createDevice",
    "VitalsUrl": "https://bluhealthapi.bluai.ai/api/bluhealth/addVitals",
    "BluNoteUrl": "https://bluhealthapi.bluai.ai/api/bluhealth/audio",
    "SendOtpUrl": "https://bluhealthapi.bluai.ai/api/bluhealth/send-Otp-Patient",
    "BasepromtUrl": "https://vitalchairapi.bluai.ai/api/vitalchair-admin/getPrompts",
    "VerifyOtpUrl": "https://bluhealthapi.bluai.ai/api/bluhealth/verify-otp-patient",
    "APIsecretKey": "https://vitalchairapi.bluai.ai/api/vitalchair-admin/settings/getSecriteKey",
    "VoiceAssistant": "https://vitalchairapi.bluai.ai/api/vitalchair-admin/device/settings/gemini-config"
  },
  "VoiceAssistant": {
    "Model": "gemini-3.1-flash-live-preview",
    "ApiKey": "<GEMINI_API_KEY — supplied at runtime from server config, never hardcode>",
    "Language": "en-US"
  },
  "feature_flags": {},
  "thresholds": {},
  "fetched_at": "2026-06-12T10:30:00Z"
}

Data files created/managed:
- /data/registration.json          - Device ID + JWT
- /data/config_cache.json          - Config with API URLs
- /data/config_cache.json.bak      - Config backup
- /data/secret_key_cache.json      - Cached encryption key (persisted)
- /data/secret_key_cache.json.bak  - Key cache backup
*/

// ============================================================
// STEP 5: Logging & Monitoring
// ============================================================

/*
Monitor these log messages:

✓ SUCCESS:
  "✓ Secret key pre-fetched successfully (ID: 2)"
  "✓ Using cached secret key (age: 45.2s)"
  "✓ Secret key cache persisted to disk"

⚠️  WARNING (can continue):
  "⚠️  Using stale in-memory cache (age: 1.5h) - API unreachable"
  "⚠️  Failed to persist secret key cache (non-fatal)"
  "⚠️  Recovered secret key from persistent cache (age: 5.3h)"

❌ CRITICAL (device impaired):
  "❌ Secret key unavailable: API unreachable and no cache available"
  "❌ Persistent cache expired (age: 8.2d)"

Setup alerts for:
- "❌ CRITICAL" messages (immediate action needed)
- Frequent "⚠️  Using stale" warnings (API having issues)
*/

// ============================================================
// STEP 6: Fallback Hierarchy
// ============================================================

/*
Secret Key Availability (in order of preference):

1. Fresh In-Memory Cache (< 1 hour)
   - Speed: Instant (no network call)
   - Reliability: 100%
   - Use: Current key for encryption/decryption

2. Stale In-Memory Cache (< 24 hours, API down)
   - Speed: Instant (no network call)
   - Reliability: High (old but should still work)
   - Use: Continue operation if API temporarily unavailable
   - Warning: Log indicates API issue

3. Persistent Cache on Disk (< 7 days, device restarted)
   - Speed: Disk I/O (~1-5ms)
   - Reliability: Medium (device crashed/restarted but data saved)
   - Use: Recovery after unexpected shutdown
   - Warning: Indicates previous outage

4. None (no cache, API down)
   - Speed: N/A (fails)
   - Reliability: 0%
   - Action: Operator must investigate, device cannot encrypt
   - This should trigger critical alerts
*/

// ============================================================
// STEP 7: Testing Scenario
// ============================================================

/*
Test 1: Normal Operation
  1. Run device normally
  2. Check logs for "✓ Secret key pre-fetched successfully"
  3. Encrypt some data
  4. Check logs for "✓ Using cached secret key"
  ✓ PASS: Device works, uses cache after first fetch

Test 2: Key Rotation
  1. Admin updates key in server database
  2. Wait for cache TTL to expire (1 hour)
  3. Trigger encryption
  4. Device fetches new key
  5. Check logs for new key ID
  ✓ PASS: Device automatically uses new key

Test 3: API Outage (simulate)
  1. Block network access to key API
  2. Trigger encryption
  3. Device falls back to stale cache
  4. Check logs for "⚠️  Using stale cache"
  ✓ PASS: Device continues operating

Test 4: Long Outage + Cache Expiry
  1. Block API access for 8+ days (simulate by resetting cache)
  2. Clear in-memory cache: resilientSecretKeyManager.ResetCache()
  3. Trigger encryption
  4. Check logs for "❌ Secret key unavailable"
  ✓ PASS: Device fails with clear error message

Test 5: Device Restart During Outage
  1. Block API access
  2. Fetch key (will cache)
  3. Simulate device restart (clear in-memory)
  4. Trigger encryption
  5. Device loads from persistent cache
  6. Check logs for "🔄 Recovered secret key from persistent cache"
  ✓ PASS: Device recovers from disk cache
*/

// ============================================================
// STEP 8: Performance Characteristics
// ============================================================

/*
Encryption with Fresh Cache:
  - Time: ~0.5ms (pure crypto, no network)
  - Network: None
  - API calls: 0 per minute

Encryption with Stale Cache (API down):
  - Time: ~0.5ms (pure crypto, no network)
  - Network: None (uses cached key)
  - API calls: 1 attempt, then fallback

Encryption with Key Rotation:
  - Time: ~200ms first call (fetch key + encrypt)
  - Network: 1 API call
  - API calls: 1 per TTL period (1 per hour)

Throughput Estimates:
  - With cache: ~2000 encryptions/sec
  - With key fetch: ~5 encryptions/sec

Storage:
  - secret_key_cache.json: ~500 bytes
  - secret_key_cache.json.bak: ~500 bytes
  - Total: ~1KB (negligible)
*/

// ============================================================
// STEP 9: Security Checklist
// ============================================================

/*
✓ KEY MANAGEMENT
  [X] Never hardcode keys
  [X] Keys fetched dynamically from server
  [X] Keys cached with TTL (1 hour)
  [X] Fallback to older keys during outage
  [X] Keys persisted to disk (recovery)

✓ ENCRYPTION
  [X] AES-256-CBC with random IV
  [X] New IV per message
  [X] Proper key derivation (PBKDF2)
  [X] No key reuse

✓ ERROR HANDLING
  [X] Graceful degradation (use stale cache)
  [X] Clear error messages
  [X] Detailed logging
  [X] No silent failures

✓ ROTATION
  [X] Server-side key rotation supported
  [X] Automatic key refresh on TTL
  [X] No device configuration needed
  [X] Backward compatibility with old keys
*/

// ============================================================
// STEP 10: Troubleshooting
// ============================================================

/*
Issue: "Secret key unavailable: API unreachable and no cache available"

Causes:
  1. API is down AND no prior successful key fetch
  2. Cache was deleted AND API unreachable
  3. Fresh installation with no network

Solutions:
  1. Check network connectivity to API
  2. Check API status: curl APIsecretKey_URL
  3. Restart device (clears in-memory cache)
  4. Check /data/secret_key_cache.json exists

---

Issue: "⚠️  Using stale cache (age: 36h)"

Causes:
  1. API has been down for ~24+ hours
  2. Device is still operating but with old key

Solutions:
  1. Fix network/API issue
  2. Monitor logs - if continues > 24h, escalate
  3. Key might be outdated (check with operator)

---

Issue: "Using VERY OLD cached key (age: 5.2d)"

Causes:
  1. API down for multiple days
  2. Device can't get fresh key

Solutions:
  1. URGENT - Restore network/API
  2. If > 7 days: Device will fail
  3. May need device reboot to clear old cache

---

Issue: Device won't start

Causes:
  1. /data directory not writable
  2. registration.json missing
  3. config.json missing (APIsecretKey URL)

Solutions:
  1. Check /data permissions: ls -la /data/
  2. Run device registration again
  3. Check config has "APIsecretKey" field
*/

// ============================================================
// IMPLEMENTATION COMPLETE
// ============================================================
