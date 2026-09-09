using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using vitalschair_prod_v1;

namespace vitalschair_prod_v1
{
    /// <summary>
    /// Resilient secret key manager with caching, TTL, and fallback strategies.
    /// Ensures device continues operating even if key API is temporarily unavailable.
    /// </summary>
    public class ResilientSecretKeyManager
    {
        private readonly SecretKeyManager _secretKeyManager;
        private static string SECRET_KEY_CACHE_FILE => Path.Combine(GetDataDirectory(), "secret_key_cache.json");
        
        private string _cachedKey;
        private DateTime _lastFetchTime = DateTime.MinValue;
        private SecretKeyManager.SecretKeyResponse _lastResponse;

        // TTL constants
        private const int NORMAL_CACHE_TTL_MINUTES = 60;        // Use fresh cache for 1 hour
        private const int EXTENDED_CACHE_TTL_HOURS = 24;        // Use stale cache for up to 24 hours if API down
        private const int PERSIST_CACHE_TTL_HOURS = 168;        // Persist cache file for up to 7 days

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

        public ResilientSecretKeyManager(SecretKeyManager secretKeyManager)
        {
            _secretKeyManager = secretKeyManager ?? throw new ArgumentNullException(nameof(secretKeyManager));
        }

        /// <summary>
        /// Gets the current secret key with intelligent caching and fallback.
        /// </summary>
        public async Task<string> GetSecretKeyAsync()
        {
            try
            {
                // Check if cached key is still fresh (within normal TTL)
                if (_cachedKey != null && IsWithinNormalTTL())
                {
                    var cacheAge = DateTime.UtcNow - _lastFetchTime;
                    Console.WriteLine($"✓ Using cached secret key (age: {cacheAge.TotalSeconds:F0}s)");
                    return _cachedKey;
                }

                // Try to fetch fresh key from server
                return await FetchAndCacheKeyAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️  Fresh key fetch failed: {ex.Message}");

                // Try to use stale cached key
                return await UseStaleCacheAsync();
            }
        }

        /// <summary>
        /// Fetches fresh key from server and updates cache.
        /// </summary>
        private async Task<string> FetchAndCacheKeyAsync()
        {
            try
            {
                Console.WriteLine("🔄 Fetching fresh secret key from server...");
                
                var response = await _secretKeyManager.FetchSecretKeyFromServerAsync();
                string keyValue = _secretKeyManager.ExtractKeyValue(response);

                // Update in-memory cache
                _cachedKey = keyValue;
                _lastFetchTime = DateTime.UtcNow;
                _lastResponse = response;

                // Persist cache to disk for recovery
                await PersistCacheAsync(response);

                Console.WriteLine($"✓ Secret key cached and persisted");
                Audit.Log("KEY_REFRESH", "success", "system",
                    new System.Collections.Generic.Dictionary<string, object?> { ["source"] = "server" });
                return keyValue;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Failed to fetch fresh secret key: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Attempts to use stale cached key when server is unreachable.
        /// </summary>
        private async Task<string> UseStaleCacheAsync()
        {
            // First, try in-memory cache
            if (_cachedKey != null)
            {
                var cacheAge = DateTime.UtcNow - _lastFetchTime;

                if (cacheAge.TotalHours < EXTENDED_CACHE_TTL_HOURS)
                {
                    Console.WriteLine(
                        $"⚠️  Using stale in-memory cache (age: {cacheAge.TotalHours:F1}h) - API unreachable");
                    Audit.Log("KEY_REFRESH", "info", "system",
                        new System.Collections.Generic.Dictionary<string, object?> { ["source"] = "stale-cache" });
                    return _cachedKey;
                }
            }

            // Second, try persistent cache file
            var persistedKey = await TryLoadPersistedCacheAsync();
            if (persistedKey != null)
            {
                _cachedKey = persistedKey;
                Audit.Log("KEY_REFRESH", "info", "system",
                    new System.Collections.Generic.Dictionary<string, object?> { ["source"] = "persisted-fallback" });
                return persistedKey;
            }

            // No cache available
            throw new InvalidOperationException(
                "❌ Secret key unavailable: API unreachable and no cache available");
        }

        /// <summary>
        /// Persists cache to disk for recovery across restarts.
        /// </summary>
        private async Task PersistCacheAsync(SecretKeyManager.SecretKeyResponse response)
        {
            try
            {
                var cacheData = new
                {
                    key_value = response.data.value,
                    key_id = response.data.id,
                    fetched_at = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    server_updated_at = response.data.updated_at.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    ttl_minutes = NORMAL_CACHE_TTL_MINUTES,
                    extended_ttl_hours = EXTENDED_CACHE_TTL_HOURS
                };

                string json = JsonSerializer.Serialize(cacheData, 
                    new JsonSerializerOptions { WriteIndented = true });

                bool writeSuccess = vitalschair_prod_v1.DeviceDataFileManager.WriteJsonFile(
                    SECRET_KEY_CACHE_FILE,
                    json,
                    createBackup: true
                );

                if (writeSuccess)
                {
                    Console.WriteLine($"💾 Secret key cache persisted to disk");
                }
                else
                {
                    Console.WriteLine($"⚠️  Failed to persist secret key cache (non-fatal)");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️  Error persisting cache: {ex.Message}");
                // Don't throw - persistence failure shouldn't break operation
            }
        }

        /// <summary>
        /// Loads persisted cache from disk if available.
        /// </summary>
        private async Task<string> TryLoadPersistedCacheAsync()
        {
            try
            {
                string cacheJson = vitalschair_prod_v1.DeviceDataFileManager.ReadJsonFile(
                    SECRET_KEY_CACHE_FILE,
                    allowBackupFallback: true
                );

                if (string.IsNullOrEmpty(cacheJson))
                {
                    return null;
                }

                using var doc = JsonDocument.Parse(cacheJson);
                var root = doc.RootElement;

                if (root.TryGetProperty("key_value", out var keyValueEl) &&
                    root.TryGetProperty("fetched_at", out var fetchedAtEl))
                {
                    string keyValue = keyValueEl.GetString();
                    DateTime fetchedAt = DateTime.Parse(fetchedAtEl.GetString());
                    var cacheAge = DateTime.UtcNow - fetchedAt;

                    if (cacheAge.TotalHours < PERSIST_CACHE_TTL_HOURS)
                    {
                        Console.WriteLine(
                            $"🔄 Recovered secret key from persistent cache (age: {cacheAge.TotalHours:F1}h)");
                        _cachedKey = keyValue;
                        _lastFetchTime = fetchedAt;
                        return keyValue;
                    }
                    else
                    {
                        Console.WriteLine(
                            $"⚠️  Persistent cache expired (age: {cacheAge.TotalDays:F1}d)");
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️  Error loading persisted cache: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Checks if cached key is within normal TTL.
        /// </summary>
        private bool IsWithinNormalTTL()
        {
            if (_cachedKey == null)
                return false;

            var cacheAge = DateTime.UtcNow - _lastFetchTime;
            return cacheAge.TotalMinutes < NORMAL_CACHE_TTL_MINUTES;
        }

        /// <summary>
        /// Gets detailed cache status for diagnostics.
        /// </summary>
        public CacheStatus GetCacheStatus()
        {
            var cacheAge = _lastFetchTime == DateTime.MinValue ? 
                TimeSpan.MaxValue : 
                DateTime.UtcNow - _lastFetchTime;

            return new CacheStatus
            {
                HasInMemoryCache = _cachedKey != null,
                InMemoryCacheAge = cacheAge,
                IsWithinNormalTTL = IsWithinNormalTTL(),
                HasPersistedCache = File.Exists(SECRET_KEY_CACHE_FILE),
                LastFetchTime = _lastFetchTime == DateTime.MinValue ? null : _lastFetchTime,
                KeyId = _lastResponse?.data?.id ?? "unknown"
            };
        }

        /// <summary>
        /// Cache status information for diagnostics.
        /// </summary>
        public class CacheStatus
        {
            public bool HasInMemoryCache { get; set; }
            public TimeSpan InMemoryCacheAge { get; set; }
            public bool IsWithinNormalTTL { get; set; }
            public bool HasPersistedCache { get; set; }
            public DateTime? LastFetchTime { get; set; }
            public string KeyId { get; set; }

            public override string ToString()
            {
                return $"[Cache Status]\n" +
                       $"  In-Memory: {(HasInMemoryCache ? "✓" : "✗")}\n" +
                       $"  Age: {(HasInMemoryCache ? $"{InMemoryCacheAge.TotalSeconds:F0}s" : "N/A")}\n" +
                       $"  Fresh: {(IsWithinNormalTTL ? "✓" : "✗")}\n" +
                       $"  Persisted: {(HasPersistedCache ? "✓" : "✗")}\n" +
                       $"  Last Fetch: {(LastFetchTime?.ToString("yyyy-MM-dd HH:mm:ssZ") ?? "Never")}\n" +
                       $"  Key ID: {KeyId}";
            }
        }

        /// <summary>
        /// Prints cache diagnostics to console.
        /// </summary>
        public void PrintCacheDiagnostics()
        {
            var status = GetCacheStatus();
            Console.WriteLine("\n╔════════════════════════════════════════════════╗");
            Console.WriteLine("║        SECRET KEY CACHE DIAGNOSTICS            ║");
            Console.WriteLine("╚════════════════════════════════════════════════╝\n");
            Console.WriteLine(status.ToString());
            Console.WriteLine();
        }
    }
}
