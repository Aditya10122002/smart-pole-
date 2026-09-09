# Complete API Request/Response Documentation

## API Endpoints Summary

All requests use these endpoints from `BootstrapConfigProvider.cs`:

```
RegisterUrl      = "https://vitalchairapi.bluai.ai/api/vitalchair-admin/device/register"
StatusCheckUrl   = "https://vitalchairapi.bluai.ai/api/vitalchair-admin/device/status-check"
```

---

## 1. REGISTRATION API - POST `/api/vitalchair-admin/device/register`

### Request Details:
```
Method: POST
URL: https://vitalchairapi.bluai.ai/api/vitalchair-admin/device/register
Headers:
  - Content-Type: application/json
  - X-Bootstrap-Token: bluai-vc-bootstrap
```

### Request Payload (JSON):
```json
{
  "device_id": "VC-IN-PB-2605-15678114",
  "serial": "SERIAL123",
  "model": "VitalsChair-V1",
  "firmware": "1.0.0",
  "mac": "AA:BB:CC:DD:EE:FF",
  "ip": "192.168.1.100",
  "location": "Bangalore, India (12.9716°, 77.5946°)",
  "config_version": 0
}
```

### Response on Success (200 OK):
```json
{
  "success": true,
  "message": "Device registered successfully. Waiting for approval.",
  "data": {
    "device_id": "VC-IN-PB-2605-15678114",
    "jwt": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
    "registered_at": "2026-06-18T07:47:39Z",
    "approval_status": "pending",
    "BluHealthApi": {
      "SendOtpUrl": "https://...",
      "VerifyOtpUrl": "https://...",
      "DeviceUrl": "https://...",
      "VitalsUrl": "https://...",
      "VoiceQuestionUrl": "https://...",
      "BluNoteUrl": "https://..."
    },
    "VoiceAssistant": {
      "ApiKey": "sk-...",
      "Model": "gemini-3.1-flash-live-preview",
      "Language": "en-IN"
    }
  }
}
```

### Response on Approved State (200 OK):
```json
{
  "success": true,
  "message": "Device registration approved",
  "data": {
    "device_id": "VC-IN-PB-2605-15678114",
    "jwt": "eyJ...",
    "approval_status": "approved",
    "BluHealthApi": {
      "SendOtpUrl": "...",
      "VerifyOtpUrl": "...",
      "DeviceUrl": "...",
      "VitalsUrl": "...",
      "VoiceQuestionUrl": "...",
      "BluNoteUrl": "..."
    },
    "VoiceAssistant": {...},
    "HL7": {
      "Enabled": true,
      "HisHost": "192.168.1.50",
      "HisPort": 2575
    }
  }
}
```

### Error Responses:
```json
// 400 Bad Request - Missing device_id
{
  "success": false,
  "message": "device_id missing or invalid"
}

// 401 Unauthorized - Invalid bootstrap token
{
  "success": false,
  "message": "Invalid bootstrap token"
}

// 500 Server Error (Backend Bug)
{
  "success": false,
  "message": "Cannot read properties of null (reading 'config')"
}
```

---

## 2. STATUS CHECK API - GET `/api/vitalchair-admin/device/status-check`

### Request Details:
```
Method: GET (with body - unconventional but used)
URL: https://vitalchairapi.bluai.ai/api/vitalchair-admin/device/status-check
Headers:
  - Authorization: Bearer {jwt}
  - Content-Type: application/json
```

### Request Payload (JSON Body):
```json
{
  "device_id": "VC-IN-PB-2605-15678114",
  "config_version": 0
}
```

### Response - Device Approved + Config Up-to-Date (200 OK):
```json
{
  "success": true,
  "data": {
    "status": "approved",
    "device_id": "VC-IN-PB-2605-15678114",
    "app_config_mismatch": false,
    "server_config_version": 1,
    "device_config_version": 0
  }
}
```

### Response - Device Approved + Config Mismatch (200 OK):
```json
{
  "success": true,
  "data": {
    "status": "approved",
    "device_id": "VC-IN-PB-2605-15678114",
    "app_config_mismatch": true,
    "server_config_version": 2,
    "device_config_version": 1
  }
}
```

### Response - Device Pending Approval (200 OK):
```json
{
  "success": true,
  "data": {
    "status": "pending",
    "device_id": "VC-IN-PB-2605-15678114",
    "app_config_mismatch": false
  }
}
```

### Response - Device Deleted from Portal (200 OK):
```json
{
  "success": true,
  "data": {
    "status": "deleted",
    "device_id": "VC-IN-PB-2605-15678114",
    "message": "Device has been removed from system"
  }
}
```

### Response - JWT Invalid/Expired (401):
```json
{
  "success": false,
  "message": "Unauthorized - JWT rejected"
}
```

### Response - Device Not Found (404):
```json
{
  "success": false,
  "message": "Device not found"
}
```

### Response - Server Error (500):
```json
{
  "success": false,
  "message": "Cannot read properties of null (reading 'config')"
}
```

---

## Request/Response Logging

To see all these details in real-time, look for logs with:

```
[INFO] [Registration] 📤 REGISTRATION API REQUEST
  | Method: POST
  | RequestBody: {...}

[INFO] [Registration] 📥 REGISTRATION API RESPONSE
  | HttpStatus: 200
  | RawBody: {...}

[INFO] [Registration] 📤 STATUS CHECK API REQUEST
  | Method: GET
  | RequestBody: {...}

[INFO] [Registration] 📥 STATUS CHECK API RESPONSE
  | HttpStatus: 200
  | RawBody: {...}
```

---

## Key Findings

### Problem Identified:
- **500 Error**: `"Cannot read properties of null (reading 'config')"`
- **Root Cause**: Server tries to access `device.config` when device record has no config attached
- **When**: Device is newly registered/re-registered after deletion, no config linked yet
- **Fix**: Device re-registers (via RegisterWithRetryAsync) on first approval to get config through registration response

### Current Solution:
- Config is delivered through registration response, not separate fetch endpoint
- On first approval: Device calls `RegisterWithRetryAsync()` again
- Response parsed by `TryParseAndSaveResponse()` which saves config
- No dependency on separate ConfigFetchUrl endpoint
