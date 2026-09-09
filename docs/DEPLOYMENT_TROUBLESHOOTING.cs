/*
 * DEPLOYMENT TROUBLESHOOTING GUIDE
 * ================================
 * 
 * Issue: In deployed mode, config.json is missing and device enters infinite loop
 * Root Cause: File system permission issues or atomic write failures in container
 * 
 * FIXES IMPLEMENTED:
 * 1. DeviceDataFileManager - Robust file operations with validation
 * 2. State tracking - Prevents infinite config fetch loops
 * 3. File write verification - Confirms files were actually created
 * 4. Backup/restore - Recovers from corrupted files
 * 5. Permission checking - Validates directory is writable
 * 6. Detailed logging - Helps diagnose what went wrong
 */

// ============================================================
// STEP 1: INITIALIZATION AT STARTUP (Program.cs)
// ============================================================

/*
// Add this to your Program.cs before starting the app:

Console.WriteLine("Initializing device...");

// 1. Initialize data directory and validate permissions
if (!await DeviceDataFileManager.InitializeDataDirectoryAsync())
{
    Console.WriteLine("❌ CRITICAL: Data directory initialization failed!");
    Console.WriteLine("Device cannot proceed without writable /data directory");
    Environment.Exit(1);
}

// 2. Run diagnostics
DeviceDataFileManager.PrintDiagnostics();

// 3. Ensure device is registered
try
{
    await DeviceRegistration.EnsureRegisteredAsync();
}
catch (Exception ex)
{
    Console.WriteLine($"❌ Device registration failed: {ex.Message}");
    Environment.Exit(1);
}

Console.WriteLine("✓ Device initialization complete");
app.Run();
*/

// ============================================================
// STEP 2: Docker Configuration for /data Volume
// ============================================================

/*
Dockerfile or docker-compose.yml:

# Ensure /data directory exists and has proper permissions
RUN mkdir -p /data && chmod 755 /data

# Run as non-root (recommended for security)
USER appuser

# Volume mount in docker-compose:
services:
  vitalschair:
    volumes:
      - device_data:/data  # Named volume for persistence
      
volumes:
  device_data:
    driver: local
*/

// ============================================================
// STEP 3: KUBERNETES ConfigMap (if using K8s)
// ============================================================

/*
apiVersion: v1
kind: PersistentVolumeClaim
metadata:
  name: device-data-pvc
spec:
  accessModes:
    - ReadWriteOnce
  resources:
    requests:
      storage: 1Gi
  storageClassName: standard

---
apiVersion: v1
kind: Pod
metadata:
  name: vitalschair
spec:
  containers:
  - name: vitalschair
    image: vitalschair:latest
    volumeMounts:
    - name: device-data
      mountPath: /data
    securityContext:
      runAsUser: 1000
      runAsGroup: 1000
  volumes:
  - name: device-data
    persistentVolumeClaim:
      claimName: device-data-pvc
*/

// ============================================================
// STEP 4: DEBUGGING CHECKLIST
// ============================================================

/*
When config.json is missing in production:

1. Check file system permissions:
   docker exec <container> ls -la /data/
   
   Expected output:
   -rw-r--r--  user  group  registration.json
   -rw-r--r--  user  group  config_cache.json
   
2. Check if /data is writable:
   docker exec <container> touch /data/.test && rm /data/.test && echo "✓ Writable"
   
3. Check device logs for errors:
   docker logs <container> | grep -i "registration\|config\|error"
   
4. Check if Docker volume is mounted:
   docker inspect <container> | grep -A 10 "Mounts"
   
5. Restart device and check fresh:
   docker restart <container>
   docker logs <container> | head -100
   
6. Manual file creation test:
   docker exec <container> bash -c 'echo "{\"test\": \"data\"}" > /data/test.json && cat /data/test.json'
*/

// ============================================================
// STEP 5: FILE WRITE VERIFICATION
// ============================================================

/*
The new TryParseAndSaveResponse method now:

1. Validates JSON can be parsed
2. Writes to temp file first
3. Verifies temp file exists and has content
4. Moves temp file to final location atomically
5. Verifies final file exists and has content
6. Returns true/false indicating success

If any step fails, detailed log message explains why:
   ✓ Successful: "File written successfully: /data/config_cache.json"
   ✗ Failed: "Temp file write failed" or "Final file is empty" etc.
*/

// ============================================================
// STEP 6: INFINITE LOOP PREVENTION
// ============================================================

/*
Device now tracks config fetch attempts:

private static int _configFetchAttempts = 0;
private const int MAX_CONFIG_FETCH_ATTEMPTS = 5;

Flow:
1. First boot: registration.json created ✓
2. Config missing: fetch from server (attempt 1)
3. Fetch failed or file write failed: retry
4. After 5 attempts: ABORT instead of infinite loop
5. Device continues with in-memory config if available

Previous behavior (infinite loop):
- Would keep fetching config forever
- Never marked the failure
- Device would restart itself repeatedly

New behavior (bounded retries):
- Attempts config fetch 5 times maximum
- Logs each attempt with attempt number
- Stops and continues with degraded config after max attempts
- Diagnostic output shows what went wrong
*/

// ============================================================
// STEP 7: BACKUP AND RECOVERY
// ============================================================

/*
When saving files, backup is created first:

1. Main file exists: Create .bak copy
2. Write to temp file
3. Move temp to main location
4. If main file fails to read later: Try .bak

Example scenario:
- Device crashes during file write
- .bak file preserved from previous successful write
- On restart: Load from .bak to recover
- Next successful fetch: Overwrite both

Backup files location:
- /data/registration.json
- /data/registration.json.bak (backup)
- /data/config_cache.json
- /data/config_cache.json.bak (backup)
*/

// ============================================================
// STEP 8: LOGGING FOR DIAGNOSTICS
// ============================================================

/*
Key log messages to watch for:

✓ GOOD signs:
  "✓ Data directory validated: /data"
  "✓ File written successfully: /data/config_cache.json"
  "✓ Registration data loaded for device"
  "Config fetched and cached successfully"

⚠️  WARNING signs:
  "⚠️  No write permission for /data"
  "⚠️  Config cache file missing or empty"
  "⚠️  Config fetch succeeded but file save failed"

❌ CRITICAL errors:
  "❌ Directory initialization failed"
  "❌ CRITICAL: Config fetch exceeded maximum attempts"
  "❌ Failed to write registration file"
  "❌ Temp file is empty"
  "❌ Final file write failed or empty"
*/

// ============================================================
// STEP 9: TESTING FILE MANAGER DIRECTLY
// ============================================================

/*
// Test file manager in isolation:

public static async Task TestFileManagerAsync()
{
    Console.WriteLine("Testing DeviceDataFileManager...");
    
    // Test 1: Initialize directory
    bool initSuccess = await DeviceDataFileManager.InitializeDataDirectoryAsync();
    Console.WriteLine($"1. Initialize: {(initSuccess ? "✓" : "✗")}");
    
    // Test 2: Write file
    string testJson = JsonSerializer.Serialize(new { test = "data", timestamp = DateTime.UtcNow });
    bool writeSuccess = DeviceDataFileManager.WriteJsonFile("/data/test.json", testJson);
    Console.WriteLine($"2. Write: {(writeSuccess ? "✓" : "✗")}");
    
    // Test 3: Read file
    string readContent = DeviceDataFileManager.ReadJsonFile("/data/test.json");
    bool readSuccess = !string.IsNullOrEmpty(readContent);
    Console.WriteLine($"3. Read: {(readSuccess ? "✓" : "✗")}");
    
    // Test 4: Validate read content
    bool contentMatches = readContent == testJson;
    Console.WriteLine($"4. Validate content: {(contentMatches ? "✓" : "✗")}");
    
    // Test 5: Delete
    bool deleteSuccess = DeviceDataFileManager.DeleteFile("/data/test.json");
    Console.WriteLine($"5. Delete: {(deleteSuccess ? "✓" : "✗")}");
    
    // Test 6: Diagnostics
    DeviceDataFileManager.PrintDiagnostics();
}
*/

// ============================================================
// STEP 10: PRODUCTION DEPLOYMENT CHECKLIST
// ============================================================

/*
Before deploying to production:

[ ] 1. Docker image includes /data directory creation
[ ] 2. Volume mount for /data is configured
[ ] 3. Directory permissions allow writes (755 or similar)
[ ] 4. Container runs with correct user/group
[ ] 5. Program.cs calls InitializeDataDirectoryAsync()
[ ] 6. Program.cs calls EnsureRegisteredAsync()
[ ] 7. Diagnostic output is logged on startup
[ ] 8. Monitor logs for file write failures
[ ] 9. Test volume persistence (restart container, data remains)
[ ] 10. Test device registration (first boot creates both files)

Quick validation:
1. Deploy container
2. Check logs for "✓ Data directory validated"
3. Verify both files exist: ls -la /data/
4. Check registration.json has device_id and jwt
5. Check config_cache.json has API endpoints
6. Restart container and verify files persist
7. Check logs show "Device already registered"
*/

// ============================================================
// STEP 11: HANDLING PARTIAL FAILURES
// ============================================================

/*
Scenarios handled by new code:

Scenario A: registration.json OK, config_cache.json missing
  → Fetch config from server
  → Save to /data/config_cache.json
  → If write fails: Log error but continue with in-memory config
  → Device remains functional but not fully persisted

Scenario B: registration.json missing, config_cache.json exists
  → Device re-registers (new device_id generated)
  → New registration saved
  → Existing config might be used if valid

Scenario C: Both files missing
  → Device registers as new device
  → Fetches config for first time
  → Creates both files

Scenario D: File write always fails (permission issue)
  → Device will fail fast with clear error message
  → Will not enter infinite loop
  → Operator can fix permissions and restart
*/

// ============================================================
// DONE - PRODUCTION READY
// ============================================================
