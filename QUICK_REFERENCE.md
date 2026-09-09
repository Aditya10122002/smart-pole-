# VitalsChair Logging - Quick Reference

## Most Common Use Cases

### 1️⃣ Info Message (Server/Network Event)
```csharp
ProductionLogger.Info(LogCategory.Server, 
    "Client disconnected",
    new Dictionary<string, object> { ["ip"] = "192.168.1.1" });
```

### 2️⃣ API Call (Request & Response)
```csharp
var sw = Stopwatch.StartNew();
var response = await client.PostAsync(url, content);
sw.Stop();

ProductionLogger.LogHttpResponse("MyAPI", 
    (int)response.StatusCode, 
    sw.ElapsedMilliseconds, 
    responseBody.Length);
```

### 3️⃣ Measurement Data (Vital Signs)
```csharp
ProductionLogger.LogMeasurement("SpO2",
    new Dictionary<string, object> {
        ["spo2_percent"] = 98,
        ["heart_rate_bpm"] = 72,
        ["signal_quality"] = "excellent"
    });
```

### 4️⃣ Error with Exception
```csharp
try { /* operation */ }
catch (Exception ex) {
    ProductionLogger.Error(LogCategory.API, 
        "API call failed", ex);
}
```

### 5️⃣ Debug Information
```csharp
ProductionLogger.Debug(LogCategory.System, 
    "Debug info here",
    new Dictionary<string, object> { ["state"] = "initializing" });
```

---

## Log Levels Cheat Sheet

| Level | Use When | Color |
|-------|----------|-------|
| `Debug` | Detailed troubleshooting info | Gray |
| `Info` | Normal operation events | White |
| `Warning` | Something unexpected | Yellow |
| `Error` | Operation failed | Red |
| `Critical` | System must stop | Red (Bold) |

---

## Categories at a Glance

```csharp
// Connection/Server events
LogCategory.Server      // TCP/UDP servers
LogCategory.Network     // Network communication

// Data & Operations
LogCategory.API         // HTTP requests/responses
LogCategory.Measurement // Vital sign readings
LogCategory.Hardware    // Device I/O, sensors
LogCategory.Data        // Data processing

// Security & Config
LogCategory.Auth        // Authentication, tokens
LogCategory.Config      // Settings, config changes

// System
LogCategory.System      // Initialization, general
LogCategory.Performance // Timing, metrics
```

---

## File Output Location

```
logs/vitalschair_YYYY-MM-dd.log
```

Each log entry is **one line of JSON** (easy to parse):

```json
{"timestamp":"2026-06-04 14:23:45.123","level":"INFO","category":"API","message":"HTTP Response: SendOTP","data":{"status_code":200}}
```

---

## Configure Logging

```csharp
// At startup in Main():

// Set minimum level (Info, Warning, Error, Critical)
ProductionLogger.SetMinimumLevel(LogLevel.Info);

// Disable console output for background services
ProductionLogger.SetConsoleOutput(false);

// File output is always recommended
ProductionLogger.SetFileOutput(true);
```

---

## Real-World Examples

### Server Start/Stop
```csharp
ProductionLogger.LogServerStart("ECG_Server", 9999);
ProductionLogger.LogServerStop("ECG_Server");
```

### Network Connection
```csharp
ProductionLogger.LogNetworkEvent("client_connected",
    client.Client.RemoteEndPoint.ToString(),
    ((IPEndPoint)client.Client.RemoteEndPoint).Port);
```

### HTTP Request
```csharp
ProductionLogger.LogHttpRequest("SendOTP", "POST", 
    "https://api.example.com/otp/send",
    new Dictionary<string, object> { ["phone"] = phone });
```

---

## What NOT to Log

❌ **Passwords** - Never log plaintext passwords  
❌ **API Keys** - Redact or omit  
❌ **JWT Tokens** - Log only ID part (first 10 chars)  
❌ **PHI** - Patient health info (use IDs instead)  
❌ **Huge Responses** - Truncate to first 200 chars  

---

## Old → New Quick Map

| Old Pattern | New Pattern |
|------------|------------|
| `Log("📥 message")` | `ProductionLogger.Info(LogCategory.Server, "message")` |
| `Log("❌ error")` | `ProductionLogger.Error(LogCategory.System, "error", ex)` |
| `Log($"Value: {x}")` | `ProductionLogger.Info(..., new Dict { ["value"] = x })` |
| `Log("📊 data")` | `ProductionLogger.LogMeasurement("name", dict)` |
| `Log($"API: {url}")` | `ProductionLogger.LogHttpRequest("api", "GET", url)` |

---

## Performance Tips

1. **Use appropriate levels** - Don't log every frame at Info
2. **Structure data** - Put values in dict, not string concat
3. **Async safe** - All logging is thread-safe
4. **File I/O** - Buffered and thread-locked
5. **No blocking** - Console output doesn't block main thread

---

## Viewing Logs

### Real-time console:
```bash
dotnet run
```

### Watch log file:
```bash
tail -f logs/vitalschair_*.log
```

### Parse JSON:
```bash
cat logs/vitalschair_*.log | jq '.data'
```

### Find errors:
```bash
grep '"level":"ERROR"' logs/vitalschair_*.log
```

---

## Common Mistakes & Fixes

### ❌ Too much in message
```csharp
Log($"Patient {id} measurements: SpO2={spo2}, HR={hr}");
```

### ✅ Correct way
```csharp
ProductionLogger.LogMeasurement("Patient Vitals",
    new Dictionary<string, object> {
        ["patient_id"] = id,
        ["spo2_percent"] = spo2,
        ["heart_rate_bpm"] = hr
    });
```

---

### ❌ Ignoring exceptions
```csharp
try { } catch (Exception ex) { Log("Error"); }
```

### ✅ Correct way
```csharp
try { } catch (Exception ex) { 
    ProductionLogger.Error(LogCategory.System, "Error", ex);
}
```

---

### ❌ Logging sensitive data
```csharp
Log($"User token: {jwt}");
```

### ✅ Correct way
```csharp
ProductionLogger.Debug(LogCategory.Auth, "JWT received",
    new Dictionary<string, object> { 
        ["token_id"] = jwt.Substring(0, 10) + "..."
    });
```

---

**Documentation Location**: See `LOGGING_GUIDE.md` for complete details

**Current Status**: ✅ Production Ready  
**Build**: ✅ 0 Errors  
**Last Updated**: June 4, 2026
