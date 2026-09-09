# VitalsChair — Audit Trail Design (Logging Phase 2)

Status: **DRAFT — for review before implementation**

## 1. Purpose

A tamper-evident, PHI-free record of *who did what, when, with what outcome* on
each device. Separate from operational/debug logs (Phase 1). Satisfies:
- **HIPAA §164.312(b)** — audit controls (record & examine activity in systems
  containing ePHI)
- **FDA Premarket Cybersecurity (2023)** — security event logging & detection
- Feeds `regulatory/FDA_ROADMAP.md` §2.4 (cybersecurity file)

## 2. Storage design

| Aspect | Decision |
|---|---|
| Location | `/data/audit/audit_YYYY-MM.jsonl` (persistent volume, survives updates) |
| Format | one JSON object per line (append-only; never rewritten) |
| Rotation | monthly file; 5MB size roll within a month (`.1`, `.2`, …) |
| Retention | **12 months on-device** (configurable). Long-term retention (6yr) is Phase 3: batched upload to the server over the existing encrypted API. |
| Integrity | **hash chain**: each record carries `prev` (previous record's hash) and `hash` (SHA-256 of this record minus the hash field). Any deletion/edit breaks the chain and is detectable. Chain seed persisted across restarts. |
| PHI policy | **no PHI ever**: no names, no phones, no vitals values, no transcript. Patient references are `sha256(patientId + device salt)` truncated to 12 hex chars. |
| Clock | timestamps in **UTC** (`ts`) + device timezone recorded at BOOT event. |

## 3. Record schema

```json
{"ts":"2026-07-14T06:32:21.104Z","seq":1042,"event":"OTP_VERIFY","outcome":"success","actor":"patient:a1b2c3d4e5f6","detail":{"digits":6},"prev":"9f31…","hash":"77ab…"}
```

| Field | Meaning |
|---|---|
| `ts` | UTC ISO-8601 |
| `seq` | monotonically increasing per device (persisted) |
| `event` | from the catalog below |
| `outcome` | `success` / `fail` / `info` |
| `actor` | `system`, `operator`, or `patient:<hashed-ref>` |
| `detail` | small event-specific map — never PHI |
| `prev`, `hash` | hash chain |

## 4. Event catalog (v1 — REVIEW THIS LIST)

### System
| Event | When | Detail |
|---|---|---|
| `BOOT` | app start | app version, image tag, timezone, OS |
| `SHUTDOWN` | graceful stop / GUI shutdown command | reason |
| `UPDATE_DETECTED` | app version differs from last BOOT | old→new version |
| `CLOCK_CHANGE` | TIME_UPDATE from GUI / large NTP jump | old→new (minutes drift) |

### Device identity & config
| Event | When | Detail |
|---|---|---|
| `REGISTRATION` | register attempt | outcome |
| `APPROVAL_CHANGE` | pending→approved / deleted | old→new status |
| `CONFIG_FETCH` | config pulled/refreshed | config version old→new |
| `KEY_REFRESH` | encryption secret key fetched | source: server / stale-cache / persisted-fallback |

### Authentication & sessions
| Event | When | Detail |
|---|---|---|
| `OTP_REQUEST` | send OTP pressed | outcome, fail-reason class (not-approved / server-down / invalid-patient) |
| `OTP_VERIFY` | verify attempted | outcome |
| `SESSION_START` | patient authenticated / offline session begins | actor, mode: online/offline |
| `SESSION_END` | measurements done / back-to-home / idle logout | reason |

### Clinical flow (types only — never values)
| Event | When | Detail |
|---|---|---|
| `MEASUREMENT` | a vital captured/stored | type: HW / TEMP / SPO2 / NIBP / ECG / GLUCOSE |
| `VITALS_SUBMIT` | record sent to server | outcome, queued-or-live, record ref |
| `VITALS_QUEUED` | stored offline for later sync | queue depth |
| `VITALS_SYNCED` | offline record delivered | record ref |
| `HL7_SENT` | HL7 push (if enabled) | outcome |

### Voice / AI
| Event | When | Detail |
|---|---|---|
| `VOICE_SESSION` | Gemini session start/end | start/end, end-reason (complete / nav-away / no-mic / danger-flag) |
| `AI_INSIGHTS` | insights generated/uploaded | outcome (no content) |
| `DANGER_FLAG` | assistant raised emergency flag | (no content — flag only) |

### Network & settings
| Event | When | Detail |
|---|---|---|
| `WIFI_CHANGE` | connect / disconnect / forget | ssid, outcome |
| `SETTINGS_CHANGE` | operator-visible setting changed | which (volume excluded — not security-relevant) |
| `API_AUTH_FAIL` | 401/403 from server APIs | endpoint class |

**Deliberately excluded:** vitals values, names/phones/identifiers, transcripts,
per-question voice content, WiFi passwords, tokens/keys.

## 5. Implementation sketch (after catalog approval)

- `AuditLogger.cs` (~150 lines): static `Audit.Log(event, outcome, actor, detail)`;
  lock-guarded append; hash chain + seq persisted in `/data/audit/state.json`.
- Call sites: ~20 one-liners at the events above (most already have Log() calls
  next to them — audit call added alongside, not replacing).
- Ops access: files readable over SSH; `RUNBOOK.md` gets a "pull audit logs"
  section; chain-verify script (`deploy/tools/verify_audit.py` or shell).
- Phase 3 (later): daily batched upload of closed audit files via the existing
  encrypted API; server-side retention 6 years.

## 6. Open questions for review
1. Event list complete? Anything clinic-specific to add (e.g. operator login if
   that concept arrives)?
2. On-device retention 12 months OK for pilot?
3. Should `WIFI_CHANGE` record SSIDs, or is site network info sensitive in your
   deployments?
4. `DANGER_FLAG` as an audit event — wanted, or clinical-record territory?
