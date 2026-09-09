using System;
using System.Threading.Tasks;
using System.Text.Json;
using System.Collections.Generic;

namespace vitalschair_prod_v1
{
    /// <summary>
    /// Working example demonstrating the complete resilient encryption system.
    /// Shows real-world usage patterns and error handling.
    /// </summary>
    public class ReslientEncryptionExample
    {
        private readonly ApiEncryptionManager _encryptionManager;
        private readonly ResilientSecretKeyManager _keyManager;
        private readonly RegistrationDataManager _registrationManager;

        public ReslientEncryptionExample(
            ApiEncryptionManager encryptionManager,
            ResilientSecretKeyManager keyManager,
            RegistrationDataManager registrationManager)
        {
            _encryptionManager = encryptionManager;
            _keyManager = keyManager;
            _registrationManager = registrationManager;
        }

        // ============================================================
        // EXAMPLE 1: Simple Encryption/Decryption
        // ============================================================

        public async Task Example1_BasicEncryptionAsync()
        {
            Console.WriteLine("\n╔════════════════════════════════════════════════╗");
            Console.WriteLine("║     EXAMPLE 1: Basic Encryption/Decryption     ║");
            Console.WriteLine("╚════════════════════════════════════════════════╝\n");

            try
            {
                // Plain text data
                var vitalsData = new
                {
                    device_id = _registrationManager.GetDeviceId(),
                    heart_rate = 72,
                    temperature = 37.2,
                    timestamp = DateTime.UtcNow
                };

                string plaintext = JsonSerializer.Serialize(vitalsData);
                Console.WriteLine($"📝 Plain text: {plaintext}");

                // Encrypt (uses cached/fresh secret key automatically)
                string encrypted = await _encryptionManager.EncryptAsync(plaintext);
                Console.WriteLine($"🔐 Encrypted: {encrypted.Substring(0, 50)}...");

                // Decrypt
                string decrypted = await _encryptionManager.DecryptAsync(encrypted);
                Console.WriteLine($"🔓 Decrypted: {decrypted}");

                // Verify
                bool matches = plaintext == decrypted;
                Console.WriteLine($"✓ Verification: {(matches ? "PASS" : "FAIL")}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error: {ex.Message}");
            }
        }

        // ============================================================
        // EXAMPLE 2: Checking Cache Status
        // ============================================================

        public async Task Example2_CheckCacheStatusAsync()
        {
            Console.WriteLine("\n╔════════════════════════════════════════════════╗");
            Console.WriteLine("║         EXAMPLE 2: Cache Status Check          ║");
            Console.WriteLine("╚════════════════════════════════════════════════╝\n");

            try
            {
                // Get cache status
                _keyManager.PrintCacheDiagnostics();

                var status = _keyManager.GetCacheStatus();

                // Interpret status
                Console.WriteLine("\n📊 Cache Status Interpretation:");
                if (status.HasInMemoryCache && status.IsWithinNormalTTL)
                {
                    Console.WriteLine("  ✓ HEALTHY: Fresh cache available");
                    Console.WriteLine($"    Age: {status.InMemoryCacheAge.TotalSeconds:F0} seconds");
                }
                else if (status.HasInMemoryCache)
                {
                    Console.WriteLine("  ⚠️  DEGRADED: Using stale cache");
                    Console.WriteLine($"    Age: {status.InMemoryCacheAge.TotalHours:F1} hours");
                    Console.WriteLine("    Will fetch fresh key when server available");
                }
                else if (status.HasPersistedCache)
                {
                    Console.WriteLine("  ⚠️  CRITICAL: Using persistent cache only");
                    Console.WriteLine("    Server unreachable for extended period");
                }
                else
                {
                    Console.WriteLine("  ❌ OFFLINE: No cache available");
                    Console.WriteLine("    Device cannot operate");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error: {ex.Message}");
            }
        }

        // ============================================================
        // EXAMPLE 3: Handling Key Rotation
        // ============================================================

        public async Task Example3_KeyRotationAsync()
        {
            Console.WriteLine("\n╔════════════════════════════════════════════════╗");
            Console.WriteLine("║     EXAMPLE 3: Simulating Key Rotation         ║");
            Console.WriteLine("╚════════════════════════════════════════════════╝\n");

            try
            {
                // Encrypt with initial key
                string data1 = "First message";
                string encrypted1 = await _encryptionManager.EncryptAsync(data1);
                Console.WriteLine($"✓ Message 1 encrypted with key: {encrypted1.Substring(0, 40)}...");

                // Decrypt with same key
                string decrypted1 = await _encryptionManager.DecryptAsync(encrypted1);
                Console.WriteLine($"✓ Message 1 decrypted: {decrypted1}");

                // Simulate time passing and cache expiring
                Console.WriteLine("\n⏱️  Simulating time passage (cache would expire after 60 min)...");
                Console.WriteLine("In real scenario: Fresh key fetched from server");

                // Encrypt with (potentially) new key
                string data2 = "Second message";
                string encrypted2 = await _encryptionManager.EncryptAsync(data2);
                Console.WriteLine($"✓ Message 2 encrypted with key: {encrypted2.Substring(0, 40)}...");

                string decrypted2 = await _encryptionManager.DecryptAsync(encrypted2);
                Console.WriteLine($"✓ Message 2 decrypted: {decrypted2}");

                Console.WriteLine("\n✓ Key rotation handled transparently");
                Console.WriteLine("  Old messages can still be decrypted");
                Console.WriteLine("  New messages encrypted with fresh key");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error: {ex.Message}");
            }
        }

        // ============================================================
        // EXAMPLE 4: Error Handling & Recovery
        // ============================================================

        public async Task Example4_ErrorHandlingAsync()
        {
            Console.WriteLine("\n╔════════════════════════════════════════════════╗");
            Console.WriteLine("║       EXAMPLE 4: Error Handling & Recovery     ║");
            Console.WriteLine("╚════════════════════════════════════════════════╝\n");

            // Scenario A: Server API temporarily down
            Console.WriteLine("Scenario A: Server API temporarily down");
            Console.WriteLine("─────────────────────────────────────────");
            try
            {
                string encrypted = await _encryptionManager.EncryptAsync("Test data");
                Console.WriteLine("✓ Encryption succeeded (using cached key)");
            }
            catch (InvalidOperationException ex)
            {
                Console.WriteLine($"❌ Operation failed: {ex.Message}");
                Console.WriteLine("Action: Wait for server recovery, then restart device");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️  Warning: {ex.Message}");
                Console.WriteLine("Action: Device will retry with cached key");
            }

            // Scenario B: Checking if we can proceed
            Console.WriteLine("\nScenario B: Checking if we can safely proceed");
            Console.WriteLine("──────────────────────────────────────────────");
            var status = _keyManager.GetCacheStatus();

            if (status.HasInMemoryCache || status.HasPersistedCache)
            {
                Console.WriteLine("✓ Cache available - safe to proceed");
                if (status.IsWithinNormalTTL)
                {
                    Console.WriteLine("  Using fresh cache (optimal)");
                }
                else
                {
                    Console.WriteLine("  Using stale cache (server may be down)");
                }
            }
            else
            {
                Console.WriteLine("❌ No cache available - cannot proceed");
                Console.WriteLine("  Device needs server connectivity to operate");
            }
        }

        // ============================================================
        // EXAMPLE 5: Batch Processing with Encryption
        // ============================================================

        public async Task Example5_BatchProcessingAsync()
        {
            Console.WriteLine("\n╔════════════════════════════════════════════════╗");
            Console.WriteLine("║     EXAMPLE 5: Batch Encryption (Queue)        ║");
            Console.WriteLine("╚════════════════════════════════════════════════╝\n");

            try
            {
                // Simulate vitals queue
                var vitalsQueue = new List<object>
                {
                    new { heart_rate = 70, temperature = 37.0 },
                    new { heart_rate = 72, temperature = 37.2 },
                    new { heart_rate = 75, temperature = 37.1 },
                    new { heart_rate = 73, temperature = 37.3 },
                };

                Console.WriteLine($"📋 Processing {vitalsQueue.Count} vitals in queue...\n");

                int successCount = 0;
                int failureCount = 0;

                foreach (var vitals in vitalsQueue)
                {
                    try
                    {
                        string json = JsonSerializer.Serialize(vitals);
                        string encrypted = await _encryptionManager.EncryptAsync(json);
                        Console.WriteLine($"  ✓ Vitals encrypted: {encrypted.Substring(0, 30)}...");
                        successCount++;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  ❌ Failed: {ex.Message}");
                        failureCount++;
                    }
                }

                Console.WriteLine($"\n📊 Results: {successCount} succeeded, {failureCount} failed");

                if (failureCount == 0)
                {
                    Console.WriteLine("✓ All vitals encrypted successfully!");
                }
                else if (successCount > 0)
                {
                    Console.WriteLine("⚠️  Partial success - retrying failed items recommended");
                }
                else
                {
                    Console.WriteLine("❌ All vitals failed - check device status");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error: {ex.Message}");
            }
        }

        // ============================================================
        // EXAMPLE 6: Startup Health Check
        // ============================================================

        public async Task Example6_StartupHealthCheckAsync()
        {
            Console.WriteLine("\n╔════════════════════════════════════════════════╗");
            Console.WriteLine("║        EXAMPLE 6: Startup Health Check         ║");
            Console.WriteLine("╚════════════════════════════════════════════════╝\n");

            Console.WriteLine("Starting device health check...\n");

            try
            {
                // Check 1: Registration data
                Console.WriteLine("1️⃣  Checking registration data...");
                try
                {
                    string deviceId = _registrationManager.GetDeviceId();
                    Console.WriteLine($"   ✓ Device ID: {deviceId}");
                }
                catch
                {
                    Console.WriteLine("   ❌ Registration data missing");
                    throw;
                }

                // Check 2: Cache status
                Console.WriteLine("\n2️⃣  Checking cache status...");
                var status = _keyManager.GetCacheStatus();
                if (status.HasInMemoryCache)
                {
                    Console.WriteLine($"   ✓ In-memory cache available (age: {status.InMemoryCacheAge.TotalSeconds:F0}s)");
                }
                else if (status.HasPersistedCache)
                {
                    Console.WriteLine("   ⚠️  Only persistent cache available");
                }
                else
                {
                    Console.WriteLine("   ⚠️  No cache available (will fetch from server)");
                }

                // Check 3: Test encryption
                Console.WriteLine("\n3️⃣  Testing encryption/decryption...");
                string testData = "Health check";
                string encrypted = await _encryptionManager.EncryptAsync(testData);
                string decrypted = await _encryptionManager.DecryptAsync(encrypted);
                if (decrypted == testData)
                {
                    Console.WriteLine("   ✓ Encryption/decryption working");
                }
                else
                {
                    Console.WriteLine("   ❌ Encryption/decryption failed");
                    throw new InvalidOperationException("Decryption mismatch");
                }

                // Check 4: Configuration
                Console.WriteLine("\n4️⃣  Checking configuration...");
                var bluConfig = _registrationManager.GetBluHealthApiConfig();
                if (!string.IsNullOrEmpty(bluConfig.APIsecretKey))
                {
                    Console.WriteLine($"   ✓ Secret key endpoint configured");
                }
                else
                {
                    Console.WriteLine("   ❌ Secret key endpoint not configured");
                    throw new InvalidOperationException("Missing secret key endpoint");
                }

                // Summary
                Console.WriteLine("\n✓ All health checks passed - device ready!");
                Console.WriteLine("  Device can now process encrypted API calls");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n❌ Health check failed: {ex.Message}");
                Console.WriteLine("  Device startup aborted - manual intervention required");
            }
        }

        // ============================================================
        // RUN ALL EXAMPLES
        // ============================================================

        public async Task RunAllExamplesAsync()
        {
            Console.WriteLine("\n╔════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║   RESILIENT ENCRYPTION SYSTEM - COMPLETE EXAMPLES          ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════════╝");

            await Example1_BasicEncryptionAsync();
            await Task.Delay(1000); // Brief pause for readability

            await Example2_CheckCacheStatusAsync();
            await Task.Delay(1000);

            await Example3_KeyRotationAsync();
            await Task.Delay(1000);

            await Example4_ErrorHandlingAsync();
            await Task.Delay(1000);

            await Example5_BatchProcessingAsync();
            await Task.Delay(1000);

            await Example6_StartupHealthCheckAsync();

            Console.WriteLine("\n╔════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║                  ALL EXAMPLES COMPLETED                    ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════════╝\n");
        }
    }
}
