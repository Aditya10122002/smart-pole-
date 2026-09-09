using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;

public static class ProductionLogger
{
    private static readonly object _lockObject = new object();

    // Persistent log location: /data is the device's persistent volume, so logs
    // survive container updates/recreates. Falls back to the app dir off-device.
    private static readonly string LogDirectory =
        Directory.Exists("/data") ? "/data/logs"
                                  : Path.Combine(AppContext.BaseDirectory, "logs");

    // Rotation/retention: one file per day (rolled at MaxFileBytes), pruned
    // after RetentionDays so the disk can never slowly fill up.
    private const int  RetentionDays = 30;
    private const long MaxFileBytes  = 20 * 1024 * 1024;
    private static string _currentDate = "";
    private static string _currentPath = "";
    private static int    _rollIndex   = 0;

    private static LogLevel _minimumLevel = LogLevel.Debug;
    private static bool _enableConsoleOutput = true;
    private static bool _enableFileOutput = true;

    static ProductionLogger()
    {
        try
        {
            if (!Directory.Exists(LogDirectory))
                Directory.CreateDirectory(LogDirectory);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FATAL: Failed to create log directory: {ex.Message}");
        }
    }

    public static void SetMinimumLevel(LogLevel level) => _minimumLevel = level;
    public static void SetConsoleOutput(bool enabled) => _enableConsoleOutput = enabled;
    public static void SetFileOutput(bool enabled) => _enableFileOutput = enabled;

    public static void Debug(LogCategory category, string message, Dictionary<string, object> data = null)
        => Log(LogLevel.Debug, category, message, data);

    public static void Info(LogCategory category, string message, Dictionary<string, object> data = null)
        => Log(LogLevel.Info, category, message, data);

    public static void Warning(LogCategory category, string message, Dictionary<string, object> data = null)
        => Log(LogLevel.Warning, category, message, data);

    public static void Error(LogCategory category, string message, Exception ex = null, Dictionary<string, object> data = null)
    {
        var enhancedData = data ?? new Dictionary<string, object>();
        if (ex != null)
        {
            enhancedData["exception_type"] = ex.GetType().Name;
            enhancedData["exception_message"] = ex.Message;
            if (ex.InnerException != null)
                enhancedData["inner_exception"] = ex.InnerException.Message;
        }
        Log(LogLevel.Error, category, message, enhancedData);
    }

    public static void Critical(LogCategory category, string message, Exception ex = null, Dictionary<string, object> data = null)
    {
        var enhancedData = data ?? new Dictionary<string, object>();
        if (ex != null)
        {
            enhancedData["exception_type"] = ex.GetType().Name;
            enhancedData["exception_message"] = ex.Message;
            enhancedData["stacktrace"] = ex.StackTrace;
        }
        Log(LogLevel.Critical, category, message, enhancedData);
    }

    public static void LogHttpRequest(string apiName, string method, string url, Dictionary<string, object> headers = null)
    {
        var data = new Dictionary<string, object>
        {
            ["method"] = method,
            ["url"] = url
        };
        if (headers != null)
            data["headers"] = string.Join(", ", headers.Keys);

        Info(LogCategory.API, $"HTTP Request: {apiName}", data);
    }

    public static void LogHttpResponse(string apiName, int statusCode, long responseTime, int contentLength, Dictionary<string, object> data = null)
    {
        var enhancedData = data ?? new Dictionary<string, object>();
        enhancedData["status_code"] = statusCode;
        enhancedData["response_time_ms"] = responseTime;
        enhancedData["content_length_bytes"] = contentLength;

        LogLevel level = statusCode >= 200 && statusCode < 300 ? LogLevel.Info :
                        statusCode >= 400 && statusCode < 500 ? LogLevel.Warning :
                        statusCode >= 500 ? LogLevel.Error : LogLevel.Info;

        Log(level, LogCategory.API, $"HTTP Response: {apiName}", enhancedData);
    }

    public static void LogMeasurement(string sensorName, Dictionary<string, object> values)
    {
        Info(LogCategory.Measurement, $"Measurement: {sensorName}", values);
    }

    public static void LogNetworkEvent(string eventType, string ipAddress, int port, Dictionary<string, object> data = null)
    {
        var enhancedData = data ?? new Dictionary<string, object>();
        enhancedData["event_type"] = eventType;
        enhancedData["ip_address"] = ipAddress;
        enhancedData["port"] = port;

        Info(LogCategory.Network, $"Network Event: {eventType}", enhancedData);
    }

    public static void LogServerStart(string serverName, int port)
    {
        Info(LogCategory.Server, $"Server started: {serverName}",
            new Dictionary<string, object> { ["port"] = port });
    }

    public static void LogServerStop(string serverName)
    {
        Info(LogCategory.Server, $"Server stopped: {serverName}");
    }

    private static void Log(LogLevel level, LogCategory category, string message, Dictionary<string, object> data = null)
    {
        if (level < _minimumLevel)
            return;

        lock (_lockObject)
        {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string levelStr = level.ToString().ToUpperInvariant();
            string categoryStr = category.ToString();
            string pid = Process.GetCurrentProcess().Id.ToString();
            string tid = Thread.CurrentThread.ManagedThreadId.ToString();

            var logEntry = new
            {
                timestamp = timestamp,
                level = levelStr,
                category = categoryStr,
                pid = pid,
                tid = tid,
                message = message,
                data = data
            };

            try
            {
                if (_enableConsoleOutput)
                    WriteToConsole(timestamp, levelStr, categoryStr, message, data);

                if (_enableFileOutput)
                    WriteToFile(JsonSerializer.Serialize(logEntry, new JsonSerializerOptions { WriteIndented = false }));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[LOGGING_ERROR] {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} Failed to write log: {ex.Message}");
            }
        }
    }

    private static void WriteToConsole(string timestamp, string level, string category, string message, Dictionary<string, object> data)
    {
        ConsoleColor originalColor = Console.ForegroundColor;

        try
        {
            // Determine color based on level
            Console.ForegroundColor = level switch
            {
                "DEBUG" => ConsoleColor.Gray,
                "INFO" => ConsoleColor.White,
                "WARNING" => ConsoleColor.Yellow,
                "ERROR" => ConsoleColor.Red,
                "CRITICAL" => ConsoleColor.Red,
                _ => ConsoleColor.White
            };

            // Format: [TIMESTAMP] [LEVEL] [CATEGORY] MESSAGE
            Console.WriteLine($"[{timestamp}] [{level}] [{category}] {message}");

            if (data != null && data.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.Gray;
                foreach (var kvp in data)
                {
                    string value = FormatValue(kvp.Value);
                    Console.WriteLine($"  | {kvp.Key}: {value}");
                }
            }
        }
        finally
        {
            Console.ForegroundColor = originalColor;
        }
    }

    private static void WriteToFile(string logLine)
    {
        try
        {
            // Compute the target file per write: the old static field froze the
            // date at process start, so the "daily" file never actually rolled.
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            if (today != _currentDate)
            {
                _currentDate = today;
                _rollIndex   = 0;
                _currentPath = Path.Combine(LogDirectory, $"vitalschair_{today}.log");
                PruneOldLogs();
            }
            else
            {
                var fi = new FileInfo(_currentPath);
                if (fi.Exists && fi.Length > MaxFileBytes)
                {
                    _rollIndex++;
                    _currentPath = Path.Combine(LogDirectory, $"vitalschair_{today}.{_rollIndex}.log");
                }
            }

            File.AppendAllText(_currentPath, logLine + Environment.NewLine, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FILE_WRITE_ERROR] {ex.Message}");
        }
    }

    private static void PruneOldLogs()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-RetentionDays);
            foreach (var f in Directory.GetFiles(LogDirectory, "vitalschair_*.log"))
            {
                if (File.GetLastWriteTime(f) < cutoff)
                    File.Delete(f);
            }
        }
        catch { /* pruning must never break logging */ }
    }

    private static string FormatValue(object value) => value switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        string s => s.Length > 200 ? s.Substring(0, 200) + "..." : s,
        int i => i.ToString(),
        long l => l.ToString(),
        double d => d.ToString("F4"),
        float f => f.ToString("F4"),
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.fff"),
        _ => value.ToString() ?? "unknown"
    };

    public static void LogSummary(string title, Dictionary<string, object> stats)
    {
        lock (_lockObject)
        {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"\n[{timestamp}] [SUMMARY] {title}");
            Console.ForegroundColor = ConsoleColor.Gray;

            foreach (var kvp in stats)
            {
                string value = FormatValue(kvp.Value);
                Console.WriteLine($"  | {kvp.Key}: {value}");
            }
            Console.ResetColor();
        }
    }

    public static string GetLogFilePath() =>
        string.IsNullOrEmpty(_currentPath)
            ? Path.Combine(LogDirectory, $"vitalschair_{DateTime.Now:yyyy-MM-dd}.log")
            : _currentPath;
}

// ── PHI/secret masking for log output ────────────────────────────────────────
// Central helpers so patient identifiers and secrets never reach logs in the
// clear. Use these at EVERY log call that touches patient data.
public static class LogMask
{
    /// "Ramesh Kumar" → "R*****"
    public static string Name(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "(none)" : name.Trim()[0] + "*****";

    /// "7981494286" → "79XXXXXX86"
    public static string Phone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return "(none)";
        var p = phone.Trim();
        return p.Length <= 4
            ? "XXXX"
            : p[..2] + new string('X', p.Length - 4) + p[^2..];
    }

    /// Generic identifier → first + last char only, e.g. "1***7"
    public static string Id(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "(none)";
        var s = id.Trim();
        return s.Length <= 2 ? "**" : $"{s[0]}***{s[^1]}";
    }
}
