using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace vitalschair_prod_v1
{
    /// <summary>
    /// Manages registration data from data directory.
    /// Provides utilities to initialize ApiEncryptionManager using JWT-based key derivation.
    /// </summary>
    public class RegistrationDataManager
    {
        private static string REGISTRATION_FILE_PATH => Path.Combine(GetDataDirectory(), "registration.json");
        private static string CONFIG_CACHE_FILE_PATH => Path.Combine(GetDataDirectory(), "config_cache.json");

        /// <summary>
        /// Gets the best writable data directory path.
        /// Tries: /data -> ./data -> ~/.vitalschair/data
        /// </summary>
        private static string GetDataDirectory()
        {
            string[] pathsToTry = new[]
            {
                "/data",
                "./data",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vitalschair", "data")
            };

            foreach (var path in pathsToTry)
            {
                try
                {
                    if (!Directory.Exists(path))
                        Directory.CreateDirectory(path);
                    
                    // Test write permission
                    string testFile = Path.Combine(path, ".write_test");
                    File.WriteAllText(testFile, "test");
                    File.Delete(testFile);
                    
                    return path;
                }
                catch { /* Try next path */ }
            }

            // Final fallback
            return "./data";
        }

        private RegistrationData _registrationData;
        private ConfigCacheData _configCacheData;

        /// <summary>
        /// Registration data structure matching /data/registration.json
        /// </summary>
        public class RegistrationData
        {
            public string device_id { get; set; }
            public string jwt { get; set; }
            public DateTime registered_at { get; set; }
        }

        /// <summary>
        /// Configuration cache data structure matching /data/config_cache.json
        /// </summary>
        public class ConfigCacheData
        {
            public int config_version { get; set; }
            public string device_id { get; set; }
            public BluHealthApiConfig BluHealthApi { get; set; }
            public VoiceAssistantConfig VoiceAssistant { get; set; }
            public HL7Config HL7 { get; set; }
            public DateTime fetched_at { get; set; }
        }

        public class BluHealthApiConfig
        {
            public string DeviceUrl { get; set; }
            public string VitalsUrl { get; set; }
            public string BluNoteUrl { get; set; }
            public string SendOtpUrl { get; set; }
            public string BasepromtUrl { get; set; }
            public string VerifyOtpUrl { get; set; }
            public string APIsecretKey { get; set; }           // Dynamic secret key API endpoint
            public string VoiceAssistant { get; set; }         // Voice assistant config API endpoint
        }

        public class VoiceAssistantConfig
        {
            public string Model { get; set; }
            public string ApiKey { get; set; }
            public string Language { get; set; }
        }

        public class HL7Config
        {
            public bool Enabled { get; set; }
            public string HisHost { get; set; }
            public int HisPort { get; set; }
        }

        /// <summary>
        /// Loads registration data from file.
        /// </summary>
        public async Task<bool> LoadRegistrationDataAsync()
        {
            try
            {
                if (!File.Exists(REGISTRATION_FILE_PATH))
                {
                    Console.WriteLine($"⚠️  Registration file not found at {REGISTRATION_FILE_PATH}");
                    return false;
                }

                string jsonContent = await File.ReadAllTextAsync(REGISTRATION_FILE_PATH);
                _registrationData = JsonSerializer.Deserialize<RegistrationData>(jsonContent);

                if (_registrationData == null || string.IsNullOrEmpty(_registrationData.device_id) || string.IsNullOrEmpty(_registrationData.jwt))
                {
                    Console.WriteLine("❌ Invalid registration data format");
                    return false;
                }

                Console.WriteLine($"✓ Registration data loaded for device: {_registrationData.device_id}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error loading registration data: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Loads configuration cache data from file.
        /// </summary>
        public async Task<bool> LoadConfigCacheAsync()
        {
            try
            {
                if (!File.Exists(CONFIG_CACHE_FILE_PATH))
                {
                    Console.WriteLine($"⚠️  Config cache file not found at {CONFIG_CACHE_FILE_PATH}");
                    return false;
                }

                string jsonContent = await File.ReadAllTextAsync(CONFIG_CACHE_FILE_PATH);
                _configCacheData = JsonSerializer.Deserialize<ConfigCacheData>(jsonContent);

                if (_configCacheData == null)
                {
                    Console.WriteLine("❌ Invalid config cache format");
                    return false;
                }

                Console.WriteLine($"✓ Config cache loaded (version {_configCacheData.config_version})");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error loading config cache: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Creates an ApiEncryptionManager from registration data.
        /// The encryption key is derived from JWT + Device ID.
        /// </summary>
        public ApiEncryptionManager CreateEncryptionManager()
        {
            if (_registrationData == null || string.IsNullOrEmpty(_registrationData.jwt) || string.IsNullOrEmpty(_registrationData.device_id))
            {
                throw new InvalidOperationException("Registration data not loaded. Call LoadRegistrationDataAsync first.");
            }

            try
            {
                string derivedKey = ApiEncryptionManager.DeriveKeyFromJWT(
                    _registrationData.jwt,
                    _registrationData.device_id
                );

                return new ApiEncryptionManager(derivedKey);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to create encryption manager", ex);
            }
        }

        /// <summary>
        /// Gets the device ID from registration data.
        /// </summary>
        public string GetDeviceId()
        {
            return _registrationData?.device_id ?? throw new InvalidOperationException("Registration data not loaded");
        }

        /// <summary>
        /// Gets the JWT from registration data.
        /// </summary>
        public string GetJWT()
        {
            return _registrationData?.jwt ?? throw new InvalidOperationException("Registration data not loaded");
        }

        /// <summary>
        /// Gets the BluHealth API configuration.
        /// Returns null if config cache not loaded (e.g., device pending approval).
        /// </summary>
        public BluHealthApiConfig GetBluHealthApiConfig()
        {
            return _configCacheData?.BluHealthApi;
        }

        /// <summary>
        /// Gets the Voice Assistant configuration.
        /// Returns null if config cache not loaded (e.g., device pending approval).
        /// </summary>
        public VoiceAssistantConfig GetVoiceAssistantConfig()
        {
            return _configCacheData?.VoiceAssistant;
        }

        /// <summary>
        /// Gets the HL7 configuration.
        /// Returns null if config cache not loaded (e.g., device pending approval).
        /// </summary>
        public HL7Config GetHL7Config()
        {
            return _configCacheData?.HL7;
        }

        /// <summary>
        /// Loads both registration and config cache data.
        /// </summary>
        public async Task<bool> LoadAllAsync()
        {
            // Registration (JWT) is REQUIRED
            bool registrationLoaded = await LoadRegistrationDataAsync();

            // Config is OPTIONAL - device can proceed without it during approval wait
            // Config will be fetched once admin approves
            await LoadConfigCacheAsync(); // Try to load but don't fail if missing

            return registrationLoaded; // Only registration is required to proceed
        }
    }
}
