using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;




public partial class VitalsChairApp
{
    private static readonly TimeSpan EcgPreviewHealthLogInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan EcgRecordingProgressLogInterval = TimeSpan.FromSeconds(30);

    static async Task StartEcgTcpServerAsync()
    {
        try
        {
            _ecgServer = new TcpListener(IPAddress.Any, EcgPort);
            _ecgServer.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _ecgServer.Start();
            Log($" ECG TCP Server started on {IPAddress.Any}:{EcgPort}");

            while (_tcpEcgRunning)
            {
                try
                {
                    LogThrottled("ecg-waiting", "Waiting for ECG connection...", TimeSpan.FromMinutes(1));
                    TcpClient client = await _ecgServer.AcceptTcpClientAsync();
                    lock (_ecgClientLock)
                    {
                        _ecgClients.Add(client);
                    }

                    LogThrottled("ecg-connected", $"New ECG client connected: {client.Client.RemoteEndPoint}", TimeSpan.FromSeconds(10));
                    _ = Task.Run(() => HandleEcgClientAsync(client, CancellationToken.None));
                }
                catch (Exception ex)
                {
                    if (_tcpEcgRunning)
                        Log($" ECG TCP Server error: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log($" ECG TCP Server startup error: {ex.Message}");
        }
    }

    static async Task HandleEcgClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            using NetworkStream stream = client.GetStream();
            client.Client.NoDelay = true;
            client.Client.SendBufferSize = 4096;
            client.Client.ReceiveBufferSize = 4096;
            stream.ReadTimeout = 1000;
            stream.WriteTimeout = 500;

            byte[] buffer = new byte[256];
            int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
            string deviceType = Encoding.UTF8.GetString(buffer, 0, bytesRead).Trim();
            LogThrottled($"ecg-handshake:{deviceType}", $"ECG Connected Remote: {client.Client.RemoteEndPoint} for {deviceType}", TimeSpan.FromSeconds(10));

            if (deviceType != "ecg7")
                return;

            var interval = TimeSpan.FromMilliseconds(2);
            var lastSendTime = DateTime.UtcNow;
            int packetCount = 0;
            int droppedPackets = 0;
            int sendCounter = 0;

            while (_tcpEcgRunning && _isECGActive && client.Connected && !cancellationToken.IsCancellationRequested)
            {
                var currentTime = DateTime.UtcNow;
                var elapsed = currentTime - lastSendTime;

                if (elapsed >= interval)
                {
                    sendCounter++;

                    if ((sendCounter % 3) != 0)
                    {
                        droppedPackets++;
                        lastSendTime = currentTime;
                    }
                    else
                    {
                        string response;
                        lock (_lock)
                        {
                            int leadIII = lastEcg2 - lastEcg1;
                            int leadAVR = -((lastEcg1 + lastEcg2) / 2);
                            int leadAVL = lastEcg2 - (leadIII / 2);
                            int leadAVF = lastEcg1 - (leadIII / 2);

                            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                            response = $"TS={timestamp}, I={lastEcg2}, II={lastEcg1}, V={lastEcg3}, " +
                                       $"III={leadIII}, aVR={leadAVR}, aVL={leadAVL}, aVF={leadAVF}, " +
                                       $"HR={lastHeartRate}, RR={calculatedRR}\n";
                        }

                        byte[] responseBytes = Encoding.UTF8.GetBytes(response);

                        if (client.Connected && stream.CanWrite)
                        {
                            try
                            {
                                if (client.Client.Poll(0, SelectMode.SelectWrite))
                                {
                                    await stream.WriteAsync(responseBytes, 0, responseBytes.Length, cancellationToken).ConfigureAwait(false);
                                    packetCount++;
                                }
                                else
                                {
                                    droppedPackets++;
                                    if (droppedPackets % 100 == 0)
                                    {
                                        LogThrottled(
                                            "ecg-preview-dropped",
                                            $"ECG preview client slow - dropped {droppedPackets} packets",
                                            EcgRecordingProgressLogInterval);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Log($"ECG write error: {ex.Message}");
                                break;
                            }
                        }
                        else
                        {
                            break;
                        }

                        lastSendTime = currentTime;
                    }
                }

                var sleepTime = (interval - (DateTime.UtcNow - lastSendTime)).TotalMilliseconds;
                if (sleepTime > 0.5)
                    await Task.Delay(TimeSpan.FromMilliseconds(sleepTime), cancellationToken);

                LogThrottled(
                    "ecg-preview-health",
                    $"ECG preview streaming: sent={packetCount}, dropped={droppedPackets}, clients={_ecgClients.Count}",
                    EcgPreviewHealthLogInterval);
            }

            Log($"ECG preview stopped: sent={packetCount}, dropped={droppedPackets}");
            if (client.Connected && stream.CanWrite)
            {
                byte[] stopBytes = Encoding.UTF8.GetBytes("ECG_STOP\n");
                await stream.WriteAsync(stopBytes, 0, stopBytes.Length, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            Log($" ECG client error: {ex.Message}");
        }
        finally
        {
            lock (_ecgClientLock)
            {
                _ecgClients.Remove(client);
            }

            client.Close();
            LogThrottled("ecg-client-disconnected", "ECG preview client disconnected", TimeSpan.FromSeconds(10));
        }
    }

    static async Task StartEcg7TcpServerAsync()
    {
        try
        {
            _ecg7Server = new TcpListener(IPAddress.Any, ECG7Port);
            _ecg7Server.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _ecg7Server.Start();
            Log($" ECG7 TCP Server started on {IPAddress.Any}:{ECG7Port}");

            while (_ecg7Running)
            {
                try
                {
                    TcpClient client = await _ecg7Server.AcceptTcpClientAsync();
                    lock (_ecg7ClientLock)
                    {
                        _ecg7Clients.Add(client);
                    }

                    _ = Task.Run(() => HandleEcg7ClientAsync(client, CancellationToken.None));
                }
                catch (Exception ex)
                {
                    if (_ecg7Running)
                        Log($" ECG7 TCP Server error: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log($" ECG7 TCP Server startup error: {ex.Message}");
        }
    }

    static async Task HandleEcg7ClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            using NetworkStream stream = client.GetStream();
            stream.ReadTimeout = 5000;
            stream.WriteTimeout = 5000;

            byte[] buffer = new byte[256];
            int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
            string deviceType = Encoding.UTF8.GetString(buffer, 0, bytesRead).Trim();
            LogThrottled($"ecg7-handshake:{deviceType}", $"ECG7 client connected: {deviceType}", TimeSpan.FromSeconds(10));

            if (deviceType != "ecg7")
                return;

            byte[] startBytes = Encoding.UTF8.GetBytes("ECG7_START\n");
            await stream.WriteAsync(startBytes, 0, startBytes.Length, cancellationToken);
            _ecg7LuaStream = stream;
            _lastLeadStatusText = null;
            PushLeadStatusToLua();

            while (ecg5LeadQueue.TryDequeue(out _)) { }

            int packetsSent = 0;
            List<string> batch = new List<string>();
            DateTime nextFrame = DateTime.UtcNow;

            while (_ecg7Running && client.Connected && !cancellationToken.IsCancellationRequested)
            {
                while (batch.Count < SAMPLES_PER_DISPLAY_FRAME)
                {
                    if (ecg5LeadQueue.TryDequeue(out string s))
                        batch.Add(s.Trim());
                    else
                        break;
                }

                if (batch.Count > 0)
                {
                    string dataToSend = string.Join(";", batch) + "\n";
                    byte[] bytes = Encoding.UTF8.GetBytes(dataToSend);

                    try
                    {
                        await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken);
                        packetsSent++;
                    }
                    catch (Exception ex)
                    {
                        Log($" ECG7 send error: {ex.Message}");
                        break;
                    }

                    batch.Clear();
                }

                nextFrame = nextFrame.AddMilliseconds(1000.0 / DISPLAY_RATE);
                var sleepTime = nextFrame - DateTime.UtcNow;

                if (sleepTime.TotalMilliseconds > 0)
                    await Task.Delay(sleepTime, cancellationToken);
                else
                    nextFrame = DateTime.UtcNow;

                LogThrottled(
                    "ecg7-preview-health",
                    $"ECG7 preview streaming: frames={packetsSent}, queue={ecg5LeadQueue.Count}, clients={_ecg7Clients.Count}",
                    EcgPreviewHealthLogInterval);
            }

            if (client.Connected && stream.CanWrite)
            {
                byte[] stopBytes = Encoding.UTF8.GetBytes("ECG7_STOP\n");
                await stream.WriteAsync(stopBytes, 0, stopBytes.Length, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            Log($" ECG7 error: {ex.Message}");
        }
        finally
        {
            lock (_ecg7ClientLock)
            {
                _ecg7Clients.Remove(client);
            }

            client.Close();
            _ecg7LuaStream = null;
            LogThrottled("ecg7-disconnected", $"ECG7 preview client disconnected. Active: {_ecg7Clients.Count}", TimeSpan.FromSeconds(10));
        }
    }

static void StartEcg5Recording()
{
    ClearEcg5Buffers();
    _isECGActive = true;

    _lastLeadStatusText = null;  // force immediate refresh
    PushLeadStatusToLua();

    Log($"ECG 5-lead recording started - storing latest {ECG_RECORDING_WINDOW_SECONDS}s, preview streaming to clients");
}

static void StopEcg5Recording()
{
    if (!_isECGActive)
        return;

    storedEcgHeartRate = lastHeartRate;
    _isECGActive = false;

    // Close all ECG5 preview clients to stop streaming
    lock (_ecgClientLock)
    {
        foreach (var client in _ecgClients.ToList())
        {
            try { client.Close(); }
            catch { }
        }
        _ecgClients.Clear();
    }
    
    _lastLeadStatusText = null;
    PushLeadStatusToLua();

    Log($"ECG 5-lead recording stopped - stored {ecg5SampleCount} samples, preview streaming ended");
}


private static void RecordTimerTick()
{
    lock (_recordLock)
    {
        _recordSecondsLeft--;

        if (_recordSecondsLeft <= 0)
        {
            _recordTimer?.Dispose();
            _recordTimer = null;
        }
    }

    _lastLeadStatusText = null;  // force send every tick (text changes every second anyway)
    PushLeadStatusToLua();

    if (_recordSecondsLeft <= 0)
    {
        StopEcg5Recording();  // auto-stop at 0 — uploads/finalizes
    }
}

    static async Task DecodeAndPrintEcgWaveAsync(byte[] data, int startIndex, CancellationToken cancellationToken)
    {
        if (startIndex + 8 >= data.Length)
            throw new ArgumentException("Invalid ECG wave frame length");

        byte head = data[startIndex + 1];
        byte ch1High = data[startIndex + 2];
        byte ch1Low = data[startIndex + 3];
        byte ch2High = data[startIndex + 4];
        byte ch2Low = data[startIndex + 5];
        byte ch3High = data[startIndex + 6];
        byte ch3Low = data[startIndex + 7];

        int ch1Raw = (((head & 0x01) << 7) | (ch1High & 0x7F)) << 8 | ((head & 0x02) << 6 | (ch1Low & 0x7F));
        int ch2Raw = (((head & 0x04) << 5) | (ch2High & 0x7F)) << 8 | ((head & 0x08) << 4 | (ch2Low & 0x7F));
        int ch3Raw = (((head & 0x10) << 3) | (ch3High & 0x7F)) << 8 | ((head & 0x20) << 2 | (ch3Low & 0x7F));

        int ecgCh1 = ch1Raw - 2048;
        int ecgCh2 = ch2Raw - 2048;
        int ecgCh3 = ch3Raw - 2048;
        bool paceFlag = (head & 0x80) != 0;
        bool heartBeatFlag = (head & 0x40) != 0;

        lock (_lock)
        {
            lastEcg1 = ecgCh1;
            lastEcg2 = ecgCh2;
            lastEcg3 = ecgCh3;

            _ecgSampleCount++;
            if ((DateTime.Now - _lastEcgCountLog).TotalSeconds >= 1.0)
            {
                LogThrottled("ecg-uart-rate", $"ECG UART healthy: {_ecgSampleCount} samples/sec (target: 500 Hz)", EcgPreviewHealthLogInterval);
                _ecgSampleCount = 0;
                _lastEcgCountLog = DateTime.Now;
            }

            lastEcgRawII = ch1Raw;
            lastEcgRawI = ch2Raw;
            lastEcgRawV = ch3Raw;
            lastPaceFlag = paceFlag;
            lastHeartBeatFlag = heartBeatFlag;

            int leadIII = ecgCh2 - ecgCh1;
            int leadAVR = -((ecgCh2 + ecgCh1) / 2);
            int leadAVL = ecgCh2 - (leadIII / 2);
            int leadAVF = ecgCh1 - (leadIII / 2);

            int leadIII_raw = lastEcgRawI - lastEcgRawII + 2048;
            int avr = 2048 - ((lastEcgRawI - 2048 + lastEcgRawII - 2048) / 2);
            int avl = lastEcgRawI - (leadIII_raw - 2048) / 2;
            int avf = lastEcgRawII - (leadIII_raw - 2048) / 2;

            EnqueueEcg5PreviewSample(leadIII_raw, avr, avl, avf);

            if (_isECGActive)
            {
                StoreEcg5Sample(ecgCh1, ecgCh2, ecgCh3, leadIII, leadAVR, leadAVL, leadAVF);
                LogEcg5RecordingProgress();
            }

            if (heartBeatFlag)
            {
                rWaveTimestamps.Enqueue(DateTime.Now);
                while (rWaveTimestamps.Count > 30)
                    rWaveTimestamps.Dequeue();
            }

            if (_isLiveMode)
            {
                PrintLiveData();
            }
        }
    }

    private static void LogEcg5RecordingProgress()
    {
        if (!_isECGActive)
            return;

        double secondsStored = Math.Min(ecg5SampleCount / (double)ECG_SAMPLE_RATE_HZ, ECG_RECORDING_WINDOW_SECONDS);
        LogThrottled(
            "ecg5-recording-progress",
            $"ECG5 recording: {ecg5SampleCount}/{ECG_BUFFER_SIZE} samples ({secondsStored:F1}s/{ECG_RECORDING_WINDOW_SECONDS}s window), HR={lastHeartRate}, RR={calculatedRR}",
            EcgRecordingProgressLogInterval);
    }

private static void EnqueueEcg5PreviewSample(int leadIII, int avr, int avl, int avf)
{
    while (ecg5LeadQueue.Count >= 500)
        ecg5LeadQueue.TryDequeue(out _);

    int leadOffMask = (leadStatus[0] ? 1 : 0) |
                      (leadStatus[1] ? 2 : 0) |
                      (leadStatus[2] ? 4 : 0) |
                      (leadStatus[3] ? 8 : 0);
    bool anyLeadOff = leadStatus[0] || leadStatus[1] || leadStatus[2] || leadStatus[3];
    int signalQuality = anyLeadOff ? 0 : 85;
    int leadStatusCode = leadOffMask;

    string sample =
        $"{lastEcgRawI}," +
        $"{lastEcgRawII}," +
        $"{lastEcgRawV}," +
        $"{leadIII}," +
        $"{avr}," +
        $"{avl}," +
        $"{avf}," +
        $"{lastHeartRate}," +
        $"{lastRR}," +
        $"{leadOffMask}," +
        $"{signalQuality}," +
        $"{leadStatusCode}\n";

    ecg5LeadQueue.Enqueue(sample);

    // ── Single call, no duplicate logic ──
    PushLeadStatusToLua();
}

private static NetworkStream _ecg7LuaStream = null;

private static void SendToEcg7Lua(string message)
{
    try
    {
        var stream = _ecg7LuaStream;
        if (stream == null || !stream.CanWrite) return;
        byte[] bytes = Encoding.UTF8.GetBytes(message + "\n");
        stream.Write(bytes, 0, bytes.Length);
    }
    catch { /* connection dropped, ignore */ }
}

    private static void StoreEcg5Sample(int leadII, int leadI, int leadV, int leadIII, int leadAVR, int leadAVL, int leadAVF)
    {
        // Circular buffer: after 40 seconds, each new sample replaces the oldest one.
        ecgLeadIBuffer[ecg5WriteIndex] = leadI;
        ecgLeadIIBuffer[ecg5WriteIndex] = leadII;
        ecgLeadIIIBuffer[ecg5WriteIndex] = leadIII;
        ecgLeadVBuffer[ecg5WriteIndex] = leadV;
        ecgLeadAvrBuffer[ecg5WriteIndex] = leadAVR;
        ecgLeadAvlBuffer[ecg5WriteIndex] = leadAVL;
        ecgLeadAvfBuffer[ecg5WriteIndex] = leadAVF;

        ecg5WriteIndex = (ecg5WriteIndex + 1) % ECG_BUFFER_SIZE;
        if (ecg5SampleCount < ECG_BUFFER_SIZE)
            ecg5SampleCount++;
    }

static async Task DecodeAndPrintEcgStatusAsync(byte[] data, int startIndex, CancellationToken cancellationToken)
{
    if (startIndex + 3 >= data.Length)   // need ID, HEAD, Status, Saturate at minimum
        throw new ArgumentException("Invalid ECG status frame length");

    byte id        = data[startIndex];       // 0x06
    byte head      = data[startIndex + 1];   // HEAD — bit7 overflow bits (ignorable here, values are small)
    byte statusByte   = data[startIndex + 2];   //  actual Status byte
    byte saturateByte = data[startIndex + 3];   //  actual Saturate byte

    bool vOff  = (statusByte & 0x08) != 0;
    bool raOff = (statusByte & 0x04) != 0;
    bool laOff = (statusByte & 0x02) != 0;
    bool llOff = (statusByte & 0x01) != 0;

    bool vSat   = (saturateByte & 0x08) != 0;
    bool iiiSat = (saturateByte & 0x04) != 0;
    bool iSat   = (saturateByte & 0x02) != 0;
    bool iiSat  = (saturateByte & 0x01) != 0;

    //Log($"🔍 FRAME 0x06 — HEAD=0x{head:X2} Status=0x{statusByte:X2} Sat=0x{saturateByte:X2} → V={vOff} RA={raOff} LA={laOff} LL={llOff}");

    lock (_lock)
    {
        leadStatus[0] = vOff;
        leadStatus[1] = raOff;
        leadStatus[2] = laOff;
        leadStatus[3] = llOff;
        leadSaturation[0] = vSat;
        leadSaturation[1] = iiiSat;
        leadSaturation[2] = iSat;
        leadSaturation[3] = iiSat;

        if (_isLiveMode)
            PrintLiveData();
        else
            LogThrottled("ecg-status",
                $"ECG5 lead status: V={(vOff?"Off":"OK")}, RA={(raOff?"Off":"OK")}, LA={(laOff?"Off":"OK")}, LL={(llOff?"Off":"OK")}",
                EcgRecordingProgressLogInterval);
    }
   PushLeadStatusToLua(); 
}

    static async Task DecodeAndPrintEcgHrAsync(byte[] data, int startIndex, CancellationToken cancellationToken)
    {
        if (startIndex + 4 >= data.Length)
            throw new ArgumentException("Invalid ECG HR frame length");

        byte id = data[startIndex];
        byte head = data[startIndex + 1];
        byte hrHigh = data[startIndex + 2];
        byte hrLow = data[startIndex + 3];

        int hr = (((head & 0x01) << 7) | (hrHigh & 0x7F)) << 8 | ((head & 0x02) << 6 | (hrLow & 0x7F));
        hr = (hr >= 0 && hr <= 300) ? hr : 0;

        lock (_lock)
        {
            lastHeartRate = hr;

            if (_isLiveMode)
                PrintLiveData();
            else
                LogThrottled("ecg-heart-rate", $"ECG5 HR: {hr} BPM", EcgRecordingProgressLogInterval);
        }
    }

    static async Task DecodeAndPrintEcgPvcAsync(byte[] data, int startIndex, CancellationToken cancellationToken)
    {
        if (startIndex + 2 >= data.Length)
            throw new ArgumentException("Invalid ECG PVC frame length");

        byte id = data[startIndex];
        byte pvcByte = data[startIndex + 1];
        int pvc = pvcByte & 0x7F;
        pvc = (pvc >= 0 && pvc <= 99) ? pvc : 0;

        lock (_lock)
        {
            if (_isLiveMode)
                PrintLiveData();
            else
                LogThrottled("ecg-pvc", $"ECG5 PVC: {pvc}/min", EcgRecordingProgressLogInterval);
        }
    }

    static async Task DecodeAndPrintRRAsync(byte[] data, int startIndex, CancellationToken cancellationToken)
    {
        if (startIndex + 5 >= data.Length)
            return;

        byte head = data[startIndex + 1];
        byte rrHigh = data[startIndex + 2];
        byte rrLow = data[startIndex + 3];
        int rr = (((head & 0x01) << 7) | (rrHigh & 0x7F)) << 8 | ((head & 0x02) << 6 | (rrLow & 0x7F));

        lock (_lock)
        {
            lastRR = (rr >= 0 && rr <= 150) ? rr : 0;
            if (_isLiveMode)
                PrintLiveData();
        }
    }

    static async Task DecodeAndPrintARRAsync(byte[] data, int startIndex, CancellationToken cancellationToken)
    {
        if (startIndex + 7 >= data.Length)
            return;

        byte head = data[startIndex + 1];
        byte arrType = data[startIndex + 2];
        int arr = (head & 0x01) << 7 | (arrType & 0x7F);

        lock (_lock)
        {
            lastARR = arr;
            if (_isLiveMode)
                PrintLiveData();
        }
    }

    static async Task DecodeAndPrintSTAsync(byte[] data, int startIndex, CancellationToken cancellationToken)
    {
        if (startIndex + 9 >= data.Length)
            return;

        byte head = data[startIndex + 1];
        byte st1High = data[startIndex + 2];
        byte st1Low = data[startIndex + 3];
        byte st2High = data[startIndex + 4];
        byte st2Low = data[startIndex + 5];
        byte st3High = data[startIndex + 6];
        byte st3Low = data[startIndex + 7];

        int st1 = (((head & 0x01) << 7) | (st1High & 0x7F)) << 8 | ((head & 0x02) << 6 | (st1Low & 0x7F));
        int st2 = (((head & 0x04) << 5) | (st2High & 0x7F)) << 8 | ((head & 0x08) << 4 | (st2Low & 0x7F));
        int st3 = (((head & 0x10) << 3) | (st3High & 0x7F)) << 8 | ((head & 0x20) << 2 | (st3Low & 0x7F));

        lock (_lock)
        {
            lastST_I = st1 - 2048;
            lastST_II = st2 - 2048;
            lastST_V = st3 - 2048;
            if (_isLiveMode)
                PrintLiveData();
        }
    }

    static async Task SetEcgFilterModeAsync(SerialPort serialPort, byte filterMode, CancellationToken cancellationToken)
    {
        byte id = 0x47;
        byte head = 0x80;
        byte data = (byte)(filterMode | 0x80);
        byte checksum = (byte)(((id + head + data) & 0x7F) | 0x80);
        byte[] commandFrame = new byte[] { id, head, data, checksum };

        await serialPort.BaseStream.WriteAsync(commandFrame, 0, commandFrame.Length, cancellationToken);
        await serialPort.BaseStream.FlushAsync(cancellationToken);
        Log($"ECG Filter Mode command sent: Mode={filterMode} (0=diagnostic, 1=monitor, 2=surgery)");
    }

    static async Task InitializeEcgSettingsAsync(SerialPort serialPort, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(1000, cancellationToken);

            byte[] resetAck = new byte[] { 0x01, 0x81 };
            serialPort.Write(resetAck, 0, resetAck.Length);
            await Task.Delay(100, cancellationToken);

            await SetEcgFilterModeAsync(serialPort, 1, cancellationToken);
            Log(" ECG settings initialized - Filter mode set to Monitor");
        }
        catch (Exception ex)
        {
            Log($" Error initializing ECG settings: {ex.Message}");
        }
    }


private static string BuildLeadStatusText()
{
    bool vOff  = leadStatus[0];
    bool raOff = leadStatus[1];
    bool laOff = leadStatus[2];
    bool llOff = leadStatus[3];

    var offLeads = new List<string>();
    if (raOff) offLeads.Add("RA");
    if (laOff) offLeads.Add("LA");
    if (llOff) offLeads.Add("LL");
    if (vOff)  offLeads.Add("V");

    if (_isECGActive)
    {
        int secondsStored = (int)Math.Min(ecg5SampleCount / (double)ECG_SAMPLE_RATE_HZ, ECG_RECORDING_WINDOW_SECONDS);
        bool isRolling = ecg5SampleCount >= ECG_BUFFER_SIZE;

        string timerText = isRolling
            ? "Recording Complete"
            : $"Recording {secondsStored}s of 40s";

        if (offLeads.Count > 0)
            return $"{timerText}  -  Lead off: {string.Join(", ", offLeads)}";

        return timerText;
    }

    if (offLeads.Count == 0)
        return "All leads connected";

    if (offLeads.Count == 4)
        return "Attach all leads";

    return $"Attach leads: {string.Join(", ", offLeads)}";
}


private static void PushLeadStatusToLua()
{
    string text = BuildLeadStatusText();

    if (text == _lastLeadStatusText) return;
    _lastLeadStatusText = text;

    SendToEcg7Lua($"LEAD_STATUS_TEXT:{text}");
}









}
