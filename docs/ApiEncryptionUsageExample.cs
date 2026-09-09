using System;
using System.Threading.Tasks;

namespace vitalschair_prod_v1
{
    /// <summary>
    /// Example usage of the API encryption/decryption system.
    /// 
    /// Key derivation approach:
    /// - Encryption key is derived from JWT + Device ID (no separate key storage needed)
    /// - Registration data loaded from /data/registration.json
    /// - Config data loaded from /data/config_cache.json
    /// 
    /// This demonstrates how to:
    /// 1. Load registration and config data
    /// 2. Initialize encryption manager from registration data
    /// 3. Encrypt outgoing API payloads
    /// 4. Decrypt incoming API responses
    /// 5. Use SecureApiClient for automatic encryption/decryption
    /// </summary>
    public class ApiEncryptionUsageExample
    {
        // ============= EXAMPLE 1: Initialize from Registration Data =============
        
        public static async Task InitializeFromRegistrationDataExample()
        {
            var registrationManager = new RegistrationDataManager();

            try
            {
                // Load registration and config data from /data
                bool loaded = await registrationManager.LoadAllAsync();
                if (!loaded)
                {
                    Console.WriteLine("Failed to load registration data");
                    return;
                }

                // Create encryption manager (key derived from JWT + Device ID)
                var encryptionManager = registrationManager.CreateEncryptionManager();

                Console.WriteLine($"✓ Encryption manager initialized");
                Console.WriteLine($"  Device ID: {registrationManager.GetDeviceId()}");
                
                // Get API endpoints from config
                var bluHealthConfig = registrationManager.GetBluHealthApiConfig();
                Console.WriteLine($"  API Base: {bluHealthConfig.VitalsUrl}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error: {ex.Message}");
            }
        }

        // ============= EXAMPLE 2: Encrypt/Decrypt with Registration Data =============
        
        public static async Task EncryptDecryptWithRegistrationExample()
        {
            var registrationManager = new RegistrationDataManager();

            try
            {
                if (!await registrationManager.LoadAllAsync())
                    return;

                var encryptionManager = registrationManager.CreateEncryptionManager();

                // Example payload
                string plainTextPayload = "{\"userId\": 123, \"heartRate\": 72, \"timestamp\": \"2026-06-12T10:30:00Z\"}";

                // Encrypt the payload before sending to API
                string encryptedPayload = await encryptionManager.EncryptAsync(plainTextPayload);
                Console.WriteLine($"✓ Encrypted: {encryptedPayload.Substring(0, 50)}...");

                // When receiving encrypted response from server
                string decryptedResponse = await encryptionManager.DecryptAsync(encryptedPayload);
                Console.WriteLine($"✓ Decrypted: {decryptedResponse}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error: {ex.Message}");
            }
        }

        // ============= EXAMPLE 3: SecureApiClient with Registration Data =============
        
        public static async Task SecureApiClientWithRegistrationExample()
        {
            var registrationManager = new RegistrationDataManager();

            try
            {
                if (!await registrationManager.LoadAllAsync())
                    return;

                var encryptionManager = registrationManager.CreateEncryptionManager();
                var bluHealthConfig = registrationManager.GetBluHealthApiConfig();
                
                // Extract base URL from full API URL (e.g., https://bluhealthapi.bluai.ai)
                var baseUrl = new Uri(bluHealthConfig.VitalsUrl).GetLeftPart(System.UriPartial.Authority);
                string derivedKey = ApiEncryptionManager.DeriveKeyFromJWT(
                    registrationManager.GetJWT(),
                    registrationManager.GetDeviceId()
                );

                using (var secureClient = new SecureApiClient(baseUrl, derivedKey))
                {
                    // POST encrypted vitals
                    var vitalsPayload = new
                    {
                        device_id = registrationManager.GetDeviceId(),
                        heart_rate = 72,
                        temperature = 37.2,
                        oxygen_saturation = 98,
                        timestamp = DateTime.UtcNow
                    };

                    string response = await secureClient.PostAsync("/api/bluhealth/addVitals", vitalsPayload);
                    Console.WriteLine($"✓ Vitals submitted: {response}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ API Error: {ex.Message}");
            }
        }

        // ============= EXAMPLE 4: Integration with Program.cs =============
        
        /// <summary>
        /// How to integrate into Program.cs for dependency injection
        /// </summary>
        public static string GetProgramCsIntegrationExample()
        {
            return @"
// In Program.cs, add this during service configuration:

var registrationManager = new RegistrationDataManager();
await registrationManager.LoadAllAsync();

// Register as singleton
services.AddSingleton(registrationManager);
services.AddSingleton(sp => registrationManager.CreateEncryptionManager());

// For dependency injection:
services.AddScoped(sp => 
{
    var regMgr = sp.GetRequiredService<RegistrationDataManager>();
    var baseUrl = regMgr.GetBluHealthApiConfig().DeviceUrl;
    var key = ApiEncryptionManager.DeriveKeyFromJWT(
        regMgr.GetJWT(), 
        regMgr.GetDeviceId()
    );
    return new SecureApiClient(baseUrl, key);
});
";
        }

        // ============= EXAMPLE 5: Usage in VitalsQueue =============
        
        public static async Task IntegrationWithVitalsQueueExample()
        {
            var registrationManager = new RegistrationDataManager();

            try
            {
                if (!await registrationManager.LoadAllAsync())
                    return;

                var encryptionManager = registrationManager.CreateEncryptionManager();

                // When pushing vitals to queue
                var vitalsData = new
                {
                    device_id = registrationManager.GetDeviceId(),
                    timestamp = DateTime.UtcNow,
                    heart_rate = 72,
                    temperature = 37.2,
                    oxygen_saturation = 98
                };

                string vitalsJson = System.Text.Json.JsonSerializer.Serialize(vitalsData);
                string encryptedVitals = await encryptionManager.EncryptAsync(vitalsJson);
                
                // Store encrypted vitals in queue
                Console.WriteLine($"✓ Encrypted vitals queued: {encryptedVitals.Substring(0, 50)}...");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error: {ex.Message}");
            }
        }

        // ============= EXAMPLE 6: Usage in DeviceRegistration =============
        
        public static async Task IntegrationWithDeviceRegistrationExample()
        {
            var registrationManager = new RegistrationDataManager();

            try
            {
                if (!await registrationManager.LoadAllAsync())
                    return;

                var encryptionManager = registrationManager.CreateEncryptionManager();
                var bluHealthConfig = registrationManager.GetBluHealthApiConfig();

                // Send encrypted device registration
                var deviceData = new
                {
                    device_id = registrationManager.GetDeviceId(),
                    jwt = registrationManager.GetJWT(),
                    model = "VitalsChair-V1"
                };

                string deviceJson = System.Text.Json.JsonSerializer.Serialize(deviceData);
                string encryptedDeviceData = await encryptionManager.EncryptAsync(deviceJson);
                
                Console.WriteLine($"✓ Encrypted device data: {encryptedDeviceData.Substring(0, 50)}...");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error: {ex.Message}");
            }
        }

        // ============= SETUP GUIDE =============
        
        public static void PrintSetupGuide()
        {
            Console.WriteLine(@"
╔════════════════════════════════════════════════════════════════╗
║     API ENCRYPTION/DECRYPTION SETUP GUIDE (JWT-Based)         ║
╚════════════════════════════════════════════════════════════════╝

FILES CREATED:
  ✓ ApiEncryptionManager.cs        - Core encryption/decryption logic
  ✓ RegistrationDataManager.cs     - Loads /data/registration.json & config
  ✓ SecureApiClient.cs             - HTTP wrapper with auto encryption
  ✓ This file                       - Usage examples & integration guide

KEY DERIVATION APPROACH:
  • Encryption key is derived from: JWT + Device ID
  • No separate key storage needed
  • Deterministic & device-specific
  • Automatic on re-registration

REQUIRED DATA FILES:
  /data/registration.json          - Contains JWT & Device ID
  /data/config_cache.json          - Contains API endpoints & configs

QUICK START:
  1. Load registration data:
     var regMgr = new RegistrationDataManager();
     await regMgr.LoadAllAsync();
  
  2. Create encryption manager:
     var encMgr = regMgr.CreateEncryptionManager();
  
  3. Encrypt outgoing data:
     string encrypted = await encMgr.EncryptAsync(plainText);
  
  4. Decrypt incoming data:
     string decrypted = await encMgr.DecryptAsync(encryptedText);

INTEGRATION POINTS:
  ✓ VitalsQueue.cs         → Load data, encrypt before queuing
  ✓ DeviceRegistration.cs  → Use JWT for registration
  ✓ Program.cs             → Initialize on startup
  ✓ API Services           → Use SecureApiClient for requests
  ✓ GeminiLiveSession.cs   → Encrypt audio/responses

EXAMPLE: Program.cs Integration
  var regMgr = new RegistrationDataManager();
  await regMgr.LoadAllAsync();
  services.AddSingleton(regMgr);
  services.AddSingleton(sp => regMgr.CreateEncryptionManager());

NO NEED FOR:
  ✗ appsettings.json secret storage
  ✗ Environment variables for encryption key
  ✗ Key management/rotation logic
  ✗ Separate key provisioning
");
        }
    }
}
