# VitalsChair — Deployment & Operations (start here)

Entry point for deploying and running a chair. New to this? Read in this order:

| Doc | What it covers |
|---|---|
| **[README.md](README.md)** | Deploy a fresh board, clean a board, update a service |
| **[RUNBOOK.md](RUNBOOK.md)** | Monitor + troubleshoot (symptom → cause → fix table) |
| **[SSH_SETUP.md](SSH_SETUP.md)** | Switch a board to key-only SSH access |

## What runs on a board
- **App stack** (`/home/torizon/vitalschair/`): `vitaldata` (backend),
  `weston` (display), `crankgui` (GUI).
- **Lifeline** (`/home/torizon/tailscale/`): `tailscale` — separate so an app
  `down` can't cut remote access.
- Boot persistence = container `restart:` policies. The TorizonCore
  `/var/sota/storage/docker-compose/` boot path is kept **empty** on purpose
  (a stale file there causes the reboot split-brain — see RUNBOOK).

---

## Pilot checklist (per board)

1. **Build & push images** (CI): backend `1.0.NN` (from `main`), GUI `Rev-NN`.
2. **Deploy** the app stack — [README.md](README.md) §B:
   - copy `app/docker-compose.yml` + `.env` to `/home/torizon/vitalschair/`
   - set `VITALDATA_TAG=1.0.NN`; create `torizon_crank-net`; `compose pull && up -d`
3. **Set timezone:** `sudo timedatectl set-timezone <Zone>`
4. **Tailscale** — [README.md](README.md) §D: copy `tailscale/` files, set a
   reusable `TS_AUTHKEY` + a **space-free** `TS_HOSTNAME`, `compose up -d`.
5. **Approve the device** in the admin console (`vitalchair-admin`) — until then
   the patient flow is blocked by design.
6. **Verify:**
   - `docker ps` → all Up, correct tags
   - `docker exec vitaldata nmcli device wifi list` → WiFi works
   - `docker exec crankgui date` → correct local time
   - Send OTP → "Sending…" → enter OTP → name/age/gender autofill
7. **Reboot test:** `sudo reboot`, then `docker ps` must show the **same**
   versions (no `:dev` / old `Rev-` — that means a stale `/var/sota` file).
8. **Harden SSH** — [SSH_SETUP.md](SSH_SETUP.md): keys-only, disable password.
   Back up the private key; disable Tailscale key expiry.

## Remote access
- Primary: **Tailscale** (key expiry disabled, restart-policy, persistent volume).
- Fallbacks if Tailscale is down: **on-site LAN SSH** (`192.168.x`), then the
  **physical serial console** (only path if SSH keys are lost). For fleet scale,
  add an independent backup tunnel (e.g. autossh reverse tunnel to a VPS).
