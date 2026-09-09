using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// SQLite-backed queue for vitals records that couldn't be sent due to offline/network issues.
/// Handles local caching and periodic retry syncing.
/// </summary>
public class VitalsQueue
{
    private static readonly string QueueDbPath =
    Path.Combine(AppContext.BaseDirectory, "data", "vitals_queue.db");

    private static readonly string ConnectionString =
    $"Data Source={QueueDbPath}";
    
    private const int MaxRetries = 5;
    private const int RetryDelayMs = 5000;
    private const int InternetCheckTimeoutMs = 5000;
    
    // ── Internet connectivity check ──────────────────────────
    private const string InternetCheckUrl = "https://clients3.google.com/generate_204";
    private static readonly HttpClient _httpClient = new HttpClient();
    private static bool? _lastOnlineState = null;

    public static string BluHealthApiUrl { get; set; } = string.Empty;

    static VitalsQueue()
    {
        _httpClient.Timeout = TimeSpan.FromSeconds(5);
    }

    // ── Public: Initialize DB schema on startup ─────────────
public static async Task InitializeAsync()
{
    try
    {
        var dbDir = Path.GetDirectoryName(QueueDbPath);

        if (string.IsNullOrWhiteSpace(dbDir))
            throw new Exception($"Invalid DB path: {QueueDbPath}");

        Directory.CreateDirectory(dbDir);

        Log($"📂 Queue DB Path: {QueueDbPath}");

        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS queued_vitals (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                patient_token TEXT,
                patient_id INTEGER,
                blue_id TEXT NOT NULL,
                patient_name TEXT,
                gender TEXT,
                pulse_rate_spo2 INTEGER,
                pulse_rate_nibp INTEGER,
                heart_rate_ecg INTEGER,
                spo2 INTEGER,
                systolic INTEGER,
                diastolic INTEGER,
                temperature REAL,
                height REAL,
                weight REAL,
                bmi REAL,
                glucose_fasting INTEGER,
                glucose_pre_meal INTEGER,
                glucose_post_meal INTEGER,
                cloud_api_payload TEXT,
                created_at_utc TEXT NOT NULL,
                last_attempt_utc TEXT,
                attempt_count INTEGER DEFAULT 0,
                status TEXT DEFAULT 'pending',
                error_message TEXT,
                hl7_message TEXT,
                target_service TEXT DEFAULT 'hl7'
            )
        ";
        cmd.ExecuteNonQuery();

        EnsureColumnExists(conn, "queued_vitals", "patient_token", "TEXT");
        EnsureColumnExists(conn, "queued_vitals", "patient_id", "INTEGER");
        EnsureColumnExists(conn, "queued_vitals", "cloud_api_payload", "TEXT");

        using var idxCmd = conn.CreateCommand();
        idxCmd.CommandText =
            "CREATE INDEX IF NOT EXISTS idx_status ON queued_vitals(status)";
        idxCmd.ExecuteNonQuery();

        Log("✅ VitalsQueue DB initialized");

        // Retention: without this the DB grows forever (each sent row keeps its
        // full JSON payload + patient name) — at high patient volume that is a
        // slow-death disk fill AND a PHI-retention problem.
        await PurgeOldRowsAsync();
    }
    catch (Exception ex)
    {
        Log($"❌ VitalsQueue DB init failed: {ex}");
        throw;
    }
}
    // ── Public: Enqueue a vitals record ─────────────────────
    public static async Task<int> EnqueueAsync(QueuedVitals vitals)
    {
        try
        {
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO queued_vitals (
                    patient_token, patient_id, blue_id, patient_name, gender,
                    pulse_rate_spo2, pulse_rate_nibp, heart_rate_ecg, spo2,
                    systolic, diastolic, temperature, height, weight, bmi,
                    glucose_fasting, glucose_pre_meal, glucose_post_meal,
                    cloud_api_payload, created_at_utc, status, target_service, hl7_message
                ) VALUES (
                    @patientToken, @patientId, @bluId, @patientName, @gender,
                    @pulseRateSpo2, @pulseRateNibp, @heartRateEcg, @spo2,
                    @systolic, @diastolic, @temperature, @height, @weight, @bmi,
                    @glucoseFasting, @glucosePreMeal, @glucosePostMeal,
                    @cloudApiPayload, @createdAtUtc, @status, @targetService, @hl7Message
                )
            ";

            BindParameters(cmd, vitals);
            cmd.ExecuteNonQuery();

            // Get last insert row ID
            using var lastIdCmd = conn.CreateCommand();
            lastIdCmd.CommandText = "SELECT last_insert_rowid()";
            int recordId = Convert.ToInt32(lastIdCmd.ExecuteScalar());

            Log($"✅ Vitals enqueued for BluID {vitals.BluId} (Queue ID: {recordId})");
            return recordId;
        }
        catch (Exception ex)
        {
            Log($"❌ Failed to enqueue vitals: {ex.Message}");
            throw;
        }
    }

    // ── Public: Get pending records ──────────────────────────
    public static async Task<List<QueuedVitals>> GetPendingAsync()
    {
        try
        {
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM queued_vitals WHERE status = 'pending' ORDER BY created_at_utc ASC";

            var records = new List<QueuedVitals>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                records.Add(ReadVitalsFromReader(reader));
            }

            return records;
        }
        catch (Exception ex)
        {
            Log($"❌ Failed to get pending vitals: {ex.Message}");
            return new List<QueuedVitals>();
        }
    }

    private static void EnsureColumnExists(SqliteConnection conn, string tableName, string columnName, string columnType)
    {
        using var pragmaCmd = conn.CreateCommand();
        pragmaCmd.CommandText = $"PRAGMA table_info({tableName})";
        using var reader = pragmaCmd.ExecuteReader();

        bool exists = false;
        while (reader.Read())
        {
            if (reader["name"].ToString() == columnName)
            {
                exists = true;
                break;
            }
        }

        if (!exists)
        {
            using var alterCmd = conn.CreateCommand();
            alterCmd.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnType}";
            alterCmd.ExecuteNonQuery();
        }
    }

    // ── Retention: purge delivered/stale rows ─────────────────
    // Sent rows are kept SentRetentionDays for troubleshooting, then deleted
    // (they contain the full payload + patient name — PHI must not sit on the
    // device forever). Any row older than HardCapDays is dropped regardless of
    // status so permanently-failing rows can't accumulate unbounded.
    private const int SentRetentionDays = 7;
    private const int HardCapDays       = 90;
    private static DateTime _lastPurgeUtc = DateTime.MinValue;

    private static async Task PurgeOldRowsAsync()
    {
        _lastPurgeUtc = DateTime.UtcNow;
        try
        {
            using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                DELETE FROM queued_vitals
                WHERE (status = 'sent' AND created_at_utc < @sentCutoff)
                   OR (created_at_utc < @hardCutoff)";
            cmd.Parameters.AddWithValue("@sentCutoff",
                DateTime.UtcNow.AddDays(-SentRetentionDays).ToString("yyyy-MM-ddTHH:mm:ssZ"));
            cmd.Parameters.AddWithValue("@hardCutoff",
                DateTime.UtcNow.AddDays(-HardCapDays).ToString("yyyy-MM-ddTHH:mm:ssZ"));

            int purged = await cmd.ExecuteNonQueryAsync();
            if (purged > 0)
                Log($"🧹 VitalsQueue retention: purged {purged} rows (sent >{SentRetentionDays}d, any >{HardCapDays}d)");
        }
        catch (Exception ex)
        {
            Log($"⚠️ VitalsQueue purge error: {ex.Message}");
        }
    }

    // ── Public: Check internet connectivity ──────────────────

    public static async Task<bool> IsInternetAvailableAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Head,
                InternetCheckUrl);

            using var response = await _httpClient.SendAsync(request);

            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Log($"⚠️ Internet check error: {ex.Message}");
            return false;
        }
    }

    // ── Public: Sync worker — retry sending pending records ──
    /// <summary>
    /// Background worker that periodically checks for pending vitals and retries sending.
    /// Call this from your startup as a fire-and-forget Task.Run.
    /// </summary>
    public static async Task SyncWorkerAsync(CancellationToken cancellationToken)
    {
        const int SyncIntervalMs = 30000; // Check every 30 seconds

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Check if internet is available
                bool online = await IsInternetAvailableAsync();
                if (_lastOnlineState != online)
                {
                    Log(online
                        ? "Internet restored - checking pending vitals"
                        : "No internet - queue sync paused");
                    _lastOnlineState = online;
                }

                if (!online)
                {
                    await Task.Delay(SyncIntervalMs, cancellationToken);
                    continue;
                }

                // Get pending records
                var pending = await GetPendingAsync();
                if (pending.Count == 0)
                {
                    // No pending records — sleep and continue
                    await Task.Delay(SyncIntervalMs, cancellationToken);
                    continue;
                }

                Log($"📤 VitalsQueue: Found {pending.Count} pending vitals — attempting sync...");

                // Try to send each one
                foreach (var vitals in pending)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    await RetryAndUpdateAsync(vitals);
                    await Task.Delay(RetryDelayMs, cancellationToken); // Stagger requests
                }

                Log($"✅ VitalsQueue sync cycle complete");

                // Periodic retention purge (max every 6h)
                if ((DateTime.UtcNow - _lastPurgeUtc).TotalHours >= 6)
                    await PurgeOldRowsAsync();

                await Task.Delay(SyncIntervalMs, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Log("🛑 VitalsQueue sync worker canceled");
                break;
            }
            catch (Exception ex)
            {
                Log($"❌ VitalsQueue sync worker error: {ex.Message}");
                try
                {
                    await Task.Delay(SyncIntervalMs, cancellationToken);
                }
                catch { break; }
            }
        }
    }

    // ── Private: Retry and update status ─────────────────────
    private static async Task RetryAndUpdateAsync(QueuedVitals vitals)
    {
        try
        {
            // Route to appropriate sender based on TargetService
            bool success = false;
            string? errorMsg = null;

            if (vitals.TargetService == "hl7")
            {
                // Try to send via HL7
                try
                {
                    var hl7Snapshot = new Hl7VitalsSnapshot
                    {
                        BluId = vitals.BluId,
                        PatientName = vitals.PatientName,
                        Gender = vitals.Gender,
                        PulseRateSpO2 = vitals.PulseRateSpO2,
                        PulseRateNIBP = vitals.PulseRateNIBP,
                        HeartRateECG = vitals.HeartRateECG,
                        SpO2 = vitals.SpO2,
                        Systolic = vitals.Systolic,
                        Diastolic = vitals.Diastolic,
                        Temperature = vitals.Temperature,
                        Height = vitals.Height,
                        Weight = vitals.Weight,
                        BMI = vitals.BMI,
                        GlucoseFasting = vitals.GlucoseFasting,
                        GlucosePreMeal = vitals.GlucosePreMeal,
                        GlucosePostMeal = vitals.GlucosePostMeal,
                    };

                    await Hl7Client.SendAsync(hl7Snapshot);
                    success = true;
                    Log($"✅ Queued vitals (ID {vitals.Id}) sent via HL7");
                }
                catch (Exception ex)
                {
                    errorMsg = ex.Message;
                    success = false;
                }
            }
            else if (vitals.TargetService == "bluhealth")
            {
                try
                {
                    if (string.IsNullOrEmpty(BluHealthApiUrl))
                        throw new InvalidOperationException("BluHealth API URL is not configured.");

                    if (string.IsNullOrEmpty(vitals.CloudApiJsonPayload))
                        throw new InvalidOperationException("No Cloud API payload available for queued vitals.");

                    using var httpClient = new HttpClient();
                    httpClient.DefaultRequestHeaders.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", vitals.PatientToken);

                    using var content = new StringContent(vitals.CloudApiJsonPayload, System.Text.Encoding.UTF8, "application/json");
                    var response = await httpClient.PostAsync(BluHealthApiUrl, content);
                    string responseBody = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                        throw new InvalidOperationException($"HTTP {response.StatusCode}: {responseBody}");

                    success = true;
                    Log($"✅ Queued vitals (ID {vitals.Id}) sent to BluHealth");
                    Audit.Log("VITALS_SYNCED", "success", Audit.PatientRef(vitals.PatientId.ToString()),
                        new System.Collections.Generic.Dictionary<string, object?> { ["queue_id"] = vitals.Id });
                }
                catch (Exception ex)
                {
                    errorMsg = ex.Message;
                    success = false;
                }
            }

            // Update queue record
            await UpdateQueueStatusAsync(vitals.Id, success, errorMsg);
        }
        catch (Exception ex)
        {
            Log($"❌ Error retrying vitals {vitals.Id}: {ex.Message}");
        }
    }

    // ── Private: Update queue record status ──────────────────
    private static async Task UpdateQueueStatusAsync(int queueId, bool success, string? errorMessage = null)
    {
        try
        {
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();

            using var cmd = conn.CreateCommand();
            if (success)
            {
                cmd.CommandText = @"
                    UPDATE queued_vitals
                    SET status = 'sent', last_attempt_utc = @lastAttempt, attempt_count = attempt_count + 1
                    WHERE id = @id
                ";
            }
            else
            {
                cmd.CommandText = @"
                    UPDATE queued_vitals
                    SET status = 'failed', last_attempt_utc = @lastAttempt, error_message = @errorMsg,
                        attempt_count = attempt_count + 1
                    WHERE id = @id
                ";
                cmd.Parameters.AddWithValue("@errorMsg", errorMessage ?? "Unknown error");
            }

            cmd.Parameters.AddWithValue("@id", queueId);
            cmd.Parameters.AddWithValue("@lastAttempt", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));

            cmd.ExecuteNonQuery();

            string status = success ? "SENT" : "FAILED";
            Log($"📝 Queue record {queueId} updated to {status}");
        }
        catch (Exception ex)
        {
            Log($"❌ Failed to update queue record {queueId}: {ex.Message}");
        }
    }

    // ── Private: Bind parameters to INSERT command ──────────
    private static void BindParameters(SqliteCommand cmd, QueuedVitals vitals)
    {
        cmd.Parameters.AddWithValue("@patientToken", vitals.PatientToken ?? string.Empty);
        cmd.Parameters.AddWithValue("@patientId", vitals.PatientId);
        cmd.Parameters.AddWithValue("@bluId", vitals.BluId ?? string.Empty);
        cmd.Parameters.AddWithValue("@patientName", vitals.PatientName ?? string.Empty);
        cmd.Parameters.AddWithValue("@gender", vitals.Gender ?? string.Empty);
        cmd.Parameters.AddWithValue("@pulseRateSpo2", vitals.PulseRateSpO2);
        cmd.Parameters.AddWithValue("@pulseRateNibp", vitals.PulseRateNIBP);
        cmd.Parameters.AddWithValue("@heartRateEcg", vitals.HeartRateECG);
        cmd.Parameters.AddWithValue("@spo2", vitals.SpO2);
        cmd.Parameters.AddWithValue("@systolic", vitals.Systolic);
        cmd.Parameters.AddWithValue("@diastolic", vitals.Diastolic);
        cmd.Parameters.AddWithValue("@temperature", vitals.Temperature);
        cmd.Parameters.AddWithValue("@height", vitals.Height);
        cmd.Parameters.AddWithValue("@weight", vitals.Weight);
        cmd.Parameters.AddWithValue("@bmi", vitals.BMI);
        cmd.Parameters.AddWithValue("@glucoseFasting", vitals.GlucoseFasting);
        cmd.Parameters.AddWithValue("@glucosePreMeal", vitals.GlucosePreMeal);
        cmd.Parameters.AddWithValue("@glucosePostMeal", vitals.GlucosePostMeal);
        cmd.Parameters.AddWithValue("@cloudApiPayload", vitals.CloudApiJsonPayload ?? string.Empty);
        cmd.Parameters.AddWithValue("@createdAtUtc", vitals.CreatedAtUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"));
        cmd.Parameters.AddWithValue("@status", vitals.Status);
        cmd.Parameters.AddWithValue("@targetService", vitals.TargetService);
        cmd.Parameters.AddWithValue("@hl7Message", vitals.Hl7Message ?? string.Empty);
    }

    // ── Private: Read vitals from SQLite reader ──────────────
    private static QueuedVitals ReadVitalsFromReader(SqliteDataReader reader)
    {
        return new QueuedVitals
        {
            Id = Convert.ToInt32(reader["id"]),
            PatientToken = reader["patient_token"].ToString() ?? string.Empty,
            PatientId = reader["patient_id"] == DBNull.Value ? 0 : Convert.ToInt32(reader["patient_id"]),
            BluId = reader["blue_id"].ToString() ?? string.Empty,
            PatientName = reader["patient_name"].ToString() ?? string.Empty,
            Gender = reader["gender"].ToString() ?? string.Empty,
            PulseRateSpO2 = Convert.ToInt32(reader["pulse_rate_spo2"]),
            PulseRateNIBP = Convert.ToInt32(reader["pulse_rate_nibp"]),
            HeartRateECG = Convert.ToInt32(reader["heart_rate_ecg"]),
            SpO2 = Convert.ToInt32(reader["spo2"]),
            Systolic = Convert.ToInt32(reader["systolic"]),
            Diastolic = Convert.ToInt32(reader["diastolic"]),
            Temperature = Convert.ToSingle(reader["temperature"]),
            Height = Convert.ToSingle(reader["height"]),
            Weight = Convert.ToSingle(reader["weight"]),
            BMI = Convert.ToSingle(reader["bmi"]),
            GlucoseFasting = Convert.ToInt32(reader["glucose_fasting"]),
            GlucosePreMeal = Convert.ToInt32(reader["glucose_pre_meal"]),
            GlucosePostMeal = Convert.ToInt32(reader["glucose_post_meal"]),
            CloudApiJsonPayload = reader["cloud_api_payload"].ToString(),
            CreatedAtUtc = DateTime.Parse(reader["created_at_utc"].ToString() ?? DateTime.UtcNow.ToString()),
            LastAttemptUtc = string.IsNullOrEmpty(reader["last_attempt_utc"].ToString()) 
                ? null 
                : DateTime.Parse(reader["last_attempt_utc"].ToString() ?? ""),
            AttemptCount = Convert.ToInt32(reader["attempt_count"]),
            Status = reader["status"].ToString() ?? "pending",
            ErrorMessage = reader["error_message"].ToString(),
            Hl7Message = reader["hl7_message"].ToString(),
            TargetService = reader["target_service"].ToString() ?? "hl7"
        };
    }

    // ── Private: Logging helper ──────────────────────────────
    private static void Log(string message)
    {
        Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [VitalsQueue] {message}");
    }
}
