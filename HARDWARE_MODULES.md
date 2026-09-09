# VitalsChair Hardware Interface Organization

## Overview
VitalsChair backend hardware functionality has been refactored into organized, well-documented manager modules. This improves code maintainability, testability, and clarity.

## Hardware Modules

### 1. **HardwareShutdown.cs** — System Power Control
**Purpose:** Graceful shutdown and reboot capabilities for the embedded system.

**Public API:**
- `SystemPower.PowerOff()` — Initiates system shutdown
- `SystemPower.Reboot()` — Initiates system warm reboot

**Implementation Details:**
- Uses `nsenter` to execute commands in host namespace from container
- Ensures safe power-down without losing data
- Location: `src/HardwareShutdown.cs`

**Usage Example:**
```csharp
await SystemPower.PowerOff();  // Shutdown system
await SystemPower.Reboot();     // Reboot system
```

---

### 2. **HardwareWifi.cs** — WiFi Management
**Purpose:** Manages WiFi connectivity, network scanning, and real-time WiFi state broadcasting.

**Public API:**
- `HardwareWifi.StartWifiTcpServerAsync()` — Launches WiFi management server (port 9000)
- Accepts WiFi commands: `SCAN`, `CONNECT`, `DISCONNECT`, `FORGET`

**Network Protocol:**
TCP server on port 9000 accepts pipe-delimited commands:
- `SCAN` — Returns list of available networks
- `CONNECT|SSID|PASSWORD` — Connects to WiFi network
- `DISCONNECT` — Disconnects from current network
- `FORGET|SSID` — Removes saved credentials

**Implementation Details:**
- Uses NetworkManager CLI (`nmcli`) for WiFi operations
- Maintains list of connected clients for broadcast messaging
- Auto-refreshes network list every 5 seconds
- Location: `src/HardwareWifi.cs`

**Usage Example:**
```csharp
await HardwareWifi.StartWifiTcpServerAsync();  // Start WiFi server
// Clients connect to port 9000 and send commands
```

---

### 3. **HardwareAudio.cs** — Audio I/O & Microphone Interface
**Purpose:** Audio playback, microphone recording, and real-time audio device detection/switching.

**Public API:**
- `HardwareAudio.StartAudioDeviceMonitor()` — Detects HDMI/USB audio devices
- `HardwareAudio.PlayWavAsync(byte[])` — Plays audio via configured output
- `HardwareAudio.StartMicrophoneStreamingAsync()` — Begins microphone recording
- `HardwareAudio.StopMicrophoneStreamingAsync()` — Stops microphone recording
- `HardwareAudio.GetAudioMode()` — Returns current audio mode
- `HardwareAudio.GetAudioOutputDevice()` — Returns ALSA output device
- `HardwareAudio.GetMicrophoneDevice()` — Returns ALSA microphone device

**Audio Modes:**
- **HdmiOnly** — HDMI audio output detected (typical)
- **UsbHeadset** — USB headset with integrated microphone
- **UsbMicOnly** — USB microphone dongle (no headset output)

**Implementation Details:**
- Uses ALSA (Advanced Linux Sound Architecture) for device management
- Microphone streaming sends PCM chunks to BluNote API (16kHz mono, 500ms chunks)
- Audio playback via ffplay with device routing
- Auto-detects and switches between HDMI and USB devices
- Runs periodic device detection (5-second interval)
- Location: `src/HardwareAudio.cs`

**Usage Example:**
```csharp
HardwareAudio.StartAudioDeviceMonitor();  // Enable auto-detection
await HardwareAudio.PlayWavAsync(audioBytes);  // Play audio
await HardwareAudio.StartMicrophoneStreamingAsync();  // Start recording
await HardwareAudio.StopMicrophoneStreamingAsync();  // Stop recording
string mode = HardwareAudio.GetAudioMode();  // Check current mode
```

**Microphone Streaming Flow:**
1. arecord records from detected microphone device
2. Audio chunked into 500ms PCM frames
3. Each chunk sent to BluNote API for transcription
4. Streaming continues until `StopMicrophoneStreamingAsync()` called

---

### 4. **HardwareSensor.cs** — Sensor Input Interface
**Purpose:** Manages hardware sensor inputs including height/weight scales via serial port.

**Public API:**
- `HardwareSensor.ReadHeightLoopAsync(serialPort, cancellationToken, onMeasurementReceived)` — Continuously reads sensor data
- `HardwareSensor.ValidateMeasurement(height, weight)` — Validates measurement ranges
- `HardwareSensor.CalculateBMI(heightCm, weightKg)` — Computes Body Mass Index
- `HardwareSensor.GetBMICategory(bmi)` — Returns WHO BMI classification

**Sensor Protocol:**
Serial port receives height/weight measurements formatted as CSV:
```
<height_cm>,<weight_kg>\n
```

**Measurement Validation:**
- Height: 50–250 cm (infant to very tall adult)
- Weight: 2–250 kg (infant to extreme cases)

**BMI Categories:**
- Underweight: < 18.5
- Normal: 18.5–24.9
- Overweight: 25–29.9
- Obese: ≥ 30

**Implementation Details:**
- Reads serial data in background with 100ms polling
- Calls callback for each valid measurement
- Includes validation and BMI calculation utilities
- Location: `src/HardwareSensor.cs`

**Usage Example:**
```csharp
await HardwareSensor.ReadHeightLoopAsync(
    serialPort,
    cancellationToken,
    (height, weight) =>
    {
        Console.WriteLine($"Height: {height}cm, Weight: {weight}kg");
        float bmi = HardwareSensor.CalculateBMI(height, weight);
        string category = HardwareSensor.GetBMICategory(bmi);
        Console.WriteLine($"BMI: {bmi:F1} ({category})");
    }
);
```

---

## Integration with Program.cs

Program.cs continues to handle:
- **Orchestration:** Initialization and lifecycle management
- **Sensor TCP servers:** Raw data collection from hardware
- **Vitals processing:** Computing heart rate, SpO2, BP, temperature
- **API integration:** Sending vitals to BluHealth and HIS systems
- **Application flow:** Device registration, voice navigation, TTS

Hardware managers are called from Program.cs when needed:
- Audio playback during TTS response
- Microphone streaming during voice question collection
- WiFi management via separate TCP server
- Power control for shutdown commands
- Sensor reading during measurement workflows

---

## Build Status
✅ All hardware modules compile cleanly (0 errors, pre-existing warnings only)

## Next Steps
1. Extract hardware function calls from Program.cs to use manager APIs
2. Add integration tests for each hardware module
3. Update device deployment documentation
4. Monitor hardware operations in production
