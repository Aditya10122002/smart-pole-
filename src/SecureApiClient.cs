using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace vitalschair_prod_v1
{
    /// <summary>
    /// HTTP client wrapper that automatically encrypts outgoing payloads and decrypts incoming responses.
    /// </summary>
    public class SecureApiClient : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly ApiEncryptionManager _encryptionManager;
        private readonly string _baseUrl;

        /// <summary>
        /// Initializes a new SecureApiClient with encryption.
        /// </summary>
        /// <param name="baseUrl">The base URL for API calls</param>
        /// <param name="secretKey">The secret key for encryption/decryption</param>
        public SecureApiClient(string baseUrl, string secretKey)
        {
            _baseUrl = baseUrl ?? throw new ArgumentNullException(nameof(baseUrl));
            _encryptionManager = new ApiEncryptionManager(secretKey);
            _httpClient = new HttpClient();
        }

        /// <summary>
        /// Sends an encrypted POST request and returns a decrypted response.
        /// </summary>
        /// <param name="endpoint">API endpoint (appended to baseUrl)</param>
        /// <param name="payload">Object to serialize, encrypt, and send</param>
        /// <returns>Decrypted response content</returns>
        public async Task<string> PostAsync(string endpoint, object payload)
        {
            try
            {
                // Serialize payload to JSON
                string jsonPayload = JsonSerializer.Serialize(payload);

                // Encrypt the payload
                string encryptedPayload = await _encryptionManager.EncryptAsync(jsonPayload);

                // Create request content
                var content = new StringContent(encryptedPayload, Encoding.UTF8, "application/json");

                // Send POST request
                var url = CombineUrl(_baseUrl, endpoint);
                HttpResponseMessage response = await _httpClient.PostAsync(url, content);

                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"API request failed with status code: {response.StatusCode}");

                // Read response
                string encryptedResponse = await response.Content.ReadAsStringAsync();

                // Decrypt response
                string decryptedResponse = await _encryptionManager.DecryptAsync(encryptedResponse);

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
        /// <param name="endpoint">API endpoint (appended to baseUrl)</param>
        /// <returns>Decrypted response content</returns>
        public async Task<string> GetAsync(string endpoint)
        {
            try
            {
                var url = CombineUrl(_baseUrl, endpoint);
                HttpResponseMessage response = await _httpClient.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"API request failed with status code: {response.StatusCode}");

                string encryptedResponse = await response.Content.ReadAsStringAsync();
                string decryptedResponse = await _encryptionManager.DecryptAsync(encryptedResponse);

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
                string jsonPayload = JsonSerializer.Serialize(payload);
                string encryptedPayload = await _encryptionManager.EncryptAsync(jsonPayload);

                var content = new StringContent(encryptedPayload, Encoding.UTF8, "application/json");
                var url = CombineUrl(_baseUrl, endpoint);
                HttpResponseMessage response = await _httpClient.PutAsync(url, content);

                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"API request failed with status code: {response.StatusCode}");

                string encryptedResponse = await response.Content.ReadAsStringAsync();
                string decryptedResponse = await _encryptionManager.DecryptAsync(encryptedResponse);

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
                var url = CombineUrl(_baseUrl, endpoint);
                HttpResponseMessage response = await _httpClient.DeleteAsync(url);

                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"API request failed with status code: {response.StatusCode}");

                string encryptedResponse = await response.Content.ReadAsStringAsync();
                string decryptedResponse = await _encryptionManager.DecryptAsync(encryptedResponse);

                return decryptedResponse;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Secure DELETE request failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Directly encrypts text. Useful for custom encryption needs.
        /// </summary>
        public async Task<string> EncryptAsync(string text) => await _encryptionManager.EncryptAsync(text);

        /// <summary>
        /// Directly decrypts text. Useful for custom decryption needs.
        /// </summary>
        public async Task<string> DecryptAsync(string encryptedText) => await _encryptionManager.DecryptAsync(encryptedText);

        /// <summary>
        /// Combines base URL with endpoint.
        /// </summary>
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
