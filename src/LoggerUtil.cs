using System;
using System.Collections.Generic;
using System.Text.Json;

public enum LogLevel
{
    Debug,
    Info,
    Success,
    Warning,
    Error,
    Critical
}

public enum LogCategory
{
    System,
    Audio,
    Vitals,
    Voice,
    API,
    ECG,
    Measurement,
    Auth,
    Network,
    Config,
    Registration,
    Server,
    Hardware,
    Data,
    Performance
}

public static class LoggerUtil
{
    private static readonly bool ShowDebug = true; // Toggle for debug logs

    public static void LogStructured(LogLevel level, LogCategory category, string title, Dictionary<string, object>? data = null)
    {
        if (level == LogLevel.Debug && !ShowDebug) return;

        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        string levelStr = GetLevelString(level);
        string separator = new string('─', 80);

        Console.WriteLine($"{separator}");
        Console.WriteLine($"{timestamp} [{levelStr}] [{category}] {title}");

        if (data != null && data.Count > 0)
        {
            foreach (var kvp in data)
            {
                Console.WriteLine($"  | {kvp.Key}: {FormatValue(kvp.Value)}");
            }
        }

        Console.WriteLine($"{separator}\n");
    }

    public static void LogApiResponse(string apiName, int statusCode, string responseBody, bool success)
    {
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        string status = success ? "SUCCESS" : "FAILED";

        Console.WriteLine($"\n[{status}] [{apiName}] API RESPONSE");
        Console.WriteLine($"{timestamp}");
        Console.WriteLine($"  | Status Code: {statusCode} {GetHttpStatus(statusCode)}");
        Console.WriteLine($"  | Response Size: {responseBody.Length} bytes");

        try
        {
            var json = JsonDocument.Parse(responseBody);
            Console.WriteLine($"  | Response Data:");
            PrintJson(json.RootElement, "  |   ");
        }
        catch
        {
            string preview = responseBody.Length > 200 ? responseBody.Substring(0, 200) + "..." : responseBody;
            Console.WriteLine($"  | Body: {preview}");
        }

        Console.WriteLine();
    }

    public static void LogMeasurementStatus(string measurement, int current, int total, bool complete)
    {
        string status = complete ? "COMPLETE" : "IN_PROGRESS";
        Console.WriteLine($"[{status}] [{measurement}] Progress: {current}/{total}");
    }

    public static void LogError(string errorName, string message, Exception? ex = null)
    {
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        Console.WriteLine($"\n[ERROR] [{errorName}]");
        Console.WriteLine($"{timestamp}");
        Console.WriteLine($"  | Message: {message}");
        if (ex != null)
        {
            Console.WriteLine($"  | Exception Type: {ex.GetType().Name}");
            Console.WriteLine($"  | Details: {ex.Message}");
        }
        Console.WriteLine();
    }

    public static void LogSuccess(string operation, Dictionary<string, object>? details = null)
    {
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        Console.WriteLine($"\n[SUCCESS] {operation}");
        Console.WriteLine($"{timestamp}");

        if (details != null)
        {
            foreach (var kvp in details)
            {
                Console.WriteLine($"  | {kvp.Key}: {FormatValue(kvp.Value)}");
            }
        }

        Console.WriteLine();
    }

    public static void LogProcessing(string processName, string stage)
    {
        Console.WriteLine($"[INFO] [{processName}] {stage}...");
    }

    public static void LogTranscript(Dictionary<int, (string question, string answer)> qa)
    {
        Console.WriteLine($"\n[INFO] VOICE TRANSCRIPT SUMMARY");
        Console.WriteLine($"Total Q&A Pairs: {qa.Count}\n");

        foreach (var item in qa)
        {
            Console.WriteLine($"Q{item.Key}: {item.Value.question}");
            Console.WriteLine($"A{item.Key}: {item.Value.answer}\n");
        }
    }

    private static string GetLevelString(LogLevel level) => level switch
    {
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO",
        LogLevel.Success => "SUCCESS",
        LogLevel.Warning => "WARNING",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "CRITICAL",
        _ => "UNKNOWN"
    };

    private static string GetHttpStatus(int code) => code switch
    {
        200 => "OK",
        201 => "Created",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        500 => "Server Error",
        503 => "Service Unavailable",
        _ => "Unknown"
    };

    private static string FormatValue(object? value) => value switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        string s => s.Length > 100 ? s.Substring(0, 100) + "..." : s,
        int i => i.ToString(),
        double d => d.ToString("F2"),
        _ => value.ToString() ?? "Unknown"
    };

    private static void PrintJson(JsonElement element, string indent)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    Console.WriteLine($"{indent}{prop.Name}: {FormatJsonValue(prop.Value)}");
                }
                break;
            case JsonValueKind.Array:
                int count = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Console.WriteLine($"{indent}[{count}]: {FormatJsonValue(item)}");
                    count++;
                }
                break;
        }
    }

    private static string FormatJsonValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => $"\"{element.GetString()}\"",
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        JsonValueKind.Object => "{...}",
        JsonValueKind.Array => "[...]",
        _ => "?"
    };
}
