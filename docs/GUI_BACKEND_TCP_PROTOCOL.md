# VitalsChair — GUI ↔ Backend TCP Protocol

Reference for the **Flutter GUI** developer. Describes how the GUI talks to the
.NET backend (`vitaldata`) over TCP: ports, the handshake, message formats, and
every feature's commands/responses.

> Source of truth: backend `src/Program.cs` + `src/HardwareWifi.cs`. A few payload
> details are marked **⚠️ confirm** — verify against the backend before relying on them.

---

## 1. Connection model

- **Transport:** plain TCP (no TLS — it's localhost-network traffic inside the device).
- **Host:** the GUI runs as a container on the `torizon_crank-net` Docker network,
  so it reaches the backend by its service name: **`vitaldata`**.
  (Equivalent to how the current Lua GUI uses `HOST = "vitaldata"`.)
- **One port per feature** (table below). Open a separate socket per feature.
- **Line-based:** every message is a single line terminated with `\n`. Read by
  lines. Some packets are multi-line (a blank-line-free group) — noted per feature.
- **Handshake:** immediately after connecting, send the **device-type string + `\n`**
  (e.g. `spo2\n`). The backend uses this to route the connection. Streaming
  features then emit a `*_START` line, data lines, and a `*_STOP` line on teardown.

**Generic flow (streaming feature):**
```
GUI  →  connect vitaldata:<port>
GUI  →  "<device_type>\n"            (handshake)
BE   →  "<DEVICE>_START\n"
BE   →  data line\n
BE   →  data line\n   ...            (until the user leaves / measurement ends)
BE   →  "<DEVICE>_STOP\n"
```

**Request/response feature (OTP, WiFi command):** open socket → send one command
line → read the reply line(s).

---

## 2. Port map

| Feature | Port | Handshake string | Direction |
|---|---|---|---|
| SpO2 (numeric) | 55555 | `spo2` | BE→GUI stream |
| SpO2/vitals graph | 55510 | `spo2_graph` / `graphs` | BE→GUI stream (JSON) |
| Combined graph | 55510 | `graphs` | BE→GUI stream (JSON) |
| ECG 5-lead (ecg7) | 55558 | `ecg7` | BE→GUI stream (high-rate) |
| ECG 12-lead | 55559 | `ecg12` | BE→GUI stream (high-rate) |
| Height/Weight + body comp | 22222 | `height_weight` | BE→GUI stream (multi-line) |
| Temperature | 65435 | `temperature` | BE→GUI stream |
| Blood Pressure (NIBP) | 55557 | `bp` | bidirectional |
| Stethoscope | 65441 | `steth` | BE→GUI stream (high-rate) |
| Analysis | 55511 | `analysis` | BE→GUI |
| Voice assistant | 65440 | `voice` | bidirectional |
| WiFi | 9000 | `wifi` | bidirectional |
| Ethernet status | 2002 | `ethernet` | BE→GUI |
| OTP send | 7777 | *(none — send command)* | request/response |
| Command channel + OTP verify | 2000 | *(none — send command)* | GUI→BE (+reply) |
| ABHA OTP | 7778 | *(none — send command)* | request/response |
| Passwords | 2003 | *(none)* | request/response |

> **⚠️ confirm:** `spo2_graph` vs `graphs` both map to 55510 in the Lua client —
> confirm which handshake the backend expects for the combined-vitals graph.

---

## 3. Message conventions

| Delimiter | Used for |
|---|---|
| `\n` | end of a record (always) |
| `,` | SpO2 fields: `spo2,hr,pi` |
| `:` | temperature / height-weight key:value: `forehead:36.7` |
| `\|` | WiFi/ethernet fields: `WIFI_LIST\|2\|SSID\|...` |
| `:::` | OTP-verify field separator: `name=John:::age=30:::...` |
| `key=value` | inside OTP-verify and `OTP_BUTTON_PRESSED` |
| JSON | the combined graph stream |

Invalid/absent numeric readings are sent as **`?`** (e.g. `?,?,?` for SpO2 with no
finger) — render as "--", don't parse as a number.

---

## 4. Authentication (OTP login)

### 4a. Standard OTP — SEND  (port **7777**)
```
GUI →  SEND_OTP:<userId>\n
BE  →  OTP_SENT:SUCCESS:<message>\n         (sent OK)
BE  →  OTP_SENT:FAIL:<message>\n            (failed; message is user-facing)
```
Failure messages are already user-friendly, e.g. `Device not approved yet. Please
ask the admin to approve this device.` / `Server not running. Please contact admin.`
The backend fails fast (~8s) if the OTP API is unreachable.

### 4b. Standard OTP — VERIFY  (port **2000**)
```
GUI →  VERIFY_OTP:<otp>\n
BE  →  OTP_VERIFIED:SUCCESS:::name=<name>:::age=<age>:::gender=<gender>:::phone=<phone>:::email=<email>\n
BE  →  OTP_VERIFIED:FAIL:::<reason>\n
```
Parse the SUCCESS line by splitting on `:::`, then each field on the first `=`.
`age` is an integer; the others are strings (may be empty → fall back to a default).

> Port 2000 also accepts `OTP_BUTTON_PRESSED:user_id=<id>\n` (alternate send entry
> that replies `OTP_SENT:...`). Prefer `SEND_OTP:` on 7777 for new code.

### 4c. ABHA OTP  (port **7778**)
```
GUI →  ABHA_SEND_OTP:<MODE>:<identifier>\n     MODE = MOBILE | NUMBER
BE  →  SUCCESS:<message>\n  |  ERROR:<message>\n

GUI →  ABHA_VERIFY_OTP:<otp>\n
BE  →  OTP_VERIFIED:SUCCESS:::name=..:::age=..:::gender=..:::phone=..:::email=..:::abha_ids=<id1,id2,...>\n
BE  →  OTP_VERIFIED:FAIL:::<reason>\n
```
`abha_ids` is a comma-separated list (may be present only for ABHA).

---

## 5. Vitals streaming

### 5a. SpO2  (port 55555, handshake `spo2`)
```
BE → SPO2_START\n
BE → <spo2>,<hr>,<pi_est>\n    e.g. 98.5,72,2.6   (any field "?" if invalid)
   ... repeating ...
BE → SPO2_STOP\n
```
Fields: SpO2 %, heart rate (bpm), **PI estimate** (0.5–6.2). ⚠️ The SpO2
module provides no true perfusion index — this value is DERIVED from the
module's pleth signal-strength indicator (0–8) mapped into the typical
physiological range, because the GUI's "PI" label is frozen. Display-only:
it must never be stored in clinical records or used in any claim. The graph
stream's `"pi"` JSON key carries the same estimate.

### 5b. Temperature  (port 65435, handshake `temperature`)
```
BE → TEMPERATURE_START\n
BE → STATUS:<patient-facing message>\n
     skin:<v>\n
     forehead:<v>\n
     body:<v>\n
   ... repeating ...
BE → TEMPERATURE_STOP\n
```
`STATUS` values: `Ready to measure` / `Hold still - measuring temperature...` /
`Too close - move back slightly` / `Recorded - proceed to next`. Temp values are
°C or `?`. (forehead = the IR reading.)

### 5c. Height / Weight / Body composition  (port 22222, handshake `height_weight`)
Multi-line packet (the Lua reads it in groups):
```
weight:<kg>\n
height:<cm>\n
bmi:<v>\n
bmi_message:<text>\n
body_fat:<pct>\n
muscle_mass:<v>\n
... (additional body-comp fields) ...
```
> **⚠️ confirm** the full field list (there are more body-comp lines).

### 5d. Blood Pressure / NIBP  (port 55557, handshake `bp`)
Bidirectional — the GUI controls when measurement starts:
```
GUI →  bp\n                 (handshake)
BE  →  BP_CONNECTED\n        (ready)
GUI →  START\n               (begin measurement)
BE  →  BP_START\n
BE  →  <readings...>\n        ⚠️ confirm exact reading line format (sys/dia/map/pulse)
BE  →  BP_STOP\n             (on completion)
```
> BP has no heartbeat and is line-by-line — don't send stray `\n` keep-alives, the
> backend treats them as malformed commands.

### 5e. Combined vitals graph  (port 55510, handshake `graphs`)
```
BE → GRAPH_START\n
BE → {"timestamp":<ms>,"hr":<n>,"spo2":<f>,"sys":<n|-1>,"dia":<n|-1>,"temp1":<v|"?">,"temp2":<v|"?">,"pi":<f>,"weight":<f>,"height":<f>}\n
   ... repeating ...
BE → GRAPH_STOP\n
```
One JSON object per line. `sys`/`dia` are `-1` when NIBP isn't active.

### 5f. ECG 5-lead (ecg7, 55558) & 12-lead (ecg12, 55559)
High-rate sample streams (enable `TCP_NODELAY`). Handshake `ecg7` / `ecg12`,
then numeric sample lines until a stop token:
- ECG 5-lead stop: `ECG_STOP`
- ECG 12-lead stop: `ECG12_STOP`
> **⚠️ confirm** the per-sample line format (channel count / ordering / scaling)
> with the backend — it's a tight loop, so get this exactly right.

### 5g. Stethoscope  (port 65441, handshake `steth`)
High-rate audio/waveform stream; stop token `STETH_STOP`.
> **⚠️ confirm** sample format.

---

## 6. Settings

### 6a. WiFi  (port 9000, handshake `wifi`)
Persistent connection. After handshake the backend sends the current state and a
network list; the GUI sends command lines on the same socket.

**GUI → backend commands:**
```
WIFI_TOGGLE\n                         turn radio on/off
WIFI_REFRESH\n                        rescan
WIFI_SELECTED|<ssid>\n                user tapped a network
WIFI_CHECK_SAVED|<ssid>\n             ask if a saved password exists
WIFI_CONNECT|<ssid>\n                 connect (open network)
WIFI_CONNECT|<ssid>|<password>\n      connect (secured)
WIFI_DISCONNECT|<ssid>\n
WIFI_FORGET|<ssid>\n
```
**backend → GUI messages:**
```
WIFI_STATE|<0|1>\n                              radio off/on
WIFI_LIST|<count>|<ssid>|<signal>|<security>|<isOpen>|...   repeated per network
WIFI_CONNECT_RESULT|<ssid>|<SUCCESS|FAILED>|<passFlag>\n
WIFI_RESTORE_CONNECTION|<ssid>\n                already-connected SSID on (re)connect
WIFI_SAVED_PASSWORD|<ssid>|<password|__USE_SAVED__>\n
WIFI_NO_SAVED_PASSWORD|<ssid>\n
WIFI_DISCONNECTED\n
WIFI_FORGOTTEN|<ssid>\n
WIFI_ICON_STATUS|<CONNECTED|DISCONNECTED>\n      drives the status-bar wifi icon
```
`WIFI_ICON_STATUS` reflects the **actual** system connection and is also sent on
connect and on every scan — use it as the single source of truth for the icon.

### 6b. Ethernet  (port 2002, handshake `ethernet`)
```
BE → ETHERNET_STATUS|CONNECTED|<ip>|<mac>|<speed>\n
BE → ETHERNET_STATUS|DISCONNECTED|0.0.0.0|00:00:00:00:00:00|0Mbps\n
```

---

## 7. Voice assistant  (port 65440, handshake `voice`)
```
GUI →  voice\n              (handshake)
GUI →  VOICE_READY\n        (sent right after handshake)
GUI →  VOLUME:<0-100>\n      set software volume (default 75)
```
> **⚠️ confirm** the full voice command/event set with the backend.

---

## 8. Command channel  (port 2000)  — GUI → backend, fire-and-forget

The GUI sends one-line commands the backend acts on (navigation, lifecycle, etc.).
Open, send a line, close (or keep open). Examples seen in the Lua:
```
NAVIGATE: <PageName>\n              e.g. NAVIGATE: Terms_Condition
USER_AGREEMENT: <Accepted|Declined>\n
KEYBOARD_EVENT: <STOP_TYPING|...>\n
ABHA_ID_SELECTED: name='<n>' id='<id>'\n
TIME_UPDATE|<DD-MMM-YYYY HH:MM AM/PM>\n
<DEVICE> CONNECTED / DISCONNECTED / START REQUESTED\n   status notifications
```
(Port 2000 also handles `VERIFY_OTP:` — §4b.)

## 9. Backend → GUI push channel  (GUI listens on port 2002)

The backend can **connect out to the GUI** and push one-line updates. The GUI
should bind a TCP listener on `2002`, accept, read one line, dispatch:
```
WIFI_LIST|... / WIFI_STATE|... / WIFI_CONNECT_RESULT|... / WIFI_RESTORE_CONNECTION|...
BLUETOOTH_STATE|... / BLUETOOTH_LIST|... / BLUETOOTH_CONNECT_RESULT|...
ETHERNET_STATUS|...
MICROPHONE_ERROR:<type>
LANGUAGE:<lang>
```
> **⚠️ confirm** with the backend whether WiFi/ethernet updates arrive on the
> persistent feature sockets (§6) or this push channel (or both). For Flutter,
> pick one authoritative path per feature to avoid double-handling.

---

## 10. Flutter implementation notes

**Connect + handshake + read lines:**
```dart
final socket = await Socket.connect('vitaldata', 55555);   // SpO2
socket.write('spo2\n');                                     // handshake

final lines = utf8.decoder.bind(socket).transform(const LineSplitter());
await for (final line in lines) {
  if (line == 'SPO2_START' || line == 'SPO2_STOP') continue;
  final p = line.split(',');            // spo2,hr,pi  (each may be "?")
  // update UI...
}
```
**Request/response (OTP send):**
```dart
final s = await Socket.connect('vitaldata', 7777);
s.write('SEND_OTP:$userId\n');
final reply = await utf8.decoder.bind(s).transform(const LineSplitter()).first;
// "OTP_SENT:SUCCESS:..." or "OTP_SENT:FAIL:..."
await s.close();
```

**Guidelines**
- **Don't block the UI isolate** on socket reads — use streams/async (this is the
  freeze the Lua GUI hit on OTP). Send the request, show "Sending…", handle the
  reply in the stream callback.
- **Reconnect with backoff** — the backend restarts; auto-reconnect per feature.
- **Treat `?` as "no reading"** — never `double.parse('?')`.
- **Set `TCP_NODELAY`** (`socket.setOption(SocketOption.tcpNoDelay, true)`) for
  ecg7/ecg12/steth/spo2_graph (low-latency waveforms).
- **Handshake first, always** — the backend won't stream until it receives
  `<device_type>\n`.
- **One socket per feature** — don't multiplex.

---

## 11. Open items to confirm with the backend dev
- BP reading line format (§5d) and ECG/steth sample formats (§5f, §5g).
- Full height/weight body-comp field list (§5c).
- `graphs` vs `spo2_graph` handshake on 55510 (§2).
- Voice command/event set (§7).
- Authoritative path for WiFi/ethernet updates: feature socket vs push channel (§9).
