# VitalsChair — Production Deployment

Runs the device off a managed Docker Compose stack instead of hand-typed
`docker run` commands.

## ⚠️ Golden rule: ONE compose location per board

A board must have **exactly one** compose file driving it. If a second copy
exists (e.g. a stale one under `/var/sota/storage/docker-compose/` that
TorizonCore's boot service runs), they fight on reboot and the wrong/older one
can win — you redeploy a new version, reboot, and the old one comes back.

**This guide uses one canonical location: `/home/torizon/vitalschair/`**, with
boot persistence from each container's `restart:` policy. We explicitly
**neutralize** the TorizonCore boot-service path so it can never compete.

| Stack | Dir on board | Containers |
|---|---|---|
| **App** | `/home/torizon/vitalschair/` | vitaldata, weston, crankgui |
| **Lifeline** | `/home/torizon/tailscale/` | tailscale (separate — see bottom) |

---

## Device lifecycle (register → approve → run)

A brand-new device does **not** serve patients until an admin approves it:

1. **Register** — on first boot `vitaldata` registers with the server, writes
   `/data/registration.json`, and waits.
2. **Pending** — the device appears in the admin console (`vitalchair-admin`) as
   *pending*. While pending, the patient flow is blocked; tapping "send OTP"
   shows *"Device not approved yet. Please ask the admin to approve this device."*
3. **Admin approves** in the console.
4. **Config fetch** — on the next status-check the device pulls its config (API
   URLs, secret-key URL, Gemini voice config, HL7) and caches it to
   `/data/config_cache.json`. Patient flow goes live.
5. **Self-healing** — config/version mismatch → auto re-fetch. The payload
   encryption secret key refreshes on a 60-min TTL (24h stale / 7-day persisted
   fallback if the API is down).

So after deploying a fresh board, **approve it in the admin console** — until
then the "waiting / not approved" state is expected.

---

## A. Clean a board to a known-good state

Run this first on any board that's in a messy/split-brain state:
```bash
# stop & remove ALL app containers (any names from past deploys)
docker rm -f vitaldata weston crankgui Rev-05 Rev-06 2>/dev/null

# neutralize the TorizonCore boot-service compose so it can't run a stale file
# (removing the file makes the service skip on boot — ConditionPathExists fails)
sudo rm -f /var/sota/storage/docker-compose/docker-compose.yml \
           /var/sota/storage/docker-compose/.env

# drop old images you no longer run (keep the ones your compose pins)
docker image prune -f
# optionally remove specific old tags:
# docker rmi anuragbluai/vitalschair-prod-v1:dev anuragbluai/crankgui_prod:Rev-06
```

---

## B. Fresh device — step by step

Assumes a blank TorizonCore board with internet and the images pushed to Docker
Hub (`vitalschair-prod-v1`, `crankgui_prod`, `weston-vitalschair`).

### 1. Copy the app files to the canonical dir
From your dev machine (repo checkout):
```bash
ssh torizon@<BOARD-IP> 'mkdir -p /home/torizon/vitalschair'
scp deploy/app/docker-compose.yml deploy/app/.env.example \
    torizon@<BOARD-IP>:/home/torizon/vitalschair/
```

### 2. Set the image version (`.env`)
```bash
ssh torizon@<BOARD-IP>
cd /home/torizon/vitalschair
cp .env.example .env
nano .env                       # set VITALDATA_TAG=1.0.NN  (a real version, not 'dev')
```
> Production versions (`1.0.NN`) come from the **main** branch build; `:dev` /
> `1.0.NN-dev` are development builds. The crank GUI is pinned in the compose
> (`crankgui_prod:Rev-NN`) on a stable service named `crankgui`.

### 3. Host timezone (so the GUI clock shows local time)
```bash
sudo timedatectl set-timezone Asia/Kolkata        # or the device's region
```

### 4. Create what the containers expect
```bash
sudo mkdir -p /home/torizon/audio_shared /mnt/shared_ipc
docker network create torizon_crank-net
```

### 5. Pull and start
```bash
cd /home/torizon/vitalschair
docker compose pull
docker compose up -d
docker compose ps                 # vitaldata, weston, crankgui all Up
```

### 6. Verify — including a REBOOT
```bash
docker ps | grep vitaldata        # confirm the tag (1.0.NN, not dev)
docker exec crankgui date         # local time (IST)
docker exec vitaldata nmcli device wifi list   # WiFi works

sudo reboot
# after it returns — the critical check:
docker ps                         # MUST be the same versions, same names
```
If reboot brings back different/older containers, a second compose is still
competing — re-run section **A** (the `/var/sota` neutralize step).

### 7. Approve the device
Open the admin console and approve this device (see lifecycle above).

---

## C. Updating a service (immutable tags)

Edit the one canonical compose, then pull+up just that service:
```bash
cd /home/torizon/vitalschair
nano docker-compose.yml           # bump image, e.g. crankgui_prod:Rev-08 or .env VITALDATA_TAG
docker compose pull crankgui      # or: docker compose pull vitaldata
docker compose up -d              # recreates only what changed
```
Always bump to a **new** tag per build (`Rev-08`, `1.0.NN`) so a tag always maps
to one exact image — clean rollbacks, unambiguous `pull`.

---

## D. Tailscale lifeline (remote access)

Separate dir/lifecycle so an app `down` can never cut your access.
```bash
ssh torizon@<BOARD-IP> 'mkdir -p /home/torizon/tailscale'
scp deploy/tailscale/docker-compose.yml deploy/tailscale/.env.example \
    torizon@<BOARD-IP>:/home/torizon/tailscale/

ssh torizon@<BOARD-IP>
cd /home/torizon/tailscale
cp .env.example .env
nano .env
#   TS_AUTHKEY = reusable key (Tailscale admin → Settings → Keys)
#   TS_HOSTNAME = vitalschair-<serial>  — DNS label only: letters/digits/hyphens,
#                 NO SPACES (a space → "not a valid DNS label" restart loop)
docker compose up -d
docker compose logs -f tailscale  # should reach "Success."
```
⚠️ Never run a blanket `docker compose down` on the **tailscale** stack remotely.

---

## Notes
- `.env` files hold secrets/per-board values and are **git-ignored** — only the
  `.env.example` templates are committed.
- The app `docker-compose.yml` is identical on every board; only `.env` differs.
- Boot persistence is via each container's `restart:` policy. We deliberately do
  NOT use the TorizonCore `docker-compose.service` boot path, to avoid the
  two-files split-brain problem (section A neutralizes it).
