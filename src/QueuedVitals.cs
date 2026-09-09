using System;

/// <summary>
/// Represents a single vitals record pending transmission to HL7 HIS and/or Cloud API.
/// Stored in SQLite queue when internet is unavailable for offline resilience.
/// </summary>
public class QueuedVitals
{
    /// <summary>
    /// Unique row ID (auto-increment in SQLite)
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Patient authentication token (required for Cloud API)
    /// </summary>
    public string PatientToken { get; set; } = string.Empty;

    /// <summary>
    /// Patient ID (integer, from authentication)
    /// </summary>
    public int PatientId { get; set; }

    /// <summary>
    /// Patient BluID (e.g. "pat_12345" or numeric ID)
    /// </summary>
    public string BluId { get; set; } = string.Empty;

    /// <summary>
    /// Patient name (from session)
    /// </summary>
    public string PatientName { get; set; } = string.Empty;

    /// <summary>
    /// Gender (M/F/U)
    /// </summary>
    public string Gender { get; set; } = string.Empty;

    /// <summary>
    /// Heart rate from SpO2 sensor
    /// </summary>
    public int PulseRateSpO2 { get; set; }

    /// <summary>
    /// Heart rate from NIBP sensor
    /// </summary>
    public int PulseRateNIBP { get; set; }

    /// <summary>
    /// Heart rate from ECG
    /// </summary>
    public int HeartRateECG { get; set; }

    /// <summary>
    /// Oxygen saturation (%)
    /// </summary>
    public int SpO2 { get; set; }

    /// <summary>
    /// Systolic BP (mmHg)
    /// </summary>
    public int Systolic { get; set; }

    /// <summary>
    /// Diastolic BP (mmHg)
    /// </summary>
    public int Diastolic { get; set; }

    /// <summary>
    /// Temperature (°C) for HL7 / single-temperature use.
    /// </summary>
    public float Temperature { get; set; }

    /// <summary>
    /// Height (cm)
    /// </summary>
    public float Height { get; set; }

    /// <summary>
    /// Weight (kg)
    /// </summary>
    public float Weight { get; set; }

    /// <summary>
    /// Body Mass Index
    /// </summary>
    public float BMI { get; set; }

    /// <summary>
    /// Blood glucose - fasting (mg/dL)
    /// </summary>
    public int GlucoseFasting { get; set; }

    /// <summary>
    /// Blood glucose - pre-meal (mg/dL)
    /// </summary>
    public int GlucosePreMeal { get; set; }

    /// <summary>
    /// Blood glucose - post-meal (mg/dL)
    /// </summary>
    public int GlucosePostMeal { get; set; }

    /// <summary>
    /// Temperature - Sensor 1 (°C)
    /// </summary>
    public float Temperature1 { get; set; }

    /// <summary>
    /// Temperature - Sensor 2 (°C)
    /// </summary>
    public float Temperature2 { get; set; }

    /// <summary>
    /// Temperature - IR sensor (°C)
    /// </summary>
    public float TemperatureIR { get; set; }

    /// <summary>
    /// Full JSON payload for Cloud API (includes all ECG data, body composition, etc.)
    /// Populated when queueing for Cloud API; NULL if HL7-only
    /// </summary>
    public string? CloudApiJsonPayload { get; set; }

    /// <summary>
    /// When this record was created (UTC)
    /// </summary>
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this record was last attempted to send (null if never tried)
    /// </summary>
    public DateTime? LastAttemptUtc { get; set; }

    /// <summary>
    /// Number of times we've tried to send this (retries)
    /// </summary>
    public int AttemptCount { get; set; }

    /// <summary>
    /// Queue status: "pending", "sent", "failed"
    /// </summary>
    public string Status { get; set; } = "pending";

    /// <summary>
    /// Error message if Status == "failed"
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// HL7 message (cached from when it was built)
    /// </summary>
    public string? Hl7Message { get; set; }

    /// <summary>
    /// For tracking: which API/service this vitals record targets (e.g. "hl7", "bluhealth")
    /// </summary>
    public string TargetService { get; set; } = "hl7";
}
