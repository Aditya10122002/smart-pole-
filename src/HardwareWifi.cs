

// HardwareWifi.cs — WiFi & Ethernet Management Interface
// BluAI Pvt. Ltd. | VitalsChair™ Backend
//
// Unified network interface managing:
//  - WiFi connectivity (connection, scan, state)
//  - Ethernet status monitoring (detection, IP, MAC, speed)
//  - Real-time state broadcast to Lua UI on port 2002
//
// Listens on port 2002 (unified WiFi + Ethernet communication).
// Power-cycle persistence: saves connected SSID to
// wifi_backend_state.json so Lua UI restores on reboot.
// ============================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Unified network hardware interface providing WiFi connection
/// management, Ethernet status monitoring, and real-time state
/// broadcasting to Lua clients on port 2002.
/// </summary>
public static class HardwareWifi
{
    // ────────────────────────────────────────────────────────
    // CONFIGURATION
    // ────────────────────────────────────────────────────────
    private const int    WIFI_PORT      = 9000;  // WiFi management
    private const int    ETHERNET_PORT  = 2002;  // Ethernet status broadcast
    private const string STATE_FILE     = "wifi_backend_state.json";

    // ────────────────────────────────────────────────────────
    // SERVER STATE - WiFi
    // ────────────────────────────────────────────────────────
    private static TcpListener _wifiServer;
    private static bool        _wifiRunning       = false;
    private static string      _cachedWifiList    = "";
    private static string      _connectedSsid     = "";   // persisted across power cycles
    private static bool?       _lastIconConnected = null; // last WiFi icon state we broadcast

    private static readonly object              _wifiLock        = new object();
    private static readonly List<NetworkStream> _wifiClients     = new List<NetworkStream>();
    private static readonly object              _wifiClientsLock = new object();

    // ────────────────────────────────────────────────────────
    // SERVER STATE - Ethernet
    // ────────────────────────────────────────────────────────
    private static TcpListener _ethernetServer;
    private static bool        _ethernetRunning   = false;

    private static readonly List<NetworkStream> _ethernetClients     = new List<NetworkStream>();
    private static readonly object              _ethernetClientsLock = new object();

    // ────────────────────────────────────────────────────────
    // ETHERNET STATE
    // ────────────────────────────────────────────────────────
    private static string   _lastEthernetIp    = "";
    private static bool     _lastEthernetState = false;
    private static DateTime _lastEthernetLog   = DateTime.MinValue;

    // ════════════════════════════════════════════════════════
    // PERSISTENCE — load / save connected SSID
    // ════════════════════════════════════════════════════════

    private static void LoadWifiState()
    {
        try
        {
            if (!File.Exists(STATE_FILE)) return;

            string json = File.ReadAllText(STATE_FILE);

            var match = Regex.Match(json,
                "\"connected_ssid\"\\s*:\\s*\"([^\"]*)\"");

            if (match.Success)
            {
                _connectedSsid = match.Groups[1].Value;
                Console.WriteLine($"[Network] Loaded persisted WiFi SSID: \"{_connectedSsid}\"");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Network] LoadWifiState error: {ex.Message}");
        }
    }

    private static void SaveWifiState()
    {
        try
        {
            string json = $"{{\"connected_ssid\":\"{_connectedSsid}\"," +
                          $"\"updated_at\":\"{DateTime.Now:O}\"}}";
            File.WriteAllText(STATE_FILE, json);
            Console.WriteLine($"[Network] WiFi state saved — SSID: \"{_connectedSsid}\"");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Network] SaveWifiState error: {ex.Message}");
        }
    }

    // ════════════════════════════════════════════════════════
    // SERVER STARTUP
    // ════════════════════════════════════════════════════════

    public static async Task StartWifiTcpServerAsync()
    {
        LoadWifiState();

        // ── Start WiFi Server on port 9000 ──────────────────────────────
        _wifiServer = new TcpListener(IPAddress.Any, WIFI_PORT);
        _wifiServer.Server.SetSocketOption(
            SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _wifiServer.Start();
        _wifiRunning = true;

        Console.WriteLine($"[Network] WiFi server started on port {WIFI_PORT}");
        if (!string.IsNullOrEmpty(_connectedSsid))
            Console.WriteLine($"[Network] Will restore WiFi connection to: \"{_connectedSsid}\"");

        // ── Start Ethernet Server on port 2002 ──────────────────────────
        _ethernetServer = new TcpListener(IPAddress.Any, ETHERNET_PORT);
        _ethernetServer.Server.SetSocketOption(
            SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _ethernetServer.Start();
        _ethernetRunning = true;

        Console.WriteLine($"[Network] Ethernet server started on port {ETHERNET_PORT}");

        // Accept WiFi clients in background
        _ = Task.Run(async () =>
        {
            while (_wifiRunning)
            {
                try
                {
                    TcpClient client = await _wifiServer.AcceptTcpClientAsync();
                    _ = Task.Run(() => HandleWifiClientAsync(client));
                }
                catch (Exception ex)
                {
                    if (_wifiRunning)
                        Console.WriteLine($"[Network] WiFi Accept error: {ex.Message}");
                }
            }
        });

        // Accept Ethernet clients in background
        _ = Task.Run(async () =>
        {
            while (_ethernetRunning)
            {
                try
                {
                    TcpClient client = await _ethernetServer.AcceptTcpClientAsync();
                    _ = Task.Run(() => HandleEthernetClientAsync(client));
                }
                catch (Exception ex)
                {
                    if (_ethernetRunning)
                        Console.WriteLine($"[Network] Ethernet Accept error: {ex.Message}");
                }
            }
        });

        // Start background ethernet monitoring
        _ = Task.Run(() => MonitorEthernetStatusAsync(CancellationToken.None));
    }

    // ════════════════════════════════════════════════════════
    // CLIENT HANDLERS
    // ════════════════════════════════════════════════════════

    private static async Task HandleWifiClientAsync(TcpClient client)
    {
        NetworkStream stream = null;
        try
        {
            stream = client.GetStream();
            lock (_wifiClientsLock)
            {
                _wifiClients.Add(stream);
            }

            Console.WriteLine($"[Network] Client connected — total: {_wifiClients.Count}");

            // 1. Send current WiFi radio state
            string stateValue = GetWifiState();
            await SendToClientAsync(stream, $"WIFI_STATE|{stateValue}");

            // 2. Send current Ethernet status
            await BroadcastEthernetStatusAsync();

            // 3. Send cached network list if we have one
            string cached;
            lock (_wifiLock) { cached = _cachedWifiList; }

            if (!string.IsNullOrEmpty(cached))
            {
                await SendToClientAsync(stream, cached);
            }
            else if (stateValue == "1")
            {
                 await ScanWifiNetworksAsync();   // ← AWAIT instead of fire-and-forget
                string freshCached;
                lock (_wifiLock) { freshCached = _cachedWifiList; }
                if (!string.IsNullOrEmpty(freshCached))
                    await SendToClientAsync(stream, freshCached);
            }

            // 4. Sync to the ACTUAL system WiFi connection (not stale saved/GUI state)
            //    so a new client always gets the truth: if the radio is really joined
            //    to a network, restore it + light the icon; otherwise show disconnected.
            string sysSsid = stateValue == "1" ? await GetActiveWifiSsidAsync() : "";
            _connectedSsid = sysSsid;   // internal state follows reality

            if (!string.IsNullOrEmpty(sysSsid))
            {
                await Task.Delay(500);
                await SendToClientAsync(stream,
                    $"WIFI_RESTORE_CONNECTION|{sysSsid}");
                Console.WriteLine(
                    $"[Network] Sent WIFI_RESTORE_CONNECTION|{sysSsid} to new client");
            }

            // 4b. Icon state straight from the real system connection.
            bool iconConnected = !string.IsNullOrEmpty(sysSsid);
            _lastIconConnected = iconConnected;
            await SendToClientAsync(stream,
                $"WIFI_ICON_STATUS|{(iconConnected ? "CONNECTED" : "DISCONNECTED")}");

            // 5. Listen for commands from Lua
            byte[] buffer = new byte[4096];
            int bytesRead;

            while ((bytesRead =
                await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                string command =
                    Encoding.UTF8.GetString(buffer, 0, bytesRead).Trim();

                Console.WriteLine($"[Network] ← Received from Lua: {command}");

                string[] parts = command.Split('|');
                string   cmd   = parts[0].ToUpper();

                switch (cmd)
                {
                    case "ETHERNET_ACK":
                    case "ETHERNET_STATE":
                        if (parts.Length >= 2)
                            Console.WriteLine($"[Network] ✓ Lua acknowledged Ethernet: {parts[1]} (0=off, 1=on)");
                        break;

                    case "ETHERNET_TOGGLE":
                        Console.WriteLine($"[Network] Lua requested Ethernet toggle");
                        break;

                    case "WIFI_TOGGLE":
                        Console.WriteLine($"[Network] Lua requested WiFi toggle");
                        _ = Task.Run(() => ToggleWifiAsync(stream));
                        break;

                    case "WIFI_REFRESH":
                        Console.WriteLine($"[Network] Lua requested WiFi refresh");
                        _ = Task.Run(() => ScanWifiNetworksAsync());
                        break;

                    case "WIFI_CONNECT":
                        if (parts.Length < 2)
                        {
                            await BroadcastWifiAsync("WIFI_CONNECT_RESULT|INVALID_ARGS|FAILED|0");
                            break;
                        }
                        string connectSsid = parts[1];
                        string password    = parts.Length >= 3 ? parts[2] : "";
                        Console.WriteLine($"[Network] Lua requested WiFi connect: {connectSsid}");
                        _ = Task.Run(() => ConnectToWifiAsync(connectSsid, password, stream));
                        break;

                    case "WIFI_DISCONNECT":
                        Console.WriteLine($"[Network] Lua requested WiFi disconnect");
                        _ = Task.Run(() => DisconnectWifiAsync(stream));
                        break;

                    case "WIFI_FORGET":
                        if (parts.Length < 2)
                        {
                            await BroadcastWifiAsync("WIFI_FORGET_RESULT|INVALID_ARGS");
                            break;
                        }
                        string forgetSsid = parts[1];
                        Console.WriteLine($"[Network] Lua requested forget network: {forgetSsid}");
                        _ = Task.Run(() => ForgetWifiAsync(forgetSsid, stream));
                        break;

                    // ── CHECK SAVED PASSWORD via nmcli profiles ──────────
                    case "WIFI_CHECK_SAVED":
                        if (parts.Length < 2)
                        {
                            await BroadcastWifiAsync("WIFI_NO_SAVED_PASSWORD|");
                            break;
                        }
                        _ = Task.Run(() => CheckSavedPasswordAsync(parts[1]));
                        break;

                    case "WIFI_SELECTED":
                        Console.WriteLine(
                            $"[Network] Network selected: {(parts.Length >= 2 ? parts[1] : "?")}");
                        break;

                    default:
                        if (cmd.StartsWith("ETHERNET"))
                            Console.WriteLine($"[Network] ⚠ Ethernet command from Lua: {command}");
                        else
                            Console.WriteLine($"[Network] ⚠ Unknown command: {cmd}");
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Network] Client error: {ex.Message}");
        }
        finally
        {
            if (stream != null)
            {
                lock (_wifiClientsLock)
                {
                    _wifiClients.Remove(stream);
                }
            }
            client.Close();
            Console.WriteLine(
                $"[Network] Client disconnected — remaining: {_wifiClients.Count}");
        }
    }

    private static async Task HandleEthernetClientAsync(TcpClient client)
    {
        NetworkStream stream = null;
        try
        {
            stream = client.GetStream();
            lock (_ethernetClientsLock)
            {
                _ethernetClients.Add(stream);
            }

            Console.WriteLine($"[Network] Ethernet client connected — total: {_ethernetClients.Count}");

            // Read and discard the handshake ("ethernet\n") that Lua sends on connect
            byte[] handshake = new byte[64];
            await stream.ReadAsync(handshake, 0, handshake.Length);

            // Do a LIVE check right now instead of trusting _lastEthernetState
            bool isConnected = DetectEthernetConnected();
            _lastEthernetState = isConnected;

            string msg;
            if (isConnected)
            {
                var (ip, mac, speed) = GetEthernetConnectionDetails();
                msg = $"ETHERNET_STATUS|CONNECTED|{ip}|{mac}|{speed}";
            }
            else
            {
                msg = "ETHERNET_STATUS|DISCONNECTED|0.0.0.0|00:00:00:00:00:00|0Mbps";
            }

            await SendToClientAsync(stream, msg);

            // Listen for acknowledgments or commands from Lua
            byte[] buffer = new byte[4096];
            int bytesRead;

            while ((bytesRead =
                await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                string command =
                    Encoding.UTF8.GetString(buffer, 0, bytesRead).Trim();

                Console.WriteLine($"[Network] ← Ethernet: Received from Lua: {command}");

                string[] parts = command.Split('|');
                string   cmd   = parts[0].ToUpper();

                switch (cmd)
                {
                    case "ETHERNET_ACK":
                    case "ETHERNET_STATE":
                        if (parts.Length >= 2)
                            Console.WriteLine($"[Network] ✓ Lua acknowledged Ethernet: {parts[1]} (0=off, 1=on)");
                        break;

                    case "ETHERNET_TOGGLE":
                        Console.WriteLine($"[Network] Lua requested Ethernet toggle");
                        break;

                    default:
                        Console.WriteLine($"[Network] ⚠ Unknown Ethernet command: {cmd}");
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Network] Ethernet client error: {ex.Message}");
        }
        finally
        {
            if (stream != null)
            {
                lock (_ethernetClientsLock)
                {
                    _ethernetClients.Remove(stream);
                }
            }
            client.Close();
            Console.WriteLine(
                $"[Network] Ethernet client disconnected — remaining: {_ethernetClients.Count}");
        }
    }

    // ════════════════════════════════════════════════════════
    // MESSAGING HELPERS
    // ════════════════════════════════════════════════════════

    private static async Task SendToClientAsync(NetworkStream stream, string message)
    {
        try
        {
            if (stream?.CanWrite != true) return;
            byte[] data = Encoding.UTF8.GetBytes(message + "\n");
            await stream.WriteAsync(data, 0, data.Length);
            await stream.FlushAsync();
            Console.WriteLine($"[Network] → {message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Network] SendToClient error: {ex.Message}");
        }
    }

    // Tell the Lua frontend which WiFi icon to show (connected vs disconnected).
    // Mirrors the Python reference: "WIFI_ICON_STATUS|CONNECTED" / "WIFI_ICON_STATUS|DISCONNECTED"
    private static async Task BroadcastWifiIconStateAsync(bool connected)
    {
        _lastIconConnected = connected;   // track so the periodic scan only re-emits on change
        string state = connected ? "CONNECTED" : "DISCONNECTED";
        await BroadcastWifiAsync($"WIFI_ICON_STATUS|{state}");
    }

    private static async Task BroadcastWifiAsync(string message)
    {
        byte[] data = Encoding.UTF8.GetBytes(message + "\n");

        List<NetworkStream> snapshot;
        lock (_wifiClientsLock)
        {
            snapshot = new List<NetworkStream>(_wifiClients);
        }

        int successCount = 0;
        foreach (var stream in snapshot)
        {
            try
            {
                if (stream?.CanWrite == true)
                {
                    await stream.WriteAsync(data, 0, data.Length);
                    await stream.FlushAsync();
                    successCount++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Network] WiFi broadcast failed to client: {ex.Message}");
                lock (_wifiClientsLock)
                {
                    _wifiClients.Remove(stream);
                }
            }
        }

        Console.WriteLine($"[Network] WiFi → {successCount} client(s): {message}");
    }

    private static async Task BroadcastEthernetAsync(string message)
    {
        byte[] data = Encoding.UTF8.GetBytes(message + "\n");

        List<NetworkStream> snapshot;
        lock (_ethernetClientsLock)
        {
            snapshot = new List<NetworkStream>(_ethernetClients);
        }

        int successCount = 0;
        foreach (var stream in snapshot)
        {
            try
            {
                if (stream?.CanWrite == true)
                {
                    await stream.WriteAsync(data, 0, data.Length);
                    await stream.FlushAsync();
                    successCount++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Network] Ethernet broadcast failed to client: {ex.Message}");
                lock (_ethernetClientsLock)
                {
                    _ethernetClients.Remove(stream);
                }
            }
        }

        Console.WriteLine($"[Network] Ethernet → {successCount} client(s): {message}");
    }

    // ════════════════════════════════════════════════════════
    // WIFI OPERATIONS
    // ════════════════════════════════════════════════════════

    private static async Task ToggleWifiAsync(NetworkStream stream)
    {
        try
        {
            var checkPsi = new ProcessStartInfo
            {
                FileName               = "nmcli",
                Arguments              = "radio wifi",
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true
            };
            using var checkProc = Process.Start(checkPsi);
            string currentState =
                (await checkProc.StandardOutput.ReadToEndAsync()).Trim();
            await checkProc.WaitForExitAsync();

            bool   isEnabled = currentState.Contains("enabled");
            string newState  = isEnabled ? "off" : "on";

            var togglePsi = new ProcessStartInfo
            {
                FileName               = "nmcli",
                Arguments              = $"radio wifi {newState}",
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true
            };
            using var toggleProc = Process.Start(togglePsi);
            await toggleProc.WaitForExitAsync();

            Console.WriteLine(
                $"[Network] Toggled → {newState} (exit: {toggleProc.ExitCode})");

            string stateValue = newState == "on" ? "1" : "0";
            await BroadcastWifiAsync($"WIFI_STATE|{stateValue}");

            if (newState == "off")
            {
                lock (_wifiLock) { _cachedWifiList = ""; }
                _connectedSsid = "";
                SaveWifiState();
                await BroadcastWifiIconStateAsync(false);
            }
            else
            {
                await Task.Delay(1000);
                await ScanWifiNetworksAsync();

                // Radio just came back on — NetworkManager may have auto-rejoined a
                // saved network. Detect it and push the connection + icon state right
                // away so the GUI icon follows the toggle instead of lagging behind.
                string activeSsid = await GetActiveWifiSsidAsync();
                if (!string.IsNullOrEmpty(activeSsid))
                {
                    _connectedSsid = activeSsid;
                    SaveWifiState();
                    await BroadcastWifiAsync($"WIFI_RESTORE_CONNECTION|{activeSsid}");
                    await BroadcastWifiIconStateAsync(true);
                }
                else
                {
                    await BroadcastWifiIconStateAsync(false);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Network] Toggle error: {ex.Message}");
        }
    }

    private static async Task ScanWifiNetworksAsync()
    {
        try
        {
            var rescanPsi = new ProcessStartInfo
            {
                FileName               = "nmcli",
                Arguments              = "device wifi rescan",
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true
            };
            using (var rescanProc = Process.Start(rescanPsi))
            {
                await rescanProc.WaitForExitAsync();
            }

            await Task.Delay(2000);

            var listPsi = new ProcessStartInfo
            {
                FileName               = "nmcli",
                Arguments              = "-t -f SSID,SIGNAL,SECURITY,ACTIVE device wifi list",
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true
            };

            using var listProc = Process.Start(listPsi);
            string output = await listProc.StandardOutput.ReadToEndAsync();
            string error  = await listProc.StandardError.ReadToEndAsync();
            await listProc.WaitForExitAsync();

            if (!string.IsNullOrEmpty(error))
                Console.WriteLine($"[Network] Scan error output: {error}");

            var networks = new List<string>();
            string activeScanSsid = "";
            foreach (string line in output.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                string[] parts = line.Split(':');
                if (parts.Length < 4) continue;

                string ssid     = string.Join(":", parts, 0, parts.Length - 3).Trim();
                string signal   = parts[parts.Length - 3].Trim();
                string security = parts[parts.Length - 2].Trim();
                string active   = parts[parts.Length - 1].Trim();   // ACTIVE flag (yes/no)

                if (string.IsNullOrEmpty(ssid)) continue;

                if (active.Equals("yes", StringComparison.OrdinalIgnoreCase))
                    activeScanSsid = ssid;

                bool isOpen = string.IsNullOrEmpty(security)
                           || security.Equals("--",   StringComparison.OrdinalIgnoreCase)
                           || security.Equals("none", StringComparison.OrdinalIgnoreCase);

                networks.Add($"{ssid}|{signal}|{security}|{(isOpen ? "1" : "0")}");
            }

            string wifiData =
                $"WIFI_LIST|{networks.Count}|" + string.Join("|", networks);

            lock (_wifiLock) { _cachedWifiList = wifiData; }

            Console.WriteLine($"[Network] Scan complete — {networks.Count} networks");
            await BroadcastWifiAsync(wifiData);

            // Keep the WiFi icon in sync with the REAL connection (from the scan's
            // ACTIVE flag) — independent of GUI/saved state. Emit only on change.
            bool wifiConnected = !string.IsNullOrEmpty(activeScanSsid);
            if (wifiConnected)
                _connectedSsid = activeScanSsid;
            if (_lastIconConnected != wifiConnected)
            {
                _lastIconConnected = wifiConnected;
                await BroadcastWifiIconStateAsync(wifiConnected);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Network] ScanWifiNetworksAsync error: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns the SSID NetworkManager is currently connected to (via Wi-Fi),
    /// or "" if none. Used to detect an auto-reconnect right after the radio
    /// is turned back on, so the GUI icon doesn't lag behind the real state.
    /// </summary>
    private static async Task<string> GetActiveWifiSsidAsync()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = "nmcli",
                Arguments              = "-t -f ACTIVE,SSID device wifi",
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true
            };

            using var proc = Process.Start(psi);
            string output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            foreach (string line in output.Split('\n'))
            {
                // Terse format is "ACTIVE:SSID" → e.g. "yes:MyNetwork"
                if (line.StartsWith("yes:", StringComparison.OrdinalIgnoreCase))
                    return line.Substring(line.IndexOf(':') + 1).Trim();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Network] GetActiveWifiSsidAsync error: {ex.Message}");
        }

        return "";
    }

    /// <summary>
    /// Checks if nmcli has a saved connection profile for the given SSID.
    /// If yes → tells Lua to connect directly using nmcli's stored credentials.
    /// If no  → tells Lua to show the password keyboard.
    /// </summary>
    private static async Task CheckSavedPasswordAsync(string ssid)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = "nmcli",
                Arguments              = "-t -f NAME,TYPE connection show",
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true
            };

            using var proc = Process.Start(psi);
            string output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            bool hasSavedProfile = false;
            foreach (string line in output.Split('\n'))
            {
                // nmcli -t format: NAME:TYPE
                string[] lineParts = line.Split(':');
                if (lineParts.Length >= 1)
                {
                    string profileName = lineParts[0].Trim();
                    if (profileName.Equals(ssid, StringComparison.OrdinalIgnoreCase))
                    {
                        hasSavedProfile = true;
                        break;
                    }
                }
            }

            if (hasSavedProfile)
            {
                Console.WriteLine($"[Network] Found saved nmcli profile for: \"{ssid}\"");
                // Tell Lua to connect — nmcli will use its stored credentials
                await BroadcastWifiAsync($"WIFI_SAVED_PASSWORD|{ssid}|__USE_SAVED__");
            }
            else
            {
                Console.WriteLine($"[Network] No saved nmcli profile for: \"{ssid}\"");
                await BroadcastWifiAsync($"WIFI_NO_SAVED_PASSWORD|{ssid}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Network] CheckSavedPasswordAsync error: {ex.Message}");
            await BroadcastWifiAsync($"WIFI_NO_SAVED_PASSWORD|{ssid}");
        }
    }

    /// <summary>
    /// Connects to a WiFi network. Supports open, secured, and __USE_SAVED__ (nmcli profile).
    /// On success: persists the SSID and broadcasts SUCCESS result.
    /// On failure: broadcasts FAILED result.
    /// </summary>
    private static async Task ConnectToWifiAsync(
        string ssid, string password, NetworkStream stream)
    {
        try
        {
            await BroadcastWifiAsync($"WIFI_CONNECTING|{ssid}");

            // If password is __USE_SAVED__ or empty, let nmcli use its stored profile
            string args;
            if (string.IsNullOrEmpty(password) || password == "__USE_SAVED__")
                args = $"device wifi connect \"{ssid}\"";
            else
                args = $"device wifi connect \"{ssid}\" password \"{password}\"";

            var psi = new ProcessStartInfo
            {
                FileName               = "nmcli",
                Arguments              = args,
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true
            };

            using var proc = Process.Start(psi);
            string output = await proc.StandardOutput.ReadToEndAsync();
            string error  = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            bool success = proc.ExitCode == 0
                        && (output.Contains("successfully")
                         || output.Contains("activated"));

            // passFlag: 0 = open/saved, 1 = user entered password
            string passFlag = (string.IsNullOrEmpty(password) || password == "__USE_SAVED__")
                ? "0" : "1";

            if (success)
            {
                _connectedSsid = ssid;
                SaveWifiState();

                Console.WriteLine($"[Network] Connected to: {ssid}");
                Audit.Log("WIFI_CHANGE", "success", "operator",
                    new Dictionary<string, object?> { ["action"] = "connect", ["ssid"] = ssid });
                await BroadcastWifiAsync(
                    $"WIFI_CONNECT_RESULT|{ssid}|SUCCESS|{passFlag}");
                await BroadcastWifiIconStateAsync(true);
            }
            else
            {
                Console.WriteLine(
                    $"[Network] Connection failed: {error.Trim()}");
                Audit.Log("WIFI_CHANGE", "fail", "operator",
                    new Dictionary<string, object?> { ["action"] = "connect", ["ssid"] = ssid });
                await BroadcastWifiAsync(
                    $"WIFI_CONNECT_RESULT|{ssid}|FAILED|{passFlag}");
                await BroadcastWifiIconStateAsync(false);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Network] ConnectToWifiAsync error: {ex.Message}");
            await BroadcastWifiAsync(
                $"WIFI_CONNECT_RESULT|{ssid}|FAILED|1");
            await BroadcastWifiIconStateAsync(false);
        }
    }

    private static async Task DisconnectWifiAsync(NetworkStream stream)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = "nmcli",
                Arguments              = "device disconnect wlan0",
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true
            };

            using var proc = Process.Start(psi);
            string output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            Console.WriteLine(
                $"[Network] Disconnected (exit: {proc.ExitCode}): {output.Trim()}");

            _connectedSsid = "";
            SaveWifiState();

            Audit.Log("WIFI_CHANGE", "success", "operator",
                new Dictionary<string, object?> { ["action"] = "disconnect" });
            await BroadcastWifiAsync("WIFI_DISCONNECTED");
            await BroadcastWifiIconStateAsync(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Network] DisconnectWifiAsync error: {ex.Message}");
        }
    }

    private static async Task ForgetWifiAsync(string ssid, NetworkStream stream)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = "nmcli",
                Arguments              = $"connection delete \"{ssid}\"",
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true
            };

            using var proc = Process.Start(psi);
            string output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            Console.WriteLine(
                $"[Network] Forgot \"{ssid}\" (exit: {proc.ExitCode}): {output.Trim()}");

            bool wasConnected = _connectedSsid.Equals(ssid, StringComparison.OrdinalIgnoreCase);
            if (wasConnected)
            {
                _connectedSsid = "";
                SaveWifiState();
            }

            Audit.Log("WIFI_CHANGE", "success", "operator",
                new Dictionary<string, object?> { ["action"] = "forget", ["ssid"] = ssid });
            await BroadcastWifiAsync($"WIFI_FORGOTTEN|{ssid}");
            if (wasConnected)
                await BroadcastWifiIconStateAsync(false);   // forgot the active network → icon off
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Network] ForgetWifiAsync error: {ex.Message}");
        }
    }

    // ════════════════════════════════════════════════════════
    // WIFI RADIO STATE
    // ════════════════════════════════════════════════════════

    private static string GetWifiState()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = "nmcli",
                Arguments              = "radio wifi",
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true
            };

            using var proc = Process.Start(psi);
            string output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit();

            return output.Contains("enabled") ? "1" : "0";
        }
        catch
        {
            return "0";
        }
    }

    // ════════════════════════════════════════════════════════
    // ETHERNET MONITORING
    // ════════════════════════════════════════════════════════

    private static async Task MonitorEthernetStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            bool firstRun = true;
            DateTime lastConsoleLog = DateTime.MinValue;
            const int CONSOLE_LOG_INTERVAL_MS = 60000;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    bool isConnected = DetectEthernetConnected();
                    bool shouldLogToConsole = firstRun ||
                        (DateTime.Now - lastConsoleLog).TotalMilliseconds >= CONSOLE_LOG_INTERVAL_MS;

                    if (shouldLogToConsole)
                    {
                        if (isConnected)
                        {
                            var (ip, mac, speed) = GetEthernetConnectionDetails();
                            Console.WriteLine($"[Network] Ethernet ✓ {ip} | {mac} | {speed}");
                        }
                        else
                        {
                            Console.WriteLine($"[Network] Ethernet ✗ Disconnected");
                        }
                        lastConsoleLog = DateTime.Now;
                    }

                    if (isConnected != _lastEthernetState)
                    {
                        _lastEthernetState = isConnected;

                        if (isConnected)
                        {
                            var (ip, mac, speed) = GetEthernetConnectionDetails();
                            _lastEthernetIp = ip;
                            string msg = $"ETHERNET_STATUS|CONNECTED|{ip}|{mac}|{speed}";
                            await BroadcastEthernetAsync(msg);
                        }
                        else
                        {
                            string msg = "ETHERNET_STATUS|DISCONNECTED|0.0.0.0|00:00:00:00:00:00|0Mbps";
                            await BroadcastEthernetAsync(msg);
                        }
                    }

                    firstRun = false;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Network] Ethernet monitor error: {ex.Message}");
                }

                await Task.Delay(5000, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Network] Ethernet monitor task failed: {ex.Message}");
        }
    }

    private static bool DetectEthernetConnected()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = "/bin/sh",
                Arguments              = "-c \"nmcli -t -f DEVICE,STATE device | grep -E 'ethernet|eth' | grep -i connected\"",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false
            };
            using var process = Process.Start(psi);
            string result = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();

            if (!string.IsNullOrEmpty(result))
                return true;

            string[] interfaces = { "ethernet0", "eth0", "end0", "enp1s0" };
            foreach (var iface in interfaces)
            {
                try
                {
                    var ipPsi = new ProcessStartInfo
                    {
                        FileName               = "/bin/sh",
                        Arguments              = $"-c \"ip -4 addr show {iface} 2>/dev/null | grep -oP '(?<=inet\\s)\\d+(\\.\\d+){{3}}'\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError  = true,
                        UseShellExecute        = false
                    };
                    using var ipProc = Process.Start(ipPsi);
                    string ipResult = ipProc.StandardOutput.ReadToEnd().Trim();
                    ipProc.WaitForExit();
                    if (!string.IsNullOrEmpty(ipResult))
                        return true;
                }
                catch { }
            }
        }
        catch { }
        return false;
    }

    private static (string ip, string mac, string speed) GetEthernetConnectionDetails()
    {
        string ip    = "0.0.0.0";
        string mac   = "00:00:00:00:00:00";
        string speed = "0Mbps";

        try
        {
            string[] interfaces = { "ethernet0", "eth0", "end0", "enp1s0" };
            foreach (var iface in interfaces)
            {
                try
                {
                    var ipPsi = new ProcessStartInfo
                    {
                        FileName               = "/bin/sh",
                        Arguments              = $"-c \"nmcli -t -f IP4.ADDRESS device show {iface} 2>/dev/null | grep -oP '\\d+(\\.\\d+){{3}}' | head -1\"",
                        RedirectStandardOutput = true,
                        UseShellExecute        = false
                    };
                    using var ipProc = Process.Start(ipPsi);
                    string ipResult = ipProc.StandardOutput.ReadToEnd().Trim();
                    ipProc.WaitForExit();

                    if (string.IsNullOrEmpty(ipResult))
                    {
                        var altIpPsi = new ProcessStartInfo
                        {
                            FileName               = "/bin/sh",
                            Arguments              = $"-c \"ip -4 addr show {iface} 2>/dev/null | grep -oP '(?<=inet\\s)\\d+(\\.\\d+){{3}}'\"",
                            RedirectStandardOutput = true,
                            UseShellExecute        = false
                        };
                        using var altIpProc = Process.Start(altIpPsi);
                        ipResult = altIpProc.StandardOutput.ReadToEnd().Trim();
                        altIpProc.WaitForExit();
                    }

                    if (!string.IsNullOrEmpty(ipResult))
                    {
                        ip = ipResult;

                        try
                        {
                            string macPath = $"/sys/class/net/{iface}/address";
                            if (File.Exists(macPath))
                            {
                                string macContent = File.ReadAllText(macPath).Trim().ToUpper();
                                if (!string.IsNullOrEmpty(macContent) && macContent != "00:00:00:00:00:00")
                                    mac = macContent;
                            }
                        }
                        catch { }

                        try
                        {
                            var speedPsi = new ProcessStartInfo
                            {
                                FileName               = "/bin/sh",
                                Arguments              = $"-c \"ethtool {iface} 2>/dev/null | grep 'Speed:' | awk '{{print $2}}'\"",
                                RedirectStandardOutput = true,
                                UseShellExecute        = false
                            };
                            using var speedProc = Process.Start(speedPsi);
                            string speedResult = speedProc.StandardOutput.ReadToEnd().Trim();
                            speedProc.WaitForExit();
                            if (!string.IsNullOrEmpty(speedResult) && speedResult != "Unknown")
                                speed = speedResult;
                        }
                        catch { }

                        return (ip, mac, speed);
                    }
                }
                catch { }
            }
        }
        catch { }

        return (ip, mac, speed);
    }

    private static async Task BroadcastEthernetStatusAsync()
    {
        try
        {
            if (_lastEthernetState)
            {
                var (ip, mac, speed) = GetEthernetConnectionDetails();
                string msg = $"ETHERNET_STATUS|CONNECTED|{ip}|{mac}|{speed}";
                await BroadcastEthernetAsync(msg);
            }
            else
            {
                string msg = "ETHERNET_STATUS|DISCONNECTED|0.0.0.0|00:00:00:00:00:00|0Mbps";
                await BroadcastEthernetAsync(msg);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Network] BroadcastEthernetStatusAsync error: {ex.Message}");
        }
    }
}
