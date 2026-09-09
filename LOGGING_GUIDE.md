# VitalsChair Production Logging Guide

## Overview

The logging system has been upgraded to professional, production-grade standards. All emoji-based indicators have been replaced with structured, semantic logging that follows industry best practices.

## Logging Architecture

### Three-Layer System

1. **Console Output** - Real-time human-readable logs
2. **File Output** - Structured JSON logs for analysis
3. **Structured Data** - Optional metadata attached to logs

### Log Levels

- **Debug** (0) - Detailed diagnostic information
- **Info** (1) - General informational messages
- **Warning** (2) - Warning conditions
- **Error** (3) - Error conditions with exceptions
- **Critical** (4) - Critical failures requiring immediate attention

### Log Categories

- **System** - System initialization, shutdown
- **Server** - TCP/UDP server events
- **Network** - Network communication, connections
- **Hardware** - Hardware operations (SPI, sensors)
- **API** - HTTP API requests/responses
- **Auth** - Authentication, authorization, tokens
- **Config** - Configuration loading, updates
- **Measurement** - Vital sign measurements (ECG, SpO2, etc.)
- **Data** - Data parsing, validation, storage
- **Performance** - Performance metrics, timing
- **Audio** - Audio input/output
- **Voice** - Voice processing, TTS
- **ECG** - ECG-specific operations
- **Vitals** - Vital sign data
- **Registration** - Device registration

## Usage Examples

### Basic Info Logging

```csharp
// Old way (removed):
Log("📥 OTP Activity client connected: 192.168.1.100:5000");

// New way:
ProductionLogger.Info(LogCategory.Server, 
    "OTP Activity client connected", 
    new Dictionary<string, object> 
    { 
        ["ip_address"] = "192.168.1.100",
        ["port"] = 5000 
    });
```

### HTTP API Logging

```csharp
var stopwatch = Stopwatch.StartNew();
// ... make HTTP request ...
stopwatch.Stop();

ProductionLogger.LogHttpResponse(
    "SendOTP",
    (int)response.StatusCode,
    stopwatch.ElapsedMilliseconds,
    responseBody.Length,
    new Dictionary<string, object>
    {
        ["phone"] = phoneNumber,
        ["attempt"] = 1
    }
);
```

### Measurement Data Logging

```csharp
// Old way:
Log($"📊 SpO2: 98% | HR: 72 bpm | PI: 2.5");

// New way:
ProductionLogger.LogMeasurement("SpO2", 
    new Dictionary<string, object>
    {
        ["spo2_percent"] = 98,
        ["heart_rate_bpm"] = 72,
        ["perfusion_index"] = 2.5,
        ["signal_quality"] = "excellent"
    }
);
```

### Error Logging

```csharp
try
{
    // ... operation ...
}
catch (HttpRequestException ex)
{
    ProductionLogger.Error(LogCategory.API,
        "Failed to connect to authentication server",
        ex,
        new Dictionary<string, object>
        {
            ["endpoint"] = "auth.example.com",
            ["timeout_ms"] = 30000,
            ["attempt"] = 2
        }
    );
}
```

### Network Events

```csharp
ProductionLogger.LogNetworkEvent(
    "client_connected",
    client.Client.RemoteEndPoint.ToString(),
    ((IPEndPoint)client.Client.RemoteEndPoint).Port,
    new Dictionary<string, object>
    {
        ["connection_type"] = "TCP",
        ["is_ipv6"] = false,
        ["server_name"] = "ECG_Server"
    }
);
```

## Backward Compatibility

The `Log(string message)` method has been retained for backward compatibility. It automatically:

1. Strips emoji characters
2. Detects the message category based on content
3. Routes to `ProductionLogger.Info()`
4. Falls back to console output if logging fails

This allows existing code to work without modification.

## Log Output Formats

### Console Output

```
[2026-06-04 14:23:45.123] [INFO] [API] HTTP Request: SendOTP
  | method: POST
  | url: https://api.example.com/otp/send
  | phone: +91-XXXXXXXXXX

[2026-06-04 14:23:46.456] [INFO] [API] HTTP Response: SendOTP
  | status_code: 200
  | response_time_ms: 1333
  | content_length_bytes: 248
```

### File Output (JSON)

```json
{
  "timestamp": "2026-06-04 14:23:45.123",
  "level": "INFO",
  "category": "API",
  "pid": 12345,
  "tid": 5,
  "message": "HTTP Request: SendOTP",
  "data": {
    "method": "POST",
    "url": "https://api.example.com/otp/send",
    "phone": "+91-XXXXXXXXXX"
  }
}
```

## Configuration

### Minimum Log Level

```csharp
// Only show warnings and above
ProductionLogger.SetMinimumLevel(LogLevel.Warning);
```

### Enable/Disable Output Streams

```csharp
// Disable console output (for production services)
ProductionLogger.SetConsoleOutput(false);

// Disable file output
ProductionLogger.SetFileOutput(true);
```

## Log File Location

Logs are stored in: `{AppDirectory}/logs/vitalschair_YYYY-MM-dd.log`

Each day gets a new log file automatically.

## Migration from Old System

All 650+ Log() calls in Program.cs have been automatically refactored to use `ProductionLogger`. The old logging still works through a compatibility layer for backward compatibility.

**Recommendation**: Gradually migrate remaining `Log()` calls to use `ProductionLogger` methods directly for better structured data capture.

### Old → New Migration Pattern

```csharp
// Old
Log($"⚠️ Error: {ex.Message}");

// New
ProductionLogger.Error(LogCategory.System, 
    "Operation failed", 
    ex);
```

## Best Practices

1. **Use appropriate log levels** - Don't use Info for Debug information
2. **Include structured data** - Put values in the `data` parameter, not string interpolation
3. **Sensitive data** - Avoid logging passwords, tokens, or PHI directly
4. **Performance** - File output is async-safe with locks to prevent corruption
5. **Categorization** - Choose the most specific category for better filtering
6. **Messages** - Keep messages concise; details go in data parameters

## Monitoring & Analysis

Files in `logs/` directory can be:

1. **Tailed in real-time**: `tail -f logs/vitalschair_*.log`
2. **Searched for patterns**: `grep "ERROR" logs/vitalschair_*.log`
3. **Parsed as JSON**: `cat logs/vitalschair_*.log | jq '.data'`
4. **Monitored by tools**: ELK Stack, Splunk, DataDog, etc.

## Examples of Removed Emojis

| Old | New |
|-----|-----|
| 📥 | Incoming connection (Network/Server category) |
| 📤 | Outgoing request (API category) |
| 🔧 | Configuration/Hardware (Config/Hardware category) |
| ⚠️ | Warning level |
| ❌ | Error level |
| ✅ | Info level (success) |
| 🔄 | Info level (rotation/retry) |
| ⏳ | Debug level (waiting) |
| 📊 | Measurement category |
| 🔐 | Auth category |

---

**Last Updated**: June 4, 2026  
**System**: VitalsChair Production v1  
**Logging Framework**: ProductionLogger v1.0
