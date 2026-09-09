using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace vitalschair_prod_v1
{
    /// <summary>
    /// Example service showing how to use ResilientApiEncryptionManager and ResilientSecureApiClient.
    /// This is a template for integrating encryption into your existing services.
    /// </summary>
    public class ExampleVitalsService
    {
        private readonly ResilientApiEncryptionManager _encryptionManager;
        private readonly ResilientSecureApiClient _apiClient;
        private readonly RegistrationDataManager _registrationManager;

        public ExampleVitalsService(
            ResilientApiEncryptionManager encryptionManager,
            ResilientSecureApiClient apiClient,
            RegistrationDataManager registrationManager)
        {
            _encryptionManager = encryptionManager ?? throw new ArgumentNullException(nameof(encryptionManager));
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            _registrationManager = registrationManager ?? throw new ArgumentNullException(nameof(registrationManager));
        }

        /// <summary>
        /// Encrypts vitals data and sends to API.
        /// Key is automatically fetched/cached with rotation support.
        /// </summary>
        public async Task SubmitVitalsAsync(VitalsData vitals)
        {
            try
            {
                Console.WriteLine("📊 Submitting vitals...");

                // Create payload with device info
                var payload = new
                {
                    device_id = _registrationManager.GetDeviceId(),
                    timestamp = DateTime.UtcNow,
                    heart_rate = vitals.HeartRate,
                    temperature = vitals.Temperature,
                    oxygen_saturation = vitals.OxygenSaturation,
                    systolic = vitals.Systolic,
                    diastolic = vitals.Diastolic
                };

                // Send encrypted request (encryption/decryption automatic)
                string response = await _apiClient.PostAsync("/api/bluhealth/addVitals", payload);

                // Response is automatically decrypted
                var result = JsonSerializer.Deserialize<JsonElement>(response);
                Console.WriteLine($"✓ Vitals submitted successfully");

                LoggerUtil.LogStructured(
                    LogLevel.Info,
                    LogCategory.Vitals,
                    "Vitals submitted",
                    new System.Collections.Generic.Dictionary<string, object>
                    {
                        { "HeartRate", vitals.HeartRate },
                        { "Temperature", vitals.Temperature },
                        { "ResponseStatus", result.GetProperty("status").GetString() }
                    }
                );
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Failed to submit vitals: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Example of manual encryption/decryption (not using SecureApiClient).
        /// Useful if you need to encrypt data before queuing or storing.
        /// </summary>
        public async Task<string> EncryptVitalsForQueueAsync(VitalsData vitals)
        {
            try
            {
                string json = JsonSerializer.Serialize(vitals);
                string encrypted = await _encryptionManager.EncryptAsync(json);
                return encrypted;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Encryption failed: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Decrypts vitals from queue.
        /// </summary>
        public async Task<VitalsData> DecryptVitalsFromQueueAsync(string encryptedData)
        {
            try
            {
                string json = await _encryptionManager.DecryptAsync(encryptedData);
                return JsonSerializer.Deserialize<VitalsData>(json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Decryption failed: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Gets encryption diagnostics (for monitoring/debugging).
        /// </summary>
        public void PrintEncryptionDiagnostics()
        {
            _encryptionManager.PrintDiagnostics();
        }
    }

    /// <summary>
    /// Vitals data structure.
    /// </summary>
    public class VitalsData
    {
        public int HeartRate { get; set; }
        public double Temperature { get; set; }
        public int OxygenSaturation { get; set; }
        public int Systolic { get; set; }
        public int Diastolic { get; set; }
    }
}
