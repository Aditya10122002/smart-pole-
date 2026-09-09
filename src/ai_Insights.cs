using System;
using System.Text.Json;
using System.Threading.Tasks;

public partial class VitalsChairApp
{
    // ========== AI INSIGHTS CACHING (SYNCHRONIZED) ==========
    private static string _cachedAiInsights = "";
    private static readonly object _aiInsightsLock = new object();  // Single unified lock for all AI insights operations
    private static bool _aiInsightsReady = false;

    public static string GetInsightsStatus()
    {
        // Check if user is authenticated (not in offline mode)
        if (!_isAuthenticated)
        {
            return "INSIGHTS_OFFLINE";
        }

        lock (_aiInsightsLock)
        {
            if (_aiInsightsReady && !string.IsNullOrEmpty(_cachedAiInsights) && _cachedAiInsights != "GENERATING")
                return "INSIGHTS_READY";
            return "INSIGHTS_GENERATING";
        }
    }

    public static async Task<string> GetAzureInsights()
    {
        // Check if user is authenticated (not in offline mode)
        if (!_isAuthenticated)
        {
            Log($"📱 [Offline Mode] AI insights not available - please login to view better insights");
            return "⚠️ Please login to view better insights\n\nOffline mode: Data is being collected locally. Login to get personalized AI-powered health insights.";
        }

        string cached;
        lock (_aiInsightsLock)
        {
            cached = _cachedAiInsights;
        }

        if (!string.IsNullOrEmpty(cached) && cached != "GENERATING")
        {
            Log($"✅ Returning cached AI insights from VitalsUrl API response ({cached.Length} chars)");
            return cached;
        }

        Log("⚠️ No AI insights cached yet - insights will be available after VitalsUrl API response is received");
        return string.Empty;
    }

    /// <summary>
    /// Sets the cached AI insights and marks them as ready (synchronized).
    /// Called by Program.cs when VitalsUrl API response is received.
    /// </summary>
    public static void SetCachedInsights(string insights)
    {
        lock (_aiInsightsLock)
        {
            _cachedAiInsights = insights ?? "";
            _aiInsightsReady = !string.IsNullOrEmpty(insights) && insights != "GENERATING";
            
            if (_aiInsightsReady)
            {
                Log($"✅ [AI Insights] Cached and marked READY ({(insights?.Length ?? 0)} chars)");
            }
            else
            {
                Log($"⏳ [AI Insights] Set to GENERATING state");
            }
        }
    }

    /// <summary>
    /// Clears the cached AI insights and marks as not ready.
    /// </summary>
    public static void ClearCachedInsights()
    {
        lock (_aiInsightsLock)
        {
            _cachedAiInsights = "";
            _aiInsightsReady = false;
            Log($"🗑️  [AI Insights] Cleared");
        }
    }

    private static string MapBluHealthResponse(string rawJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;

            // Extract AI Analysis data from VitalsUrl API response
            if (!root.TryGetProperty("data", out JsonElement data))
                return string.Empty;

            if (!data.TryGetProperty("patientVitals", out JsonElement vitals))
                return string.Empty;

            // Extract any AI analysis if present in the response
            if (vitals.TryGetProperty("aiAnalysis", out JsonElement aiAnalysis))
            {
                return aiAnalysis.GetRawText();
            }

            // If no AI analysis in response, return vitals data
            return vitals.GetRawText();
        }
        catch (Exception ex)
        {
            Log($"⚠️ MapBluHealthResponse error: {ex.Message}");
            return string.Empty;
        }
    }

    
    /// <summary>
    /// Handles the All_Vitals_:FORWARD trigger from Lua.
    /// Triggers AI insights retrieval (insights come from VitalsUrl API response).
    /// </summary>
    public static void TriggerAllVitalsInsightGeneration()
    {
        Log($"📱 [GUI] All Vitals page accessed - insights from VitalsUrl API response");

        lock (_aiInsightsLock)
        {
            if (_aiInsightsReady && !string.IsNullOrEmpty(_cachedAiInsights))
            {
                Log($"✅ AI insights available from VitalsUrl API response - Lua will fetch via port 55511");
            }
            else
            {
                Log($"⏳ Waiting for VitalsUrl API response with AI insights");
            }
        }
    }

    /// <summary>
    /// Returns the current insights status for Lua.
    /// </summary>
    public static string GetInsightsStatusMessage()
    {
        // Check if user is authenticated (not in offline mode)
        if (!_isAuthenticated)
        {
            Log($"📱 [Offline Mode] Insights not available - user not authenticated");
            return "INSIGHTS_OFFLINE";
        }

        lock (_aiInsightsLock)
        {
            if (_aiInsightsReady && !string.IsNullOrEmpty(_cachedAiInsights) && _cachedAiInsights != "GENERATING")
            {
                Log($"✅ [Insights] Status: READY");
                return "INSIGHTS_READY";
            }
            else
            {
                Log($"⏳ [Insights] Status: GENERATING");
                return "INSIGHTS_GENERATING";
            }
        }
    }

    // How long the insights request will hold "Loading" waiting for the
    // VitalsUrl API response before giving the user a fallback message.
    // (Was 5s, which the API regularly exceeded — the GUI then received an
    // EMPTY string and fell back to a blank/idle page.)
    private const int InsightsWaitSeconds = 45;

    // Patient-facing fallback messages — this endpoint must NEVER return blank.
    private const string InsightsOfflineMessage =
        "⚠️ Please login to view AI insights\n\n" +
        "You are in offline mode. Your measurements are saved on the device — " +
        "login as a patient to receive personalized AI-powered health insights.";

    private const string InsightsUnavailableMessage =
        "⚠️ AI insights are not available right now\n\n" +
        "This is usually a network or server issue. Your vitals have been " +
        "recorded — please try again in a moment, or contact the administrator " +
        "if the problem continues.";

    /// <summary>
    /// Retrieves insights from VitalsUrl API response cache.
    /// Holds up to InsightsWaitSeconds while the API responds (GUI keeps
    /// showing Loading), then ALWAYS returns something displayable — either
    /// the insights, or a clear offline / unavailable message. Never blank.
    /// </summary>
    public static async Task<string> GetInsightsForClient(CancellationToken cancellationToken = default)
    {
        // Not logged in (offline mode): insights can never arrive — say so
        // immediately instead of holding a Loading screen.
        if (!_isAuthenticated)
        {
            Log("📱 [Socket Request] Insights requested in offline mode — sending login message");
            return InsightsOfflineMessage;
        }

        string aiResponse = "";

        // Check if data is already ready
        lock (_aiInsightsLock)
        {
            if (!string.IsNullOrEmpty(_cachedAiInsights) && _cachedAiInsights != "GENERATING")
            {
                aiResponse = _cachedAiInsights;
                Log($"✅ [Socket Request] AI insights immediately available ({aiResponse.Length} chars)");
                return aiResponse;
            }
        }

        // Data not ready yet — hold while the VitalsUrl API responds.
        Log($"⏳ [Socket Request] Waiting up to {InsightsWaitSeconds}s for AI insights from VitalsUrl API...");
        int waitCount = 0;
        int maxWaits = InsightsWaitSeconds * 2;   // 500ms steps

        while (waitCount < maxWaits)
        {
            lock (_aiInsightsLock)
            {
                if (!string.IsNullOrEmpty(_cachedAiInsights) && _cachedAiInsights != "GENERATING")
                {
                    aiResponse = _cachedAiInsights;
                    Log($"✅ [Socket Request] AI insights received after {waitCount * 500}ms ({aiResponse.Length} chars)");
                    return aiResponse;
                }
                waitCount++;
            }

            await Task.Delay(500, cancellationToken);
        }

        // Fallback timer expired — never return blank.
        Log($"⚠️  [Socket Request] Timeout after {InsightsWaitSeconds}s — sending unavailable message (never blank)");
        return InsightsUnavailableMessage;
    }
}
