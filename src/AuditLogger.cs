using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>
/// Append-only, PHI-free, hash-chained audit trail (see docs/AUDIT_LOG_DESIGN.md).
///
/// - One JSON record per line at /data/audit/audit_YYYY-MM.jsonl (persistent
///   volume — survives container updates). Monthly files, 5MB size roll,
///   12-month retention.
/// - Tamper evidence: every record carries prev (previous record's hash) and
///   hash (SHA-256 of the record without the hash field). Editing or deleting
///   any line breaks the chain detectably. seq/last-hash/salt persist across
///   restarts in state.json.
/// - NO PHI EVER: no names, phones, vitals values, or transcript content.
///   Patient references are salted hashes via PatientRef().
/// - Must never throw or block the app: all failures degrade to a stderr note.
/// </summary>
public static class Audit
{
    private static readonly object _lock = new();
    private static readonly string Dir =
        Directory.Exists("/data") ? "/data/audit"
                                  : Path.Combine(AppContext.BaseDirectory, "audit");
    private static readonly string StatePath = Path.Combine(Dir, "state.json");

    private const long MaxFileBytes    = 5 * 1024 * 1024;
    private const int  RetentionMonths = 12;

    private static long   _seq;
    private static string _prevHash    = "";
    private static string _salt        = "";
    private static string _lastVersion = "";
    private static string _month       = "";
    private static string _path        = "";
    private static int    _roll;
    private static bool   _ready;

    static Audit()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            if (File.Exists(StatePath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(StatePath));
                var r = doc.RootElement;
                _seq         = r.TryGetProperty("seq", out var s) ? s.GetInt64() : 0;
                _prevHash    = r.TryGetProperty("last_hash", out var h) ? h.GetString() ?? "" : "";
                _salt        = r.TryGetProperty("salt", out var sa) ? sa.GetString() ?? "" : "";
                _lastVersion = r.TryGetProperty("last_version", out var v) ? v.GetString() ?? "" : "";
            }
            if (string.IsNullOrEmpty(_salt))
                _salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            _ready = true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[AUDIT] init failed: {ex.Message}");
        }
    }

    /// <summary>Salted, PHI-free patient reference for the actor field.</summary>
    public static string PatientRef(string? patientId)
    {
        if (string.IsNullOrWhiteSpace(patientId)) return "patient:unknown";
        var h = SHA256.HashData(Encoding.UTF8.GetBytes(_salt + patientId.Trim()));
        return "patient:" + Convert.ToHexString(h)[..12].ToLowerInvariant();
    }

    /// <summary>Append one audit record. outcome: success | fail | info.</summary>
    public static void Log(string evt, string outcome = "info",
                           string actor = "system",
                           Dictionary<string, object?>? detail = null)
    {
        if (!_ready) return;
        lock (_lock)
        {
            try
            {
                RollFiles();
                _seq++;

                var rec = new Dictionary<string, object?>
                {
                    ["ts"]      = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    ["seq"]     = _seq,
                    ["event"]   = evt,
                    ["outcome"] = outcome,
                    ["actor"]   = actor,
                    ["detail"]  = detail,
                    ["prev"]    = _prevHash
                };

                string body = JsonSerializer.Serialize(rec);
                string hash = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
                string line = body[..^1] + $",\"hash\":\"{hash}\"}}";

                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
                _prevHash = hash;
                SaveState();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AUDIT] write failed: {ex.Message}");
            }
        }
    }

    /// <summary>BOOT event + UPDATE_DETECTED when the app version changed.</summary>
    public static void LogBoot(string appVersion)
    {
        string prev = _lastVersion;

        Log("BOOT", "info", "system", new Dictionary<string, object?>
        {
            ["version"]  = appVersion,
            ["timezone"] = TimeZoneInfo.Local.Id,
            ["os"]       = Environment.OSVersion.VersionString
        });

        if (!string.IsNullOrEmpty(prev) && prev != appVersion)
            Log("UPDATE_DETECTED", "info", "system", new Dictionary<string, object?>
            {
                ["from"] = prev,
                ["to"]   = appVersion
            });

        lock (_lock)
        {
            _lastVersion = appVersion;
            try { SaveState(); } catch { }
        }
    }

    // ── internals (call under _lock) ────────────────────────────────────────

    private static void RollFiles()
    {
        string month = DateTime.UtcNow.ToString("yyyy-MM");
        if (month != _month)
        {
            _month = month;
            _roll  = 0;
            _path  = Path.Combine(Dir, $"audit_{month}.jsonl");
            Prune();
        }
        else
        {
            var fi = new FileInfo(_path);
            if (fi.Exists && fi.Length > MaxFileBytes)
            {
                _roll++;
                _path = Path.Combine(Dir, $"audit_{month}.{_roll}.jsonl");
            }
        }
    }

    private static void Prune()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddMonths(-RetentionMonths);
            foreach (var f in Directory.GetFiles(Dir, "audit_*.jsonl"))
                if (File.GetLastWriteTimeUtc(f) < cutoff)
                    File.Delete(f);
        }
        catch { /* retention must never break auditing */ }
    }

    private static void SaveState()
    {
        var state = new Dictionary<string, object?>
        {
            ["seq"]          = _seq,
            ["last_hash"]    = _prevHash,
            ["salt"]         = _salt,
            ["last_version"] = _lastVersion
        };
        File.WriteAllText(StatePath, JsonSerializer.Serialize(state), Encoding.UTF8);
    }
}
