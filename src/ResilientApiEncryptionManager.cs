using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace vitalschair_prod_v1
{
    /// <summary>
    /// API Encryption Manager with resilient secret key management.
    /// Automatically fetches and rotates encryption keys from the server.
    /// Provides fallback to cached keys if API is unavailable.
    /// </summary>
    public class ResilientApiEncryptionManager
    {
        private const string ALGORITHM = "AES";
        private const int IV_LENGTH = 16; // 128 bits for AES
        private const int KEY_LENGTH = 32; // 256 bits for AES-256
        private const CipherMode CIPHER_MODE = CipherMode.CBC;
        private const PaddingMode PADDING_MODE = PaddingMode.PKCS7;

        private readonly ResilientSecretKeyManager _secretKeyManager;

        /// <summary>
        /// Initializes encryption manager with resilient secret key provider.
        /// </summary>
        public ResilientApiEncryptionManager(ResilientSecretKeyManager secretKeyManager)
        {
            _secretKeyManager = secretKeyManager ?? throw new ArgumentNullException(nameof(secretKeyManager));
        }

        /// <summary>
        /// Encrypts text using dynamically fetched encryption key.
        /// The key is fetched from the server with caching and fallback.
        /// </summary>
        public async Task<string> EncryptAsync(string text)
        {
            if (string.IsNullOrEmpty(text))
                throw new ArgumentException("Text cannot be null or empty", nameof(text));

            try
            {
                // Get current secret key (with caching)
                string secretKey = await _secretKeyManager.GetSecretKeyAsync();
                LoggerUtil.LogStructured(
                    LogLevel.Info,
                    LogCategory.Auth,
                    "🔐 ENCRYPTION INITIATED",
                    new System.Collections.Generic.Dictionary<string, object>
                    {
                        { "InputLength", text.Length },
                        { "KeySourced", "From ResilientSecretKeyManager" }
                    }
                );
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
                        string resultFormat = $"{ivHex}:{encryptedHex}";
                        
                        LoggerUtil.LogStructured(
                            LogLevel.Info,
                            LogCategory.Auth,
                            "✅ ENCRYPTION SUCCESSFUL",
                            new System.Collections.Generic.Dictionary<string, object>
                            {
                                { "OriginalSize", text.Length },
                                { "EncryptedSize", resultFormat.Length },
                                { "Algorithm", "AES-256-CBC" },
                                { "IVLength", iv.Length },
                                { "Compression", $"{((double)resultFormat.Length / text.Length).ToString("F2")}x" }
                            }
                        );
                        
                        return resultFormat;
                    }
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogStructured(
                    LogLevel.Error,
                    LogCategory.Auth,
                    "Encryption failed",
                    new System.Collections.Generic.Dictionary<string, object>
                    {
                        { "Error", ex.Message }
                    }
                );
                throw new InvalidOperationException("Encryption failed", ex);
            }
        }

        /// <summary>
        /// Decrypts encrypted text using dynamically fetched encryption key.
        /// </summary>
        public async Task<string> DecryptAsync(string encryptedText)
        {
            if (string.IsNullOrEmpty(encryptedText))
                throw new ArgumentException("Encrypted text cannot be null or empty", nameof(encryptedText));

            try
            {
                LoggerUtil.LogStructured(
                    LogLevel.Info,
                    LogCategory.Auth,
                    "🔓 DECRYPTION INITIATED",
                    new System.Collections.Generic.Dictionary<string, object>
                    {
                        { "EncryptedSize", encryptedText.Length },
                        { "Format", "IV:EncryptedContent (hex)" }
                    }
                );
                
                // Get current secret key (with caching)
                string secretKey = await _secretKeyManager.GetSecretKeyAsync();
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
                        string decrypted = Encoding.UTF8.GetString(decryptedBytes);
                        
                        LoggerUtil.LogStructured(
                            LogLevel.Info,
                            LogCategory.Auth,
                            "✅ DECRYPTION SUCCESSFUL",
                            new System.Collections.Generic.Dictionary<string, object>
                            {
                                { "EncryptedSize", encryptedText.Length },
                                { "DecryptedSize", decrypted.Length },
                                { "Algorithm", "AES-256-CBC" },
                                { "IVLength", iv.Length }
                            }
                        );
                        
                        return decrypted;
                    }
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogStructured(
                    LogLevel.Error,
                    LogCategory.Auth,
                    "Decryption failed",
                    new System.Collections.Generic.Dictionary<string, object>
                    {
                        { "Error", ex.Message }
                    }
                );
                throw new InvalidOperationException("Decryption failed", ex);
            }
        }

        /// <summary>
        /// Matches the Node API key derivation:
        /// crypto.createHash('sha256').update(secret).digest('base64').substr(0, 32)
        /// The resulting 32 ASCII characters are used directly as the AES-256 key.
        /// </summary>
        private byte[] DeriveKeyFromString(string input)
        {
            using (var sha256 = SHA256.Create())
            {
                byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
                string base64Hash = Convert.ToBase64String(hashBytes);
                string nodeCompatibleKey = base64Hash.Substring(0, KEY_LENGTH);
                return Encoding.UTF8.GetBytes(nodeCompatibleKey);
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

        /// <summary>
        /// Gets cache diagnostics.
        /// </summary>
        public void PrintDiagnostics()
        {
            _secretKeyManager.PrintCacheDiagnostics();
        }
    }
}
