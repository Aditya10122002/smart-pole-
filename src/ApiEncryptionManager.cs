using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace vitalschair_prod_v1
{
    /// <summary>
    /// Manages API payload encryption and decryption for secure communication.
    /// All outgoing and incoming API payloads are encrypted using AES-256-CBC.
    /// 
    /// Supports both static keys and dynamic keys fetched from server with caching.
    /// Encryption keys are derived from JWT + Device ID or fetched from secret key API.
    /// </summary>
    public class ApiEncryptionManager
    {
        private const string ALGORITHM = "AES";
        private const int IV_LENGTH = 16; // 128 bits for AES
        private const int KEY_LENGTH = 32; // 256 bits for AES-256
        private const CipherMode CIPHER_MODE = CipherMode.CBC;
        private const PaddingMode PADDING_MODE = PaddingMode.PKCS7;
        private const string KEY_DERIVATION_SALT = "VitalschairSalt"; // Fixed salt for consistent key derivation
        private const int PBKDF2_ITERATIONS = 10000;

        private readonly string _staticSecretKey;
        private readonly Func<Task<string>> _dynamicSecretKeyProvider;

        /// <summary>
        /// Initializes the encryption manager with a static pre-derived secret key.
        /// </summary>
        /// <param name="secretKey">The static secret key for encryption/decryption</param>
        public ApiEncryptionManager(string secretKey)
        {
            if (string.IsNullOrEmpty(secretKey))
                throw new ArgumentException("Secret key cannot be null or empty", nameof(secretKey));

            _staticSecretKey = secretKey;
            _dynamicSecretKeyProvider = null;
        }

        /// <summary>
        /// Initializes the encryption manager with a dynamic secret key provider.
        /// Enables server-side key rotation with caching and fallback.
        /// </summary>
        /// <param name="secretKeyProvider">Async function that provides the current secret key</param>
        public ApiEncryptionManager(Func<Task<string>> secretKeyProvider)
        {
            if (secretKeyProvider == null)
                throw new ArgumentNullException(nameof(secretKeyProvider));

            _dynamicSecretKeyProvider = secretKeyProvider;
            _staticSecretKey = null;
        }

        /// <summary>
        /// Derives an encryption key from JWT and Device ID.
        /// Use this method when initializing the encryption manager from registration data.
        /// </summary>
        /// <param name="jwt">The JWT token from registration.json</param>
        /// <param name="deviceId">The device ID from registration.json</param>
        /// <returns>A derived encryption key string</returns>
        public static string DeriveKeyFromJWT(string jwt, string deviceId)
        {
            if (string.IsNullOrEmpty(jwt))
                throw new ArgumentException("JWT cannot be null or empty", nameof(jwt));
            if (string.IsNullOrEmpty(deviceId))
                throw new ArgumentException("Device ID cannot be null or empty", nameof(deviceId));

            try
            {
                string combinedInput = jwt + deviceId;
                byte[] saltBytes = Encoding.UTF8.GetBytes(KEY_DERIVATION_SALT);

                using (var pbkdf2 = new Rfc2898DeriveBytes(
                    combinedInput,
                    saltBytes,
                    PBKDF2_ITERATIONS,
                    HashAlgorithmName.SHA256))
                {
                    byte[] keyBytes = pbkdf2.GetBytes(KEY_LENGTH);
                    return Convert.ToBase64String(keyBytes);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Key derivation from JWT failed", ex);
            }
        }

        /// <summary>
        /// Encrypts the provided text using AES-256-CBC.
        /// </summary>
        /// <param name="text">The plain text to encrypt</param>
        /// <returns>Encrypted text in format: IV:EncryptedContent (hex encoded)</returns>
        public async Task<string> EncryptAsync(string text)
        {
            if (string.IsNullOrEmpty(text))
                throw new ArgumentException("Text cannot be null or empty", nameof(text));

            // Get the current secret key (static or dynamic)
            string secretKey = await GetCurrentSecretKeyAsync();

            return await Task.Run(() => Encrypt(text, secretKey));
        }

        /// <summary>
        /// Decrypts the provided encrypted text.
        /// </summary>
        /// <param name="encryptedText">Encrypted text in format: IV:EncryptedContent (hex encoded)</param>
        /// <returns>Decrypted plain text</returns>
        public async Task<string> DecryptAsync(string encryptedText)
        {
            if (string.IsNullOrEmpty(encryptedText))
                throw new ArgumentException("Encrypted text cannot be null or empty", nameof(encryptedText));

            // Get the current secret key (static or dynamic)
            string secretKey = await GetCurrentSecretKeyAsync();

            return await Task.Run(() => Decrypt(encryptedText, secretKey));
        }

        /// <summary>
        /// Gets the current secret key from static or dynamic provider.
        /// </summary>
        private async Task<string> GetCurrentSecretKeyAsync()
        {
            if (_staticSecretKey != null)
            {
                // Using static key
                return _staticSecretKey;
            }
            else if (_dynamicSecretKeyProvider != null)
            {
                // Using dynamic key provider (with caching/fallback)
                return await _dynamicSecretKeyProvider();
            }
            else
            {
                throw new InvalidOperationException("No secret key available (static or dynamic)");
            }
        }

        /// <summary>
        /// Synchronous encryption method.
        /// </summary>
        private string Encrypt(string text, string secretKey)
        {
            try
            {
                byte[] secretKeyBytes = DeriveKeyFromString(secretKey);

                // Generate random IV
                byte[] iv = new byte[IV_LENGTH];
                using (var rng = new RNGCryptoServiceProvider())
                {
                    rng.GetBytes(iv);
                }

                using (var aes = Aes.Create())
                {
                    aes.Mode = CIPHER_MODE;
                    aes.Padding = PADDING_MODE;
                    aes.Key = secretKeyBytes;
                    aes.IV = iv;

                    using (var cipher = aes.CreateEncryptor(aes.Key, aes.IV))
                    {
                        byte[] textBytes = Encoding.UTF8.GetBytes(text);
                        byte[] encryptedBytes = cipher.TransformFinalBlock(textBytes, 0, textBytes.Length);

                        // Combine IV + Encrypted content
                        byte[] result = new byte[iv.Length + encryptedBytes.Length];
                        Buffer.BlockCopy(iv, 0, result, 0, iv.Length);
                        Buffer.BlockCopy(encryptedBytes, 0, result, iv.Length, encryptedBytes.Length);

                        // Return as hex string: IV:EncryptedContent
                        string ivHex = BitConverter.ToString(iv).Replace("-", "").ToLower();
                        string encryptedHex = BitConverter.ToString(encryptedBytes).Replace("-", "").ToLower();

                        return $"{ivHex}:{encryptedHex}";
                    }
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Encryption failed", ex);
            }
        }

        /// <summary>
        /// Synchronous decryption method.
        /// </summary>
        private string Decrypt(string encryptedText, string secretKey)
        {
            try
            {
                byte[] secretKeyBytes = DeriveKeyFromString(secretKey);

                // Parse IV and encrypted content from hex format
                string[] parts = encryptedText.Split(':');
                if (parts.Length != 2)
                    throw new FormatException("Invalid encrypted text format. Expected 'IV:EncryptedContent'");

                byte[] iv = HexStringToByteArray(parts[0]);
                byte[] encryptedContent = HexStringToByteArray(parts[1]);

                using (var aes = Aes.Create())
                {
                    aes.Mode = CIPHER_MODE;
                    aes.Padding = PADDING_MODE;
                    aes.Key = secretKeyBytes;
                    aes.IV = iv;

                    using (var decipher = aes.CreateDecryptor(aes.Key, aes.IV))
                    {
                        byte[] decryptedBytes = decipher.TransformFinalBlock(encryptedContent, 0, encryptedContent.Length);
                        return Encoding.UTF8.GetString(decryptedBytes);
                    }
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Decryption failed", ex);
            }
        }

        /// <summary>
        /// Derives a cryptographic key from the provided string.
        /// Uses PBKDF2 for key derivation with UTF8 encoding.
        /// </summary>
        private byte[] DeriveKeyFromString(string input)
        {
            // If the input is already a base64-encoded key (from DeriveKeyFromJWT), decode it directly
            if (IsBase64String(input))
            {
                try
                {
                    return Convert.FromBase64String(input);
                }
                catch
                {
                    // Fall through to PBKDF2 derivation if base64 decode fails
                }
            }

            // Otherwise, use PBKDF2 to derive a key from the input string
            using (var pbkdf2 = new Rfc2898DeriveBytes(input, Encoding.UTF8.GetBytes("SaltValue"), 10000, HashAlgorithmName.SHA256))
            {
                return pbkdf2.GetBytes(KEY_LENGTH);
            }
        }

        /// <summary>
        /// Checks if a string is valid base64.
        /// </summary>
        private bool IsBase64String(string input)
        {
            if (string.IsNullOrEmpty(input) || input.Length % 4 != 0)
                return false;

            try
            {
                Convert.FromBase64String(input);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Converts a hex string to a byte array.
        /// </summary>
        private byte[] HexStringToByteArray(string hexString)
        {
            int numberChars = hexString.Length;
            byte[] bytes = new byte[numberChars / 2];

            for (int i = 0; i < numberChars; i += 2)
                bytes[i / 2] = Convert.ToByte(hexString.Substring(i, 2), 16);

            return bytes;
        }
    }
}
