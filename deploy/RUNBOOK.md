# VitalsChair — Operations Runbook

Field guide for deploying, updating, monitoring, and fixing a chair. For the full
fresh-deploy walkthrough see [README.md](README.md); for SSH access see
[SSH_SETUP.md](SSH_SETUP.md).

---

## At a glance

| What | Detail |
|---|---|
| Board | Toradex Verdin iMX8MP, TorizonCore |
| App containers | `vitaldata` (backend), `weston` (display), `crankgui` (GUI) |
| Lifeline | `tailscale` (separate stack, remote access) |
| Compose dir (canonical) | `/home/torizon/vitalschair/` (`docker-compose.yml` + `.env`) |
| Tailscale dir | `/home/torizon/tailscale/` |
| Network | `torizon_crank-net` (bridge) |
| Boot persistence | container `restart:` policies (NOT the `/var/sota` boot service — keep it empty) |
| Backend image | `anuragbluai/vitalschair-prod-v1:1.0.NN` (prod) / `:dev` |
| GUI image | `anuragbluai/crankgui_prod:Rev-NN` |
| Access | SSH key-only over LAN / Tailscale |

---

## First thing when you connect — health check

```bash
docker ps                                  # all 4 Up? note the image tags
docker stats --no-stream                   # CPU/mem per container
cd /home/torizon/vitalschair
docker compose ps
```
Quick per-area checks:
```bash
docker exec vitaldata nmcli device wifi list   # WiFi alive
docker exec crankgui date                       # clock correct (local tz)
docker logs --tail 50 vitaldata                 # recent backend activity
```

---

## Common tasks

### Update the backend
```bash
cd /home/torizon/vitalschair
nano .env                                  # VITALDATA_TAG=1.0.NN  (new version)
docker compose pull vitaldata
docker compose up -d                       # recreates only vitaldata
docker ps | grep vitaldata                 # confirm new tag
```

### Update the GUI
```bash
cd /home/torizon/vitalschair
nano docker-compose.yml                     # crankgui image -> Rev-NN (NEW tag)
docker compose pull crankgui
docker compose up -d
```
> If you **reused** a GUI tag instead of cutting a new one, you MUST
> `docker compose pull crankgui` (the cached image is stale) and may need
> `docker compose up -d --force-recreate crankgui`. Prefer a new tag each build.

### Restart / logs
```bash
docker compose restart vitaldata           # or crankgui / weston
docker compose logs -f vitaldata
docker logs -f --tail 0 vitaldata          # live, from now
```

---

## Troubleshooting (symptom → cause → fix)

| Symptom | Cause | Fix |
|---|---|---|
| **WiFi/Ethernet list empty**, logs: `Could not create NMClient object: ... No such file or directory` | container can't reach host D-Bus / nmcli | compose must mount `- /run/dbus:/run/dbus` and set `DBUS_SYSTEM_BUS_ADDRESS=unix:path=/run/dbus/system_bus_socket`. Recreate: `docker compose up -d vitaldata`. Verify host: `systemctl is-active NetworkManager` |
| **After reboot, old/wrong containers come back** (e.g. `vitaldata:dev`, GUI as `Rev-05`) | a stale compose at `/var/sota/storage/docker-compose/` is run by TorizonCore's boot service, overriding your deploy | `sudo rm -f /var/sota/storage/docker-compose/docker-compose.yml /var/sota/storage/docker-compose/.env` then redeploy from `/home/torizon/vitalschair`. Check: `sudo ls /var/sota/storage/docker-compose/` must be empty |
| **vitaldata runs `:dev` not the version** | `.env` missing/not read; compose default is `${VITALDATA_TAG:-dev}` | ensure `/home/torizon/vitalschair/.env` has `VITALDATA_TAG=1.0.NN`, then `docker compose up -d vitaldata`. For boards where `.env` isn't honored, pin the tag directly in the compose |
| **tailscale restart-loops**, logs: `not a valid DNS label: contains invalid character ' '` | `TS_HOSTNAME` has a space | edit `/home/torizon/tailscale/.env`, set `TS_HOSTNAME=vitalschair-<serial>` (letters/digits/hyphens, NO spaces), then `docker compose up -d --force-recreate tailscale` |
| **tailscale won't auth** | bad/expired/single-use key | put a **reusable** key in `/home/torizon/tailscale/.env`, `docker compose up -d --force-recreate tailscale`, watch `docker logs -f tailscale` for "Success" |
| **`compose pull` fails on crankgui** | the `Rev-NN` tag isn't on Docker Hub yet | finish the GUI build/push first, or temporarily point the compose at an existing tag |
| **GUI clock wrong/frozen** | container timezone not following host, or GUI tick timer not running | host: `sudo timedatectl set-timezone <Zone>`; compose mounts `/etc/localtime` + `/etc/timezone` ro; GUI must use a repeating `gre.timer_set_interval` clock tick. `docker exec crankgui date` to confirm |
| **"Send OTP" → "Device not approved yet"** | device not approved in admin console | approve it in `vitalchair-admin`; until then patient flow is blocked **by design** (see Device lifecycle) |
| **"Send OTP" → "Server not running" / "Cannot reach server"** | the OTP API endpoint is down/unreachable | check connectivity + the API service; the device fails fast (~8s) by design instead of hanging |
| **OTP verifies but name/age/gender don't autofill** | backend extracted empty patient data, OR GUI parse | live-capture: `docker logs -f --tail 0 vitaldata 2>&1 \| grep -iE "OTP Activity\|Patient data\|Sent patient data\|schema="`; if `Sent patient data: name=` is empty → server response field names; if populated → GUI parse |
| **A container keeps restarting** | crash loop | `docker logs --tail 100 <name>` for the error; `docker inspect <name> --format '{{.State.Health.Status}}'` for healthcheck state |
| **Out of disk** | image/log buildup | `docker image prune -f`; logs are capped (json-file 10–20m ×3) but check `df -h` |

---

## Device lifecycle (why a fresh board "does nothing")

1. Boots → `vitaldata` **registers** with the server → waits.
2. Shows in **admin console** as *pending*; patient flow blocked.
3. **Admin approves** the device.
4. Device fetches + caches config (`/data/config_cache.json`) → patient flow live.
5. Config/version mismatch → auto re-fetch. Secret key refreshes on a 60-min TTL
   (24h stale / 7-day persisted fallback if the API is down).

**New board not working?** First check it's **approved** in the admin console.

---

## Logs & audit trail

Both live on the persistent `/data` volume (survive updates). Read via:
```bash
docker exec vitaldata sh -c 'ls -la /data/logs /data/audit'
docker exec vitaldata sh -c 'tail -50 /data/logs/vitalschair_$(date +%F).log'   # operational log (30-day retention)
docker exec vitaldata sh -c 'tail -20 /data/audit/audit_$(date +%Y-%m).jsonl'   # audit trail (12-month retention)
```
- **Operational logs** (`/data/logs`): PHI-masked debug/info; verbosity via `LOG_LEVEL` env (default Info).
- **Audit trail** (`/data/audit`): append-only JSON lines, hash-chained (each record's
  `prev` = previous record's `hash` — a broken chain means tampering/data loss).
  Events: boot/update, registration/approval/config, OTP request/verify, session
  start/end, measurements (types only), vitals submit/queue/sync, voice sessions,
  danger flags, WiFi changes, key refreshes. **Never contains PHI** — patient refs
  are salted hashes. Design: `docs/AUDIT_LOG_DESIGN.md`.

---

## Recovery / escalation

- **Locked out of SSH** → physical **serial/USB console** on the Verdin (only path if keys are lost; see [SSH_SETUP.md](SSH_SETUP.md)).
- **Board won't boot containers** → `sudo systemctl status docker`, then `docker compose up -d` from `/home/torizon/vitalschair`.
- **Total reset of the app stack:**
  ```bash
  cd /home/torizon/vitalschair
  docker compose down
  docker compose pull && docker compose up -d
  ```
  (Tailscale is a separate stack and is unaffected — never run a blanket
  `compose down` on tailscale remotely.)

---

## Quick command cheat sheet

```bash
docker ps                                   # what's running + tags
docker compose ps                           # (from /home/torizon/vitalschair)
docker logs -f --tail 50 vitaldata          # follow backend logs
docker exec vitaldata nmcli device wifi list# wifi check
docker exec crankgui date                   # clock/timezone check
docker compose pull <svc> && docker compose up -d   # update a service
docker compose restart <svc>                # restart one container
sudo timedatectl set-timezone <Zone>        # set device timezone
```
