using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;

public class DeviceIdentity
{
    // ── Constants ──────────────────────────────────────────────
    public const string PRODUCT_CODE     = "VC";
    public const string COUNTRY_CODE     = "IN";
    public const string STATE_CODE       = "PB";
    public const string FIRMWARE_VERSION = "2.1.0";
    public const string HARDWARE_VERSION = "Rev 4.0";
    public const string MODEL_NUMBER     = "VitalsChair-V1";
    public const string MANUFACTURER     = "BluAI Pvt Ltd";

    private static string _cachedDeviceId = null;

    // ── Device ID ──────────────────────────────────────────────
    public static string GetDeviceId()
    {
        if (_cachedDeviceId != null) return _cachedDeviceId;
        string serial        = GetHardwareSerial();
        string provisionDate = GetOrCreateProvisionDate();
        _cachedDeviceId = $"{PRODUCT_CODE}-{COUNTRY_CODE}-{STATE_CODE}-{provisionDate}-{serial}";
        return _cachedDeviceId;
    }

    // ── Device fingerprint (single source of truth) ────────────
    // The identity block stamped into every outbound patient/device record
    // (vitals, voice QA, OTP…) so multi-site deployments can tell which
    // device/location produced each record. Returned as a Dictionary so
    // System.Text.Json serialises the fields (an object-returning helper would
    // serialise as {}). Pair with a flat top-level device_id for easy indexing.
    public static Dictionary<string, object> GetFingerprint() => new()
    {
        ["device_id"]        = GetDeviceId(),
        ["model_number"]     = MODEL_NUMBER,
        ["serial_number"]    = GetHardwareSerial(),
        ["firmware_version"] = FIRMWARE_VERSION,
        ["app_version"]      = GetAppVersion(),
        ["manufacturer"]     = MANUFACTURER
    };

    // ── App/container version ──────────────────────────────────
    // The image tag this build shipped as (APP_VERSION env, e.g. "1.0.42").
    // Ties every vitals record to the exact software build for traceability
    // and recall. "unknown" outside the compose stack (dev runs).
    public static string GetAppVersion()
        => Environment.GetEnvironmentVariable("APP_VERSION") ?? "unknown";

    // ── Hardware Serial ────────────────────────────────────────
    public static string GetHardwareSerial()
    {
        try
        {
            string snPath = "/proc/device-tree/serial-number";
            if (File.Exists(snPath))
            {
                string serial = File.ReadAllText(snPath)
                                    .Trim().Replace("\0", "").ToUpper();
                if (!string.IsNullOrEmpty(serial))
                    return serial.PadLeft(8, '0');
            }
        }
        catch { }

        try
        {
            string mac = GetMACAddress().Replace(":", "").Replace("-", "").ToUpper();
            if (mac.Length >= 8) return mac.Substring(mac.Length - 8);
        }
        catch { }

        return "00000000";
    }

    // ── Provision Date ─────────────────────────────────────────
    private static string GetOrCreateProvisionDate()
    {
        string cachePath = "/data/provision_date.txt";
        try
        {
            if (File.Exists(cachePath))
                return File.ReadAllText(cachePath).Trim();

            string date = DateTime.UtcNow.ToString("yyMM");
            Directory.CreateDirectory("/data");
            File.WriteAllText(cachePath, date);
            return date;
        }
        catch { return DateTime.UtcNow.ToString("yyMM"); }
    }

    public static string GetProvisionDate()
    {
        try
        {
            string raw = File.ReadAllText("/data/provision_date.txt").Trim();
            if (raw.Length == 4 &&
                int.TryParse(raw.Substring(0, 2), out int yy) &&
                int.TryParse(raw.Substring(2, 2), out int mm))
                return new DateTime(2000 + yy, mm, 1).ToString("MMM yyyy");
        }
        catch { }
        return DateTime.UtcNow.ToString("MMM yyyy");
    }

    // ── Network ────────────────────────────────────────────────
public static string GetLocalIPAddress()
{
    try
    {
        // Try common interface names
        string[] interfaces = { "eth0", "ethernet0", "end0", "enp1s0" };
        foreach (var iface in interfaces)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                Arguments = $"-c \"ip -4 addr show {iface} | grep -oP '(?<=inet\\s)\\d+(\\.\\d+){{3}}'\"",
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            using var process = Process.Start(psi);
            string result = process.StandardOutput.ReadToEnd().Trim();
            if (!string.IsNullOrEmpty(result)) return result;
        }
    }
    catch { }
    return "Unknown";
}
public static string GetMACAddress()
{
    try
    {
        // Read directly from host sysfs — bypasses Docker virtual interface
        string[] interfaces = { "eth0", "end0", "ethernet0", "enp1s0" };
        foreach (var iface in interfaces)
        {
            string path = $"/sys/class/net/{iface}/address";
            if (File.Exists(path))
            {
                string mac = File.ReadAllText(path).Trim().ToUpper();
                if (!string.IsNullOrEmpty(mac) && mac != "00:00:00:00:00:00")
                    return mac;
            }
        }
    }
    catch { }
    return "00:00:00:00:00:00";
}
    // ── System Performance ─────────────────────────────────────
    public static string GetCPUUsage()
    {
        try
        {
            var proc     = Process.GetCurrentProcess();
            var cpuTime  = proc.TotalProcessorTime;
            var wallTime = DateTime.Now - proc.StartTime;
            var usage    = (cpuTime.TotalMilliseconds / wallTime.TotalMilliseconds
                           / Environment.ProcessorCount) * 100;
            return $"{Math.Min(100, usage):F0}%";
        }
        catch { return "N/A"; }
    }

    public static string GetMemoryUsage()
    {
        try
        {
            var mb = Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024);
            return $"{mb} MB";
        }
        catch { return "N/A"; }
    }

    public static (string usagePercent, string availableStorage) GetStorageInfo()
    {
        try
        {
            var drive   = new DriveInfo("/");
            var totalGB = drive.TotalSize / (1024.0 * 1024 * 1024);
            var freeGB  = drive.AvailableFreeSpace / (1024.0 * 1024 * 1024);
            var usedGB  = totalGB - freeGB;
            return ($"{(usedGB / totalGB) * 100:F0}%", $"{freeGB:F0} GB / {totalGB:F0} GB");
        }
        catch { return ("N/A", "N/A"); }
    }

    public static string GetCPUTemperature()
    {
        try
        {
            string[] paths =
            {
                "/sys/class/thermal/thermal_zone0/temp",
                "/sys/class/thermal/thermal_zone1/temp"
            };
            foreach (var path in paths)
            {
                if (File.Exists(path))
                {
                    string raw = File.ReadAllText(path).Trim();
                    if (int.TryParse(raw, out int milli))
                        return $"{milli / 1000.0:F1}°C";
                }
            }
        }
        catch { }
        return "N/A";
    }

    // ── Full Payload Builder ───────────────────────────────────
    public static object BuildDevicePayload(
        string currentState,
        bool isLiveMode,
        DateTime deviceStartTime,
        double temperature1,
        double temperature2)
    {
        var uptime    = DateTime.Now - deviceStartTime;
        var storageInfo = GetStorageInfo();

        return new
        {
            device_id        = GetDeviceId(),
            device_status    = "Active",
            firmware_version = FIRMWARE_VERSION,
            hardware_version = HARDWARE_VERSION,
            ip_address       = GetLocalIPAddress(),

            metadata = new
            {
                DeviceStatus = new
                {
                    MachineStatus = "ONLINE",
                    PowerStatus   = "ON",
                    OperatingMode = isLiveMode ? "Live Monitoring" : currentState,
                    WakeUpTime    = deviceStartTime.ToString("hh:mm tt"),
                    Uptime        = $"{(int)uptime.TotalDays}d {uptime.Hours}h {uptime.Minutes}m",
                    LastUpdate    = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                },

                TemperatureAndEnvironment = new
                {
                    DeviceTemperature  = $"{temperature1:F1}°C",
                    AmbientTemperature = $"{temperature2:F1}°C",
                    CPUTemperature     = GetCPUTemperature()
                },

                SystemPerformance = new
                {
                    CPUUsage         = GetCPUUsage(),
                    MemoryUsage      = GetMemoryUsage(),
                    StorageUsage     = storageInfo.usagePercent,
                    AvailableStorage = storageInfo.availableStorage
                },

                PowerInformation = new
                {
                    PowerSource = "DC 12V Adapter",
                    BatteryLevel = "N/A"
                },

                NetworkInformation = new
                {
                    IPAddress  = GetLocalIPAddress(),
                    MACAddress = GetMACAddress()
                },

                DeviceDetails = new
                {
                    ModelNumber      = MODEL_NUMBER,
                    SerialNumber     = GetHardwareSerial(),
                    FirmwareVersion  = FIRMWARE_VERSION,
                    HardwareRevision = HARDWARE_VERSION,
                    Manufacturer     = MANUFACTURER,
                    ProvisionDate    = GetProvisionDate()
                }
            }
        };
    }
}