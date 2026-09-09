# SSH Key Setup (per board)

Switches a chair from password SSH to **key-only** login — safe even on
networks/routers we don't control (no password to brute-force).

> 🔑 **Golden rule:** keep your current SSH session open the whole time, and
> verify key login in a *second* window **before** disabling passwords. A mistake
> otherwise needs the physical serial console to recover.

---

## One-time, on YOUR laptop — make a key (skip if you already have one)

```bash
ls ~/.ssh/id_ed25519.pub                 # if it exists, skip this step
ssh-keygen -t ed25519 -C "vitalschair-admin"
# Enter = default path (~/.ssh/id_ed25519); set a passphrase (recommended)
```
The pair is saved automatically:
- `~/.ssh/id_ed25519`      → **private key** (the secret — never share/commit)
- `~/.ssh/id_ed25519.pub`  → **public key** (safe to copy anywhere)

**Back up `id_ed25519` now** (password manager / encrypted USB). With passwords
disabled, this key is the only way in. Optionally add a 2nd admin's `.pub` to each
board so access doesn't depend on one laptop.

---

## Per board

Replace `<DEVICE>` with the board IP (e.g. `192.168.8.136`) or its Tailscale name.

### 1. Copy your public key to the board
```bash
# Mac/Linux:
ssh-copy-id torizon@<DEVICE>             # enter the device password one last time

# Windows PowerShell (no ssh-copy-id):
type $env:USERPROFILE\.ssh\id_ed25519.pub | ssh torizon@<DEVICE> "mkdir -p ~/.ssh && cat >> ~/.ssh/authorized_keys && chmod 700 ~/.ssh && chmod 600 ~/.ssh/authorized_keys"
```

### 2. ⚠️ TEST key login in a NEW window (do not skip)
```bash
ssh torizon@<DEVICE>                      # must log in WITHOUT the device password
```
If it still asks for the device password → STOP, fix step 1 before continuing.

### 3. Disable password auth (in your already-open session)
```bash
sudo sed -i 's/^#*PasswordAuthentication.*/PasswordAuthentication no/' /etc/ssh/sshd_config
sudo sed -i 's/^#*PermitRootLogin.*/PermitRootLogin no/'                /etc/ssh/sshd_config
sudo sed -i 's/^#*PubkeyAuthentication.*/PubkeyAuthentication yes/'     /etc/ssh/sshd_config

# check no override hides in a drop-in (set to "no" there too if found):
sudo grep -rE "PasswordAuthentication" /etc/ssh/sshd_config /etc/ssh/sshd_config.d/ 2>/dev/null
```

### 4. Validate and restart sshd (does NOT drop your session)
```bash
sudo sshd -t                              # must print nothing (no errors)
systemctl list-units | grep ssh           # find the service name: ssh OR sshd
sudo systemctl restart ssh                # use whichever name showed up
```

### 5. Verify both ways (keep the old session open!)
```bash
ssh torizon@<DEVICE>                       # key login still works  ✓
ssh -o PreferredAuthentications=password -o PubkeyAuthentication=no torizon@<DEVICE>
# expected: "Permission denied (publickey)."  → passwords are off  ✓
```
Key works + password denied → done. Now close the old session.

---

## Notes
- Same key works for every board — copy the one `.pub` to each; the private key
  never leaves your laptop.
- Lockout recovery = physical serial/USB console on the Verdin (that's why we test
  before disabling passwords).
- Post-pilot / fleet: consider **Tailscale SSH** for keyless, centrally-controlled,
  audit-logged access (also feeds the HIPAA audit trail).
