using System;
using System.Threading.Tasks;

namespace vitalschair_prod_v1
{
    /// <summary>
    /// Device startup initializer that sets up all required services.
    /// Handles registration, encryption key management, and configuration loading.
    /// Call Initialize() at application startup.
    /// </summary>
    public class DeviceStartupInitializer
    {
        private RegistrationDataManager _registrationManager;
        private SecretKeyManager _secretKeyManager;
        private ResilientSecretKeyManager _resilientSecretKeyManager;
        private ResilientApiEncryptionManager _encryptionManager;
        private ResilientSecureApiClient _secureApiClient;

        /// <summary>
        /// Initializes all device services in the correct order.
        /// Must be called once at application startup.
        /// </summary>
        public async Task InitializeAsync()
        {
            try
            {
                Console.WriteLine("\n═══════════════════════════════════════════════════════════════");
                Console.WriteLine("🔷 ENCRYPTION & DEVICE INITIALIZATION SEQUENCE");
                Console.WriteLine("═══════════════════════════════════════════════════════════════\n");

                // Step 1: Initialize file system
                Console.WriteLine("📁 Step 1: Data Directory Initialization...");
                if (!await DeviceDataFileManager.InitializeDataDirectoryAsync())
                {
                    throw new InvalidOperationException("Data directory initialization failed");
                }

                // Step 2: Device registration
                Console.WriteLine("\n🔧 Step 2: Device Registration...");
                await DeviceRegistration.EnsureRegisteredAsync();

                // Step 3: Load registration data
                Console.WriteLine("\n📋 Step 3: Loading Registration Data...");
                _registrationManager = new RegistrationDataManager();
                if (!await _registrationManager.LoadAllAsync())
                {
                    throw new InvalidOperationException("Failed to load registration data");
                }
                Console.WriteLine($"✓ Device ID: {_registrationManager.GetDeviceId()}");

                // Step 3b: Guard — config must exist before secret key fetch
                Console.WriteLine("\n⚙️  Step 3b: Verifying config availability...");
                if (_registrationManager.GetBluHealthApiConfig() == null)
                {
                    Console.WriteLine("  ↳ config_cache.json missing — retrying status check...");
                    await DeviceRegistration.CheckDeviceStatusAsync();

                    // Give file system a moment to write
                    await Task.Delay(TimeSpan.FromSeconds(2));

                    await _registrationManager.LoadConfigCacheAsync();

                    if (_registrationManager.GetBluHealthApiConfig() == null)
                    {
                        Console.WriteLine("  ⏳ Device pending admin approval — waiting for config...");

                        int attempt = 0;
                        while (_registrationManager.GetBluHealthApiConfig() == null)
                        {
                            attempt++;
                            Console.WriteLine($"  ↳ Attempt {attempt}: Rechecking in 30 seconds...");
                            await Task.Delay(TimeSpan.FromSeconds(30));

                            await DeviceRegistration.CheckDeviceStatusAsync();
                            await _registrationManager.LoadConfigCacheAsync();

                            Console.WriteLine($"  ↳ Config: {(_registrationManager.GetBluHealthApiConfig() == null ? "still pending..." : "received ✓")}");
                        }

                        Console.WriteLine("  ✓ Admin approval received — continuing boot");
                    }
                }
                Console.WriteLine("  ✓ Config available");

                // Step 4: Initialize secret key management
                Console.WriteLine("\n🔐 Step 4: Secret Key Management Initialization...");
                _secretKeyManager = new SecretKeyManager(_registrationManager);
                _resilientSecretKeyManager = new ResilientSecretKeyManager(_secretKeyManager);

                // Pre-fetch secret key
                Console.WriteLine("  ↳ Fetching encryption key from server...");
                _resilientSecretKeyManager.PrintCacheDiagnostics();

                // Step 5: Initialize encryption manager
                Console.WriteLine("\n🔐 Step 5: Encryption Manager Initialization...");
                _encryptionManager = new ResilientApiEncryptionManager(_resilientSecretKeyManager);
                Console.WriteLine("✓ AES-256-CBC encryption manager ready");
                Console.WriteLine("  ✓ Dynamic key rotation enabled");
                Console.WriteLine("  ✓ IV randomization enabled\n");

                // Step 6: Initialize secure API client
                Console.WriteLine("📡 Step 6: Secure API Client Initialization...");
                var bluHealthConfig = _registrationManager.GetBluHealthApiConfig();

                if (bluHealthConfig == null || string.IsNullOrWhiteSpace(bluHealthConfig.VitalsUrl))
                {
                    Console.WriteLine("⏳ Config not yet available (device pending approval)");
                    Console.WriteLine("  ℹ️  Secure API client will be initialized after approval\n");
                    _secureApiClient = null; // Will be initialized later once config available
                }
                else
                {
                    var baseUrl = new Uri(bluHealthConfig.VitalsUrl).GetLeftPart(System.UriPartial.Authority);
                    _secureApiClient = new ResilientSecureApiClient(baseUrl, _encryptionManager);
                    Console.WriteLine($"✓ Secure API client configured");
                    Console.WriteLine($"  ✓ Base URL: {baseUrl}");
                    Console.WriteLine($"  ✓ All requests/responses encrypted\n");
                }

                Console.WriteLine("═══════════════════════════════════════════════════════════════");
                Console.WriteLine("✅ INITIALIZATION COMPLETE - ENCRYPTION SYSTEM ACTIVE");
                Console.WriteLine("═══════════════════════════════════════════════════════════════");
                Console.WriteLine("📊 ENCRYPTION STATUS:");
                Console.WriteLine("  ✓ Algorithm: AES-256-CBC");
                Console.WriteLine("  ✓ Key Source: Dynamic server-fetched with TTL caching");
                Console.WriteLine("  ✓ IV: Random per message (128-bit)");
                Console.WriteLine("  ✓ Format: HEX(IV):HEX(EncryptedData)");
                Console.WriteLine("═══════════════════════════════════════════════════════════════\n");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n❌ INITIALIZATION FAILED: {ex.Message}");
                Console.WriteLine($"Stack Trace: {ex.StackTrace}");
                LoggerUtil.LogStructured(
                    LogLevel.Error,
                    LogCategory.Auth,
                    "Encryption initialization failed",
                    new System.Collections.Generic.Dictionary<string, object>
                    {
                        { "Error", ex.Message }
                    }
                );
                throw;
            }
        }

        // Getters for initialized services
        public RegistrationDataManager GetRegistrationManager() => _registrationManager;
        public ResilientApiEncryptionManager GetEncryptionManager() => _encryptionManager;
        public ResilientSecureApiClient GetSecureApiClient() => _secureApiClient;
        public ResilientSecretKeyManager GetResilientSecretKeyManager() => _resilientSecretKeyManager;
    }
}
