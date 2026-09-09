using System.Text.Json;

public class BootstrapUrls
{
    public string RegisterUrl { get; set; }
    public string StatusCheckUrl { get; set; }
    public int Version { get; set; }
}

public static class BootstrapConfigProvider
{
    private const string FirebaseUrl =
        "https://vitalschair-v1-1ea0f-default-rtdb.firebaseio.com/bootstrapConfig.json";

    private const string CachePath = "/data/bootstrap_config_cache.json";

    // Today's known-good URLs — used only if Firebase AND the cache both fail
    private static readonly BootstrapUrls HardcodedFallback = new BootstrapUrls
    {
        RegisterUrl = "https://vitalchairapi.bluai.ai/api/vitalchair-admin/device/register",
        StatusCheckUrl = "https://vitalchairapi.bluai.ai/api/vitalchair-admin/device/status-check",
        Version = 0
    };

    public static async Task<BootstrapUrls> ResolveAsync(HttpClient http)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        string source = "Unknown";
        BootstrapUrls? result = null;

        try
        {
            // ── Attempt 1: Firebase ────────────────────────────────────
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var resp = await http.GetAsync(FirebaseUrl, cts.Token);

                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync();
                    var urls = JsonSerializer.Deserialize<BootstrapUrls>(json, JsonOptions);

                    // ← BUG FIX: Explicitly log when response is success but RegisterUrl is empty
                    if (urls == null || string.IsNullOrWhiteSpace(urls.RegisterUrl))
                    {
                        LoggerUtil.LogStructured(
                            LogLevel.Warning,
                            LogCategory.Registration,
                            "Firebase response success but BootstrapUrls.RegisterUrl is empty or null",
                            new Dictionary<string, object>
                            {
                                { "HttpStatus", (int)resp.StatusCode },
                                { "RawResponseBody", json.Length > 500 ? json[..500] + "..." : json },
                                { "DeserializedUrls", urls == null ? "null" : $"RegisterUrl={urls.RegisterUrl}" }
                            }
                        );
                    }
                    else
                    {
                        // Success path
                        await File.WriteAllTextAsync(CachePath, json);
                        LoggerUtil.LogStructured(
                            LogLevel.Info,
                            LogCategory.Registration,
                            "Firebase fetch succeeded, URLs cached",
                            new Dictionary<string, object>
                            {
                                { "HttpStatus", (int)resp.StatusCode },
                                { "Version", urls.Version }
                            }
                        );
                        result = urls;
                        source = "Firebase";
                    }
                }
                else
                {
                    // Non-success HTTP response
                    LoggerUtil.LogStructured(
                        LogLevel.Warning,
                        LogCategory.Registration,
                        "Firebase fetch returned non-success HTTP status",
                        new Dictionary<string, object>
                        {
                            { "HttpStatus", (int)resp.StatusCode },
                            { "ReasonPhrase", resp.ReasonPhrase ?? "Unknown" }
                        }
                    );
                }
            }
            catch (OperationCanceledException)
            {
                LoggerUtil.LogStructured(
                    LogLevel.Warning,
                    LogCategory.Registration,
                    "Firebase fetch timed out",
                    new Dictionary<string, object>
                    {
                        { "ExceptionType", nameof(OperationCanceledException) },
                        { "TimeoutSeconds", 5 }
                    }
                );
            }
            catch (HttpRequestException ex)
            {
                LoggerUtil.LogStructured(
                    LogLevel.Warning,
                    LogCategory.Network,
                    "Firebase fetch network error (DNS, TLS, connection failed)",
                    new Dictionary<string, object>
                    {
                        { "ExceptionType", nameof(HttpRequestException) },
                        { "Message", ex.Message }
                    }
                );
            }
            catch (Exception ex)
            {
                LoggerUtil.LogStructured(
                    LogLevel.Warning,
                    LogCategory.Registration,
                    "Firebase fetch unexpected exception",
                    new Dictionary<string, object>
                    {
                        { "ExceptionType", ex.GetType().Name },
                        { "Message", ex.Message }
                    }
                );
            }

            // ── If Firebase succeeded, we're done ────────────────────
            if (result != null)
            {
                stopwatch.Stop();
                LoggerUtil.LogStructured(
                    LogLevel.Info,
                    LogCategory.Registration,
                    "Bootstrap resolved",
                    new Dictionary<string, object>
                    {
                        { "Source", source },
                        { "Version", result.Version },
                        { "ElapsedMs", stopwatch.ElapsedMilliseconds }
                    }
                );
                return result;
            }

            // ── Attempt 2: Cache fallback ──────────────────────────────
            if (File.Exists(CachePath))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(CachePath);
                    var cached = JsonSerializer.Deserialize<BootstrapUrls>(json, JsonOptions);

                    if (cached != null && !string.IsNullOrWhiteSpace(cached.RegisterUrl))
                    {
                        LoggerUtil.LogStructured(
                            LogLevel.Info,
                            LogCategory.Registration,
                            "Cache fallback: successfully deserialized bootstrap URLs",
                            new Dictionary<string, object>
                            {
                                { "CachePath", CachePath },
                                { "Version", cached.Version }
                            }
                        );
                        result = cached;
                        source = "Cache";
                    }
                    else
                    {
                        LoggerUtil.LogStructured(
                            LogLevel.Warning,
                            LogCategory.Registration,
                            "Cache fallback: deserialized but RegisterUrl is empty/null",
                            new Dictionary<string, object>
                            {
                                { "CachePath", CachePath }
                            }
                        );
                    }
                }
                catch (Exception ex)
                {
                    LoggerUtil.LogStructured(
                        LogLevel.Warning,
                        LogCategory.Registration,
                        "Cache fallback: failed to parse cached bootstrap URLs",
                        new Dictionary<string, object>
                        {
                            { "CachePath", CachePath },
                            { "ExceptionType", ex.GetType().Name },
                            { "Message", ex.Message }
                        }
                    );
                }
            }
            else
            {
                LoggerUtil.LogStructured(
                    LogLevel.Warning,
                    LogCategory.Registration,
                    "Cache fallback: cache file does not exist",
                    new Dictionary<string, object>
                    {
                        { "CachePath", CachePath }
                    }
                );
            }

            // ── If cache succeeded, we're done ─────────────────────────
            if (result != null)
            {
                stopwatch.Stop();
                LoggerUtil.LogStructured(
                    LogLevel.Info,
                    LogCategory.Registration,
                    "Bootstrap resolved",
                    new Dictionary<string, object>
                    {
                        { "Source", source },
                        { "Version", result.Version },
                        { "ElapsedMs", stopwatch.ElapsedMilliseconds }
                    }
                );
                return result;
            }

            // ── Attempt 3: Hardcoded fallback ──────────────────────────
            LoggerUtil.LogStructured(
                LogLevel.Warning,
                LogCategory.Registration,
                "All dynamic sources failed, using hardcoded fallback bootstrap URLs"
            );
            result = HardcodedFallback;
            source = "Hardcoded";
        }
        finally
        {
            // ── Final summary log (always emitted) ──────────────────────
            if (result != null && !stopwatch.IsRunning)
            {
                LoggerUtil.LogStructured(
                    LogLevel.Info,
                    LogCategory.Registration,
                    "Bootstrap resolved",
                    new Dictionary<string, object>
                    {
                        { "Source", source },
                        { "Version", result.Version },
                        { "ElapsedMs", stopwatch.ElapsedMilliseconds }
                    }
                );
            }
            else if (result == null)
            {
                result = HardcodedFallback;
                if (stopwatch.IsRunning) stopwatch.Stop();
                LoggerUtil.LogStructured(
                    LogLevel.Info,
                    LogCategory.Registration,
                    "Bootstrap resolved",
                    new Dictionary<string, object>
                    {
                        { "Source", "Hardcoded" },
                        { "Version", HardcodedFallback.Version },
                        { "ElapsedMs", stopwatch.ElapsedMilliseconds }
                    }
                );
            }
        }

        return result ?? HardcodedFallback;
    }
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };



}