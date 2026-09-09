/*
════════════════════════════════════════════════════════════════
  MIGRATION GUIDE: Integrating Resilient Encryption
════════════════════════════════════════════════════════════════

NEW IMPLEMENTATION STATUS:
  ✓ ResilientSecureApiClient.cs     - Ready to use
  ✓ DeviceStartupInitializer.cs     - Ready to use
  ✓ ExampleVitalsService.cs         - Template for integration
  ✓ ResilientApiEncryptionManager   - Already created
  ✓ ResilientSecretKeyManager       - Already created
  ✓ SecretKeyManager                - Already created
  ✓ DeviceDataFileManager           - Already created (updated for atomic writes)
  ✓ DeviceRegistration              - Already created (with retry limits)
  ✓ RegistrationDataManager         - Already created

════════════════════════════════════════════════════════════════
STEP-BY-STEP MIGRATION
════════════════════════════════════════════════════════════════

STEP 1: Update Program.cs
────────────────────────────────────────────────────────────────

From:
  var app = builder.Build();
  // ... existing code ...

To:
  // Initialize device services at startup
  var initializer = new DeviceStartupInitializer();
  await initializer.InitializeAsync();

  // Register with dependency injection
  builder.Services.AddSingleton(initializer.GetRegistrationManager());
  builder.Services.AddSingleton(initializer.GetResilientSecretKeyManager());
  builder.Services.AddSingleton(initializer.GetEncryptionManager());
  builder.Services.AddScoped(_ => initializer.GetSecureApiClient());

  var app = builder.Build();
  // ... rest of code ...

Reference: PROGRAM_CS_TEMPLATE.cs for complete example


STEP 2: Update Existing Services (e.g., VitalsQueue.cs)
────────────────────────────────────────────────────────────────

OLD WAY (don't use):
  public class VitalsQueue
  {
      private readonly HttpClient _httpClient;
      private readonly string _secretKey = "hardcoded-key";
      
      public async Task SendVitals(VitalsData vitals)
      {
          var json = JsonSerializer.Serialize(vitals);
          // Manual encryption
          var encrypted = ApiEncryptionManager.Encrypt(json, _secretKey);
          var request = new StringContent(encrypted, ...);
          var response = await _httpClient.PostAsync(url, request);
          var encryptedResp = await response.Content.ReadAsStringAsync();
          var json = ApiEncryptionManager.Decrypt(encryptedResp, _secretKey);
      }
  }

NEW WAY (use this):
  public class VitalsQueue
  {
      private readonly ResilientSecureApiClient _apiClient;
      private readonly ResilientApiEncryptionManager _encryptionManager;
      
      public VitalsQueue(
          ResilientSecureApiClient apiClient,
          ResilientApiEncryptionManager encryptionManager)
      {
          _apiClient = apiClient;
          _encryptionManager = encryptionManager;
      }
      
      public async Task SendVitals(VitalsData vitals)
      {
          // Encryption/decryption automatic, key rotation handled
          string response = await _apiClient.PostAsync(
              "/api/bluhealth/addVitals",
              vitals
          );
      }
  }

Reference: ExampleVitalsService.cs for complete example


STEP 3: Update GeminiLiveSession or Other Services
────────────────────────────────────────────────────────────────

Same pattern as VitalsQueue:

Constructor injection:
  public GeminiLiveSession(
      ResilientSecureApiClient apiClient,
      ResilientApiEncryptionManager encryptionManager)
  {
      _apiClient = apiClient;
      _encryptionManager = encryptionManager;
  }

Usage:
  // For simple encrypted API calls:
  var response = await _apiClient.PostAsync("/endpoint", payload);
  
  // For manual encryption (e.g., before queuing):
  var encrypted = await _encryptionManager.EncryptAsync(jsonData);
  
  // For manual decryption (e.g., from cache):
  var decrypted = await _encryptionManager.DecryptAsync(encryptedData);


════════════════════════════════════════════════════════════════
FEATURES YOU GET AUTOMATICALLY
════════════════════════════════════════════════════════════════

✓ DYNAMIC KEY ROTATION
  - Server can rotate encryption keys without device updates
  - Keys are fetched from: https://vitalchairapi.bluai.ai/api/vitalchair-admin/settings/getSecriteKey

✓ INTELLIGENT CACHING
  - 60-minute normal cache (no network latency)
  - 24-hour fallback cache (API temporarily unreachable)
  - 7-day persistent disk cache (app restart resilience)

✓ AUTOMATIC FALLBACK
  - If API unreachable: Uses cached key
  - If all caches expired: Retries with fallback strategy
  - If everything fails: Encryption fails with clear error

✓ ASYNC OPERATIONS
  - Non-blocking encryption/decryption
  - Efficient key fetching in background

✓ LOGGING & DIAGNOSTICS
  - Automatic logging of all operations
  - Cache diagnostics: call PrintCacheDiagnostics()
  - Encryption diagnostics: call PrintDiagnostics()


════════════════════════════════════════════════════════════════
REMOVING OLD CODE
════════════════════════════════════════════════════════════════

These can be deprecated/removed after migration:

  ❌ ApiEncryptionManager.cs (static DeriveKeyFromJWT method)
     → Replaced by: ResilientApiEncryptionManager

  ❌ SecureApiClient.cs (old static secretKey version)
     → Replaced by: ResilientSecureApiClient

  ⚠️  Keep but don't use directly:
     - ApiEncryptionManager - Still used internally by RegistrationDataManager
                             if needed for backward compatibility


════════════════════════════════════════════════════════════════
TROUBLESHOOTING
════════════════════════════════════════════════════════════════

Issue: "Secret key unavailable"
  → Check if APIsecretKey is set in config
  → Verify API is reachable: https://vitalchairapi.bluai.ai/api/vitalchair-admin/settings/getSecriteKey
  → Check /data/secret_key_cache.json exists and valid

Issue: "Device initialization failed"
  → Check /data/ directory exists and writable
  → Check /data/registration.json exists with device_id
  → Review console output for specific error

Issue: "Stale key in use"
  → This is normal with API down
  → Monitor logs for how long it persists
  → Set up alert if > 30min

Issue: "Encryption performance degradation"
  → First request in 60min fetches new key (adds ~100-500ms)
  → Subsequent requests use cache (instant)
  → Pre-fetch on startup: await mgr.PreFetchSecretKeyAsync()


════════════════════════════════════════════════════════════════
TESTING THE INTEGRATION
════════════════════════════════════════════════════════════════

1. Verify Startup:
   var init = new DeviceStartupInitializer();
   await init.InitializeAsync();
   // Should output initialization sequence

2. Test Encryption:
   var encMgr = init.GetEncryptionManager();
   var encrypted = await encMgr.EncryptAsync("test");
   var decrypted = await encMgr.DecryptAsync(encrypted);
   Assert.AreEqual("test", decrypted);

3. Test API Calls:
   var client = init.GetSecureApiClient();
   var response = await client.PostAsync("/endpoint", new { test = "data" });
   // Should be automatically encrypted/decrypted

4. Monitor Cache:
   var secretMgr = init.GetResilientSecretKeyManager();
   secretMgr.PrintCacheDiagnostics();
   // Should show cache age, TTL remaining, etc.


════════════════════════════════════════════════════════════════
FILES TO UPDATE
════════════════════════════════════════════════════════════════

Priority 1 (Required for encryption to work):
  □ Program.cs            - Add DeviceStartupInitializer initialization

Priority 2 (Update services to use new system):
  □ VitalsQueue.cs        - Use ResilientSecureApiClient instead of HttpClient
  □ GeminiLiveSession.cs  - Use ResilientSecureApiClient
  □ Any other API clients - Use ResilientSecureApiClient

Priority 3 (Optional cleanup):
  □ Remove old SecureApiClient.cs (if not needed)
  □ Remove old ApiEncryptionManager.cs (if not needed)


════════════════════════════════════════════════════════════════
QUICK REFERENCE
════════════════════════════════════════════════════════════════

Initialize at startup:
  var init = new DeviceStartupInitializer();
  await init.InitializeAsync();

Inject into constructor:
  public MyService(ResilientSecureApiClient apiClient,
                   ResilientApiEncryptionManager encMgr)

Send encrypted API call:
  var response = await apiClient.PostAsync("/endpoint", payload);

Manually encrypt data:
  var encrypted = await encMgr.EncryptAsync(jsonString);

Manually decrypt data:
  var decrypted = await encMgr.DecryptAsync(encryptedString);

Check cache status:
  secretMgr.PrintCacheDiagnostics();

Check encryption status:
  encMgr.PrintDiagnostics();
*/
