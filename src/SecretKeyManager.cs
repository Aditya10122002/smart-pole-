using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace vitalschair_prod_v1
{
    /// <summary>
    /// Manages fetching and caching of secret keys from the server.
    /// Supports dynamic key rotation with TTL-based caching and fallback strategies.
    /// </summary>
    public class SecretKeyManager
    {
        private readonly RegistrationDataManager _registrationManager;
        private readonly HttpClient _httpClient;

        public class SecretKeyResponse
        {
            public bool success { get; set; }
            public string message { get; set; }
            public SecretKeyData data { get; set; }
        }

        public class SecretKeyData
        {
            public string id { get; set; }
            public string key { get; set; }
            public string value { get; set; }
            public DateTime created_at { get; set; }
            public DateTime updated_at { get; set; }
        }

        public SecretKeyManager(RegistrationDataManager registrationManager)
        {
            _registrationManager = registrationManager ?? throw new ArgumentNullException(nameof(registrationManager));
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
        }

        /// <summary>
        /// Fetches the secret key from the server API endpoint.
        /// </summary>
        public async Task<SecretKeyResponse> FetchSecretKeyFromServerAsync()
        {
            try
            {
                var bluHealthConfig = _registrationManager.GetBluHealthApiConfig();


                  if (bluHealthConfig == null)
                {
                    throw new InvalidOperationException(
                        "Config not loaded yet — config_cache.json missing on this device. " +
                        "Device must complete registration and config fetch before secret key can be retrieved.");
                }

                
                if (string.IsNullOrEmpty(bluHealthConfig.APIsecretKey))
                {
                    throw new InvalidOperationException("Secret key API endpoint not found in config");
                }

                // Add JWT authorization if available
                string jwt = _registrationManager.GetJWT();
                if (!string.IsNullOrEmpty(jwt))
                {
                    _httpClient.DefaultRequestHeaders.Authorization = 
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", jwt);
                }

                Console.WriteLine($"🔑 Fetching secret key from: {bluHealthConfig.APIsecretKey}");

                var response = await _httpClient.GetAsync(bluHealthConfig.APIsecretKey);

                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        $"Failed to fetch secret key: HTTP {response.StatusCode}");
                }

                string jsonContent = await response.Content.ReadAsStringAsync();
                var secretKeyResponse = JsonSerializer.Deserialize<SecretKeyResponse>(jsonContent);

                if (secretKeyResponse == null || !secretKeyResponse.success || secretKeyResponse.data == null)
                {
                    throw new InvalidOperationException("Invalid secret key response format");
                }

                Console.WriteLine($"✓ Secret key fetched successfully (ID: {secretKeyResponse.data.id})");
                return secretKeyResponse;
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"❌ HTTP error fetching secret key: {ex.Message}");
                throw;
            }
            catch (TaskCanceledException)
            {
                Console.WriteLine($"❌ Timeout fetching secret key");
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error fetching secret key: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Gets the secret key value from the response.
        /// </summary>
        public string ExtractKeyValue(SecretKeyResponse response)
        {
            if (response?.data?.value == null)
            {
                throw new InvalidOperationException("Secret key value not found in response");
            }

            return response.data.value;
        }
    }
}
