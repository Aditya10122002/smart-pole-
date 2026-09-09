using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace vitalschair_prod_v1
{
    /// <summary>
    /// Resilient secure HTTP client with dynamic encryption key rotation.
    /// Automatically fetches and rotates encryption keys from the server.
    /// Encrypts outgoing payloads and decrypts incoming responses.
    /// </summary>
    public class ResilientSecureApiClient : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly ResilientApiEncryptionManager _encryptionManager;
        private readonly string _baseUrl;

        public ResilientSecureApiClient(string baseUrl, ResilientApiEncryptionManager encryptionManager)
        {
            _baseUrl = baseUrl ?? throw new ArgumentNullException(nameof(baseUrl));
            _encryptionManager = encryptionManager ?? throw new ArgumentNullException(nameof(encryptionManager));
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        /// <summary>
        /// Sends an encrypted POST request and returns a decrypted response.
        /// </summary>
        public async Task<string> PostAsync(string endpoint, object payload)
        {
            try
            {
                LoggerUtil.LogStructured(
                    LogLevel.Info,
                    LogCategory.Auth,
                    "📤 API POST REQUEST INITIATED (ENCRYPTED)",
                    new System.Collections.Generic.Dictionary<string, object>
                    {
                        { "Endpoint", endpoint },
                        { "EncryptionStatus", "ENABLED" }
                    }
                );
                
                string jsonPayload = JsonSerializer.Serialize(payload);
                string encryptedPayload = await _encryptionManager.EncryptAsync(jsonPayload);

                var content = new StringContent(encryptedPayload, Encoding.UTF8, "application/json");
                var url = CombineUrl(_baseUrl, endpoint);
                
                HttpResponseMessage response = await _httpClient.PostAsync(url, content);

                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"API request failed: {response.StatusCode}");

                string encryptedResponse = await response.Content.ReadAsStringAsync();
                string decryptedResponse = await _encryptionManager.DecryptAsync(encryptedResponse);
                
                LoggerUtil.LogStructured(
                    LogLevel.Info,
                    LogCategory.Auth,
                    "✅ API POST RESPONSE DECRYPTED",
                    new System.Collections.Generic.Dictionary<string, object>
                    {
                        { "Endpoint", endpoint },
                        { "ResponseSize", decryptedResponse.Length },
                        { "Status", response.StatusCode }
                    }
                );

                return decryptedResponse;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Secure POST request failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Sends an encrypted GET request and returns a decrypted response.
        /// </summary>
        public async Task<string> GetAsync(string endpoint)
        {
            try
            {
                LoggerUtil.LogStructured(
                    LogLevel.Info,
                    LogCategory.Auth,
                    "📥 API GET REQUEST INITIATED (ENCRYPTED)",
                    new System.Collections.Generic.Dictionary<string, object>
                    {
                        { "Endpoint", endpoint },
                        { "EncryptionStatus", "ENABLED" }
                    }
                );
                
                var url = CombineUrl(_baseUrl, endpoint);
                HttpResponseMessage response = await _httpClient.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"API request failed: {response.StatusCode}");

                string encryptedResponse = await response.Content.ReadAsStringAsync();
                string decryptedResponse = await _encryptionManager.DecryptAsync(encryptedResponse);
                
                LoggerUtil.LogStructured(
                    LogLevel.Info,
                    LogCategory.Auth,
                    "✅ API GET RESPONSE DECRYPTED",
                    new System.Collections.Generic.Dictionary<string, object>
                    {
                        { "Endpoint", endpoint },
                        { "ResponseSize", decryptedResponse.Length },
                        { "Status", response.StatusCode }
                    }
                );

                return decryptedResponse;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Secure GET request failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Sends an encrypted PUT request and returns a decrypted response.
        /// </summary>
        public async Task<string> PutAsync(string endpoint, object payload)
        {
            try
            {
                LoggerUtil.LogStructured(
                    LogLevel.Info,
                    LogCategory.Auth,
                    "📤 API PUT REQUEST INITIATED (ENCRYPTED)",
                    new System.Collections.Generic.Dictionary<string, object>
                    {
                        { "Endpoint", endpoint },
                        { "EncryptionStatus", "ENABLED" }
                    }
                );
                
                string jsonPayload = JsonSerializer.Serialize(payload);
                string encryptedPayload = await _encryptionManager.EncryptAsync(jsonPayload);

                var content = new StringContent(encryptedPayload, Encoding.UTF8, "application/json");
                var url = CombineUrl(_baseUrl, endpoint);
                
                HttpResponseMessage response = await _httpClient.PutAsync(url, content);

                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"API request failed: {response.StatusCode}");

                string encryptedResponse = await response.Content.ReadAsStringAsync();
                string decryptedResponse = await _encryptionManager.DecryptAsync(encryptedResponse);
                
                LoggerUtil.LogStructured(
                    LogLevel.Info,
                    LogCategory.Auth,
                    "✅ API PUT RESPONSE DECRYPTED",
                    new System.Collections.Generic.Dictionary<string, object>
                    {
                        { "Endpoint", endpoint },
                        { "ResponseSize", decryptedResponse.Length },
                        { "Status", response.StatusCode }
                    }
                );

                return decryptedResponse;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Secure PUT request failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Sends an encrypted DELETE request and returns a decrypted response.
        /// </summary>
        public async Task<string> DeleteAsync(string endpoint)
        {
            try
            {
                LoggerUtil.LogStructured(
                    LogLevel.Info,
                    LogCategory.Auth,
                    "🗑️ API DELETE REQUEST INITIATED (ENCRYPTED)",
                    new System.Collections.Generic.Dictionary<string, object>
                    {
                        { "Endpoint", endpoint },
                        { "EncryptionStatus", "ENABLED" }
                    }
                );
                
                var url = CombineUrl(_baseUrl, endpoint);
                HttpResponseMessage response = await _httpClient.DeleteAsync(url);

                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"API request failed: {response.StatusCode}");

                string encryptedResponse = await response.Content.ReadAsStringAsync();
                string decryptedResponse = await _encryptionManager.DecryptAsync(encryptedResponse);
                
                LoggerUtil.LogStructured(
                    LogLevel.Info,
                    LogCategory.Auth,
                    "✅ API DELETE RESPONSE DECRYPTED",
                    new System.Collections.Generic.Dictionary<string, object>
                    {
                        { "Endpoint", endpoint },
                        { "ResponseSize", decryptedResponse.Length },
                        { "Status", response.StatusCode }
                    }
                );

                return decryptedResponse;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Secure DELETE request failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Directly encrypts text.
        /// </summary>
        public async Task<string> EncryptAsync(string text) => await _encryptionManager.EncryptAsync(text);

        /// <summary>
        /// Directly decrypts text.
        /// </summary>
        public async Task<string> DecryptAsync(string encryptedText) => await _encryptionManager.DecryptAsync(encryptedText);

        private string CombineUrl(string baseUrl, string endpoint)
        {
            baseUrl = baseUrl.TrimEnd('/');
            endpoint = endpoint.TrimStart('/');
            return $"{baseUrl}/{endpoint}";
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}
