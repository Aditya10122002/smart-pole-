using System;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace vitalschair_prod_v1
{
    /// <summary>
    /// Complete integration guide for resilient secret key management with API encryption.
    /// 
    /// Flow:
    /// 1. Device startup: Load registration data
    /// 2. Initialize ResilientSecretKeyManager
    /// 3. Create ApiEncryptionManager with dynamic key provider
    /// 4. On each API call: Get fresh/cached secret key
    /// 5. Encrypt payloads and decrypt responses
    /// 6. If server unreachable: Use cached key automatically
    /// </summary>
    public class SecretKeyIntegrationGuide
    {
        // ============================================================
        // STEP 1: PROGRAM.CS INITIALIZATION
        // ============================================================

        public static string GetProgramCsInitialization()
        {
            return @"
using vitalschair_prod_v1;

// In Program.cs, add this during service setup:

var builder = WebApplication.CreateBuilder(args);

try
{
    // 1. Initialize and validate data directory
    Console.WriteLine(""📁 Initializing data directory..."");
    if (!await DeviceDataFileManager.InitializeDataDirectoryAsync())
    {
        Console.WriteLine(""❌ CRITICAL: Data directory initialization failed!"");
        Environment.Exit(1);
    }

    // 2. Print file diagnostics
    DeviceDataFileManager.PrintDiagnostics();

    // 3. Ensure device is registered
    Console.WriteLine(""🔐 Checking device registration..."");
    await DeviceRegistration.EnsureRegisteredAsync();

    // 4. Load registration data
    var registrationManager = new RegistrationDataManager();
    if (!await registrationManager.LoadAllAsync())
    {
        Console.WriteLine(""❌ Failed to load registration data!"");
        Environment.Exit(1);
    }
    
    Console.WriteLine(""✓ Registration data loaded"");

    // 5. Create secret key managers
    var secretKeyManager = new SecretKeyManager(registrationManager);
    var resilientKeyManager = new ResilientSecretKeyManager(secretKeyManager);
    
    resilientKeyManager.PrintCacheDiagnostics();

    // 6. Register services with dependency injection
    builder.Services.AddSingleton(registrationManager);
    builder.Services.AddSingleton(resilientKeyManager);
    
    // 7. Create encryption manager with dynamic key provider
    builder.Services.AddSingleton(sp => 
    {
        var keyMgr = sp.GetRequiredService<ResilientSecretKeyManager>();
        return new ApiEncryptionManager(
            async () => await keyMgr.GetSecretKeyAsync()
        );
    });

    // 8. Create secure API client
    builder.Services.AddScoped(sp =>
    {
        var regMgr = sp.GetRequiredService<RegistrationDataManager>();
        var bluConfig = regMgr.GetBluHealthApiConfig();
        var baseUrl = new Uri(bluConfig.VitalsUrl).GetLeftPart(System.UriPartial.Authority);
        
        // Get initial key
        string initialKey = bluConfig.APIsecretKey; // Used for initial setup
        return new SecureApiClient(baseUrl, initialKey);
    });

    var app = builder.Build();
    app.Run();
}
catch (Exception ex)
{
    Console.WriteLine($""❌ FATAL: {ex.Message}"");
    Console.WriteLine(ex.StackTrace);
    Environment.Exit(1);
}
";
        }

        // ============================================================
        // STEP 2: VitalsService INTEGRATION
        // ============================================================

        public static string GetVitalsServiceExample()
        {
            return @"
public class VitalsService
{
    private readonly ApiEncryptionManager _encryptionManager;
    private readonly ResilientSecretKeyManager _keyManager;
    private readonly RegistrationDataManager _registrationManager;
    private readonly HttpClient _httpClient;

    public VitalsService(
        ApiEncryptionManager encryptionManager,
        ResilientSecretKeyManager keyManager,
        RegistrationDataManager registrationManager)
    {
        _encryptionManager = encryptionManager;
        _keyManager = keyManager;
        _registrationManager = registrationManager;
        _httpClient = new HttpClient();
    }

    /// <summary>
    /// Submits vitals with automatic encryption using fresh/cached secret key.
    /// </summary>
    public async Task<bool> SubmitVitalsAsync(VitalsData vitals)
    {
        try
        {
            var bluConfig = _registrationManager.GetBluHealthApiConfig();
            
            // Vitals payload
            var payload = new
            {
                device_id = _registrationManager.GetDeviceId(),
                heart_rate = vitals.HeartRate,
                temperature = vitals.Temperature,
                oxygen_saturation = vitals.OxygenSaturation,
                timestamp = DateTime.UtcNow
            };

            // Serialize
            string json = JsonSerializer.Serialize(payload);

            // Encrypt (uses fresh/cached secret key automatically)
            string encrypted = await _encryptionManager.EncryptAsync(json);

            // Send to API
            using var request = new HttpRequestMessage(HttpMethod.Post, bluConfig.VitalsUrl);
            request.Content = new StringContent(encrypted, Encoding.UTF8, ""application/json"");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                ""Bearer"",
                _registrationManager.GetJWT()
            );

            var response = await _httpClient.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                Console.WriteLine(""✓ Vitals submitted successfully"");
                return true;
            }
            else
            {
                Console.WriteLine($""❌ Vitals submission failed: {response.StatusCode}"");
                return false;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($""❌ Vitals submission error: {ex.Message}"");
            return false;
        }
    }
}
";
        }

        // ============================================================
        // STEP 3: DOCKER DEPLOYMENT
        // ============================================================

        public static string GetDockerComposExample()
        {
            return @"
version: '3.8'

services:
  vitalschair:
    image: vitalschair:latest
    container_name: vitalschair_prod
    restart: unless-stopped
    
    # Mount persistent volume for /data
    volumes:
      - device_data:/data
    
    # Environment variables
    environment:
      - LOG_LEVEL=INFO
      - ENABLE_DIAGNOSTICS=true
    
    # Health check
    healthcheck:
      test: [""CMD"", ""curl"", ""-f"", ""http://localhost:5000/health""]
      interval: 30s
      timeout: 10s
      retries: 3
      start_period: 40s
    
    # Security options
    security_opt:
      - no-new-privileges:true
    
    # User (non-root recommended)
    user: ""1000:1000""

volumes:
  device_data:
    driver: local
    driver_opts:
      type: none
      o: bind
      device: /var/lib/vitalschair/data
";
        }

        // ============================================================
        // STEP 4: MONITORING & DIAGNOSTICS
        // ============================================================

        public static void PrintDiagnosticsExample()
        {
            Console.WriteLine(@"
╔════════════════════════════════════════════════════════════════╗
║     RESILIENT SECRET KEY MANAGEMENT - DIAGNOSTICS             ║
╚════════════════════════════════════════════════════════════════╝

CACHE TTL STRATEGY:
  • Normal Cache (Fresh): 60 minutes
    → Use if server is reachable and cache is fresh
    → Most efficient, no extra API calls
  
  • Extended Cache (Stale): 24 hours
    → Use if server API is down temporarily
    → Device continues operating with slightly delayed key rotation
  
  • Persistent Cache (Disk): 7 days
    → Use if device restarts while API is down
    → Recovers from disk, minimizes startup delays
  
  • No Cache: Fail immediately
    → Device cannot proceed without key
    → Requires manual intervention or server recovery

KEY ROTATION FLOW:
  1. Server rotates key and updates database
  2. Device fetches new key within 60 minutes
  3. Old key still works for up to 24 hours (extended cache)
  4. Graceful transition with no client failures

FALLBACK BEHAVIOR:
  Scenario A: Server is reachable
    → Fetch fresh key every 60 minutes
    → Use in-memory cache between fetches
    → Optimal performance
  
  Scenario B: Server temporarily down (< 24 hours)
    → Use extended stale cache automatically
    → Device continues operating
    → Retries server connection every hour
    → When server recovers, fresh key fetched
  
  Scenario C: Server down > 24 hours
    → Try persistent cache from disk
    → Last resort fallback
    → If no disk cache available, device fails
    → Requires manual intervention

MONITORING:
  • Check cache status on startup
  • Log key fetch success/failure
  • Alert if using extended cache for > 2 hours
  • Alert if using persistent cache
  • Track key rotation ages
");
        }

        // ============================================================
        // STEP 5: ERROR HANDLING
        // ============================================================

        public static string GetErrorHandlingExample()
        {
            return @"
public class ErrorHandlingExample
{
    private readonly ResilientSecretKeyManager _keyManager;
    
    public ErrorHandlingExample(ResilientSecretKeyManager keyManager)
    {
        _keyManager = keyManager;
    }

    /// <summary>
    /// Demonstrates error handling for encryption operations.
    /// </summary>
    public async Task HandleEncryptionErrorsAsync()
    {
        try
        {
            // Try to get secret key
            string secretKey = await _keyManager.GetSecretKeyAsync();
            Console.WriteLine(""✓ Secret key obtained successfully"");
        }
        catch (InvalidOperationException ex)
        {
            // No key available at all
            Console.WriteLine($""❌ CRITICAL: {ex.Message}"");
            Console.WriteLine(""Device cannot operate without secret key"");
            // Trigger alert/notification
        }
        catch (HttpRequestException ex)
        {
            // Server error (but might have fallback)
            Console.WriteLine($""⚠️  Server communication error: {ex.Message}"");
            Console.WriteLine(""Attempting to use cached key..."");
        }
        catch (TaskCanceledException)
        {
            // Timeout
            Console.WriteLine(""⚠️  Secret key fetch timeout"");
            Console.WriteLine(""Using cached key if available..."");
        }
        catch (Exception ex)
        {
            // Unknown error
            Console.WriteLine($""❌ Unexpected error: {ex.Message}"");
            throw;
        }
    }

    /// <summary>
    /// Diagnostic method to check system health.
    /// </summary>
    public void PrintHealthStatus()
    {
        _keyManager.PrintCacheDiagnostics();
        
        var status = _keyManager.GetCacheStatus();
        
        if (!status.HasInMemoryCache && !status.HasPersistedCache)
        {
            Console.WriteLine(""❌ CRITICAL: No cache available!"");
            Console.WriteLine(""Device is vulnerable to service interruptions"");
        }
        else if (status.IsWithinNormalTTL)
        {
            Console.WriteLine(""✓ System healthy - fresh cache available"");
        }
        else
        {
            Console.WriteLine(""⚠️  Using stale cache - server may be unreachable"");
        }
    }
}
";
        }

        // ============================================================
        // STEP 6: PRODUCTION CHECKLIST
        // ============================================================

        public static void PrintProductionChecklist()
        {
            Console.WriteLine(@"
╔════════════════════════════════════════════════════════════════╗
║        PRODUCTION DEPLOYMENT CHECKLIST                         ║
╚════════════════════════════════════════════════════════════════╝

PRE-DEPLOYMENT:
  [ ] 1. SecretKeyManager configured with correct API URL
  [ ] 2. ResilientSecretKeyManager initialized in Program.cs
  [ ] 3. /data volume mounted and writable in container
  [ ] 4. Cache TTL values reviewed for your use case
  [ ] 5. Logging configured to capture key fetch events
  [ ] 6. Monitoring/alerting setup for cache issues

DEPLOYMENT:
  [ ] 7. Device registration verified (both files created)
  [ ] 8. Initial secret key fetched successfully
  [ ] 9. Cache status diagnostics show healthy state
  [ ] 10. API calls working with encrypted payloads

MONITORING (First 24 hours):
  [ ] 11. Check logs for ""Secret key fetched successfully""
  [ ] 12. Verify cache persists to /data/secret_key_cache.json
  [ ] 13. Monitor for failed key fetch attempts
  [ ] 14. Confirm device continues if API is briefly unavailable

LONG-TERM MONITORING:
  [ ] 15. Key rotations happening on server
  [ ] 16. Device picking up new keys within 60 minutes
  [ ] 17. No stale cache warnings in logs
  [ ] 18. Cache file updated regularly
  [ ] 19. API calls succeeding with encryption/decryption
  [ ] 20. No secret key exposure in logs

INCIDENT RESPONSE:
  [ ] 21. Secret key API goes down
      → Device continues with cached key for up to 24 hours
      → Monitor logs for ""Using stale cache"" messages
      → No immediate action required

  [ ] 22. Key rotation on server
      → Device fetches new key within 60 minutes
      → Old key still works during transition (backward compatible)
      → No restart or intervention required

  [ ] 23. Device restart during API outage
      → Device loads persisted cache from disk
      → Minimal startup delay
      → Automatic recovery when API returns

  [ ] 24. Persistent cache expired (7+ days no server contact)
      → Device alerts: ""Persistent cache expired""
      → Manual intervention required
      → Restart device when API returns
");
        }

        // ============================================================
        // STEP 7: CONFIGURATION
        // ============================================================

        public static void PrintConfigurationExample()
        {
            Console.WriteLine(@"
Config file now includes:

{
  ""BluHealthApi"": {
    ""APIsecretKey"": ""https://vitalchairapi.bluai.ai/api/vitalchair-admin/settings/getSecriteKey"",
    ""VoiceAssistant"": ""https://vitalchairapi.bluai.ai/api/vitalchair-admin/device/settings/gemini-config""
  }
}

Environment variables (optional):
  - NORMAL_CACHE_TTL_MINUTES=60 (default)
  - EXTENDED_CACHE_TTL_HOURS=24 (default)
  - PERSIST_CACHE_TTL_HOURS=168 (default)
  - SECRET_KEY_FETCH_TIMEOUT_SECONDS=30 (default)

No additional configuration needed for standard deployments.
");
        }
    }
}
