# VitalsChair — FDA Submission Roadmap (living doc)

**Purpose:** internal planning map for FDA clearance — the full document set,
status, owners, standards, and the decisions that gate everything.

> ⚠️ **Not regulatory advice.** Classification, pathway, predicate, and clinical
> strategy are determinations a qualified **Regulatory Affairs professional / FDA
> consultant must make/confirm.** This doc organizes the work; it does not replace
> RA counsel. Engage one before committing to a pathway.

Legend: 🔴 not started · 🟡 in progress / material exists · 🟢 drafted · ⛔ blocked on a gating decision

---

## 0. Gating decisions (decide FIRST — everything keys off these)

| # | Decision | Status | Notes |
|---|---|---|---|
| 1 | **Intended Use / Indications for Use** (one sentence) | 🔴 TBD | Diagnostic vs monitoring vs wellness; Rx vs OTC. Drives class, pathway, claims, testing. |
| 2 | **Device classification & product code(s)** | 🔴 TBD | Multi-parameter monitor → likely **Class II**; each modality has its own product code/regulation. |
| 3 | **Regulatory pathway** | 🔴 TBD | Likely **510(k)**; **De Novo** if no predicate; AI may differ. |
| 4 | **Predicate device(s)** (for 510(k)) | 🔴 TBD | Need a legally-marketed, substantially-equivalent device per function. |
| 5 | **AI/BluNotes scope** | 🔴 TBD | Separate SaMD submission or bundled? Triggers GMLP + Predetermined Change Control Plan (PCCP). |
| 6 | **Markets** | 🔴 TBD | US (FDA) only, or + India (CDSCO/DPDP) + EU (MDR)? Affects parallel docs. |
| 7 | **QMS in place?** (ISO 13485) | 🔴 TBD | Design Controls + DHF require a QMS. |
| 8 | **RA consultant engaged?** | 🔴 TBD | Strongly recommended before Phase 1. |

---

## 1. Regulatory strategy (to confirm with RA)

VitalsChair is a **multi-parameter patient monitor** (SpO2, ECG, NIBP, temperature,
body composition) **+ AI/ML software functions** (AI Insights, BluNotes). Two
likely workstreams:

- **The measurement device** — each modality is a regulated function with its own
  standard + product code; most are **Class II / 510(k)** with established predicates.
- **The AI/ML software (SaMD)** — may need its own determination, GMLP alignment,
  and a **Predetermined Change Control Plan** so model updates don't require a new
  submission each time.

> Confirm with RA whether this is one 510(k), multiple, or a 510(k) + separate SaMD.

---

## 2. Submission document set (the backbone)

### 2.1 Quality system & design controls
| Document | Standard / Ref | Status | Owner |
|---|---|---|---|
| Quality Manual / procedures | ISO 13485, 21 CFR 820 | 🔴 | — |
| Design History File (DHF) structure | 21 CFR 820.30 | 🔴 | — |
| Design & Development Plan | 820.30(b) | 🔴 | — |

### 2.2 Risk management
| Risk Management File (plan, hazard analysis, FMEA, RMR) | ISO 14971 | 🔴 | — |

### 2.3 Software (IEC 62304)
| Document | Status | Notes |
|---|---|---|
| Software level of concern / safety class | 🔴 | likely Class B/C |
| Software Requirements Spec (SRS) | 🔴 | |
| **Software architecture / description** | 🟡 | have: `docs/GUI_BACKEND_TCP_PROTOCOL.md`, `HARDWARE_MODULES.md`, deploy docs |
| Detailed design | 🔴 | |
| V&V / test protocols + reports | 🔴 | |
| Traceability matrix (req→design→test) | 🔴 | |
| Unresolved anomalies list | 🔴 | |

### 2.4 Cybersecurity (FDA Premarket Cybersecurity, 2023)
| Document | Status | Notes |
|---|---|---|
| Security risk assessment / threat model | 🟡 | encryption + secret-key + SSH hardening designed |
| **SBOM** | 🟡 | derivable from `.csproj` packages + Docker base images |
| Architecture views (data flow, trust boundaries) | 🟡 | deploy + protocol docs cover much of it |
| Security controls (encryption, auth, update) | 🟡 | AES-256-CBC payloads, server-fetched keys, key-only SSH |
| Vulnerability mgmt / patch plan | 🔴 | |

### 2.5 Usability / human factors
| HF engineering file, use-related risk, validation | IEC 62366-1 | 🔴 | — |

### 2.6 Electrical safety & EMC (accredited test lab)
| IEC 60601-1 (basic safety), 60601-1-2 (EMC) | 🔴 | external lab — schedule early, long lead time |

### 2.7 Modality-specific performance
| Modality | Standard | Status |
|---|---|---|
| SpO2 | ISO 80601-2-61 | 🔴 |
| ECG (5/12-lead) | IEC 60601-2-25 / -2-27 | 🔴 |
| NIBP | ISO 80601-2-30 | 🔴 |
| Temperature | ISO 80601-2-56 | 🔴 |
| Body composition | (determine applicable standard) | 🔴 |

### 2.8 AI/ML (if in scope)
| GMLP alignment, model description, training/validation data, bias analysis, PCCP | FDA AI/ML guidance | 🔴 | — |

### 2.9 Clinical / performance evidence
| Clinical evaluation / performance testing per modality | 🔴 | scope set by intended use + predicates |

### 2.10 Labeling
| IFU, device labels, claims (must match cleared indications) | 21 CFR 801 | 🔴 | — |

---

## 3. In-house vs external

| We can draft in-house (with the material we have) | Needs external / specialist |
|---|---|
| Software description & architecture | Electrical safety / EMC testing (accredited lab) |
| Cybersecurity file + SBOM + threat model | Modality performance/clinical testing |
| Data-flow & PHI handling map | Predicate analysis & substantial-equivalence (RA) |
| Risk management seed (hazard analysis) | Classification & pathway determination (RA) |
| Requirements & traceability scaffold | Human-factors validation (HF specialist) |

---

## 4. Phased plan

**Phase 0 — Foundations (weeks 1–4)**
1. Draft **Intended Use / Indications for Use** (with RA).
2. **Engage RA consultant** → confirm classification, pathway, predicate(s).
3. Stand up / confirm **QMS (ISO 13485)** and DHF structure.
4. In parallel (no dependency): begin **Software Description**, **Cybersecurity/SBOM**,
   **data-flow map**, **risk-management skeleton** — material is fresh.

**Phase 1 — Core technical file (months 2–4)**
- Complete IEC 62304 software docs + traceability.
- Risk management file (ISO 14971).
- Cybersecurity file finalized.
- Usability/HF plan.

**Phase 2 — Testing (months 3–8, long lead times — start booking early)**
- Electrical safety + EMC at an accredited lab.
- Modality performance testing.
- AI/ML validation + PCCP (if in scope).

**Phase 3 — Assemble & submit**
- Substantial-equivalence comparison, labeling, eSTAR/eCopy assembly, RA review, submit.

---

## 5. Standards quick-reference
ISO 13485 · 21 CFR 820.30 · ISO 14971 · IEC 62304 · IEC 62366-1 · IEC 60601-1 ·
IEC 60601-1-2 · ISO 80601-2-61 (SpO2) · IEC 60601-2-25/-27 (ECG) · ISO 80601-2-30
(NIBP) · ISO 80601-2-56 (temp) · FDA Premarket Cybersecurity (2023) · FDA AI/ML / GMLP / PCCP.

---

## 6. Immediate next actions
- [ ] Write the one-line **Intended Use** (gates everything).
- [ ] Engage an **RA consultant**.
- [ ] Confirm **markets** (US only vs +CDSCO/+MDR).
- [ ] Start in-house: **Software Description + Cybersecurity/SBOM** (we have the material).
- [ ] Book an **electrical-safety/EMC test lab** slot (long lead time).
