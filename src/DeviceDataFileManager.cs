using System;
using System.IO;
using System.Threading.Tasks;

namespace vitalschair_prod_v1
{
    /// <summary>
    /// Robust file management for device registration and configuration.
    /// Handles directory creation, permission validation, and atomic writes with error recovery.
    /// </summary>
    public class DeviceDataFileManager
    {
        // Use relative path that works with app directory; fallback to home if /data not writable
        private static string DATA_DIRECTORY => GetDataDirectory();
        private static string REGISTRATION_FILE => Path.Combine(GetDataDirectory(), "registration.json");
        private static string CONFIG_CACHE_FILE => Path.Combine(GetDataDirectory(), "config_cache.json");
        private static string CONFIG_BACKUP_FILE => Path.Combine(GetDataDirectory(), "config_cache.json.bak");
        private static string SECRET_KEY_CACHE_FILE => Path.Combine(GetDataDirectory(), "secret_key_cache.json");

        /// <summary>
        /// Determines the best writable data directory path.
        /// Tries: /data -> ./data -> ~/.vitalschair/data
        /// </summary>
        private static string GetDataDirectory()
        {
            // Try /data first (production/container use)
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

            // Final fallback to relative path (will be created on first write)
            return "./data";
        }

        /// <summary>
        /// Initializes and validates the data directory at startup.
        /// </summary>
        public static async Task<bool> InitializeDataDirectoryAsync()
        {
            try
            {
                string dataDir = DATA_DIRECTORY; // Triggers GetDataDirectory() logic
                
                // 1. Ensure directory exists
                if (!Directory.Exists(dataDir))
                {
                    Console.WriteLine($"📁 Creating data directory: {dataDir}");
                    Directory.CreateDirectory(dataDir);
                }

                // 2. Validate write permissions
                if (!CanWriteToDirectory(dataDir))
                {
                    Console.WriteLine($"❌ ERROR: No write permission for {dataDir}");
                    return false;
                }

                Console.WriteLine($"✓ Data directory ready: {Path.GetFullPath(dataDir)}");

                // 3. Check existing files
                bool hasRegistration = File.Exists(REGISTRATION_FILE);
                bool hasConfig = File.Exists(CONFIG_CACHE_FILE);
                bool hasSecretKey = File.Exists(SECRET_KEY_CACHE_FILE);

                Console.WriteLine($"  - registration.json: {(hasRegistration ? "✓ Exists" : "✗ Missing (will create on registration)")}");
                Console.WriteLine($"  - config_cache.json: {(hasConfig ? "✓ Exists" : "✗ Missing (will create on config fetch)")}");
                Console.WriteLine($"  - secret_key_cache.json: {(hasSecretKey ? "✓ Exists" : "✗ Missing (will create on key rotation)")}");

                Console.WriteLine($"✓ Data file system initialized successfully\n");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Directory initialization failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Validates that a directory is writable.
        /// </summary>
        private static bool CanWriteToDirectory(string directory)
        {
            try
            {
                string testFile = Path.Combine(directory, ".write_test");
                File.WriteAllText(testFile, "test");
                File.Delete(testFile);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Writes JSON file atomically with backup creation.
        /// </summary>
        public static bool WriteJsonFile(string filePath, string jsonContent, bool createBackup = false)
        {
            try
            {
                // Validate parent directory exists
                string directory = Path.GetDirectoryName(filePath);
                if (!Directory.Exists(directory))
                {
                    Console.WriteLine($"📁 Creating directory: {directory}");
                    Directory.CreateDirectory(directory);
                }

                // Create backup if requested
                if (createBackup && File.Exists(filePath))
                {
                    string backupPath = filePath + ".bak";
                    try
                    {
                        File.Copy(filePath, backupPath, overwrite: true);
                        Console.WriteLine($"💾 Backup created: {backupPath}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"⚠️  Failed to create backup: {ex.Message}");
                        // Continue anyway - backup failure shouldn't block write
                    }
                }

                // Atomic write: write to temp file first, then move
                string tmpFile = filePath + ".tmp";

                try
                {
                    File.WriteAllText(tmpFile, jsonContent);

                    // Verify write was successful
                    if (!File.Exists(tmpFile))
                    {
                        Console.WriteLine($"❌ Temp file write failed: {tmpFile}");
                        return false;
                    }

                    // Verify file size is not zero
                    var fileInfo = new FileInfo(tmpFile);
                    if (fileInfo.Length == 0)
                    {
                        Console.WriteLine($"❌ Temp file is empty: {tmpFile}");
                        File.Delete(tmpFile);
                        return false;
                    }

                    // Move temp file to final location
                    File.Move(tmpFile, filePath, overwrite: true);

                    // Verify final file exists and has content
                    if (!File.Exists(filePath) || new FileInfo(filePath).Length == 0)
                    {
                        Console.WriteLine($"❌ Final file write failed or empty: {filePath}");
                        return false;
                    }

                    Console.WriteLine($"✓ File written successfully: {filePath}");
                    return true;
                }
                finally
                {
                    // Clean up temp file if it still exists
                    if (File.Exists(tmpFile))
                    {
                        try { File.Delete(tmpFile); }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ File write error ({filePath}): {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Reads JSON file with error handling and optional fallback to backup.
        /// </summary>
        public static string ReadJsonFile(string filePath, bool allowBackupFallback = true)
        {
            try
            {
                // Try to read main file
                if (File.Exists(filePath))
                {
                    string content = File.ReadAllText(filePath);

                    // Validate file is not empty or corrupted JSON
                    if (string.IsNullOrWhiteSpace(content))
                    {
                        Console.WriteLine($"⚠️  File is empty: {filePath}");

                        if (allowBackupFallback)
                        {
                            return TryReadBackup(filePath);
                        }
                        return null;
                    }

                    return content;
                }

                Console.WriteLine($"ℹ️  File not found: {filePath}");
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ File read error ({filePath}): {ex.Message}");

                if (allowBackupFallback)
                {
                    return TryReadBackup(filePath);
                }
                return null;
            }
        }

        /// <summary>
        /// Attempts to read from backup file if main file fails.
        /// </summary>
        private static string TryReadBackup(string filePath)
        {
            string backupPath = filePath + ".bak";

            try
            {
                if (File.Exists(backupPath))
                {
                    Console.WriteLine($"🔄 Attempting to read from backup: {backupPath}");
                    string content = File.ReadAllText(backupPath);

                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        Console.WriteLine($"✓ Recovered from backup: {backupPath}");
                        return content;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Backup read failed: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Safely deletes a file with error handling.
        /// </summary>
        public static bool DeleteFile(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    Console.WriteLine($"🗑️  Deleted: {filePath}");
                    return true;
                }
                return true; // Already doesn't exist
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️  Failed to delete {filePath}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Validates registration file integrity.
        /// </summary>
        public static bool ValidateRegistrationFile()
        {
            try
            {
                string content = ReadJsonFile(REGISTRATION_FILE, allowBackupFallback: false);
                if (string.IsNullOrEmpty(content))
                {
                    Console.WriteLine("❌ Registration file missing or empty");
                    return false;
                }

                var doc = System.Text.Json.JsonDocument.Parse(content);
                bool hasDeviceId = doc.RootElement.TryGetProperty("device_id", out var deviceIdEl) &&
                                  deviceIdEl.ValueKind == System.Text.Json.JsonValueKind.String &&
                                  !string.IsNullOrWhiteSpace(deviceIdEl.GetString());

                bool hasJwt = doc.RootElement.TryGetProperty("jwt", out var jwtEl) &&
                             jwtEl.ValueKind == System.Text.Json.JsonValueKind.String &&
                             !string.IsNullOrWhiteSpace(jwtEl.GetString());

                if (!hasDeviceId || !hasJwt)
                {
                    Console.WriteLine("❌ Registration file missing device_id or jwt");
                    return false;
                }

                Console.WriteLine("✓ Registration file valid");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Registration validation failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Validates configuration cache file integrity.
        /// </summary>
        public static bool ValidateConfigCacheFile()
        {
            try
            {
                string content = ReadJsonFile(CONFIG_CACHE_FILE, allowBackupFallback: false);
                if (string.IsNullOrEmpty(content))
                {
                    Console.WriteLine("ℹ️  Config cache file missing or empty");
                    return false;
                }

                var doc = System.Text.Json.JsonDocument.Parse(content);
                bool hasVersionAndDeviceId = doc.RootElement.TryGetProperty("config_version", out _) &&
                                            doc.RootElement.TryGetProperty("device_id", out _);

                if (!hasVersionAndDeviceId)
                {
                    Console.WriteLine("❌ Config cache missing required fields");
                    return false;
                }

                Console.WriteLine("✓ Config cache file valid");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Config validation failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Gets file info for diagnostic purposes.
        /// </summary>
        public static void PrintDiagnostics()
        {
            Console.WriteLine("\n╔════════════════════════════════════════════════╗");
            Console.WriteLine("║        DEVICE DATA FILES DIAGNOSTICS           ║");
            Console.WriteLine("╚════════════════════════════════════════════════╝\n");

            PrintFileInfo(REGISTRATION_FILE);
            PrintFileInfo(CONFIG_CACHE_FILE);
            PrintFileInfo(CONFIG_BACKUP_FILE);
            PrintDirectoryInfo(DATA_DIRECTORY);

            Console.WriteLine();
        }

        private static void PrintFileInfo(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    var info = new FileInfo(filePath);
                    Console.WriteLine($"📄 {filePath}");
                    Console.WriteLine($"   Size: {info.Length} bytes");
                    Console.WriteLine($"   Modified: {info.LastWriteTimeUtc:yyyy-MM-dd HH:mm:ssZ}");
                    Console.WriteLine($"   Readable: ✓");
                }
                else
                {
                    Console.WriteLine($"📄 {filePath} - ✗ NOT FOUND");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"📄 {filePath} - ❌ ERROR: {ex.Message}");
            }
        }

        private static void PrintDirectoryInfo(string dirPath)
        {
            try
            {
                if (Directory.Exists(dirPath))
                {
                    var info = new DirectoryInfo(dirPath);
                    Console.WriteLine($"📁 {dirPath}");
                    Console.WriteLine($"   Exists: ✓");
                    Console.WriteLine($"   Writable: {(CanWriteToDirectory(dirPath) ? "✓" : "✗")}");
                    Console.WriteLine($"   Files: {Directory.GetFiles(dirPath).Length}");
                }
                else
                {
                    Console.WriteLine($"📁 {dirPath} - ✗ NOT FOUND");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"📁 {dirPath} - ❌ ERROR: {ex.Message}");
            }
        }
    }
}
