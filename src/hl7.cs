// ============================================================
//  hl7.cs — HL7 v2.5 / MLLP Client for VitalsChair™
//  BluAI Pvt. Ltd. | BLUAI-ENG-HL7-001
//
//  Builds an ORU^R01 message from the current session vitals
//  and sends it to a local HIS over MLLP (TCP port 2575).
//
//  Called from Program.cs DONE handler alongside
//  SendVitalsToBluHealth().
//
//  Environment variables (set in docker-compose.yml):
//    HL7_ENABLED   →  "true" / "false"   (default: false)
//    HL7_HIS_HOST  →  LAN IP of HIS      (e.g. "192.168.1.50")
//    HL7_HIS_PORT  →  MLLP port          (default: "2575")
// ============================================================

using System;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

public static class Hl7Client
{
    // ── MLLP framing bytes ───────────────────────────────────
    private const byte MLLP_START = 0x0B;
    private const byte MLLP_END   = 0x1C;
    private const byte MLLP_CR    = 0x0D;

    // ── Retry config ─────────────────────────────────────────
    private const int MAX_RETRIES      = 3;
    private const int RETRY_DELAY_MS   = 2000;
    private const int CONNECT_TIMEOUT  = 5000;
    private const int ACK_TIMEOUT      = 10000;

    // ── LOINC codes used ─────────────────────────────────────
    // Keeping them named so HIS engineer can cross-reference
    private const string LOINC_HR_SPO2      = "8867-4";   // Heart rate (SpO2 source)
    private const string LOINC_HR_NIBP      = "8867-4";   // Heart rate (NIBP source)
    private const string LOINC_HR_ECG       = "8867-4";   // Heart rate (ECG source)
    private const string LOINC_SPO2         = "59408-5";  // Oxygen saturation
    private const string LOINC_BP           = "55284-4";  // Blood pressure systolic/diastolic
    private const string LOINC_TEMP         = "8310-5";   // Body temperature
    private const string LOINC_HEIGHT       = "8302-2";   // Body height
    private const string LOINC_WEIGHT       = "29463-7";  // Body weight
    private const string LOINC_BMI          = "39156-5";  // BMI
    private const string LOINC_GLUCOSE_FAST = "1558-6";   // Fasting glucose
    private const string LOINC_GLUCOSE_PRE  = "14743-9";  // Pre-meal glucose
    private const string LOINC_GLUCOSE_POST = "14760-3";  // Post-meal glucose

    // ────────────────────────────────────────────────────────
    //  PUBLIC ENTRY POINT
    //  Call this from the DONE handler in Program.cs
    // ────────────────────────────────────────────────────────
    public static async Task SendAsync(Hl7VitalsSnapshot snapshot)
    {
        // Determine HL7 settings (prefer runtime config/cache via ConfigManager)
        bool enabled;
        string host;
        int port;

        try
        {
            enabled = ConfigManager.GetHl7Enabled();
            host = ConfigManager.GetHl7Host();
            port = ConfigManager.GetHl7Port();
        }
        catch
        {
            // Fallback: read from local appsettings.json
            enabled = false;
            host = "127.0.0.1";
            port = 2575;
            try
            {
                var cfg = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                            .AddJsonFile(System.IO.Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true, reloadOnChange: false)
                            .Build();
                var hl7Section = cfg.GetSection("HL7");
                if (hl7Section.Exists())
                {
                    enabled = string.Equals(hl7Section["Enabled"], "true", StringComparison.OrdinalIgnoreCase);
                    host = hl7Section["HisHost"] ?? host;
                    port = int.TryParse(hl7Section["HisPort"], out var p) ? p : port;
                }
            }
            catch { }
        }

        if (!enabled)
        {
            Log("ℹ️  HL7 disabled (HL7.Enabled != true). Skipping.");
            return;
        }

        Log($"📤 [HL7] Building ORU^R01 for patient BluID:{snapshot.BluId} → {host}:{port}");

        string hl7Message = BuildOruR01(snapshot);

        bool success = await SendWithRetryAsync(hl7Message, host, port);

        if (success)
            Log($"✅ [HL7] Vitals delivered to HIS for BluID:{snapshot.BluId}");
        else
            Log($"❌ [HL7] Failed to deliver vitals after {MAX_RETRIES} attempts — logged, session continues.");
    }

    // ────────────────────────────────────────────────────────
    //  BUILD ORU^R01 MESSAGE
    // ────────────────────────────────────────────────────────
    private static string BuildOruR01(Hl7VitalsSnapshot v)
    {
        string ts    = DateTime.Now.ToString("yyyyMMddHHmmss");
        string msgId = $"VC{ts}_{v.BluId}";

        var sb = new StringBuilder();

        // ── MSH — Message Header ─────────────────────────────
        sb.Append($"MSH|^~\\&|VITALSCHAIR|BLUAI|HIS|HOSPITAL|{ts}||ORU^R01|{msgId}|P|2.5");
        sb.Append('\r');

        // ── PID — Patient Identification ─────────────────────
        // MRN field uses BluID prefixed with "BLUID-" under BLUAI assigning authority
        // Name: stored as single string in _patientName — sent as-is in family name position
        // DOB: not stored — omitted (field left blank)
        // Gender: M / F / U (unknown if blank)
        string gender = string.IsNullOrWhiteSpace(v.Gender) ? "U" : v.Gender.ToUpper();
        sb.Append($"PID|1||BLUID-{v.BluId}^^^BLUAI||{EscapeName(v.PatientName)}|||{gender}");
        sb.Append('\r');

        // ── PV1 — Patient Visit ──────────────────────────────
        sb.Append($"PV1|1|O|||||||||||||||VIS-{v.BluId}-{ts}");
        sb.Append('\r');

        // ── OBR — Observation Request ────────────────────────
        sb.Append($"OBR|1|||VITALS_PANEL^VitalsChair Complete Observation Panel|||{ts}");
        sb.Append('\r');

        // ── OBX segments — one per measured parameter ────────
        // Only appended if value > 0 (zero = not measured this session)
        int obxIndex = 1;

        // Heart rate — SpO2 source
        if (v.PulseRateSpO2 > 0)
        {
            sb.Append(Obx(obxIndex++, LOINC_HR_SPO2, "Heart rate (SpO2)", v.PulseRateSpO2.ToString(), "/min", "60-100", ts));
            sb.Append('\r');
        }

        // Heart rate — NIBP source
        if (v.PulseRateNIBP > 0)
        {
            sb.Append(Obx(obxIndex++, LOINC_HR_NIBP, "Heart rate (NIBP)", v.PulseRateNIBP.ToString(), "/min", "60-100", ts));
            sb.Append('\r');
        }

        // Heart rate — ECG source
        if (v.HeartRateECG > 0)
        {
            sb.Append(Obx(obxIndex++, LOINC_HR_ECG, "Heart rate (ECG)", v.HeartRateECG.ToString(), "/min", "60-100", ts));
            sb.Append('\r');
        }

        // SpO2
        if (v.SpO2 > 0)
        {
            sb.Append(Obx(obxIndex++, LOINC_SPO2, "Oxygen saturation", v.SpO2.ToString(), "%", "95-100", ts));
            sb.Append('\r');
        }

        // Blood pressure — only if both systolic and diastolic are present
        if (v.Systolic > 0 && v.Diastolic > 0)
        {
            sb.Append(Obx(obxIndex++, LOINC_BP, "Blood pressure", $"{v.Systolic}/{v.Diastolic}", "mm[Hg]", "", ts));
            sb.Append('\r');
        }

        // Temperature (primary sensor)
        if (v.Temperature > 0)
        {
            sb.Append(Obx(obxIndex++, LOINC_TEMP, "Body temperature", v.Temperature.ToString("F1"), "Cel", "36.1-37.2", ts));
            sb.Append('\r');
        }

        // Height
        if (v.Height > 0)
        {
            sb.Append(Obx(obxIndex++, LOINC_HEIGHT, "Body height", v.Height.ToString("F1"), "cm", "", ts));
            sb.Append('\r');
        }

        // Weight
        if (v.Weight > 0)
        {
            sb.Append(Obx(obxIndex++, LOINC_WEIGHT, "Body weight", v.Weight.ToString("F1"), "kg", "", ts));
            sb.Append('\r');
        }

        // BMI
        if (v.BMI > 0)
        {
            sb.Append(Obx(obxIndex++, LOINC_BMI, "Body mass index", v.BMI.ToString("F1"), "kg/m2", "18.5-24.9", ts));
            sb.Append('\r');
        }

        // Blood glucose — fasting
        if (v.GlucoseFasting > 0)
        {
            sb.Append(Obx(obxIndex++, LOINC_GLUCOSE_FAST, "Glucose fasting", v.GlucoseFasting.ToString(), "mg/dL", "70-99", ts));
            sb.Append('\r');
        }

        // Blood glucose — pre-meal
        if (v.GlucosePreMeal > 0)
        {
            sb.Append(Obx(obxIndex++, LOINC_GLUCOSE_PRE, "Glucose pre-meal", v.GlucosePreMeal.ToString(), "mg/dL", "70-139", ts));
            sb.Append('\r');
        }

        // Blood glucose — post-meal
        if (v.GlucosePostMeal > 0)
        {
            sb.Append(Obx(obxIndex++, LOINC_GLUCOSE_POST, "Glucose post-meal", v.GlucosePostMeal.ToString(), "mg/dL", "70-199", ts));
            sb.Append('\r');
        }

        return sb.ToString().TrimEnd('\r');
    }

    // ────────────────────────────────────────────────────────
    //  OBX SEGMENT BUILDER
    //  OBX|index|NM|loincCode^label^LN||value|unit|refRange||||F|||timestamp
    // ────────────────────────────────────────────────────────
    private static string Obx(int index, string loinc, string label, string value, string unit, string refRange, string ts)
    {
        return $"OBX|{index}|NM|{loinc}^{label}^LN||{value}|{unit}|{refRange}||||F|||{ts}";
    }

    // ────────────────────────────────────────────────────────
    //  MLLP SEND WITH RETRY
    // ────────────────────────────────────────────────────────
    private static async Task<bool> SendWithRetryAsync(string hl7Message, string host, int port)
    {
        for (int attempt = 1; attempt <= MAX_RETRIES; attempt++)
        {
            try
            {
                bool accepted = await SendOnceAsync(hl7Message, host, port);
                if (accepted)
                    return true;

                Log($"⚠️  [HL7] Attempt {attempt}/{MAX_RETRIES}: HIS returned AE/AR. Retrying in {RETRY_DELAY_MS}ms...");
            }
            catch (Exception ex)
            {
                Log($"⚠️  [HL7] Attempt {attempt}/{MAX_RETRIES} failed: {ex.Message}");
            }

            if (attempt < MAX_RETRIES)
                await Task.Delay(RETRY_DELAY_MS);
        }

        return false;
    }

    // ────────────────────────────────────────────────────────
    //  SINGLE MLLP SEND ATTEMPT
    // ────────────────────────────────────────────────────────
    private static async Task<bool> SendOnceAsync(string hl7Message, string host, int port)
    {
        using var client = new TcpClient();

        // Connect with timeout
        var connectTask = client.ConnectAsync(host, port);
        if (await Task.WhenAny(connectTask, Task.Delay(CONNECT_TIMEOUT)) != connectTask)
            throw new TimeoutException($"Connection to {host}:{port} timed out after {CONNECT_TIMEOUT}ms.");

        if (connectTask.IsFaulted)
            throw connectTask.Exception!.InnerException!;

        Log($"🔗 [HL7] Connected to {host}:{port}");

        using var stream = client.GetStream();

        // ── Wrap in MLLP framing and send ───────────────────
        byte[] body  = Encoding.UTF8.GetBytes(hl7Message);
        byte[] frame = new byte[body.Length + 3];
        frame[0] = MLLP_START;
        Buffer.BlockCopy(body, 0, frame, 1, body.Length);
        frame[^2] = MLLP_END;
        frame[^1] = MLLP_CR;

        await stream.WriteAsync(frame);
        Log($"📨 [HL7] Message sent ({frame.Length} bytes). Waiting for ACK...");

        // ── Read ACK with timeout ────────────────────────────
        stream.ReadTimeout = ACK_TIMEOUT;
        byte[] buf = new byte[4096];
        int    n   = await stream.ReadAsync(buf);

        if (n < 3)
            throw new Exception("ACK response too short — possibly malformed.");

        // Strip MLLP framing from ACK (skip byte 0, strip last 2)
        string ackText = Encoding.UTF8.GetString(buf, 1, n - 3);

        Log($"📩 [HL7] ACK received ({n} bytes).");

        // ── Parse MSA segment ────────────────────────────────
        foreach (string seg in ackText.Split('\r'))
        {
            if (!seg.StartsWith("MSA")) continue;

            string[] fields = seg.Split('|');
            string   code   = fields.Length > 1 ? fields[1] : "??";

            switch (code)
            {
                case "AA":
                    Log($"✅ [HL7] ACK-AA — HIS accepted the message.");
                    return true;
                case "AE":
                    Log($"❌ [HL7] ACK-AE — HIS application error. Check HIS logs.");
                    return false;
                case "AR":
                    Log($"❌ [HL7] ACK-AR — HIS rejected message (unknown BluID/MRN?).");
                    return false;
                default:
                    Log($"⚠️  [HL7] Unknown ACK code: {code}");
                    return false;
            }
        }

        Log("⚠️  [HL7] No MSA segment found in ACK response.");
        return false;
    }

    // ────────────────────────────────────────────────────────
    //  HELPERS
    // ────────────────────────────────────────────────────────

    // Escape patient name for HL7 — remove pipe and caret characters
    // _patientName is a single string so it goes into the family name position
    private static string EscapeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Unknown";
        return name.Replace("|", "").Replace("^", "").Trim();
    }

    private static void Log(string message)
    {
        try
        {
            ProductionLogger.Info(LogCategory.Data, message);
        }
        catch
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
        }
    }
}

// ============================================================
//  Hl7VitalsSnapshot
//  A clean data bag — populated in Program.cs DONE handler
//  from the same stored fields used by SendVitalsToBluHealth()
//  No lock needed here — snapshot is taken inside the lock
//  then passed out.
// ============================================================
public class Hl7VitalsSnapshot
{
    // Patient
    public string BluId       { get; set; } = "";
    public string PatientName { get; set; } = "";
    public string Gender      { get; set; } = "";

    // Heart rate — three sources, each optional
    public int PulseRateSpO2 { get; set; }   // storedPulseRate
    public int PulseRateNIBP { get; set; }   // storedPulseRate2
    public int HeartRateECG  { get; set; }   // storedEcgHeartRate

    // Vitals
    public int   SpO2        { get; set; }   // storedSpO2
    public int   Systolic    { get; set; }   // storedSys
    public int   Diastolic   { get; set; }   // storedDia
    public float Temperature { get; set; }   // storedTemperature1
    public float Height      { get; set; }   // storedHeight
    public float Weight      { get; set; }   // storedWeight
    public float BMI         { get; set; }   // storedBMI

    // Blood glucose — all three optional
    public int GlucoseFasting  { get; set; }  // storedBloodGlucoseFasting
    public int GlucosePreMeal  { get; set; }  // storedBloodGlucosePreMeal
    public int GlucosePostMeal { get; set; }  // storedBloodGlucosePostMeal
}
