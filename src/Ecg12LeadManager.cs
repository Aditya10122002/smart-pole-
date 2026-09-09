using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

public partial class VitalsChairApp
{
    private static readonly ConcurrentQueue<string> ecg12LeadQueue = new ConcurrentQueue<string>();
    private static readonly DateTime[] _leadLastUpdate = new DateTime[12];
    private static DateTime _hrLastUpdate = DateTime.MinValue;
    private static readonly TimeSpan SnapshotFreshness = TimeSpan.FromMilliseconds(120);
    private static bool _got0x02Packet = false;
    private static bool _got0x12Packet = false;
    private static readonly object _packetLock = new object();
    // private static bool _isECG12PreviewRunning = false;  // Track if preview should stream to clients

    static async Task StartEcg12TcpServerAsync()
    {
        try
        {
            _ecg12Server = new TcpListener(IPAddress.Any, ECG12Port);
            _ecg12Server.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _ecg12Server.Start();
            Log($" ECG12 TCP Server started on {IPAddress.Any}:{ECG12Port}");

            while (_ecg12Running)
            {
                try
                {
                    TcpClient client = await _ecg12Server.AcceptTcpClientAsync();
                    lock (_ecg12ClientLock)
                    {
                        _ecg12Clients.Add(client);
                    }

                    _ = Task.Run(() => HandleEcg12ClientAsync(client, CancellationToken.None));
                }
                catch (Exception ex)
                {
                    if (_ecg12Running)
                        Log($" ECG12 TCP Server error: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log($" ECG12 TCP Server startup error: {ex.Message}");
        }
    }

    static async Task HandleEcg12ClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            using NetworkStream stream = client.GetStream();
            client.Client.NoDelay = true;
            client.Client.SendBufferSize = 16384;
            client.Client.ReceiveBufferSize = 4096;
            stream.WriteTimeout = 1000;
            stream.ReadTimeout = 5000;

            byte[] buffer = new byte[256];
            int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
            if (bytesRead == 0)
                return;

            string deviceType = Encoding.UTF8.GetString(buffer, 0, bytesRead).Trim();
            LogThrottled($"ecg12-handshake:{deviceType}", $"ECG12 client connected: {deviceType}", TimeSpan.FromSeconds(10));

            if (deviceType != "ecg12")
                return;



            _ecg12LuaStream = stream;

            await stream.WriteAsync(Encoding.UTF8.GetBytes("ECG12_START\n"), cancellationToken);
            await stream.FlushAsync(cancellationToken);


            _lastEcg12LeadStatusText = null;
            PushEcg12LeadStatusToLua();
            while (ecg12LeadQueue.TryDequeue(out _)) { }

            List<string> batch = new List<string>(ECG12_BATCH_SIZE);
            DateTime nextFrameTime = DateTime.UtcNow;
            int framesSent = 0;
            int bytesSent = 0;

            while (_ecg12Running && client.Connected && !cancellationToken.IsCancellationRequested)

            {
                while (batch.Count < ECG12_BATCH_SIZE)
                {
                    if (ecg12LeadQueue.TryDequeue(out string s))
                        batch.Add(s);
                    else
                        break;
                }



                if (batch.Count >= ECG12_BATCH_SIZE ||
                    (batch.Count > 0 && (DateTime.UtcNow - nextFrameTime).TotalMilliseconds > 2))
                {
                    string payload = string.Join(";", batch) + "\n";
                    byte[] bytes = Encoding.UTF8.GetBytes(payload);

                    try
                    {
                        await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken);
                        framesSent++;
                        bytesSent += bytes.Length;
                        batch.Clear();
                    }
                    catch (Exception ex)
                    {
                        Log($" ECG12 network write failed: {ex.Message}");
                        break;
                    }

                    nextFrameTime = nextFrameTime.AddMilliseconds(ECG12_FRAME_INTERVAL_MS);
                    var delay = nextFrameTime - DateTime.UtcNow;

                    if (delay.TotalMilliseconds > 1)
                        await Task.Delay(delay, cancellationToken);
                    else if (delay.TotalMilliseconds < -ECG12_FRAME_INTERVAL_MS)
                        nextFrameTime = DateTime.UtcNow;

                    LogThrottled(
                        "ecg12-preview-health",
                        $"ECG12 preview streaming: frames={framesSent}, bytes={bytesSent}, queue={ecg12LeadQueue.Count}, clients={_ecg12Clients.Count}",
                        EcgPreviewHealthLogInterval);
                }
                else
                {
                    await Task.Delay(1, cancellationToken);
                }
            }

            try
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes("ECG12_STOP\n"), cancellationToken);
            }
            catch { }
        }
        catch (Exception ex)
        {
            Log($" ECG12 client error: {ex.Message}");
        }
        finally
        {
            _ecg12LuaStream = null;

            lock (_ecg12ClientLock)
            {
                _ecg12Clients.Remove(client);
            }

            try { client.Close(); } catch { }
            LogThrottled("ecg12-client-disconnected", "ECG12 preview client disconnected", TimeSpan.FromSeconds(10));
        }
    }

    static void StartEcg12Recording()
    {
        ClearEcg12Buffers();
        _isECG12Active = true;
        InitializeEcg12Mode();

        _lastEcg12LeadStatusText = null;
        PushEcg12LeadStatusToLua();

        Log($"ECG 12-lead recording started - storing latest {ECG_RECORDING_WINDOW_SECONDS}s, preview already streaming");
    }


    static void StopEcg12Recording()
    {
        if (!_isECG12Active)
            return;

        _isECG12Active = false;

        // Close all preview clients to stop streaming (Lua will auto-reconnect)
        lock (_ecg12ClientLock)
        {
            foreach (var client in _ecg12Clients.ToList())
            {
                try { client.Close(); }
                catch { }
            }
            _ecg12Clients.Clear();
        }

        // Clear preview queue
        while (ecg12LeadQueue.TryDequeue(out _)) { }

        _lastEcg12LeadStatusText = null;
        PushEcg12LeadStatusToLua();

        Log($"ECG 12-lead recording stopped - stored {ecg12SampleCount} samples, preview streaming ended");
    }


static async Task ProcessEcg12DataAsync(byte[] data, int length, CancellationToken cancellationToken)
{
    int idx = 0;

    while (idx < length)
    {
        byte id = data[idx];

        // ── TEMP DIAGNOSTIC: dump raw bytes around 0x09 ──
        if (id == 0x09)
        {
            int dumpLen = Math.Min(6, length - idx);
            var hexDump = string.Join(" ", Enumerable.Range(0, dumpLen)
                .Select(i => $"0x{data[idx + i]:X2}"));
           // Log($"🔍 RAW 0x09 PACKET — bytes: {hexDump}");
        }

   int packetSize = id switch
{
    0x01 => 6,
    0x02 => 6,
    0x12 => 7,
    0x04 => 6,
    0x09 => 3,   
    0x13 => 2,
    0x52 => 1,
    _ => 1
};

        if (idx + packetSize > length)
            break;

        if (id == 0x02)
            DecodeEcg12Wave_I_II_V1(data, idx);
        else if (id == 0x12)
            DecodeEcg12Wave_V2_V6(data, idx);
        else if (id == 0x04)
            DecodeEcg12Hr(data, idx);
        else if (id == 0x09)
            DecodeEcg12Status(data, idx);
        else if (id == 0x13)
            DecodeEcg12VStatus(data, idx);
        else if (id == 0x52)
            HandleCommandResponse(0x52);

        idx += packetSize;
    }
}

    static byte[] UnpackData(byte[] packedData)
    {
        if (packedData.Length < 2)
            return new byte[0];

        byte byte1 = packedData[0];
        byte[] unpacked = new byte[packedData.Length - 1];

        for (int i = 0; i < unpacked.Length; i++)
        {
            byte low7 = (byte)(packedData[i + 1] & 0x7F);
            byte highBit = (byte)((byte1 >> i) & 0x01);
            unpacked[i] = (byte)(low7 | (highBit << 7));
        }

        return unpacked;
    }

    static int UnpackedToSigned(byte value)
    {
        return value - 128;
    }

    static void SendEcgCommand(byte commandId, byte controlByte)
    {
        try
        {
            byte identifier = commandId;
            byte dataByte = (byte)(0x80 | controlByte);
            byte[] command = new byte[] { identifier, dataByte };
            _serialPortECG12.Write(command, 0, command.Length);
            Log($"Sent ECG12 command 0x{commandId:X2} with control byte 0x{controlByte:X2}");
        }
        catch (Exception ex)
        {
            Log($"Error sending ECG12 command: {ex.Message}");
        }
    }

    static void HandleCommandResponse(byte commandId)
    {
    }

    static void InitializeEcg12Mode()
    {
        try
        {
            byte identifier = 0x41;
            byte dataByte = (byte)(0x80 | 0x01);
            byte[] filterCommand = new byte[] { identifier, dataByte };
            _serialPortECG12.Write(filterCommand, 0, filterCommand.Length);
            Thread.Sleep(100);

            SendEcgCommand(0x52, 0x02);
            Thread.Sleep(100);
            Log("ECG12 initialized with Monitor filter and 12-lead mode");
        }
        catch (Exception ex)
        {
            Log($"ECG12 initialization failed: {ex.Message}");
        }
    }

    static void DecodeEcg12Wave_I_II_V1(byte[] data, int startIndex)
    {
        if (startIndex + 6 >= data.Length)
            return;

        byte[] packedData = new byte[5];
        Array.Copy(data, startIndex + 1, packedData, 0, 5);
        byte[] unpacked = UnpackData(packedData);
        if (unpacked.Length < 4)
            return;

        int leadII_raw = UnpackedToSigned(unpacked[0]);
        int leadI_raw = UnpackedToSigned(unpacked[1]);
        int leadV1_raw = UnpackedToSigned(unpacked[2]);
        byte flags = unpacked[3];

        int leadIII = leadII_raw - leadI_raw;
        int leadAVR = -((leadI_raw + leadII_raw) / 2);
        int leadAVL = leadI_raw - (leadIII / 2);
        int leadAVF = leadII_raw - (leadIII / 2);
        bool paceFlag = (flags & 0x02) != 0;
        bool heartBeatFlag = (flags & 0x01) != 0;

        lock (_lock)
        {
            lastEcg12_I = (leadI_raw * 16) + 2048;
            lastEcg12_II = (leadII_raw * 16) + 2048;
            lastEcg12_III = (leadIII * 16) + 2048;
            lastEcg12_aVR = (leadAVR * 16) + 2048;
            lastEcg12_aVL = (leadAVL * 16) + 2048;
            lastEcg12_aVF = (leadAVF * 16) + 2048;
            lastEcg12_V1 = (leadV1_raw * 16) + 2048;
            lastEcg12PaceFlag = paceFlag;
            lastEcg12HeartBeatFlag = heartBeatFlag;

            _leadLastUpdate[0] = DateTime.UtcNow;
            _leadLastUpdate[1] = DateTime.UtcNow;
            _leadLastUpdate[2] = DateTime.UtcNow;
            _leadLastUpdate[3] = DateTime.UtcNow;
            _leadLastUpdate[4] = DateTime.UtcNow;
            _leadLastUpdate[5] = DateTime.UtcNow;
            _leadLastUpdate[6] = DateTime.UtcNow;

            MarkEcg12Packet(0x02);
        }
    }

    static void DecodeEcg12Wave_V2_V6(byte[] data, int startIndex)
    {
        if (startIndex + 6 >= data.Length)
            return;

        byte[] packedData = new byte[6];
        Array.Copy(data, startIndex + 1, packedData, 0, 6);
        byte[] unpacked = UnpackData(packedData);
        if (unpacked.Length < 5)
            return;

        lock (_lock)
        {
            lastEcg12_V2 = (UnpackedToSigned(unpacked[0]) * 16) + 2048;
            lastEcg12_V3 = (UnpackedToSigned(unpacked[1]) * 16) + 2048;
            lastEcg12_V4 = (UnpackedToSigned(unpacked[2]) * 16) + 2048;
            lastEcg12_V5 = (UnpackedToSigned(unpacked[3]) * 16) + 2048;
            lastEcg12_V6 = (UnpackedToSigned(unpacked[4]) * 16) + 2048;

            _leadLastUpdate[7] = DateTime.UtcNow;
            _leadLastUpdate[8] = DateTime.UtcNow;
            _leadLastUpdate[9] = DateTime.UtcNow;
            _leadLastUpdate[10] = DateTime.UtcNow;
            _leadLastUpdate[11] = DateTime.UtcNow;

            MarkEcg12Packet(0x12);
        }
    }

    private static void MarkEcg12Packet(byte packetId)
    {
        lock (_packetLock)
        {
            if (packetId == 0x02)
                _got0x02Packet = true;
            else if (packetId == 0x12)
                _got0x12Packet = true;

            if (!_got0x02Packet || !_got0x12Packet)
                return;

            EnqueueEcg12Sample();

            if (_isECG12Active)
            {
                StoreEcg12Sample();
                LogEcg12RecordingProgress();
            }

            _got0x02Packet = false;
            _got0x12Packet = false;
        }
    }

    private static void LogEcg12RecordingProgress()
    {
        if (!_isECG12Active)
            return;

        double secondsStored = Math.Min(ecg12SampleCount / (double)ECG_SAMPLE_RATE_HZ, ECG_RECORDING_WINDOW_SECONDS);
        LogThrottled(
            "ecg12-recording-progress",
            $"ECG12 recording: {ecg12SampleCount}/{ECG_BUFFER_SIZE} samples ({secondsStored:F1}s/{ECG_RECORDING_WINDOW_SECONDS}s window), HR={lastEcg12HeartRate}, RR={lastEcg12RR}",
            EcgRecordingProgressLogInterval);
    }

    private static void StoreEcg12Sample()
    {
        // Circular buffer: after 40 seconds, each new sample replaces the oldest one.
        ecg12LeadIBuffer[ecg12WriteIndex] = lastEcg12_I;
        ecg12LeadIIBuffer[ecg12WriteIndex] = lastEcg12_II;
        ecg12LeadIIIBuffer[ecg12WriteIndex] = lastEcg12_III;
        ecg12LeadAvrBuffer[ecg12WriteIndex] = lastEcg12_aVR;
        ecg12LeadAvlBuffer[ecg12WriteIndex] = lastEcg12_aVL;
        ecg12LeadAvfBuffer[ecg12WriteIndex] = lastEcg12_aVF;
        ecg12LeadV1Buffer[ecg12WriteIndex] = lastEcg12_V1;
        ecg12LeadV2Buffer[ecg12WriteIndex] = lastEcg12_V2;
        ecg12LeadV3Buffer[ecg12WriteIndex] = lastEcg12_V3;
        ecg12LeadV4Buffer[ecg12WriteIndex] = lastEcg12_V4;
        ecg12LeadV5Buffer[ecg12WriteIndex] = lastEcg12_V5;
        ecg12LeadV6Buffer[ecg12WriteIndex] = lastEcg12_V6;

        ecg12WriteIndex = (ecg12WriteIndex + 1) % ECG_BUFFER_SIZE;
        if (ecg12SampleCount < ECG_BUFFER_SIZE)
            ecg12SampleCount++;
    }

    private static void EnqueueEcg12Sample()
    {
        bool[] statuses = new bool[9];
        lock (_lock)
        {
            Array.Copy(lead12Status, statuses, statuses.Length);
        }

        int leadOffMask = CalculateEcg12LeadOffMask(statuses);
        int signalQuality = CalculateEcg12SignalQuality(statuses);
        int leadStatusCode = leadOffMask;

        string sample =
            $"{lastEcg12_I},{lastEcg12_II},{lastEcg12_III}," +
            $"{lastEcg12_aVR},{lastEcg12_aVL},{lastEcg12_aVF}," +
            $"{lastEcg12_V1},{lastEcg12_V2},{lastEcg12_V3}," +
            $"{lastEcg12_V4},{lastEcg12_V5},{lastEcg12_V6}," +
            $"{lastEcg12HeartRate},{lastEcg12RR}," +
            $"{leadOffMask},{signalQuality},{leadStatusCode}";

        ecg12LeadQueue.Enqueue(sample);

        while (ecg12LeadQueue.Count >= 500)
            ecg12LeadQueue.TryDequeue(out _);
    }

    private static int CalculateEcg12LeadOffMask(bool[] statuses)
    {
        int mask = 0;
        if (statuses[5]) mask |= 1;   // RA
        if (statuses[6]) mask |= 2;   // LA
        if (statuses[7]) mask |= 4;   // LL
        if (statuses[8]) mask |= 8;   // V1
        if (statuses[0]) mask |= 16;  // V2
        if (statuses[1]) mask |= 32;  // V3
        if (statuses[2]) mask |= 64;  // V4
        if (statuses[3]) mask |= 128; // V5
        if (statuses[4]) mask |= 256; // V6
        return mask;
    }

    private static int CalculateEcg12SignalQuality(bool[] statuses)
    {
        bool anyLeadOff = statuses.Any(isOff => isOff);
        return anyLeadOff ? 0 : 85;
    }

    static void DecodeEcg12Hr(byte[] data, int startIndex)
    {
        if (startIndex + 5 >= data.Length)
            return;

        byte[] packedData = new byte[5];
        Array.Copy(data, startIndex + 1, packedData, 0, 5);
        byte[] unpacked = UnpackData(packedData);
        if (unpacked.Length < 4)
            return;

        int hr = (unpacked[1] << 8) | unpacked[0];
        int rr = (unpacked[3] << 8) | unpacked[2];

        lock (_lock)
        {
            lastEcg12HeartRate = (hr >= 0 && hr <= 300) ? hr : 0;
            lastEcg12RR = (rr >= 0 && rr <= 60) ? rr : 0;
            _hrLastUpdate = DateTime.UtcNow;
            LogThrottled("ecg12-heart-rate", $"ECG12 HR: {lastEcg12HeartRate} BPM, RR={lastEcg12RR}", EcgRecordingProgressLogInterval);
        }
    }

static void DecodeEcg12Status(byte[] data, int startIndex)
{
    if (startIndex + 2 >= data.Length)
        return;

    byte head = data[startIndex + 1];   // 0x80 marker
    byte info = data[startIndex + 2];   // actual status byte
    byte unpackedInfo = (byte)(info & 0x7F);  // strip bit 7

    lock (_lock)
    {
        lead12Status[5] = (unpackedInfo & 0x20) != 0;  // RA — bit 5
        lead12Status[6] = (unpackedInfo & 0x10) != 0;  // LA — bit 4
        lead12Status[7] = (unpackedInfo & 0x08) != 0;  // LL — bit 3
        lead12Status[8] = (unpackedInfo & 0x04) != 0;  // V1 — bit 2
        LogThrottled(
            "ecg12-limb-status",
            $"ECG12 limb status: RA={(lead12Status[5] ? "Off" : "OK")}, LA={(lead12Status[6] ? "Off" : "OK")}, LL={(lead12Status[7] ? "Off" : "OK")}, V1={(lead12Status[8] ? "Off" : "OK")}",
            EcgRecordingProgressLogInterval);
    }

    PushEcg12LeadStatusToLua();
}
static void DecodeEcg12VStatus(byte[] data, int startIndex)
{
    if (startIndex + 2 >= data.Length)
        return;

    byte dataByte = data[startIndex + 1];
    lock (_lock)
    {
        lead12Status[0] = (dataByte & 0x01) != 0;  // V2
        lead12Status[1] = (dataByte & 0x02) != 0;  // V3
        lead12Status[2] = (dataByte & 0x04) != 0;  // V4
        lead12Status[3] = (dataByte & 0x08) != 0;  // V5
        lead12Status[4] = (dataByte & 0x10) != 0;  // V6
        LogThrottled(
            "ecg12-v-status",
            $"ECG12 chest status: V2={(lead12Status[0] ? "Off" : "OK")}, V3={(lead12Status[1] ? "Off" : "OK")}, V4={(lead12Status[2] ? "Off" : "OK")}, V5={(lead12Status[3] ? "Off" : "OK")}, V6={(lead12Status[4] ? "Off" : "OK")}",
            EcgRecordingProgressLogInterval);
    }

    PushEcg12LeadStatusToLua();
}

    static void SetEcg12FilterMode(byte filterMode)
    {
        if (filterMode > 3)
        {
            Log($"Invalid ECG12 filter mode: {filterMode}. Must be 0-3");
            return;
        }

        try
        {
            byte[] command = new byte[] { 0x41, (byte)(0x80 | filterMode) };
            _serialPortECG12.Write(command, 0, command.Length);
            string[] modeNames = { "Diagnosis", "Monitor", "Operation", "Strong filter" };
            Log($"ECG12 filter mode set to {modeNames[filterMode]}");
            Thread.Sleep(100);
        }
        catch (Exception ex)
        {
            Log($"ECG12 filter mode error: {ex.Message}");
        }
    }

    private static void ClearEcg12Buffers()
    {
        ecg12WriteIndex = 0;
        ecg12SampleCount = 0;
        Log("ECG12 buffers cleared");
    }

private static string BuildEcg12LeadStatusText()
{
    bool v1Off, raOff, laOff, llOff, v2Off, v3Off, v4Off, v5Off, v6Off;

    lock (_lock)
    {
        v1Off = lead12Status[8];
        raOff = lead12Status[5];
        laOff = lead12Status[6];
        llOff = lead12Status[7];
        v2Off = lead12Status[0];
        v3Off = lead12Status[1];
        v4Off = lead12Status[2];
        v5Off = lead12Status[3];
        v6Off = lead12Status[4];
    }

    var offLeads = new List<string>();
    if (raOff) offLeads.Add("RA");
    if (laOff) offLeads.Add("LA");
    if (llOff) offLeads.Add("LL");
    if (v1Off) offLeads.Add("V1");
    if (v2Off) offLeads.Add("V2");
    if (v3Off) offLeads.Add("V3");
    if (v4Off) offLeads.Add("V4");
    if (v5Off) offLeads.Add("V5");
    if (v6Off) offLeads.Add("V6");

    if (_isECG12Active)
    {
        int secondsStored = (int)Math.Min(ecg12SampleCount / (double)ECG_SAMPLE_RATE_HZ, ECG_RECORDING_WINDOW_SECONDS);
        bool isRolling = ecg12SampleCount >= ECG_BUFFER_SIZE;

        string timerText = isRolling
            ? "Recording Complete"
            : $"Recording {secondsStored}s of 40s";

        if (offLeads.Count > 0)
            return $"{timerText}  -  Lead off: {string.Join(", ", offLeads)}";

        return timerText;
    }

    if (offLeads.Count == 0)
        return "All leads connected";

    if (offLeads.Count == 9)
        return "Attach all leads";

    return $"Attach lead: {string.Join(", ", offLeads)}";
}


    private static NetworkStream _ecg12LuaStream = null;  // only if not already saved elsewhere

    private static void SendToEcg12Lua(string message)
    {
        try
        {
            var stream = _ecg12LuaStream;
            if (stream == null || !stream.CanWrite) return;
            byte[] bytes = Encoding.UTF8.GetBytes(message + "\n");
            stream.Write(bytes, 0, bytes.Length);
        }
        catch { }
    }


    private static void PushEcg12LeadStatusToLua()
    {
        string text = BuildEcg12LeadStatusText();
        if (text == _lastEcg12LeadStatusText) return;
        _lastEcg12LeadStatusText = text;
        SendToEcg12Lua($"LEAD_STATUS_TEXT:{text}");
    }




}
