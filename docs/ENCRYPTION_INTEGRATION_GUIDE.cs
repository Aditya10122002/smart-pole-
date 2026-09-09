/*
 * API ENCRYPTION INTEGRATION GUIDE
 * ================================
 * 
 * This document explains how to integrate the encryption system into your existing code.
 * 
 * Files Involved:
 * - ApiEncryptionManager.cs        (Core encryption/decryption)
 * - RegistrationDataManager.cs     (Loads registration & config data)
 * - SecureApiClient.cs             (HTTP wrapper with auto encryption)
 * - ApiEncryptionUsageExample.cs   (Usage examples)
 */

// ============================================
// STEP 1: Initialize in Program.cs
// ============================================

/*
var builder = WebApplication.CreateBuilder(args);

// Load registration and config data
var registrationManager = new RegistrationDataManager();
await registrationManager.LoadAllAsync();

// Register as singleton services
builder.Services.AddSingleton(registrationManager);
builder.Services.AddSingleton(sp => registrationManager.CreateEncryptionManager());

// For SecureApiClient, create a factory
builder.Services.AddScoped(sp => 
{
    var regMgr = sp.GetRequiredService<RegistrationDataManager>();
    var baseUrl = new Uri(regMgr.GetBluHealthApiConfig().VitalsUrl)
        .GetLeftPart(System.UriPartial.Authority);
    var key = ApiEncryptionManager.DeriveKeyFromJWT(
        regMgr.GetJWT(), 
        regMgr.GetDeviceId()
    );
    return new SecureApiClient(baseUrl, key);
});

var app = builder.Build();
app.Run();
*/

// ============================================
// STEP 2: Update VitalsQueue.cs
// ============================================

/*
public class VitalsQueue
{
    private readonly ApiEncryptionManager _encryptionManager;
    
    public VitalsQueue(ApiEncryptionManager encryptionManager)
    {
        _encryptionManager = encryptionManager;
    }
    
    public async Task EnqueueVitalsAsync(VitalsData vitals)
    {
        // Serialize vitals to JSON
        string json = JsonSerializer.Serialize(vitals);
        
        // ENCRYPT before storing in queue
        string encrypted = await _encryptionManager.EncryptAsync(json);
        
        // Store encrypted vitals in queue
        await _queue.EnqueueAsync(encrypted);
    }
    
    public async Task<VitalsData> DequeueVitalsAsync()
    {
        // Get encrypted vitals from queue
        string encrypted = await _queue.DequeueAsync();
        
        // DECRYPT the vitals
        string json = await _encryptionManager.DecryptAsync(encrypted);
        
        // Deserialize back to object
        return JsonSerializer.Deserialize<VitalsData>(json);
    }
}
*/

// ============================================
// STEP 3: Update DeviceRegistration.cs
// ============================================

/*
public class DeviceRegistration
{
    private readonly HttpClient _httpClient;
    private readonly SecureApiClient _secureApiClient;
    private readonly RegistrationDataManager _registrationManager;
    
    public DeviceRegistration(
        HttpClient httpClient, 
        SecureApiClient secureApiClient,
        RegistrationDataManager registrationManager)
    {
        _httpClient = httpClient;
        _secureApiClient = secureApiClient;
        _registrationManager = registrationManager;
    }
    
    public async Task<bool> RegisterDeviceAsync()
    {
        var deviceData = new
        {
            device_id = _registrationManager.GetDeviceId(),
            model = "VitalsChair-V1",
            serial_number = GetSerialNumber(),
            mac_address = GetMacAddress()
        };
        
        try
        {
            // Use SecureApiClient to send encrypted registration data
            string response = await _secureApiClient.PostAsync(
                "/api/device/register", 
                deviceData
            );
            
            // Response is automatically decrypted
            Console.WriteLine($"Registration response: {response}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Registration failed: {ex.Message}");
            return false;
        }
    }
}
*/

// ============================================
// STEP 4: Update API Service Classes
// ============================================

/*
public class VitalsApiService
{
    private readonly SecureApiClient _secureApiClient;
    private readonly RegistrationDataManager _registrationManager;
    
    public VitalsApiService(
        SecureApiClient secureApiClient,
        RegistrationDataManager registrationManager)
    {
        _secureApiClient = secureApiClient;
        _registrationManager = registrationManager;
    }
    
    public async Task<ApiResponse> SubmitVitalsAsync(VitalsData vitals)
    {
        var payload = new
        {
            device_id = _registrationManager.GetDeviceId(),
            jwt = _registrationManager.GetJWT(),
            heart_rate = vitals.HeartRate,
            temperature = vitals.Temperature,
            oxygen_saturation = vitals.OxygenSaturation,
            timestamp = DateTime.UtcNow
        };
        
        // Payload is encrypted, sent, and response is decrypted automatically
        string response = await _secureApiClient.PostAsync(
            "/api/bluhealth/addVitals", 
            payload
        );
        
        return JsonSerializer.Deserialize<ApiResponse>(response);
    }
    
    public async Task<PatientData> GetPatientDataAsync(string patientId)
    {
        // Automatically decrypts the response
        string response = await _secureApiClient.GetAsync(
            $"/api/bluhealth/patient/{patientId}"
        );
        
        return JsonSerializer.Deserialize<PatientData>(response);
    }
}
*/

// ============================================
// STEP 5: Update GeminiLiveSession.cs
// ============================================

/*
public class GeminiLiveSession
{
    private readonly ApiEncryptionManager _encryptionManager;
    
    public GeminiLiveSession(ApiEncryptionManager encryptionManager)
    {
        _encryptionManager = encryptionManager;
    }
    
    public async Task SendAudioAsync(byte[] audioData)
    {
        // Encrypt audio data before sending
        string base64Audio = Convert.ToBase64String(audioData);
        string encrypted = await _encryptionManager.EncryptAsync(base64Audio);
        
        // Send encrypted audio to server
        await SendToServerAsync(encrypted);
    }
    
    public async Task<string> ReceiveResponseAsync()
    {
        // Receive encrypted response
        string encrypted = await ReceiveFromServerAsync();
        
        // Decrypt the response
        string decrypted = await _encryptionManager.DecryptAsync(encrypted);
        
        return decrypted;
    }
}
*/

// ============================================
// STEP 6: Flow Diagram
// ============================================

/*
DEVICE STARTUP:
  1. Program.cs loads /data/registration.json
  2. RegistrationDataManager extracts JWT + Device ID
  3. ApiEncryptionManager derives encryption key from JWT + Device ID
  4. Services are registered with dependency injection
  5. App starts and is ready to encrypt/decrypt

SENDING VITALS:
  1. VitalsQueue.EnqueueVitalsAsync called with vitals data
  2. Vitals serialized to JSON
  3. ApiEncryptionManager.EncryptAsync encrypts JSON
  4. Encrypted data stored in queue
  5. Later, VitalsApiService.SubmitVitalsAsync sends encrypted data
  6. Server receives encrypted payload and processes

RECEIVING RESPONSE:
  1. SecureApiClient.PostAsync sends encrypted request
  2. Server responds with encrypted response
  3. SecureApiClient.PostAsync automatically decrypts response
  4. JSON response deserialized and returned to caller
  5. VitalsApiService processes decrypted data

REGISTRATION UPDATE:
  1. Device requests new configuration from API
  2. Server sends encrypted config_cache.json
  3. RegistrationDataManager.LoadConfigCacheAsync decrypts and loads
  4. New API endpoints are cached for future use
  5. Feature flags and thresholds are updated
*/

// ============================================
// STEP 7: Security Checklist
// ============================================

/*
✓ ENCRYPTION SCHEME
  [X] AES-256-CBC with random IV per message
  [X] Key derived from JWT + Device ID (PBKDF2)
  [X] IV included in ciphertext (hex:hex format)

✓ KEY MANAGEMENT
  [X] No hardcoded keys
  [X] No keys in config files
  [X] No keys in environment variables
  [X] Deterministic derivation from JWT

✓ DATA PROTECTION
  [X] All outgoing API payloads encrypted
  [X] All incoming API responses encrypted
  [X] Queue storage encrypted at rest
  [X] Audio data encrypted before transmission

✓ ERROR HANDLING
  [X] Invalid ciphertext rejected
  [X] Decryption failures logged
  [X] Encryption failures raise exceptions
  [X] Graceful degradation not applicable (must encrypt)

✓ LOGGING
  [X] DO NOT log encrypted data
  [X] DO log: device_id, timestamps, operation types
  [X] DO log: success/failure of encryption operations
  [X] DO NOT log: JWT, keys, plaintext data
*/

// ============================================
// STEP 8: Example: Complete Flow
// ============================================

/*
EXAMPLE: Submit vitals from startup to API

1. STARTUP (Program.cs):
   var regMgr = new RegistrationDataManager();
   await regMgr.LoadAllAsync();  // Load /data/registration.json
   services.AddSingleton(regMgr);
   services.AddSingleton(sp => regMgr.CreateEncryptionManager());

2. COLLECT VITALS (MainService):
   var vitals = new VitalsData
   {
       HeartRate = 72,
       Temperature = 37.2,
       OxygenSaturation = 98
   };

3. QUEUE VITALS (VitalsQueue):
   await vitalsQueue.EnqueueVitalsAsync(vitals);
   // Internally: 
   //   - Serialize to JSON
   //   - Encrypt with ApiEncryptionManager
   //   - Store encrypted data

4. SEND TO API (VitalsApiService):
   await vitalsApiService.SubmitVitalsAsync(vitals);
   // Internally:
   //   - Serialize to JSON
   //   - Use SecureApiClient.PostAsync
   //   - SecureApiClient automatically encrypts payload
   //   - Sends to server
   //   - Receives encrypted response
   //   - SecureApiClient automatically decrypts response
   //   - Returns decrypted JSON

5. RESPONSE:
   {
       "status": "success",
       "device_id": "VC-IN-PB-2605-15678114",
       "timestamp": "2026-06-12T10:30:00Z"
   }
*/

// ============================================
// STEP 9: No Changes Needed For:
// ============================================

/*
✓ appsettings.json (No secret keys needed)
✓ Configuration/secrets (No new secrets)
✓ Docker/deployment (Encryption is transparent)
✓ Database schemas (Encrypted at transport layer)
✓ API contracts (Same JSON format, just encrypted in transit)
*/

// ============================================
// STEP 10: Testing
// ============================================

/*
Unit Tests:

1. Test encryption/decryption round trip
   var encMgr = new ApiEncryptionManager(
       ApiEncryptionManager.DeriveKeyFromJWT(jwt, deviceId)
   );
   string encrypted = await encMgr.EncryptAsync(plaintext);
   string decrypted = await encMgr.DecryptAsync(encrypted);
   Assert.Equal(plaintext, decrypted);

2. Test key derivation is deterministic
   string key1 = ApiEncryptionManager.DeriveKeyFromJWT(jwt, deviceId);
   string key2 = ApiEncryptionManager.DeriveKeyFromJWT(jwt, deviceId);
   Assert.Equal(key1, key2);

3. Test different devices get different keys
   string key1 = ApiEncryptionManager.DeriveKeyFromJWT(jwt, "device1");
   string key2 = ApiEncryptionManager.DeriveKeyFromJWT(jwt, "device2");
   Assert.NotEqual(key1, key2);

4. Test SecureApiClient integration
   Mock API server to expect encrypted payload
   Mock API server to respond with encrypted response
   Verify SecureApiClient sends/receives correctly

5. Test RegistrationDataManager
   Create mock /data/registration.json
   Create mock /data/config_cache.json
   Verify RegistrationDataManager loads correctly
*/

// ============================================
// DONE!
// ============================================
// 
// Your API encryption system is now integrated!
// No separate keys to manage, no config changes needed.
// Encryption is automatic and transparent.
//
