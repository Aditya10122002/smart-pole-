using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using System.Collections.Generic;

public class DeviceRegistration
{
    // ── Real API endpoints (resolved at startup from BootstrapConfigProvider) ───
    private static string RegisterUrl = string.Empty;
    private static string StatusCheckUrl = string.Empty;
    private static string ConfigFetchUrl = string.Empty;
    private const string BootstrapToken = "bluai-vc-bootstrap";

    /// <summary>
    /// Initialize DeviceRegistration with resolved bootstrap URLs.
    /// Call once at app startup before EnsureRegisteredAsync().
    /// </summary>
    public static void Initialize(BootstrapUrls urls)
    {
        RegisterUrl = urls.RegisterUrl;
        StatusCheckUrl = urls.StatusCheckUrl;
        ConfigFetchUrl = BuildConfigFetchUrl(StatusCheckUrl);
    }

    // ── Local paths (dynamic - fallback to writable directories) ──
    private static string RegistrationPath => Path.Combine(GetDataDirectory(), "registration.json");
    private static string ConfigCachePath => Path.Combine(GetDataDirectory(), "config_cache.json");

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

    private static bool _isRegistered = false;
    private static bool _isDataDirectoryValid = false;
    private static DateTime _lastForcedConfigFetchUtc = DateTime.MinValue;
    private static readonly TimeSpan ForcedConfigFetchInterval = TimeSpan.FromMinutes(15);

    // ── Public: call this on every startup ────────────────────
    // Returns only when device is: registered + approved + config available
    public static async Task EnsureRegisteredAsync()
    {
        LoggerUtil.LogStructured(
            LogLevel.Info,
            LogCategory.Registration,
            "🔄 Starting robust registration flow — will not proceed until: registered + approved + config available");

        // ── STAGE 1: Initialize and validate data directory ──
        if (!await InitializeDataDirectoryAsync())
        {
            LoggerUtil.LogStructured(
                LogLevel.Critical,
                LogCategory.Registration,
                "❌ CRITICAL: Failed to initialize data directory. Device cannot proceed.");
            throw new InvalidOperationException("Data directory initialization failed");
        }

        // ── STAGE 2: Check registration status ──
        if (!IsAlreadyRegistered())
        {
            LoggerUtil.LogStructured(
                LogLevel.Info,
                LogCategory.Registration,
                "📝 STAGE 2: Device not registered locally — starting first-time registration");
            await RegisterWithRetryAsync();
            // After registration, device is waiting for admin approval
            // Config will be available once approved
            return;
        }

        LoggerUtil.LogStructured(
            LogLevel.Info,
            LogCategory.Registration,
            " STAGE 2: Device already registered locally");
        _isRegistered = true;

        // ── STAGE 3: Check approval status & device validity ──
        LoggerUtil.LogStructured(
            LogLevel.Info,
            LogCategory.Registration,
            " STAGE 3: Checking device approval status with server");
        await CheckDeviceStatusAsync();  // 

        // If config still missing after status check, force re-register to get it
        if (!File.Exists(ConfigCachePath))
        {
            LoggerUtil.LogStructured(
                LogLevel.Warning,
                LogCategory.Registration,
                "Config cache still missing after status check — forcing re-registration to fetch config");
            await RegisterWithRetryAsync();
        }

        LoggerUtil.LogStructured(
            LogLevel.Success,
            LogCategory.Registration,
            " DEVICE READY: Registered + Approved + Config Available");



    }

    /// <summary>
    /// Initializes and validates the data directory.
    /// </summary>
    private static async Task<bool> InitializeDataDirectoryAsync()
    {
        if (_isDataDirectoryValid)
            return true; // Already validated

        try
        {
            bool success = await vitalschair_prod_v1.DeviceDataFileManager.InitializeDataDirectoryAsync();
            _isDataDirectoryValid = success;
            return success;
        }
        catch (Exception ex)
        {
            Log($"❌ Data directory initialization error: {ex.Message}");
            return false;
        }
    }

    // ── Check local registration file ─────────────────────────
    private static bool IsAlreadyRegistered()
    {
        try
        {
            // Use the new file manager for validation
            return vitalschair_prod_v1.DeviceDataFileManager.ValidateRegistrationFile();
        }
        catch { return false; }
    }

    // ── Register with exponential backoff ─────────────────────
    public static async Task RegisterWithRetryAsync()
    {
        if (string.IsNullOrWhiteSpace(RegisterUrl))
        {
            LoggerUtil.LogStructured(
    LogLevel.Error,
    LogCategory.Registration,
    "Cannot register device: RegisterUrl not initialized (call DeviceRegistration.Initialize first)",
    new Dictionary<string, object>
    {
        { "RegisterUrl", RegisterUrl ?? "null" }
    });
            return;
        }

        int maxRetries = 5;
        int delaySeconds = 10;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                LoggerUtil.LogStructured(
    LogLevel.Info,
    LogCategory.Registration,
    "Device Registration Attempt",
    new Dictionary<string, object>
    {
        { "Attempt", attempt },
        { "MaxRetries", maxRetries },
        { "RegisterUrl", RegisterUrl }
    });

                using var http = new HttpClient();
                http.Timeout = TimeSpan.FromSeconds(30);
                http.DefaultRequestHeaders.Add("X-Bootstrap-Token", BootstrapToken);

                var currentConfigVersion = GetCachedConfigVersion();
                string locationString = await GetLocationStringAsync();

                var payload = new
                {
                    device_id = DeviceIdentity.GetDeviceId(),
                    serial = DeviceIdentity.GetHardwareSerial(),
                    model = DeviceIdentity.MODEL_NUMBER,
                    firmware = DeviceIdentity.FIRMWARE_VERSION,
                    mac = DeviceIdentity.GetMACAddress(),
                    ip = DeviceIdentity.GetLocalIPAddress(),
                    location = locationString,
                    config_version = currentConfigVersion
                };

                string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
                {
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });

                LoggerUtil.LogStructured(
    LogLevel.Debug,
    LogCategory.Registration,
    "Registration Request",
    new Dictionary<string, object>
    {
        { "DeviceId", DeviceIdentity.GetDeviceId() },
        { "ConfigVersion", currentConfigVersion },
        { "Model", DeviceIdentity.MODEL_NUMBER }
    });

                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await http.PostAsync(RegisterUrl, content);
                string body = await response.Content.ReadAsStringAsync();

                LoggerUtil.LogStructured(
    LogLevel.Info,
    LogCategory.Registration,
    "Registration Response Received",
    new Dictionary<string, object>
    {
        { "HttpStatus", (int)response.StatusCode },
        { "ResponseSize", body.Length },
        { "RawBody", body.Length > 1000 ? body[..1000] + "..." : body }
    });

                if (response.IsSuccessStatusCode)
                {
                    TryParseAndSaveResponse(body);
                    _isRegistered = true;
                    Audit.Log("REGISTRATION", "success");
                    LoggerUtil.LogStructured(
    LogLevel.Success,
    LogCategory.Registration,
    "Device registered successfully");
                    return;
                }
                else if ((int)response.StatusCode == 401)
                {
                    LoggerUtil.LogStructured(
    LogLevel.Error,
    LogCategory.Auth,
    "Invalid bootstrap token");
                    return;
                }
                else if ((int)response.StatusCode == 400)
                {
                    LoggerUtil.LogStructured(
    LogLevel.Error,
    LogCategory.Registration,
    "Bad request — device_id missing or invalid");
                    return;
                }
                else
                {
                    LoggerUtil.LogStructured(
    LogLevel.Warning,
    LogCategory.API,
    "Registration server error",
    new Dictionary<string, object>
    {
        { "HttpStatus", (int)response.StatusCode },
        { "WillRetry", true }
    });
                }
            }
            catch (HttpRequestException ex)
            {
                LoggerUtil.LogStructured(
    LogLevel.Error,
    LogCategory.API,
    "HTTP request error",
    new Dictionary<string, object>
    {
        { "Message", ex.Message }
    });
            }
            catch (TaskCanceledException)
            {
                LoggerUtil.LogStructured(
    LogLevel.Warning,
    LogCategory.Network,
    "Registration request timeout",
    new Dictionary<string, object>
    {
        { "Attempt", attempt }
    });
            }
            catch (Exception ex)
            {
                LoggerUtil.LogStructured(
    LogLevel.Error,
    LogCategory.Registration,
    "Registration failed",
    new Dictionary<string, object>
    {
        { "Attempt", attempt },
        { "Error", ex.Message }
    });
            }

            if (attempt < maxRetries)
            {
                LoggerUtil.LogStructured(
    LogLevel.Info,
    LogCategory.Registration,
    "Retry scheduled",
    new Dictionary<string, object>
    {
        { "DelaySeconds", delaySeconds }
    });
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
                delaySeconds = Math.Min(delaySeconds * 2, 60);
            }
        }

        LoggerUtil.LogStructured(
    LogLevel.Critical,
    LogCategory.Registration,
    "Registration failed after maximum retries");
    }

    // ── Fetch config from server when cache is missing ────────

    // ── Check device status on server ─────────────────────────
    public static async Task<bool> CheckDeviceStatusAsync()
    {
        var jwt = GetSavedJwt();
        if (string.IsNullOrWhiteSpace(jwt))
        {
            LoggerUtil.LogStructured(
    LogLevel.Warning,
    LogCategory.Registration,
    "Cannot check device status: saved JWT is missing or invalid");
            return false;
        }

        if (string.IsNullOrWhiteSpace(StatusCheckUrl))
        {
            LoggerUtil.LogStructured(
    LogLevel.Warning,
    LogCategory.Registration,
    "Cannot check device status: StatusCheckUrl not initialized (call DeviceRegistration.Initialize first)",
    new Dictionary<string, object>
    {
        { "StatusCheckUrl", StatusCheckUrl ?? "null" }
    });
            return false;
        }

        try
        {
            LoggerUtil.LogStructured(
    LogLevel.Info,
    LogCategory.Registration,
    "📡 Checking device status with server",
    new Dictionary<string, object>
    {
        { "StatusCheckUrl", StatusCheckUrl },
        { "DeviceId", DeviceIdentity.GetDeviceId() }
    });

            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(15);

            var request = new HttpRequestMessage(HttpMethod.Get, StatusCheckUrl);
            request.Headers.Add("Authorization", $"Bearer {jwt}");
            // Send the CURRENT location on every check-in so the portal tracks a
            // moved device (cached hourly). The portal must read this and update
            // the device's location, not only the one-time registration value.
            string currentLocation = await GetLocationStringAsync();
            request.Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    device_id = DeviceIdentity.GetDeviceId(),
                    config_version = GetCachedConfigVersion(),
                    location = currentLocation
                }),
                Encoding.UTF8, "application/json");

            var response = await http.SendAsync(request);
            string body = await response.Content.ReadAsStringAsync();

            LoggerUtil.LogStructured(
    LogLevel.Info,
    LogCategory.Registration,
    "📥 Status check response received",
    new Dictionary<string, object>
    {
        { "StatusCode", (int)response.StatusCode },
        { "ResponseSize", body.Length },
        { "RawBody", body.Length > 1000 ? body[..1000] + "..." : body }
    });

            // ── Case D: 401 JWT rejected ────────────────────────
            if ((int)response.StatusCode == 401)
            {
                LoggerUtil.LogStructured(
    LogLevel.Warning,
    LogCategory.Registration,
                " JWT rejected by server — device may have been deleted from admin portal — wiping local data and re-registering");
                WipeLocalData();
                await RegisterWithRetryAsync();
                return File.Exists(ConfigCachePath);
            }

            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode == 404)
                {
                    LoggerUtil.LogStructured(
        LogLevel.Warning,
        LogCategory.Registration,
        "  Device not found on server (404) — admin may have deleted it — wiping local data and re-registering",
        new Dictionary<string, object>
        {
            { "ResponseBody", body }
                    });
                    WipeLocalData();
                    await RegisterWithRetryAsync();
                    return File.Exists(ConfigCachePath);
                }
                else
                {
                    LoggerUtil.LogStructured(
        LogLevel.Warning,
        LogCategory.Registration,
        "  Status check server error",
        new Dictionary<string, object>
        {
            { "StatusCode", (int)response.StatusCode },
                        { "ResponseBody", body }
                    });
                }
                return false;
            }

            // ── Parse response ──────────────────────────────────
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var dataEl))
            {
                LoggerUtil.LogStructured(
        LogLevel.Warning,
        LogCategory.Registration,
        "  Status check response missing 'data' field");
                return false;
            }

            // Extract status and config fields
            var status = dataEl.TryGetProperty("status", out var statusEl)
                ? statusEl.GetString()?.Trim().ToLowerInvariant()
                : null;

            var configMismatch = false;
            if (dataEl.TryGetProperty("app_config_mismatch", out var mismatchEl))
            {
                if (mismatchEl.ValueKind == JsonValueKind.True || mismatchEl.ValueKind == JsonValueKind.False)
                    configMismatch = mismatchEl.GetBoolean();
                else if (mismatchEl.ValueKind == JsonValueKind.String)
                    configMismatch = string.Equals(mismatchEl.GetString(), "true", StringComparison.OrdinalIgnoreCase);
            }

            var serverConfigVersion = dataEl.TryGetProperty("server_config_version", out var serverVerEl) &&
                serverVerEl.ValueKind == JsonValueKind.Number ? serverVerEl.GetInt32() : 0;
            var deviceConfigVersion = dataEl.TryGetProperty("device_config_version", out var deviceVerEl) &&
                deviceVerEl.ValueKind == JsonValueKind.Number ? deviceVerEl.GetInt32() : 0;
            int cachedConfigVersion = GetCachedConfigVersion();

            if (!configMismatch && serverConfigVersion > 0 && serverConfigVersion > cachedConfigVersion)
            {
                configMismatch = true;
                LoggerUtil.LogStructured(
                    LogLevel.Info,
                    LogCategory.Registration,
                    "Server config version is newer than local cache — treating as config mismatch",
                    new Dictionary<string, object>
                    {
                        { "ServerVersion", serverConfigVersion },
                        { "CachedVersion", cachedConfigVersion },
                        { "DeviceVersionFromServer", deviceConfigVersion }
                    });
            }

            Log($"📊 Status: {status} | Config Mismatch: {configMismatch} | Server v{serverConfigVersion} / Device v{deviceConfigVersion} / Cache v{cachedConfigVersion}");

            // ── Decision Tree: Device Status Cases ──────────────────────

            // ── CASE A: APPROVED + NO CONFIG MISMATCH ──────────────
            if (status == "approved" && !configMismatch)
            {
                // Special case: First approval - device has no config yet (version=0)
                // Must fetch config even if server reports no mismatch
                //if (deviceConfigVersion == 0 && !File.Exists(ConfigCachePath))
                if (!File.Exists(ConfigCachePath))
                {
                    Audit.Log("APPROVAL_CHANGE", "success", "system",
                        new Dictionary<string, object?> { ["status"] = "approved" });
                    LoggerUtil.LogStructured(
    LogLevel.Info,
    LogCategory.Registration,
    "🔄 CASE A (First Approval): Device APPROVED + needs initial config fetch",
    new Dictionary<string, object>
    {
        { "Status", status },
        { "DeviceVersion", deviceConfigVersion },
        { "Decision", "First approval after pending — re-registering to get config from registration response" }
    });
                    bool updated = await TryFetchAndSaveLatestConfigAsync(jwt, serverConfigVersion, "first-approval");
                    if (!updated)
                    {
                        await RegisterWithRetryAsync();
                        updated = File.Exists(ConfigCachePath);
                    }

                    Audit.Log("CONFIG_FETCH", updated ? "success" : "fail", "system",
                        new Dictionary<string, object?> { ["reason"] = "first-approval" });
                    return updated;
                }

                if (DateTime.UtcNow - _lastForcedConfigFetchUtc >= ForcedConfigFetchInterval)
                {
                    _lastForcedConfigFetchUtc = DateTime.UtcNow;
                    LoggerUtil.LogStructured(
                        LogLevel.Info,
                        LogCategory.Registration,
                        "Approved device periodic config refresh",
                        new Dictionary<string, object>
                        {
                            { "IntervalMinutes", ForcedConfigFetchInterval.TotalMinutes },
                            { "CachedVersion", cachedConfigVersion },
                            { "ServerVersion", serverConfigVersion }
                        });

                    bool refreshed = await TryFetchAndSaveLatestConfigAsync(jwt, serverConfigVersion, "periodic-approved-refresh");
                    if (refreshed)
                        return true;
                }

                LoggerUtil.LogStructured(
    LogLevel.Success,
    LogCategory.Registration,
    "✅ CASE A: Device APPROVED + Config up-to-date",
    new Dictionary<string, object>
    {
        { "Status", status },
        { "ConfigMismatch", configMismatch },
        { "ServerVersion", serverConfigVersion },
        { "DeviceVersion", deviceConfigVersion },
        { "Decision", "Device is ready — no action needed" }
                });
                return false;
            }

            // ── CASE B: APPROVED + CONFIG MISMATCH ──────────────────
            if (status == "approved" && configMismatch)
            {
                LoggerUtil.LogStructured(
    LogLevel.Info,
    LogCategory.Registration,
    "🔄 CASE B: Device APPROVED + Config mismatch detected",
    new Dictionary<string, object>
    {
        { "Status", status },
        { "ConfigMismatch", configMismatch },
        { "ServerVersion", serverConfigVersion },
        { "DeviceVersion", deviceConfigVersion },
        { "Decision", "Fetching updated config from config endpoint, with registration fallback" }
    });
                BackupConfig();
                LoggerUtil.LogStructured(
    LogLevel.Info,
    LogCategory.Registration,
    "  💾 Config backup created: config_cache.json.bak");

                bool updated = await TryFetchAndSaveLatestConfigAsync(jwt, serverConfigVersion, "mismatch-refresh");
                if (!updated)
                {
                    LoggerUtil.LogStructured(
        LogLevel.Warning,
        LogCategory.Registration,
        "  ⚠️ Direct config fetch failed — trying registration fallback before restoring backup");
                    await RegisterWithRetryAsync();
                    updated = File.Exists(ConfigCachePath) && GetCachedConfigVersion() >= serverConfigVersion;
                }

                if (!updated)
                {
                    LoggerUtil.LogStructured(
        LogLevel.Warning,
        LogCategory.Registration,
        "  ⚠️ Config fetch failed — restoring from backup",
        new Dictionary<string, object>
        {
            { "Decision", "Continuing with previous config version" }
        });
                    RestoreConfigFromBackup();
                    Audit.Log("CONFIG_FETCH", "fail", "system",
                        new Dictionary<string, object?> { ["reason"] = "mismatch-refresh", ["restored_backup"] = true });
                }
                else
                {
                    LoggerUtil.LogStructured(
        LogLevel.Success,
        LogCategory.Registration,
        "  ✅ Config updated successfully",
        new Dictionary<string, object>
        {
            { "NewVersion", serverConfigVersion }
        });
                    Audit.Log("CONFIG_FETCH", "success", "system",
                        new Dictionary<string, object?> { ["reason"] = "mismatch-refresh", ["new_version"] = serverConfigVersion });
                }
                return updated;
            }

            // ── CASE C: PENDING APPROVAL ────────────────────────────
            if (status == "pending")
            {
                LoggerUtil.LogStructured(
    LogLevel.Warning,
    LogCategory.Registration,
    "⏳ CASE C: Device PENDING admin approval",
    new Dictionary<string, object>
    {
        { "Status", status },
        { "Decision", "Waiting for admin approval — will retry later" },
        { "NextRetrySeconds", "30" }
                });
                return false;
            }

            // ── CASE D: DELETED FROM ADMIN PORTAL ───────────────────
            if (status == "deleted")
            {
                Audit.Log("APPROVAL_CHANGE", "info", "system",
                    new Dictionary<string, object?> { ["status"] = "deleted", ["action"] = "wipe-and-reregister" });
                LoggerUtil.LogStructured(
    LogLevel.Warning,
    LogCategory.Registration,
    "🗑️ CASE D: Device DELETED from admin portal",
    new Dictionary<string, object>
    {
        { "Status", status },
        { "Decision", "Auto-recovery: wiping local data and re-registering for admin to re-add it" }
    });
                WipeLocalData();
                await RegisterWithRetryAsync();
                return File.Exists(ConfigCachePath);
            }

            // ── CASE E: SUSPENDED ───────────────────────────────────
            if (status == "suspended")
            {
                LoggerUtil.LogStructured(
    LogLevel.Error,
    LogCategory.Registration,
    "🚫 CASE E: Device SUSPENDED by admin",
    new Dictionary<string, object>
    {
        { "Status", status },
        { "Decision", "Device suspended — cannot proceed until admin unsuspends it" }
    });
                WipeLocalData();
                await RegisterWithRetryAsync();
                return File.Exists(ConfigCachePath);
            }

            // ── CASE F: UNKNOWN STATUS ──────────────────────────────
            LoggerUtil.LogStructured(
        LogLevel.Warning,
        LogCategory.Registration,
        $"❓ CASE F: Unknown device status '{status}'",
        new Dictionary<string, object>
        {
            { "Status", status ?? "null" },
            { "Decision", "Continuing with cached config — will retry on next check" }
        });
        }
        catch (HttpRequestException ex)
        {
            LoggerUtil.LogStructured(
                LogLevel.Warning,
                LogCategory.Registration,
                "🌐 Status check: No internet connection",
                new Dictionary<string, object>
                {
                    { "Error", ex.Message },
                    { "Decision", "Will use cached config and retry when internet available" }
                });
            return false;
        }
        catch (TaskCanceledException)
        {
            LoggerUtil.LogStructured(
                LogLevel.Warning,
                LogCategory.Registration,
                "⏱️ Status check timeout",
                new Dictionary<string, object>
                {
                    { "TimeoutSeconds", 15 },
                    { "Decision", "Will retry on next check — continuing with cached config" }
                });
            return false;
        }
        catch (Exception ex)
        {
            LoggerUtil.LogStructured(
                LogLevel.Warning,
                LogCategory.Registration,
                "❌ Status check error",
                new Dictionary<string, object>
                {
                    { "Error", ex.Message },
                    { "ExceptionType", ex.GetType().Name },
                    { "Decision", "Will retry on next check — continuing with cached config" }
                });
            return false;
        }

        return false;
    }

    private static async Task<bool> TryFetchAndSaveLatestConfigAsync(string jwt, int expectedServerVersion, string reason)
    {
        if (string.IsNullOrWhiteSpace(ConfigFetchUrl))
        {
            LoggerUtil.LogStructured(
                LogLevel.Warning,
                LogCategory.Registration,
                "Cannot fetch config: ConfigFetchUrl is not available",
                new Dictionary<string, object>
                {
                    { "StatusCheckUrl", StatusCheckUrl ?? "null" }
                });
            return false;
        }

        int previousVersion = GetCachedConfigVersion();

        try
        {
            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(20);

            string deviceId = DeviceIdentity.GetDeviceId();
            string fetchUrlWithQuery =
                $"{ConfigFetchUrl}?device_id={Uri.EscapeDataString(deviceId)}&config_version={previousVersion}";

            LoggerUtil.LogStructured(
                LogLevel.Info,
                LogCategory.Registration,
                "📦 Fetching latest config from portal",
                new Dictionary<string, object>
                {
                    { "Reason", reason },
                    { "FetchUrl", ConfigFetchUrl },
                    { "CurrentVersion", previousVersion },
                    { "ExpectedServerVersion", expectedServerVersion }
                });

            var getRequest = new HttpRequestMessage(HttpMethod.Get, fetchUrlWithQuery);
            getRequest.Headers.Add("Authorization", $"Bearer {jwt}");

            var response = await http.SendAsync(getRequest);
            string body = await response.Content.ReadAsStringAsync();

            if ((int)response.StatusCode == 405 || (int)response.StatusCode == 400)
            {
                LoggerUtil.LogStructured(
                    LogLevel.Info,
                    LogCategory.Registration,
                    "Config fetch GET was not accepted — retrying with POST",
                    new Dictionary<string, object>
                    {
                        { "StatusCode", (int)response.StatusCode }
                    });

                var postRequest = new HttpRequestMessage(HttpMethod.Post, ConfigFetchUrl);
                postRequest.Headers.Add("Authorization", $"Bearer {jwt}");
                postRequest.Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        device_id = deviceId,
                        config_version = previousVersion
                    }),
                    Encoding.UTF8,
                    "application/json");

                response = await http.SendAsync(postRequest);
                body = await response.Content.ReadAsStringAsync();
            }

            LoggerUtil.LogStructured(
                LogLevel.Info,
                LogCategory.Registration,
                "📥 Config fetch response received",
                new Dictionary<string, object>
                {
                    { "StatusCode", (int)response.StatusCode },
                    { "ResponseSize", body.Length },
                    { "RawBody", body.Length > 1000 ? body[..1000] + "..." : body }
                });

            if (!response.IsSuccessStatusCode)
            {
                LoggerUtil.LogStructured(
                    LogLevel.Warning,
                    LogCategory.Registration,
                    "Config fetch failed — keeping previous config cache",
                    new Dictionary<string, object>
                    {
                        { "StatusCode", (int)response.StatusCode },
                        { "PreviousVersion", previousVersion }
                    });
                return false;
            }

            if (!TryParseAndSaveResponse(body))
            {
                LoggerUtil.LogStructured(
                    LogLevel.Warning,
                    LogCategory.Registration,
                    "Config fetch payload was not usable — keeping previous config cache",
                    new Dictionary<string, object>
                    {
                        { "PreviousVersion", previousVersion },
                        { "ExpectedServerVersion", expectedServerVersion }
                    });
                RestoreConfigFromBackup();
                return false;
            }

            int newVersion = GetCachedConfigVersion();
            bool versionOk = expectedServerVersion <= 0 || newVersion >= expectedServerVersion || newVersion > previousVersion;

            if (!versionOk)
            {
                LoggerUtil.LogStructured(
                    LogLevel.Warning,
                    LogCategory.Registration,
                    "Config fetch saved cache but version did not advance as expected — restoring previous cache",
                    new Dictionary<string, object>
                    {
                        { "PreviousVersion", previousVersion },
                        { "NewVersion", newVersion },
                        { "ExpectedServerVersion", expectedServerVersion }
                    });
                RestoreConfigFromBackup();
                return false;
            }

            LoggerUtil.LogStructured(
                LogLevel.Success,
                LogCategory.Registration,
                "Config fetch saved latest config cache",
                new Dictionary<string, object>
                {
                    { "PreviousVersion", previousVersion },
                    { "NewVersion", newVersion }
                });

            _lastForcedConfigFetchUtc = DateTime.UtcNow;
            return true;
        }
        catch (Exception ex)
        {
            LoggerUtil.LogStructured(
                LogLevel.Warning,
                LogCategory.Registration,
                "Config fetch error — keeping previous config cache",
                new Dictionary<string, object>
                {
                    { "Error", ex.Message },
                    { "ExceptionType", ex.GetType().Name },
                    { "PreviousVersion", previousVersion }
                });
            RestoreConfigFromBackup();
            return false;
        }
    }

    // ── Parse and save response to /data/ ─────────────────────
    private static bool TryParseAndSaveResponse(string responseBody)
    {
        try
        {
            var doc = JsonDocument.Parse(responseBody);
            var dataElement = doc.RootElement.GetProperty("data");
            var data = NormalizeJsonElement(dataElement);

            if (data.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Response 'data' must be a JSON object.");

            // ── Dictionaries for config sections ───────────────
            var bluDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var voiceDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // ── Save JWT → /data/registration.json ────────────
            if (data.TryGetProperty("device_id", out var deviceIdEl) &&
                deviceIdEl.ValueKind == JsonValueKind.String &&
                data.TryGetProperty("jwt", out var jwtEl) &&
                jwtEl.ValueKind == JsonValueKind.String)
            {
                var registration = new
                {
                    device_id = deviceIdEl.GetString(),
                    jwt = jwtEl.GetString(),
                    registered_at = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
                };

                bool regWriteSuccess = vitalschair_prod_v1.DeviceDataFileManager.WriteJsonFile(
                    RegistrationPath,
                    JsonSerializer.Serialize(registration,
                        new JsonSerializerOptions { WriteIndented = true }),
                    createBackup: true
                );

                if (!regWriteSuccess)
                {
                    Log($"❌ Failed to write registration file: {RegistrationPath}");
                    return false;
                }

                Log($"💾 JWT saved to {RegistrationPath}");
            }
            else
            {
                LoggerUtil.LogStructured(
                    LogLevel.Info,
                    LogCategory.Registration,
                    "ℹ️ No registration payload in response — skipping registration save");
            }

            // ── Skip config save if approval is pending ────────
            var approvalStatus = data.TryGetProperty("approval_status", out var approvalEl)
                ? approvalEl.GetString()?.Trim().ToLowerInvariant()
                : null;

            if (approvalStatus == "pending")
            {
                LoggerUtil.LogStructured(
                    LogLevel.Info,
                    LogCategory.Registration,
                    "ℹ️ Approval pending — skipping config cache save");
                return true; // Return true since this is expected
            }

            // ══════════════════════════════════════════════════
            // CONFIG PARSING — priority order:
            //   1. data.bluhealth_config (fetchconfig endpoint)
            //   2. data.fetchconfig.bluhealth_config (register endpoint, approved)
            //   3. data.BluHealthApi direct object (admin JSON push)
            //   4. data/fetchconfig urls[] array (legacy format)
            // ══════════════════════════════════════════════════

            int configVersion = 0;
            var configSource = data;
            bool skipUrlsParsing = false;

            // ── Priority 1: bluhealth_config directly under data ──
            // This is what /device/config/fetch returns
            if (data.TryGetProperty("bluhealth_config", out var bluhealthConfigDirect) &&
                bluhealthConfigDirect.ValueKind == JsonValueKind.Object)
            {
                ExtractFromBluhealthConfig(bluhealthConfigDirect, bluDict, voiceDict, ref configVersion);
                Log(" Config loaded from data.bluhealth_config (fetchconfig endpoint)");
                skipUrlsParsing = true;
            }
            // ── Priority 2: fetchconfig wrapper (register endpoint) ──
            else if (data.TryGetProperty("fetchconfig", out var fetchConfigEl))
            {
                if (fetchConfigEl.ValueKind == JsonValueKind.String &&
                    string.IsNullOrWhiteSpace(fetchConfigEl.GetString()))
                {
                    Log("ℹ️ fetchconfig is empty string — skipping config parse");
                    skipUrlsParsing = true;
                }
                else
                {
                    configSource = NormalizeJsonElement(fetchConfigEl);

                    if (configSource.TryGetProperty("bluhealth_config", out var bluhealthConfigNested) &&
                        bluhealthConfigNested.ValueKind == JsonValueKind.Object)
                    {
                        ExtractFromBluhealthConfig(bluhealthConfigNested, bluDict, voiceDict, ref configVersion);
                        Log(" Config loaded from fetchconfig.bluhealth_config (register endpoint)");
                        skipUrlsParsing = true;
                    }
                }
            }

            // ── Priority 3: direct object under data (admin push) ──
            if (bluDict.Count == 0 &&
                data.TryGetProperty("BluHealthApi", out var directBluObj) &&
                directBluObj.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in directBluObj.EnumerateObject())
                    bluDict[prop.Name] = prop.Value.GetString() ?? string.Empty;
                Log(" BluHealthApi loaded from direct object format");
            }

            if (voiceDict.Count == 0 &&
                data.TryGetProperty("VoiceAssistant", out var directVoiceObj) &&
                directVoiceObj.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in directVoiceObj.EnumerateObject())
                    voiceDict[prop.Name] = prop.Value.GetString() ?? string.Empty;
                Log(" VoiceAssistant loaded from direct object format");
            }

            // ── Priority 4: urls[] array (legacy format) ──────
            if (!skipUrlsParsing &&
                configSource.TryGetProperty("urls", out var urls) &&
                urls.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in urls.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    if (!item.TryGetProperty("name", out var nameEl) ||
                        !item.TryGetProperty("url", out var valueEl) ||
                        !item.TryGetProperty("category", out var categoryEl)) continue;

                    var name = nameEl.GetString() ?? string.Empty;
                    var value = valueEl.GetString() ?? string.Empty;
                    var category = categoryEl.GetString()?.Trim().ToLowerInvariant() ?? string.Empty;

                    switch (category)
                    {
                        case "bluhealth_api": bluDict[name] = value; break;
                        case "voice_assistant": voiceDict[name] = value; break;
                    }
                }

                Log(" Config loaded from urls[] array (legacy format)");
            }

            // ── config_version fallback ────────────────────────
            if (configVersion == 0)
            {
                if (data.TryGetProperty("config_version", out var cv) && cv.ValueKind == JsonValueKind.Number)
                    configVersion = cv.GetInt32();
                else if (configSource.TryGetProperty("config_version", out var cv2) && cv2.ValueKind == JsonValueKind.Number)
                    configVersion = cv2.GetInt32();
            }

            // ── Read HL7, feature_flags, thresholds from data ──
            // Always from top-level data regardless of config source
            var hl7Config = data.TryGetProperty("hl7_config", out var hl7El) ? NormalizeJsonElement(hl7El) : default;
            var featureFlags = data.TryGetProperty("feature_flags", out var ff) ? NormalizeJsonElement(ff) : default;
            var thresholds = data.TryGetProperty("thresholds", out var th) ? NormalizeJsonElement(th) : default;
            var aiModels = data.TryGetProperty("ai_models", out var aiModelsEl) ? NormalizeJsonElement(aiModelsEl) : default;

            // ── Build HL7 object ───────────────────────────────
            object hl7Object = new { };
            if (hl7Config.ValueKind == JsonValueKind.Object)
            {
                bool enabled = false;
                string host = string.Empty;
                int port = 0;

                if (hl7Config.TryGetProperty("enabled", out var enabledEl))
                {
                    if (enabledEl.ValueKind == JsonValueKind.True || enabledEl.ValueKind == JsonValueKind.False)
                        enabled = enabledEl.GetBoolean();
                    else
                        enabled = string.Equals(enabledEl.GetString(), "true", StringComparison.OrdinalIgnoreCase);
                }

                if (hl7Config.TryGetProperty("host", out var hostEl))
                    host = hostEl.GetString() ?? string.Empty;
                if (hl7Config.TryGetProperty("port", out var portEl) && portEl.ValueKind == JsonValueKind.Number)
                    port = portEl.GetInt32();
                else if (hl7Config.TryGetProperty("port", out var portStr))
                    int.TryParse(portStr.GetString(), out port);

                hl7Object = new { Enabled = enabled, HisHost = host, HisPort = port };
            }

            // ── Save config_cache.json ─────────────────────────
            if (bluDict.Count == 0)
            {
                LoggerUtil.LogStructured(
                    LogLevel.Warning,
                    LogCategory.Registration,
                    "Config payload did not contain BluHealthApi endpoints — refusing to overwrite cached config",
                    new Dictionary<string, object>
                    {
                        { "ConfigVersion", configVersion },
                        { "HasExistingConfig", File.Exists(ConfigCachePath) }
                    });
                return false;
            }

            var configCache = new
            {
                config_version = configVersion,
                device_id = DeviceIdentity.GetDeviceId(),
                BluHealthApi = bluDict.Count > 0 ? (object)bluDict : new { },
                VoiceAssistant = voiceDict.Count > 0 ? (object)voiceDict : new { },
                HL7 = hl7Object,
                feature_flags = featureFlags.ValueKind != JsonValueKind.Undefined ? (object)featureFlags : new { },
                thresholds = thresholds.ValueKind != JsonValueKind.Undefined ? (object)thresholds : new { },
                ai_models = aiModels.ValueKind != JsonValueKind.Undefined ? (object)aiModels : new { },
                fetched_at = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
            };

            bool configWriteSuccess = vitalschair_prod_v1.DeviceDataFileManager.WriteJsonFile(
                ConfigCachePath,
                JsonSerializer.Serialize(configCache,
                    new JsonSerializerOptions { WriteIndented = true }),
                createBackup: true
            );

            if (!configWriteSuccess)
            {
                Log($"❌ Failed to write config cache file: {ConfigCachePath}");
                return false;
            }

            Log($" Config cache saved to {ConfigCachePath}");
            Log($" BluHealthApi keys saved: {bluDict.Count} | VoiceAssistant keys saved: {voiceDict.Count}");
            return true;
        }
        catch (Exception ex)
        {
            Log($"❌ Failed to save registration/config data: {ex.Message}");
            return false;
        }
    }

    // ── Helper: extract from bluhealth_config block ───────────
    private static void ExtractFromBluhealthConfig(
        JsonElement bluhealthConfig,
        Dictionary<string, string> bluDict,
        Dictionary<string, string> voiceDict,
        ref int configVersion)
    {
        if (bluhealthConfig.TryGetProperty("BluHealthApi", out var wrapperBluObj) &&
            wrapperBluObj.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in wrapperBluObj.EnumerateObject())
                bluDict[prop.Name] = prop.Value.GetString() ?? string.Empty;
        }

        if (bluhealthConfig.TryGetProperty("VoiceAssistant", out var wrapperVoiceObj) &&
            wrapperVoiceObj.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in wrapperVoiceObj.EnumerateObject())
                voiceDict[prop.Name] = prop.Value.GetString() ?? string.Empty;
        }

        if (bluhealthConfig.TryGetProperty("config_version", out var cv))
        {
            if (cv.ValueKind == JsonValueKind.Number)
                configVersion = cv.GetInt32();
            else if (cv.ValueKind == JsonValueKind.String && int.TryParse(cv.GetString(), out var parsed))
                configVersion = parsed;
        }
    }

    // ── Helper: atomic file write (power-cut safe) ────────────
    private static void AtomicWrite(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }

    // ── Helper: wipe local registration and config ────────────
    private static void WipeLocalData()
    {
        try
        {
            if (File.Exists(RegistrationPath)) File.Delete(RegistrationPath);
            if (File.Exists(ConfigCachePath)) File.Delete(ConfigCachePath);
            if (File.Exists(ConfigCachePath + ".bak")) File.Delete(ConfigCachePath + ".bak");
            _isRegistered = false;
            Log(" Local device data cleared");
        }
        catch (Exception ex)
        {
            Log($"  Error clearing device data: {ex.Message}");
        }
    }

    // ── Helper: backup config before fetching ──────────────────
    private static void BackupConfig()
    {
        try
        {
            if (File.Exists(ConfigCachePath))
                File.Copy(ConfigCachePath, ConfigCachePath + ".bak", overwrite: true);
        }
        catch (Exception ex)
        {
            Log($"  Error creating config backup: {ex.Message}");
        }
    }

    // ── Helper: restore config from backup ──────────────────────
    private static void RestoreConfigFromBackup()
    {
        try
        {
            if (File.Exists(ConfigCachePath + ".bak"))
            {
                File.Copy(ConfigCachePath + ".bak", ConfigCachePath, overwrite: true);
                Log(" Config restored from backup");
            }
        }
        catch (Exception ex)
        {
            LoggerUtil.LogStructured(
                LogLevel.Error,
                LogCategory.Registration,
                $" Error restoring config from backup: {ex.Message}");
        }
    }

    private static string BuildConfigFetchUrl(string statusCheckUrl)
    {
        if (string.IsNullOrWhiteSpace(statusCheckUrl))
            return string.Empty;

        if (statusCheckUrl.Contains("/device/status-check", StringComparison.OrdinalIgnoreCase))
            return statusCheckUrl.Replace("/device/status-check", "/device/config/fetch", StringComparison.OrdinalIgnoreCase);

        if (statusCheckUrl.Contains("status-check", StringComparison.OrdinalIgnoreCase))
            return statusCheckUrl.Replace("status-check", "config/fetch", StringComparison.OrdinalIgnoreCase);

        try
        {
            var uri = new Uri(statusCheckUrl);
            var baseUri = uri.GetLeftPart(UriPartial.Authority);
            return $"{baseUri}/api/vitalchair-admin/device/config/fetch";
        }
        catch
        {
            return string.Empty;
        }
    }

    // ── Helper: get device_id from registration.json ──────────
    private static string GetDeviceIdFromRegistration()
    {
        try
        {
            if (File.Exists(RegistrationPath))
            {
                var doc = JsonDocument.Parse(File.ReadAllText(RegistrationPath));
                if (doc.RootElement.TryGetProperty("device_id", out var deviceIdEl))
                    return deviceIdEl.GetString() ?? DeviceIdentity.GetDeviceId();
            }
        }
        catch { }
        return DeviceIdentity.GetDeviceId();
    }

    // ── Helper: normalize JSON element ────────────────────────
    private static JsonElement NormalizeJsonElement(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var raw = element.GetString();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                try
                {
                    using var nested = JsonDocument.Parse(raw);
                    return nested.RootElement.Clone();
                }
                catch { }
            }
        }
        return element;
    }

    // ── Fetch location from ip-api.com ────────────────────────
    // Cached current location string, refreshed at most hourly. Used by BOTH
    // registration and the periodic status-check so a device that is physically
    // MOVED updates its portal location within the hour — previously location
    // was captured only once at registration, so a relocated device kept showing
    // its original site forever.
    private static string   _cachedLocationString = "Unknown";
    private static DateTime _lastLocationFetchUtc = DateTime.MinValue;
    private static readonly TimeSpan LocationRefreshInterval = TimeSpan.FromMinutes(60);

    public static async Task<string> GetLocationStringAsync()
    {
        if (_cachedLocationString != "Unknown"
            && DateTime.UtcNow - _lastLocationFetchUtc < LocationRefreshInterval)
            return _cachedLocationString;

        var (lat, lon, city, country) = await FetchLocationAsync();
        if (!string.IsNullOrEmpty(city) && !string.IsNullOrEmpty(country))
        {
            _cachedLocationString = $"{city}, {country} ({lat}, {lon})";
            _lastLocationFetchUtc = DateTime.UtcNow;
        }
        // On failure keep the last known value rather than regressing to Unknown.
        return _cachedLocationString;
    }

    private static async Task<(double lat, double lon, string city, string country)> FetchLocationAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await http.GetAsync("http://ip-api.com/json/");
            if (!response.IsSuccessStatusCode) return (0, 0, string.Empty, string.Empty);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var lat = root.TryGetProperty("lat", out var latEl) && latEl.ValueKind == JsonValueKind.Number ? latEl.GetDouble() : 0.0;
            var lon = root.TryGetProperty("lon", out var lonEl) && lonEl.ValueKind == JsonValueKind.Number ? lonEl.GetDouble() : 0.0;
            var city = root.TryGetProperty("city", out var cityEl) ? cityEl.GetString() ?? string.Empty : string.Empty;
            var country = root.TryGetProperty("country", out var countryEl) ? countryEl.GetString() ?? string.Empty : string.Empty;

            return (lat, lon, city, country);
        }
        catch
        {
            return (0, 0, string.Empty, string.Empty);
        }
    }

    // ── Read cached config version ─────────────────────────────
    private static int GetCachedConfigVersion()
    {
        try
        {
            if (!File.Exists(ConfigCachePath)) return 0;
            var doc = JsonDocument.Parse(File.ReadAllText(ConfigCachePath));
            if (doc.RootElement.TryGetProperty("config_version", out var cv))
            {
                if (cv.ValueKind == JsonValueKind.Number) return cv.GetInt32();
                if (cv.ValueKind == JsonValueKind.String && int.TryParse(cv.GetString(), out var v)) return v;
            }
        }
        catch { }
        return 0;
    }

    // ── Public getters ─────────────────────────────────────────
    public static string? GetSavedJwt()
    {
        try
        {
            if (!File.Exists(RegistrationPath)) return null;
            var doc = JsonDocument.Parse(File.ReadAllText(RegistrationPath));
            return doc.RootElement.GetProperty("jwt").GetString();
        }
        catch { return null; }
    }

    public static bool IsRegistered() => _isRegistered;

    public static string? GetDeviceIdFromCache()
    {
        try
        {
            if (File.Exists(ConfigCachePath))
            {
                var doc = JsonDocument.Parse(File.ReadAllText(ConfigCachePath));
                if (doc.RootElement.TryGetProperty("device_id", out var deviceIdEl))
                    return deviceIdEl.GetString();
            }
        }
        catch { }
        return null;
    }

    private static void Log(string msg) =>
        Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {msg}");
}

// Last Edited 
