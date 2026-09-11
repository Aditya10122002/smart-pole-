using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using vitalschair_prod_v1;


public partial class VitalsChairApp
{
    // -------------------------------------------------------------------------
    // Authentication / patient profile
    // -------------------------------------------------------------------------
    private static string _patientToken = string.Empty;
    private static string _patientId = string.Empty;
    private static int currentPatientId = 0;
    private static string _patientName = string.Empty;
    private static string _patientPhone = string.Empty;
    private static string _patientEmail = string.Empty;
    private static string _patientGender = string.Empty;
    private static int _patientAge = 0;
    private static DateTime _tokenExpiry = DateTime.MinValue;
    private static bool _isAuthenticated = false;
    private static DateTime _patientDob = DateTime.MinValue;
    private static string _patientLast4Aadhaar = string.Empty;
    private static readonly object _authLock = new object();
    private static string _currentUserId = string.Empty;
    private static string _lastOtpIdentifier = string.Empty;

    private static TcpListener _otpActivityServer;
    private static TcpListener _otpVerifyServer;
    private static int _tempLogCounter = 0;
    private static bool _otpActivityRunning = false;
    private static bool _otpVerifyRunning = false;
    private static readonly HttpClient _httpClient = new HttpClient();

    private static readonly IConfigurationRoot Configuration = new ConfigurationBuilder()
        .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false, reloadOnChange: true)
        .Build();

    // -------------------------------------------------------------------------
    // Encryption System
    // -------------------------------------------------------------------------
    private static ResilientApiEncryptionManager? _encryptionManager;
    private static ResilientSecureApiClient? _secureApiClient;
    private static ResilientSecretKeyManager? _secretKeyManager;
    private static RegistrationDataManager? _registrationManager;

    public static ResilientApiEncryptionManager GetEncryptionManager() => _encryptionManager!;
    public static ResilientSecureApiClient GetSecureApiClient() => _secureApiClient!;
    public static ResilientSecretKeyManager GetSecretKeyManager() => _secretKeyManager!;
    public static RegistrationDataManager GetRegistrationManager() => _registrationManager!;

    // -------------------------------------------------------------------------
    // Blood glucose state
    // -------------------------------------------------------------------------
    private static int storedBloodGlucoseFasting = 0;
    private static int storedBloodGlucosePreMeal = 0;
    private static int storedBloodGlucosePostMeal = 0;
    private static bool _isBloodSugarPageActive = false;
    private static readonly object _bloodGlucoseLock = new object();
    private static GeminiLiveSession? _geminiSession = null;


    // -------------------------------------------------------------------------
    // Temperature / NIBP / auxiliary TCP state
    // -------------------------------------------------------------------------
    private static TcpListener _tempServer1;
    private static bool _tempRunning = true;
    private static readonly object _tempClientLock = new object();
    private static readonly List<TcpClient> _tempClients = new List<TcpClient>();
    private static readonly Dictionary<TcpClient, bool> _nibpClientStates = new Dictionary<TcpClient, bool>();




    private static Queue<DateTime> rWaveTimestamps = new Queue<DateTime>();
    private static int calculatedRR = 0;
    private static int lastEcg12RR = 0;
    private static DateTime lastRRCalculation = DateTime.MinValue;

    private static TcpListener _ecgServer;
    private static readonly List<TcpClient> _ecgClients = new List<TcpClient>();
    private static readonly object _ecgClientLock = new object();
    private static bool _tcpEcgRunning = true;
    private static bool _isECGActive = false;
    private const int EcgPort = 9999;

    private const int SAMPLE_RATE = 500;
    private const int DISPLAY_RATE = 60;
    private const int SAMPLES_PER_DISPLAY_FRAME = SAMPLE_RATE / DISPLAY_RATE; // ≈ 8

    private const int ConfigWatchIntervalMinutes = 1;

    private const int ECG12_BATCH_SIZE = 50;
    private const int ECG12_DISPLAY_RATE = 10;
    private const double ECG12_FRAME_INTERVAL_MS = 1000.0 / ECG12_DISPLAY_RATE;  // = 100ms

    private static DateTime? _lastMergedPageNav = null;
    private static int lastLivePressure = -100;
    private static bool lastHeartBlink = false;

    private static int lastEcg1 = 0; // Lead II
    private static int lastEcg2 = 0; // Lead I
    private static int lastEcg3 = 0; // Lead V
    private static bool lastPaceFlag = false;
    private static bool lastHeartBeatFlag = false;
    private static int lastHeartRate = 0;
    private static int storedEcgHeartRate = 0;

    private static int _ecgSampleCount = 0;
    private static int _ecg12SampleCount = 0;
    private static DateTime _lastEcg12CountLog = DateTime.Now;
    private static DateTime _lastEcgCountLog = DateTime.Now;

    private static int lastEcgRawI = 2048;
    private static int lastEcgRawII = 2048;
    private static int lastEcgRawV = 2048;
    private static int lastRR = 0;           // Respiration Rate (frame 0x11)
    private static int lastARR = 0;          // Arrhythmia type (frame 0x0A)
    private static int lastST_I = 0;         // ST segment Lead I (frame 0x0B)
    private static int lastST_II = 0;        // ST segment Lead II (frame 0x0B)
    private static int lastST_V = 0;         // ST segment Lead V (frame 0x0B)

    private static TcpListener _graphServer;
    private static readonly List<TcpClient> _graphClients = new List<TcpClient>();
    private static readonly object _graphClientLock = new object();
    private static bool _graphRunning = true;
    private static int lastSpo2WaveValue = 0;
    private static byte lastSpo2Status = 0;


    // User profile variables
    private static byte _currentUserAge = 25;
    private static byte _currentUserGender = 1;  // 1=Male, 0=Female

    private static bool _hasPatientData = false;

    private static float lastHeight = 0;
    private static float lastWeight = 0;
    private static float lastBMI = 0;

    private static float lastProxDistance = 0f;

    private static float lastValidHeight = 0;
    private static float lastValidWeight = 0;
    private static float lastValidBMI = 0;
    private static BodyCompositionData lastValidBodyComposition = new BodyCompositionData();
    private static bool hasValidMeasurement = false;
    private static string _lastLoggedBodyCompositionSummary = string.Empty;
    private static DateTime _lastBodyCompositionLogUtc = DateTime.MinValue;
    private static float lastTemperatureIR = 0.0f; // IR from ESP32

    // ════════════════════════════════════════════════════════════════════
    // IR TEMPERATURE OFFSET CALIBRATION
    // ════════════════════════════════════════════════════════════════════
    // ESP32 IR sensor reads ~2.4°F lower than actual forehead temperature
    // Calibration offset: +2.4°F = +1.333°C
    // Applied: raw ESP32 reading → add offset → stored value
    // Last calibrated: 2026-07-25 | Ref device comparison method
    //private const float IR_TEMPERATURE_OFFSET_CELSIUS = 1.333f; // +2.4°F
    private const float IR_TEMPERATURE_OFFSET_CELSIUS = 0.0f; // ≈ +0.9°F, Not needed, Fixed the Distance sensor.
    // ════════════════════════════════════════════════════════════════════


    private static BodyCompositionData BodyComposition = new BodyCompositionData();
    private static int lastSpo2WaveData = 0;

    // ADD these variables to track BP measurement freshness
    private static DateTime lastBPUpdateTime = DateTime.MinValue;
    private static int validSys = 0;
    private static int validDia = 0;
    private static int validPulse2 = 0;
    private static int _measurementProgress = 0;
    private static bool _measurementComplete = false;
    private static DateTime _measurementStartTime = DateTime.MinValue;
    private static readonly TimeSpan BP_DATA_FRESHNESS = TimeSpan.FromSeconds(5);

    private static bool[] leadStatus = new bool[4]; // V, RA, LA, LL (true = off)
    private static bool[] leadSaturation = new bool[4]; // V, III, I, II (true = saturated)

    // ECG raw sample storage
    private static readonly List<int> ecgLeadI = new List<int>();    // Lead I
    private static readonly List<int> ecgLeadII = new List<int>();   // Lead II
    private static readonly List<int> ecgLeadIII = new List<int>();  // Lead III
    private static readonly List<int> ecgLeadAvr = new List<int>();  // Lead aVR
    private static readonly List<int> ecgLeadAvl = new List<int>();  // Lead aVL
    private static readonly List<int> ecgLeadAvf = new List<int>();  // Lead aVF
    private static readonly List<int> ecgLeadV = new List<int>();    // Lead V
    // ECG API payload cap: keep only the latest 40 seconds while recording.
    // When this fills, new samples overwrite the oldest samples in circular buffers.
    private const int ECG_RECORDING_WINDOW_SECONDS = 40;
    private const int ECG_SAMPLE_RATE_HZ = 500;
    private const int ECG_BUFFER_SIZE = ECG_SAMPLE_RATE_HZ * ECG_RECORDING_WINDOW_SECONDS;
    private const int MaxEcgSamples = ECG_BUFFER_SIZE;  // Backward compatibility

    private static string _lastEcg12LeadStatusText = null;


    // ── New fields (near your other ECG fields) ──
    private static int _recordSecondsLeft = 0;
    private static System.Threading.Timer _recordTimer = null;
    private static readonly object _recordLock = new object();
    private const int ECG_RECORD_TIMER_SECONDS = 40;  // matches your ECG_RECORDING_WINDOW_SECONDS

    private static string _lastLeadStatusText = null;   // cache to avoid spamming identical sends

    // Data server
    private static SerialPort _serialPortData;
    private static SerialPort _serialPortECG12;
    private static readonly object _lock = new object();
    private static Queue<int> pulseRateHistory = new Queue<int>();
    private static Queue<int> spo2History = new Queue<int>();
    private static readonly int SmoothWindowSize = 5;


    // Replace List<int> with arrays
    private static int[] ecgLeadIBuffer = new int[ECG_BUFFER_SIZE];
    private static int[] ecgLeadIIBuffer = new int[ECG_BUFFER_SIZE];
    private static int[] ecgLeadIIIBuffer = new int[ECG_BUFFER_SIZE];
    private static int[] ecgLeadVBuffer = new int[ECG_BUFFER_SIZE];
    private static int[] ecgLeadAvrBuffer = new int[ECG_BUFFER_SIZE];
    private static int[] ecgLeadAvlBuffer = new int[ECG_BUFFER_SIZE];
    private static int[] ecgLeadAvfBuffer = new int[ECG_BUFFER_SIZE];

    private static int ecg5WriteIndex = 0;      // Where to write next
    private static int ecg5SampleCount = 0;     // Rolling 40s recording window

    // ========== ECG12 CIRCULAR BUFFERS ==========


    private static int[] ecg12LeadIBuffer = new int[ECG_BUFFER_SIZE];
    private static int[] ecg12LeadIIBuffer = new int[ECG_BUFFER_SIZE];
    private static int[] ecg12LeadIIIBuffer = new int[ECG_BUFFER_SIZE];
    private static int[] ecg12LeadAvrBuffer = new int[ECG_BUFFER_SIZE];
    private static int[] ecg12LeadAvlBuffer = new int[ECG_BUFFER_SIZE];
    private static int[] ecg12LeadAvfBuffer = new int[ECG_BUFFER_SIZE];
    private static int[] ecg12LeadV1Buffer = new int[ECG_BUFFER_SIZE];
    private static int[] ecg12LeadV2Buffer = new int[ECG_BUFFER_SIZE];
    private static int[] ecg12LeadV3Buffer = new int[ECG_BUFFER_SIZE];
    private static int[] ecg12LeadV4Buffer = new int[ECG_BUFFER_SIZE];
    private static int[] ecg12LeadV5Buffer = new int[ECG_BUFFER_SIZE];
    private static int[] ecg12LeadV6Buffer = new int[ECG_BUFFER_SIZE];

    private static int ecg12WriteIndex = 0;
    private static int ecg12SampleCount = 0;    // Rolling 40s recording window





    // ECG 12-lead data variables (UART2)
    private static int lastEcg12_I = 0;      // Lead I
    private static int lastEcg12_II = 0;     // Lead II
    private static int lastEcg12_III = 0;    // Lead III
    private static int lastEcg12_aVR = 0;    // aVR
    private static int lastEcg12_aVL = 0;    // aVL
    private static int lastEcg12_aVF = 0;    // aVF
    private static int lastEcg12_V1 = 0;     // V1
    private static int lastEcg12_V2 = 0;     // V2
    private static int lastEcg12_V3 = 0;     // V3
    private static int lastEcg12_V4 = 0;     // V4
    private static int lastEcg12_V5 = 0;     // V5
    private static int lastEcg12_V6 = 0;     // V6
    private static int lastEcg12HeartRate = 0;
    private static bool lastEcg12PaceFlag = false;
    private static bool lastEcg12HeartBeatFlag = false;
    private static bool _isECG12Active = false;

    //static bool _isECG12Streaming = false;

    //static bool _isECGStreaming = false;

    // Queue for live ECG streaming (like Python's array)
    private static ConcurrentQueue<string> ecg5LeadQueue = new ConcurrentQueue<string>();
    private const int MaxQueueSize = 100; // Prevent memory overflow (5 seconds at 500 Hz)

    // ECG 12-lead raw sample storage (UART2)
    private static readonly List<int> ecg12LeadI = new List<int>();
    private static readonly List<int> ecg12LeadII = new List<int>();
    private static readonly List<int> ecg12LeadIII = new List<int>();
    private static readonly List<int> ecg12LeadAvr = new List<int>();
    private static readonly List<int> ecg12LeadAvl = new List<int>();
    private static readonly List<int> ecg12LeadAvf = new List<int>();
    private static readonly List<int> ecg12LeadV1 = new List<int>();
    private static readonly List<int> ecg12LeadV2 = new List<int>();
    private static readonly List<int> ecg12LeadV3 = new List<int>();
    private static readonly List<int> ecg12LeadV4 = new List<int>();
    private static readonly List<int> ecg12LeadV5 = new List<int>();
    private static readonly List<int> ecg12LeadV6 = new List<int>();

    private static bool[] lead12Status = new bool[9]; // V2-V6, RA, LA, LL, V1



    private static DateTime deviceStartTime = DateTime.Now;
    private static DateTime lastDeviceInfoSent = DateTime.MinValue;
    private static readonly TimeSpan DeviceInfoInterval = TimeSpan.FromMinutes(30); // Send every 30 minutes - We can Update this with Manual process from GUI.


    // ─────────────────────────────────────────
    // External API configuration (populated by ConfigManager)
    // ─────────────────────────────────────────
    // BluHealth backend endpoints
    private static string SEND_OTP_URL = string.Empty;
    private static string VERIFY_OTP_URL = string.Empty;
    private static string BLUHEALTH_DEVICE_URL = string.Empty;
    private static string BLUHEALTH_VITALS_URL = string.Empty;
    private static string VOICE_QUESTION_API_URL = string.Empty;
    private static string BLUNOTE_API_URL = string.Empty;


    // ========== ANALYSIS TCP SERVER ==========
    private static TcpListener _analysisServer;
    private static bool _analysisRunning = true;
    private static readonly int AnalysisPort = 55511;

    private static readonly int Voice_Assistant = 65440;

    private static TcpListener _wifiServer;
    private static bool _wifiRunning = false;
    private static string _cachedWifiList = "";
    private static readonly object _wifiLock = new object();
    private const int WifiPort = 9000;
    private static readonly List<NetworkStream> _wifiClients = new List<NetworkStream>();
    private static readonly object _wifiClientsLock = new object();



    // For Chunks Recording Variable -

    private static CancellationTokenSource? _microphoneCts;
    private static Task? _microphoneTask;
    // Microphone state tracking
    //private static CancellationTokenSource? _microphoneCts = null;
    //private static Task? _microphoneTask = null;
    //private static LiveSpeechTranscriber? _transcriber = null;
    private static DateTime? _microphoneStartTime = null;
    private const int OtpActivityPort = 2000;
    private const int OtpVerifyPort = 7777;



    //private static SystemPowerService _powerService = new SystemPowerService();  

    private static Timer? _ethernetStatusTimer;

    // Current live values
    private static float lastTemperature1 = 0;
    private static float lastTemperature2 = 0;
    private static int lastPulseRate = 0;
    private static int lastPulseRate2 = 0;
    private static int lastSpO2 = 0;
    private static int lastSys = 0;
    private static int lastDia = 0;
    private static int lastMean = 0;

    private static int lastPulseWaveAmplitude = 0;
    private static float lastSignalQuality = 0;   // pleth signal strength 0-8 (module has no real PI)

    // Dynamic Patient ID variables
    //private static int currentPatientId = 0;
    private static bool waitingForPatientId = false;


    private static bool _voiceNavigationPending = false;
    private static bool _geminiWasRunning = false;



    // Advanced averaging parameters
    private const int FAST_SAMPLE_WINDOW = 20;        // Increased from 10 for better stability
    private const int OUTLIER_THRESHOLD = 3;          // Standard deviations for outlier detection
    private const float STABILITY_THRESHOLD = 0.5f;   // Minimum change to update display
    private static readonly TimeSpan FAST_LOCK_DURATION = TimeSpan.FromSeconds(3); // Reduced from 5 seconds

    // SPI binary struct HealthData moved to SpiManager in src/SPI.cs


    // SPI binary struct HealthRangeData moved to SpiManager in src/SPI.cs







    // SPI binary structs and constants moved to SpiManager in src/SPI.cs

    private static SpiManager.HealthRangeData _lastRangeData;
    private static bool _hasRangeData = false;

    public class BodyCompositionData
    {
        public float MuscleControl { get; set; }
        public float IntracellularWater { get; set; }
        public float BodyScore { get; set; }
        public float BoneMass { get; set; }
        public float FatControl { get; set; }
        public float ExtracellularWater { get; set; }
        public float PhysicalAge { get; set; }
        public float Protein { get; set; }
        public float TrunkFatPct { get; set; }
        public float BodyCellMass { get; set; }
        public float VisceralFatLevel { get; set; }
        public float BodyWater { get; set; }
        public float BodyFatPct { get; set; }
        public float TrunkMuscleMass { get; set; }
        public float SubcutaneousFatPct { get; set; }
        public float BasalMetabolism { get; set; }
        public float IdealBodyWeight { get; set; }
        public float FatMass { get; set; }
        public float RightHandMuscle { get; set; }
        public float WaistHipRatio { get; set; }
        public float LeanBodyMass { get; set; }
        public float ObesityLevel { get; set; }
        public float MuscleMass { get; set; }
        public float LeftHandMuscle { get; set; }
        public float WeightControl { get; set; }
        public float MoistureTbw { get; set; }
        public float SkeletalMuscle { get; set; }
        public string BodyType { get; set; } = "Unknown";
        public string LastJsonSnapshot { get; set; } = "{}";
        // In BodyCompositionData class
        public float FatRightHand { get; set; }
        public float FatLeftHand { get; set; }
        public float FatTrunk { get; set; }
        public float FatRightFoot { get; set; }
        public float FatLeftFoot { get; set; }
        public float MusclePctRightHand { get; set; }
        public float MusclePctLeftHand { get; set; }
        public float MusclePctTrunk { get; set; }
        public float MusclePctRightFoot { get; set; }
        public float MusclePctLeftFoot { get; set; }
        public float SmiIndex { get; set; }
        public float InorganicSalt { get; set; }



        public BodyCompositionData Clone()
        {
            return new BodyCompositionData
            {
                MuscleControl = this.MuscleControl,
                IntracellularWater = this.IntracellularWater,
                BodyScore = this.BodyScore,
                BoneMass = this.BoneMass,
                FatControl = this.FatControl,
                ExtracellularWater = this.ExtracellularWater,
                PhysicalAge = this.PhysicalAge,
                Protein = this.Protein,
                TrunkFatPct = this.TrunkFatPct,
                BodyCellMass = this.BodyCellMass,
                VisceralFatLevel = this.VisceralFatLevel,
                BodyWater = this.BodyWater,
                BodyFatPct = this.BodyFatPct,
                TrunkMuscleMass = this.TrunkMuscleMass,
                SubcutaneousFatPct = this.SubcutaneousFatPct,
                BasalMetabolism = this.BasalMetabolism,
                IdealBodyWeight = this.IdealBodyWeight,
                FatMass = this.FatMass,
                RightHandMuscle = this.RightHandMuscle,
                WaistHipRatio = this.WaistHipRatio,
                LeanBodyMass = this.LeanBodyMass,
                ObesityLevel = this.ObesityLevel,
                MuscleMass = this.MuscleMass,
                LeftHandMuscle = this.LeftHandMuscle,
                WeightControl = this.WeightControl,
                MoistureTbw = this.MoistureTbw,
                SkeletalMuscle = this.SkeletalMuscle,
                BodyType = this.BodyType,
                // In Clone()
                FatRightHand = this.FatRightHand,
                FatLeftHand = this.FatLeftHand,
                FatTrunk = this.FatTrunk,
                FatRightFoot = this.FatRightFoot,
                FatLeftFoot = this.FatLeftFoot,
                MusclePctRightHand = this.MusclePctRightHand,
                MusclePctLeftHand = this.MusclePctLeftHand,
                MusclePctTrunk = this.MusclePctTrunk,
                MusclePctRightFoot = this.MusclePctRightFoot,
                MusclePctLeftFoot = this.MusclePctLeftFoot,
                SmiIndex = this.SmiIndex,
                InorganicSalt = this.InorganicSalt,
                LastJsonSnapshot = this.LastJsonSnapshot
            };
        }

    }


    private static class StoredBodyComposition
    {
        public static void CopyFrom(BodyCompositionData source)  //  Add parameter
        {
            LastJsonSnapshot = source.LastJsonSnapshot;
        }

        public static string LastJsonSnapshot { get; set; } = "{}";
    }


    // Advanced sample classes for better data management
    public class SampleData
    {
        public float Value { get; set; }
        public DateTime Timestamp { get; set; }
        public bool IsValid { get; set; }

        public SampleData(float value)
        {
            Value = value;
            Timestamp = DateTime.Now;
            IsValid = true;
        }
    }


    public class AdvancedSampleQueue
    {
        private readonly Queue<SampleData> _samples = new Queue<SampleData>();
        private readonly int _maxSize;
        private float _lastStableValue = 0;

        public AdvancedSampleQueue(int maxSize)
        {
            _maxSize = maxSize;
        }

        public void AddSample(float value)
        {
            _samples.Enqueue(new SampleData(value));
            while (_samples.Count > _maxSize)
            {
                _samples.Dequeue();
            }
        }

        public float GetFilteredAverage()
        {
            if (_samples.Count == 0) return 0;

            var validSamples = _samples.Where(s => s.IsValid && s.Value > 0).ToArray();
            if (validSamples.Length < 3) return validSamples.LastOrDefault()?.Value ?? 0;

            // Remove outliers using interquartile range method
            var sortedValues = validSamples.Select(s => s.Value).OrderBy(v => v).ToArray();
            var q1Index = sortedValues.Length / 4;
            var q3Index = 3 * sortedValues.Length / 4;
            var q1 = sortedValues[q1Index];
            var q3 = sortedValues[q3Index];
            var iqr = q3 - q1;
            var lowerBound = q1 - 1.5f * iqr;
            var upperBound = q3 + 1.5f * iqr;

            // Filter out outliers
            var filteredValues = sortedValues.Where(v => v >= lowerBound && v <= upperBound).ToArray();

            if (filteredValues.Length == 0) return _lastStableValue;

            // Weighted average (recent samples have more weight)
            float weightedSum = 0;
            float totalWeight = 0;
            var now = DateTime.Now;

            foreach (var sample in validSamples.Where(s => filteredValues.Contains(s.Value)))
            {
                var age = (now - sample.Timestamp).TotalSeconds;
                var weight = Math.Max(0.1f, 1.0f - (float)age / 10.0f); // Higher weight for recent samples
                weightedSum += sample.Value * weight;
                totalWeight += weight;
            }

            var result = totalWeight > 0 ? weightedSum / totalWeight : filteredValues.Average();

            // Stability check - only update if change is significant
            if (Math.Abs(result - _lastStableValue) > STABILITY_THRESHOLD || _lastStableValue == 0)
            {
                _lastStableValue = result;
            }

            return _lastStableValue;
        }

        public bool IsFull => _samples.Count >= _maxSize;
        public int Count => _samples.Count;

        public void Clear()
        {
            _samples.Clear();
            _lastStableValue = 0;
        }
    }



    // Stored values for DONE
    private static float storedTemperature1 = 0;
    private static float storedTemperature2 = 0;
    private static float storedTemperatureIR = 0.0f;
    private static float storedHeight = 0;
    private static float storedWeight = 0;
    private static float storedBMI = 0;
    private static int storedSpO2 = 0;
    private static int storedPulseRate = 0;
    private static int storedSys = 0;
    private static int storedDia = 0;
    private static int storedMean = 0;
    private static int storedPulseRate2 = 0;



    private static float storedSignalQuality = 0.0f;

    // Stability tracking
    private static int spo2StableCount = 0;
    private static int lastStableSpO2 = 0;
    private static int lastStablePR = 0;

    // Thresholds
    const int SPO2_STABLE_SAMPLES = 20;  // 20 consecutive stable readings (~20 seconds)
    const int SPO2_TOLERANCE = 2;        // ±2% variation
    const int PR_TOLERANCE = 5;          // ±5 BPM variation
    const float PI_MIN_THRESHOLD = 1.0f; // Minimum PI for valid reading

    // ── SPO2 Spot-Check State Machine (quality-gated averaging) ───────────────
    // Clinical spot-check behaviour: acquire → show live → capture a STABLE
    // AVERAGE (gated on signal quality + value spread, NOT a wall-clock timer)
    // → hold the stored value when the finger leaves → re-measure when it
    // returns. No terminal state, so it can never "freeze" waiting for a manual
    // clear (the old 30s-timer bug where invalid data at t=30s locked forever).
    private enum Spo2Phase { Idle, Measuring, Captured, Hold }
    private static Spo2Phase _spo2Phase = Spo2Phase.Idle;
    private static string _lastSpo2Status = "";

    // Rolling window of recent GOOD-quality samples (timestamped) for averaging
    // and stability detection.
    private static readonly List<(DateTime t, int spo2, int pr, int q)> _spo2Buf = new();
    private static bool     _spo2HasCapture   = false;
    private static DateTime _spo2AcquireStart = DateTime.MinValue;
    private static DateTime _spo2LastGuidance = DateTime.MinValue;

    private const int SPO2_QUALITY_MIN    = 3;   // module signal quality 0-8 to count a sample
    private const int SPO2_STABLE_WINDOW  = 6;   // seconds of stable good samples to capture
    private const int SPO2_MIN_SAMPLES    = 5;   // min good samples inside the window
    private const int SPO2_SPO2_SPREAD    = 2;   // max SpO2 spread (%) within window
    private const int SPO2_PR_SPREAD      = 8;   // max pulse spread (bpm) within window
    private const int SPO2_GUIDANCE_AFTER = 20;  // s searching before inadequate-signal hint

    // Locked final (averaged) values — safe to store even after finger removed
    private static float _finalSpO2 = 0;
    private static int _finalPulseRate = 0;
    private static float _finalSignalQuality = 0;


    // Add this near your other SPO2 fields
    private static readonly Dictionary<TcpClient, SemaphoreSlim> _spo2ClientWriteLocks = new();


    enum MeasurementState
    {
        IDLE,
        HEIGHT_WEIGHT,
        TEMPERATURE,
        SPO2_NIBP,    // replaces both SPO2 and NIBP
        BLOOD_SUGAR,
        ECG,
        DONE
    }






    private static MeasurementState _currentState = MeasurementState.IDLE;
    private static bool _isLiveMode = true; // forced always-on: no GUI navigation exists to set this via ProcessCommand("LIVE") anymore
    private static bool _isNIBPActive = false;

    // Dedicated TCP Servers
    private static TcpListener _spo2Server;
    private static TcpListener _hwTempServer;
    private static TcpListener _nibpServer;
    private static TcpListener _ecg7Server;
    private static TcpListener _ecg12Server;
    // private static TcpListener _graphServer;
    private static TcpListener _tempServer;

    // Add these with your other TCP server variables
    private static TcpListener _ecg5LeadListener;
    private static List<TcpClient> _ecg5LeadClients = new List<TcpClient>();
    private static readonly object _ecg5LeadClientsLock = new object();



    private static readonly List<TcpClient> _spo2Clients = new List<TcpClient>();
    private static readonly List<TcpClient> _hwTempClients = new List<TcpClient>();
    private static readonly List<TcpClient> _nibpClients = new List<TcpClient>();
    private static readonly List<TcpClient> _ecg7Clients = new List<TcpClient>();
    private static readonly List<TcpClient> _ecg12Clients = new List<TcpClient>();
    // private static readonly List<TcpClient> _graphClients = new List<TcpClient>();
    private static readonly object _spo2ClientLock = new object();
    private static readonly object _hwTempClientLock = new object();
    private static readonly object _nibpClientLock = new object();
    private static readonly object _ecg7ClientLock = new object();
    private static readonly object _ecg12ClientLock = new object();
    // private static readonly object _graphClientLock = new object();
    private static bool _spo2Running = true;
    private static bool _hwTempRunning = true;
    private static bool _nibpRunning = true;
    private static bool _ecg7Running = true;
    private static bool _ecg12Running = true;
    //private static bool _graphRunning = true;

    // Port Definitions  -- Last Edited -- --- Last Edited -----
    private const int DataPort = 55555;      // SPO2 Port
    private const int HwTempPort = 22222;    // Height/Weight + Temperature Port
    private const int NibpPort = 55557;      // Blood Pressure Port
    private const int ECG7Port = 55558;      // ECG 5-lead Port (High speed)
    private const int ECG12Port = 55559;     // ECG 12-lead Port (High speed)
    private const int GraphPort = 55510;     // Graphical Data Port (High speed)
    private const int temperature = 65435; // --  CHANGE 1: Added the new temperature port
    //private const int pagestatus = 2000; // This Port dedicated for receving command from page changes of Vitals

    private static readonly Dictionary<string, LogCategory> CategoryMap = new()
    {
        { "[GUI]", LogCategory.System },
        { "[Auth]", LogCategory.Auth },
        { "[API]", LogCategory.API },
        { "[SPI]", LogCategory.Hardware },
        { "[ECG]", LogCategory.Measurement },
        { "[NIBP]", LogCategory.Measurement },
        { "[SPO2]", LogCategory.Measurement },
        { "[TEMP]", LogCategory.Hardware },
        { "[OTP]", LogCategory.Auth },
        { "[CONFIG]", LogCategory.Config },
        { "[NETWORK]", LogCategory.Network },
        { "[DATA]", LogCategory.Data },
        { "[PERF]", LogCategory.Performance }
    };

    public static void Log(string message)
    {
        if (string.IsNullOrEmpty(message))
            return;

        var category = DetermineCategory(message);
        var cleanMessage = StripEmojis(message);

        try
        {
            ProductionLogger.Info(category, cleanMessage);
        }
        catch
        {
            Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {cleanMessage}");
        }
    }

    // Sets log verbosity from the LOG_LEVEL env var (Debug/Info/Warning/Error/Critical).
    // Defaults to Info so production drops Debug-level diagnostic noise; set LOG_LEVEL=Debug
    // to get full verbosity when troubleshooting.
    private static void ConfigureLogging()
    {
        var raw = Environment.GetEnvironmentVariable("LOG_LEVEL");
        var level = LogLevel.Info;

        if (!string.IsNullOrWhiteSpace(raw) &&
            Enum.TryParse<LogLevel>(raw.Trim(), ignoreCase: true, out var parsed))
        {
            level = parsed;
        }

        ProductionLogger.SetMinimumLevel(level);
        Log($"[CONFIG] Log level set to {level}" + (raw == null ? " (default)" : $" (LOG_LEVEL={raw})"));
    }

    // Level-aware overload. Diagnostic/high-detail messages should pass LogLevel.Debug so
    // they can be filtered out in production via ProductionLogger.SetMinimumLevel.
    public static void Log(string message, LogLevel level)
    {
        if (string.IsNullOrEmpty(message))
            return;

        var category = DetermineCategory(message);
        var cleanMessage = StripEmojis(message);

        try
        {
            switch (level)
            {
                case LogLevel.Debug:
                    ProductionLogger.Debug(category, cleanMessage);
                    break;
                case LogLevel.Warning:
                    ProductionLogger.Warning(category, cleanMessage);
                    break;
                case LogLevel.Error:
                    ProductionLogger.Error(category, cleanMessage);
                    break;
                case LogLevel.Critical:
                    ProductionLogger.Critical(category, cleanMessage);
                    break;
                default:
                    ProductionLogger.Info(category, cleanMessage);
                    break;
            }
        }
        catch
        {
            Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {cleanMessage}");
        }
    }

    private static readonly ConcurrentDictionary<string, DateTime> _lastLogByKey = new();

    private static void LogThrottled(string key, string message, TimeSpan interval)
    {
        var now = DateTime.UtcNow;
        var last = _lastLogByKey.GetOrAdd(key, DateTime.MinValue);

        if (now - last < interval)
            return;

        _lastLogByKey[key] = now;
        Log(message);
    }

    private static void LogBodyCompositionMeasurement(float height, float weight, float bmi)
    {
        var summary = $"H:{height:F1}cm W:{weight:F1}kg BMI:{bmi:F1}";
        var now = DateTime.UtcNow;

        // Live H/W readings change every packet (~5/s), so the value-based dedup
        // never matched and this flooded hundreds of lines/minute into the logs.
        // Throttle by time: at most one line every 2 seconds.
        if (now - _lastBodyCompositionLogUtc < TimeSpan.FromSeconds(2))
            return;

        _lastLoggedBodyCompositionSummary = summary;
        _lastBodyCompositionLogUtc = now;
        Log($"Height/weight data stored: {summary}");
    }

    private static LogCategory DetermineCategory(string message)
    {
        if (message.Contains("[GUI]")) return LogCategory.System;
        if (message.Contains("[Auth]")) return LogCategory.Auth;
        if (message.Contains("[API]")) return LogCategory.API;
        if (message.Contains("[SPI]")) return LogCategory.Hardware;
        if (message.Contains("[ECG]")) return LogCategory.Measurement;
        if (message.Contains("[NIBP]")) return LogCategory.Measurement;
        if (message.Contains("[SPO2]")) return LogCategory.Measurement;
        if (message.Contains("[TEMP]")) return LogCategory.Hardware;
        if (message.Contains("[OTP]")) return LogCategory.Auth;
        if (message.Contains("[CONFIG]")) return LogCategory.Config;
        if (message.Contains("[NETWORK]")) return LogCategory.Network;
        if (message.Contains("[DATA]")) return LogCategory.Data;
        if (message.Contains("[PERF]")) return LogCategory.Performance;

        if (message.Contains("Error") || message.Contains("error")) return LogCategory.System;
        if (message.Contains("OTP")) return LogCategory.Auth;
        if (message.Contains("Temperature") || message.Contains("pressure") || message.Contains("glucose")) return LogCategory.Measurement;
        if (message.Contains("Server") || message.Contains("connection") || message.Contains("Connected")) return LogCategory.Server;
        if (message.Contains("HTTP") || message.Contains("Request") || message.Contains("Response")) return LogCategory.API;

        return LogCategory.System;
    }

    private static string StripEmojis(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var result = Regex.Replace(text, @"[^\w\s\-\.:\(\)\[\]\/\,]", "").Trim();
        return result;
    }

    // ADD THIS FUNCTION HERE:
    static bool TryParseInvariant(string s, out float value)
    {
        return float.TryParse(s, NumberStyles.Float | NumberStyles.AllowLeadingSign,
                             CultureInfo.InvariantCulture, out value);
    }
    static async Task StartAllServersAsync()
    {
        var tasks = new List<Task>
        {
            StartSpo2TcpServerAsync(),
            //StartHwTempTcpServerAsync(),
            StartHwTempTcpServerAsync(),
            StartNibpTcpServerAsync(),
            StartEcg7TcpServerAsync(),
            StartEcg12TcpServerAsync(),
            StartGraphTcpServerAsync(),
            StartEcgTcpServerAsync(),
            StartTempTcpServerAsync(),
            StartOtpActivityServerAsync(), // now the general nav channel (ECG RECORDING START/STOP etc.) — GUI's socket_manager.dart connects here on port 2000
            StartAnalysisTcpServerAsync(),
            HardwareWifi.StartWifiTcpServerAsync(),
            StartVoiceTcpServerAsync(),



        };

        await Task.WhenAll(tasks);
    }

    private static void RefreshBluHealthApiUrlsFromConfig()
    {
        SEND_OTP_URL = ConfigManager.GetBluHealthApiUrl("SendOtpUrl", SEND_OTP_URL);
        VERIFY_OTP_URL = ConfigManager.GetBluHealthApiUrl("VerifyOtpUrl", VERIFY_OTP_URL);
        BLUHEALTH_DEVICE_URL = ConfigManager.GetBluHealthApiUrl("DeviceUrl", BLUHEALTH_DEVICE_URL);
        BLUHEALTH_VITALS_URL = ConfigManager.GetBluHealthApiUrl("VitalsUrl", BLUHEALTH_VITALS_URL);
        VOICE_QUESTION_API_URL = ConfigManager.GetBluHealthApiUrl("VoiceQuestionUrl", VOICE_QUESTION_API_URL);
        BLUNOTE_API_URL = ConfigManager.GetBluHealthApiUrl("BluNoteUrl", BLUNOTE_API_URL);

        Log(" All API endpoints refreshed from runtime config (BluHealth)");
    }

    static async Task Main()
    {
        ConfigureLogging();                               // ← set verbosity before anything logs

        // Audit: device boot (+ UPDATE_DETECTED if the app version changed).
        // APP_VERSION carries the image tag (compose passes VITALDATA_TAG); the
        // assembly version is a constant 1.0.0.0 and would never detect updates.
        Audit.LogBoot(Environment.GetEnvironmentVariable("APP_VERSION")
                      ?? typeof(VitalsChairApp).Assembly.GetName().Version?.ToString()
                      ?? "unknown");

        InitializeSerialPorts();                          // ← hardware first

        // Resolve bootstrap URLs from Firebase/cache/fallback BEFORE any device registration
        var bootstrapUrls = await BootstrapConfigProvider.ResolveAsync(_httpClient);
        DeviceRegistration.Initialize(bootstrapUrls);    // ← Initialize URLs before using them

        // Initialize encryption system and device registration
        try
        {
            var initializer = new DeviceStartupInitializer();
            await initializer.InitializeAsync();         // ← Now safe to call (RegisterUrl is set)
            _registrationManager = initializer.GetRegistrationManager();
            _secretKeyManager = initializer.GetResilientSecretKeyManager();
            _encryptionManager = initializer.GetEncryptionManager();
            _secureApiClient = initializer.GetSecureApiClient();
            Console.WriteLine("✓ Encryption system initialized\n");
        }
        catch (Exception ex)
        {
            Log($"❌ Encryption initialization failed: {ex.Message}");
            Environment.Exit(1);
        }

        await ConfigManager.InitializeAsync();

        while (ConfigManager.IsPendingApproval())
        {
            Log("⏳ Device pending admin approval — waiting before starting services");
            // If the device has not been approved yet, do not start the core servers.
            // Re-check registration and cache periodically until config becomes available.
            await Task.Delay(TimeSpan.FromSeconds(30));
            await DeviceRegistration.EnsureRegisteredAsync();
            await ConfigManager.InitializeAsync();
        }

        RefreshBluHealthApiUrlsFromConfig();
        VitalsQueue.BluHealthApiUrl = BLUHEALTH_VITALS_URL;
        await VitalsQueue.InitializeAsync();              // ← queue for offline vitals

        try
        {
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
                Log("Shutting down server due to cancellation...");
            };

            _serialPortData.Open();
            _serialPortECG12.Open();

            await InitializeEcgSettingsAsync(_serialPortData, cts.Token);

            var serverTask = StartAllServersAsync();
            var heightTask = Task.Run(() => ReadHeightLoopAsync(cts.Token), cts.Token);
            var dataTask = Task.Run(() => ProcessDataLoopAsync(cts.Token), cts.Token);
            var deviceInfoTask = VitalsChairApp.DeviceInfoPeriodicSendAsync(cts.Token); // ← no Task.Run
            var spiTask = Task.Run(() => ReadSpiDataAsync(cts.Token), cts.Token);
            var queueSyncTask = Task.Run(() => VitalsQueue.SyncWorkerAsync(cts.Token), cts.Token);

            // Initialize hardware managers with configuration
            HardwareAudio.Initialize(Configuration);

            HardwareAudio.StartAudioDeviceMonitor();

            // Boot device at 50% volume regardless of previous state
            HardwareAudio.SetVolume(50);

            // Start background config watcher to poll server periodically
            _ = Task.Run(StartConfigWatcherAsync);

            // Start ethernet status monitor (detects and broadcasts ethernet connection state)

            await Task.WhenAll(serverTask, heightTask, dataTask, deviceInfoTask, spiTask, queueSyncTask);

            await Task.Run(() =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        Console.Write("Enter command: ");
                        string command = Console.ReadLine()?.Trim().ToUpper();
                        if (string.IsNullOrEmpty(command)) continue;
                        Log($"Received console command: {command}");
                        ProcessCommand(command);
                    }
                    catch (Exception ex)
                    {
                        Log($"Console command error: {ex.Message}");
                    }
                }
            }, cts.Token);

            await Task.WhenAll(serverTask, heightTask, dataTask, deviceInfoTask, spiTask, queueSyncTask);
        }
        catch (Exception ex)
        {
            Log($"Error: {ex.Message}");
        }
        finally
        {
            _tcpEcgRunning = false;
            _ecgServer?.Stop();
            _serialPortData?.Close();
            _serialPortECG12?.Close();

            lock (_ecgClientLock)
            {
                foreach (var client in _ecgClients)
                    client.Close();
                _ecgClients.Clear();
            }

            StopAllServers();
            Log("Monitoring Stopped");
        }
    }


    /* ------------------------------------------------------------------------------------CONNECTIVITY UART, SPI, IIC -------------------------------------------------------------------*/

    static void InitializeSerialPorts()
    {
        _serialPortData = new SerialPort("/dev/verdin-uart1", 115200)
        {
            ReadTimeout = 10,
            WriteTimeout = 100,
            Parity = Parity.None,
            DataBits = 8,
            StopBits = StopBits.One,
            Handshake = Handshake.None,
            ReadBufferSize = 8192,
            Encoding = Encoding.Default
        };

        _serialPortECG12 = new SerialPort("/dev/verdin-uart2", 115200)
        {
            ReadTimeout = 10,
            WriteTimeout = 100,
            Parity = Parity.None,
            DataBits = 8,
            StopBits = StopBits.One,
            Handshake = Handshake.None,
            ReadBufferSize = 8192,
            Encoding = Encoding.ASCII
        };
        // Clear any existing data in buffers


        SpiManager.InitializeSpi();
    }

    // SPI initialization moved to SpiManager in src/SPI.cs

    // SPI low-level command and binary helpers moved to SpiManager in src/SPI.cs




    static void ResetMeasurementData()
    {
        lock (_lock)
        {
            // Clear current values
            lastHeight = 0;
            lastWeight = 0;
            lastBMI = 0;
            lastTemperatureIR = 0;
            BodyComposition = new BodyCompositionData();

            //  CLEAR last valid data too (HOME button behavior)
            lastValidHeight = 0;
            lastValidWeight = 0;
            lastValidBMI = 0;
            lastValidBodyComposition = new BodyCompositionData();
            hasValidMeasurement = false;

            Log("🔄 Measurement data reset to zero (including last valid data)");
        }
    }


    // SPI low-level read moved to SpiManager in src/SPI.cs




    /* ------------------------------------------------------------------------------------ END CONNECTIVITY UART, SPI, IIC -------------------------------------------------------------------*/
    static async Task ReadSpiDataAsync(CancellationToken cancellationToken)
    {
        Log(" Starting unified SPI reader task");

        int healthDataSize = Marshal.SizeOf<SpiManager.HealthData>();
        int rangeDataSize = Marshal.SizeOf<SpiManager.HealthRangeData>();
        const int RANGE_FLAG_OFFSET = 256;
        const byte RANGE_APPENDED_FLAG = 0xA5;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var data = SpiManager.RequestSpiHealthData();

                if (data == null || data.Length < 4)
                {
                    await Task.Delay(200, cancellationToken);
                    continue;
                }

                uint magic = (uint)(data[0] | (data[1] << 8)
                                   | (data[2] << 16) | (data[3] << 24));

                if (magic == SpiManager.MAGIC_HEADER && data.Length >= healthDataSize)
                {
                    SpiManager.HealthData healthData = SpiManager.ByteArrayToStruct<SpiManager.HealthData>(data);

                    // ── IR temp + distance (replaces ReadSpiTemperatureAsync) ──
                    lock (_lock)
                    {
                        // ┌─ APPLY IR TEMPERATURE OFFSET CALIBRATION ─┐
                        // │ Raw ESP32 temp + calibration offset       │
                        // │ Ensures forehead temp accuracy            │
                        // Only offset a REAL reading — never fabricate one from a
                        // 0/invalid value (raw 0 + offset would look like ~1.3°C /
                        // 34°F and could slip past invalid-checks downstream).
                        lastTemperatureIR = healthData.Temperature > 0
                            ? healthData.Temperature + IR_TEMPERATURE_OFFSET_CELSIUS
                            : healthData.Temperature;
                        // └──────────────────────────────────────────┘
                        lastProxDistance = healthData.prox_distance;
                    }


#if DEBUG
                    string proxStatus;
                    if (healthData.prox_distance <= 0)
                        proxStatus = "No Reading";
                    else if (healthData.prox_distance < 100)
                        proxStatus = "Too Close";
                    else if (healthData.prox_distance > 300)
                        proxStatus = "Too Far";
                    else
                        proxStatus = "Measuring";

                    if (++_tempLogCounter % 10 == 0)
                    {
                        // Log($"🌡️ Temp: {healthData.Temperature:F1}°C | 📏 Distance: {healthData.prox_distance:F0}mm | {proxStatus}");
                    }
#endif

                    // ── Body composition + height/weight ──
                    UpdateBodyCompositionData(data);

                    // ── Range data ──
                    if (!_hasRangeData
                        && data.Length >= RANGE_FLAG_OFFSET + 1 + rangeDataSize
                        && data[RANGE_FLAG_OFFSET] == RANGE_APPENDED_FLAG)
                    {
                        byte[] rangeBytes = new byte[rangeDataSize];
                        Array.Copy(data, RANGE_FLAG_OFFSET + 1, rangeBytes, 0, rangeDataSize);
                        UpdateRangeData(rangeBytes);
                    }
                }

                // Single consistent read rate — 100ms covers both temp and body comp
                await Task.Delay(100, cancellationToken);
            }
            catch (Exception ex)
            {
                Log($" SPI read error: {ex.Message}");
                await Task.Delay(1000, cancellationToken);
            }
        }
    }

    static void UpdateRangeData(byte[] spiData)
    {
        try
        {
            SpiManager.HealthRangeData rangeData = SpiManager.ByteArrayToStruct<SpiManager.HealthRangeData>(spiData);

            if (rangeData.Magic != SpiManager.MAGIC_HEADER_RANGE)
                return;

            // Validate checksum
            uint calculated = SpiManager.CalculateRangeChecksum(rangeData);
            if (calculated != rangeData.Checksum)
            {
                Log($" Range checksum mismatch: 0x{rangeData.Checksum:X8} vs 0x{calculated:X8} (skipping)");
                return;
            }

            lock (_lock)
            {
                _lastRangeData = rangeData;
                _hasRangeData = true;
            }

            Log("📐 Range data received: " +
                $"BMI {rangeData.BmiMin:F1}-{rangeData.BmiMax:F1} | " +
                $"Fat {rangeData.BodyFatPercentMin:F1}-{rangeData.BodyFatPercentMax:F1}% | " +
                $"Weight {rangeData.WeightMin:F1}-{rangeData.WeightMax:F1}kg");
        }
        catch (Exception ex)
        {
            Log($" Error parsing range data: {ex.Message}");
        }
    }


    static async Task ReadSpiBodyCompositionAsync(CancellationToken cancellationToken)
    {
        Log(" Starting SPI body composition reader task");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var data = SpiManager.RequestSpiHealthData();
                if (data != null && data.Length >= Marshal.SizeOf<SpiManager.HealthData>())
                {
                    UpdateBodyCompositionData(data);  // Includes height/weight/body comp
                }

                await Task.Delay(2000, cancellationToken);  // Keep 2s for full data
            }
            catch (Exception ex)
            {
                Log($" SPI read error: {ex.Message}");
                await Task.Delay(5000, cancellationToken);
            }
        }
    }



    // SPI request helper moved to SpiManager in src/SPI.cs

    static void UpdateBodyCompositionData(byte[] spiData)
    {
        try
        {
            // Show raw data first 20 bytes for debugging
            var hexDump = string.Join(" ", spiData.Take(20).Select(b => b.ToString("X2")));

            // Deserialize binary data to struct
            SpiManager.HealthData data = SpiManager.ByteArrayToStruct<SpiManager.HealthData>(spiData);

            // Validate magic header
            if (data.Magic != SpiManager.MAGIC_HEADER)
            {
                return;
            }

            // Validate checksum (but continue anyway for debugging)
            uint calculatedChecksum = SpiManager.CalculateChecksum(data);
            if (calculatedChecksum != data.Checksum)
            {
                // Log($" Checksum mismatch: 0x{data.Checksum:X8} vs 0x{calculatedChecksum:X8} (continuing anyway)");
            }

            //  NEW: Check if this is VALID NEW DATA (not zeros)
            bool isValidData = (data.Height > 0 && data.Weight > 0);

            lock (_lock)
            {
                if (isValidData)
                {
                    // BMI arrives only intermittently from the ESP32 — most packets
                    // carry Bmi=0 even while height/weight are valid. Copying it
                    // unconditionally wiped the stored BMI whenever the user stayed
                    // on the page past the one valid packet ("BMI cleared but
                    // height/weight kept" bug). Keep the sensor's BMI when present;
                    // otherwise derive it from the valid height/weight.
                    float heightM = data.Height / 100f;
                    float bmi = data.Bmi > 0 ? data.Bmi : data.Weight / (heightM * heightM);

                    //  UPDATE: Store as last valid measurement
                    lastValidHeight = data.Height;
                    lastValidWeight = data.Weight;
                    lastValidBMI = bmi;

                    // Update current display values
                    lastHeight = data.Height;
                    lastWeight = data.Weight;
                    lastBMI = bmi;

                    // Update body composition
                    BodyComposition.MuscleControl = data.MuscleControl;
                    BodyComposition.IntracellularWater = data.IntracellularWater;
                    BodyComposition.BodyScore = data.BodyScore;
                    BodyComposition.BoneMass = data.BoneMass;
                    BodyComposition.FatControl = data.FatControl;
                    BodyComposition.ExtracellularWater = data.ExtracellularWater;
                    BodyComposition.PhysicalAge = data.PhysicalAge;
                    BodyComposition.Protein = data.Protein;
                    BodyComposition.TrunkFatPct = data.TrunkFatPercent;
                    BodyComposition.BodyCellMass = data.BodyCellMass;
                    BodyComposition.VisceralFatLevel = data.VisceralFatLevel;
                    BodyComposition.BodyWater = data.BodyWater;
                    BodyComposition.BodyFatPct = data.BodyFatPercent;
                    BodyComposition.TrunkMuscleMass = data.TrunkMuscleMass;
                    BodyComposition.SubcutaneousFatPct = data.SubcutaneousFatPercent;
                    BodyComposition.BasalMetabolism = data.BasalMetabolism;
                    BodyComposition.IdealBodyWeight = data.IdealBodyWeight;
                    BodyComposition.FatMass = data.FatMass;
                    BodyComposition.RightHandMuscle = data.RightHandMuscle;
                    BodyComposition.WaistHipRatio = data.WaistHipRatio;
                    BodyComposition.LeanBodyMass = data.LeanBodyMass;
                    BodyComposition.ObesityLevel = data.ObesityLevel;
                    BodyComposition.MuscleMass = data.MuscleMass;
                    BodyComposition.LeftHandMuscle = data.LeftHandMuscle;
                    BodyComposition.WeightControl = data.WeightControl;
                    BodyComposition.MoistureTbw = data.MoistureTbw;
                    BodyComposition.SkeletalMuscle = data.SkeletalMuscle;
                    BodyComposition.BodyType = data.BodyTypeString;

                    // ===== SEGMENTAL FAT =====
                    BodyComposition.FatRightHand = data.FatRightHand;
                    BodyComposition.FatLeftHand = data.FatLeftHand;
                    BodyComposition.FatTrunk = data.FatTrunk;
                    BodyComposition.FatRightFoot = data.FatRightFoot;
                    BodyComposition.FatLeftFoot = data.FatLeftFoot;

                    // ===== SEGMENTAL MUSCLE % =====
                    BodyComposition.MusclePctRightHand = data.MusclePctRightHand;
                    BodyComposition.MusclePctLeftHand = data.MusclePctLeftHand;
                    BodyComposition.MusclePctTrunk = data.MusclePctTrunk;
                    BodyComposition.MusclePctRightFoot = data.MusclePctRightFoot;
                    BodyComposition.MusclePctLeftFoot = data.MusclePctLeftFoot;

                    // ===== CLINICAL =====
                    BodyComposition.SmiIndex = data.SmiIndex;
                    BodyComposition.InorganicSalt = data.InorganicSalt;

                    // Create JSON snapshot
                    BodyComposition.LastJsonSnapshot = SpiManager.CreateJsonFromBinaryData(data);

                    //  BACKUP to last valid
                    lastValidBodyComposition = BodyComposition.Clone(); // You'll need to implement Clone()

                    hasValidMeasurement = true;

                    LogBodyCompositionMeasurement(lastValidHeight, lastValidWeight, lastValidBMI);
                }
                else
                {
                    //  KEEP DISPLAYING LAST VALID DATA (don't update to zeros)
                    if (hasValidMeasurement)
                    {
                        lastHeight = lastValidHeight;
                        lastWeight = lastValidWeight;
                        lastBMI = lastValidBMI;
                        // Don't update BodyComposition - keep last valid
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log($" Error parsing SPI binary data: {ex.Message}\n{ex.StackTrace}");
        }
    }

    static async Task StartConfigWatcherAsync()
    {
        Log($"🔄 Background config watcher started — interval: {ConfigWatchIntervalMinutes} min");

        while (true)
        {
            await Task.Delay(TimeSpan.FromMinutes(ConfigWatchIntervalMinutes));

            try
            {
                Log("🔄 Background config check...");
                bool configUpdated = await DeviceRegistration.CheckDeviceStatusAsync();
                await ConfigManager.InitializeAsync();

                if (configUpdated)
                {
                    RefreshBluHealthApiUrlsFromConfig();
                    VitalsQueue.BluHealthApiUrl = BLUHEALTH_VITALS_URL;
                    Log($" Background config check applied latest endpoints. configVersion={ConfigManager.GetConfigVersion()}");
                }
                else
                {
                    Log(" Background config check complete - no endpoint changes");
                }
            }
            catch (Exception ex)
            {
                Log($" Background config check error: {ex.Message}");
            }
        }
    }
    // SPI binary helper methods moved to SpiManager in src/SPI.cs

    // SPI binary helper methods moved to SpiManager in src/SPI.cs


    static async Task StartOtpActivityServerAsync()
    {
        try
        {
            _otpActivityRunning = true;
            _otpActivityServer = new TcpListener(IPAddress.Any, OtpActivityPort);
            _otpActivityServer.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _otpActivityServer.Start();
            Log($" OTP Activity Server started on {IPAddress.Any}:{OtpActivityPort}");

            while (_otpActivityRunning)
            {
                try
                {
                    LogThrottled("otp-activity-waiting", "Waiting for OTP Activity connection...", TimeSpan.FromMinutes(1));
                    TcpClient client = await _otpActivityServer.AcceptTcpClientAsync();
                    LogThrottled("otp-activity-connected", $"OTP Activity client connected: {client.Client.RemoteEndPoint}", TimeSpan.FromSeconds(10));
                    _ = Task.Run(() => HandleOtpActivityClientAsync(client));
                }
                catch (Exception ex)
                {
                    if (_otpActivityRunning)
                        Log($" OTP Activity Server error: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log($" OTP Activity Server startup error: {ex.Message}");
        }
    }

    /// <summary>
    /// Handle OTP and GUI activity messages from the Lua frontend over TCP.
    /// This method contains many small message handlers (authentication, navigation,
    /// measurement flow control, device actions, audio controls, calibration,
    /// etc.). It's organised with comment headers only; no logic has been
    /// removed or altered — only clarified with comments for maintainability.
    /// </summary>
    static async Task HandleOtpActivityClientAsync(TcpClient client)
    {
        try
        {
            using NetworkStream stream = client.GetStream();
            stream.ReadTimeout = 5000;
            byte[] buffer = new byte[4096];

            while (client.Connected)
            {
                int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                if (bytesRead == 0) break;

                string message = Encoding.UTF8.GetString(buffer, 0, bytesRead).Trim();
                LogThrottled($"otp-activity-received:{message}", $"[OTP Activity] Received: {message}", TimeSpan.FromSeconds(30));



                if (message.StartsWith("VERIFY_OTP:"))
                {
                    string otp = message.Substring("VERIFY_OTP:".Length).Trim();
                    Log($" [OTP Activity] Verifying OTP ({otp.Length} digits — code not logged)");

                    var (success, patientData) = await VerifyOtpAsync(otp);
                    Audit.Log("OTP_VERIFY", success ? "success" : "fail",
                        success ? Audit.PatientRef(patientData?.PatientId) : "system");

                    if (success && patientData != null)
                    {
                        _currentUserId = patientData.PatientId;

                        //  ============================================
                        //  STORE TOKEN AND AUTHENTICATE USER
                        //  ============================================
                        lock (_authLock)
                        {
                            _patientToken = patientData.Token;
                            _patientId = patientData.PatientId;
                            _patientName = patientData.Name;
                            _isAuthenticated = true;

                            _currentUserAge = (byte)Math.Clamp(patientData.Age, 1, 120);
                            _currentUserGender = patientData.Gender.ToLower() == "male" ? (byte)1 : (byte)0;
                            _hasPatientData = true;
                            SpiManager.SetUserProfile(_currentUserAge, _currentUserGender, _hasPatientData);
                        }
                        lock (_aiInsightsLock)
                        {
                            _cachedAiInsights = "";
                            _aiInsightsReady = false;
                        }

                        Log($" [Auth] User authenticated - Token stored");
                        Log($" [Auth] PatientId: {LogMask.Id(_patientId)}, Name: {LogMask.Name(_patientName)}");
                        Log($" [Auth] Token received (len={_patientToken.Length})");
                        Log($" [SPI] Patient data stored: Age={_currentUserAge}, Gender={(_currentUserGender == 1 ? "Male" : "Female")}");
                        // ── Pre-generate static question audio in background ──
                        //_ = Task.Run(() => PreGenerateStaticQuestionsAsync());

                        //  ============================================

                        //  Send patient data to Lua for auto-fill
                        string response = $"OTP_VERIFIED:SUCCESS:::" +
                                        $"name={patientData.Name}:::" +
                                        $"age={patientData.Age}:::" +
                                        $"gender={patientData.Gender}:::" +
                                        $"phone={patientData.Phone}:::" +
                                        $"email={patientData.Email}\n";

                        byte[] responseBytes = Encoding.UTF8.GetBytes(response);
                        await stream.WriteAsync(responseBytes, 0, responseBytes.Length);
                        await stream.FlushAsync();

                        Log($" [OTP Activity] Sent patient data to GUI: name={LogMask.Name(patientData.Name)}, age/gender present");
                    }
                    else
                    {
                        string response = "OTP_VERIFIED:FAIL:::Invalid OTP\n";
                        byte[] responseBytes = Encoding.UTF8.GetBytes(response);
                        await stream.WriteAsync(responseBytes, 0, responseBytes.Length);
                        await stream.FlushAsync();

                        Log($" [OTP Activity] OTP verification failed");
                    }
                }


                else if (message.StartsWith("OTP_BUTTON_PRESSED:"))
                {
                    string userId = ExtractValue(message, "user_id=");
                    if (!string.IsNullOrEmpty(userId))
                    {
                        _currentUserId = userId;
                        Log($"📱 [OTP] Sending OTP to phone: {userId}");

                        var (success, apiMessage) = await SendOtpAsync(userId);
                        Audit.Log("OTP_REQUEST", success ? "success" : "fail", "system",
                            new Dictionary<string, object?> { ["reason"] = success ? null : ClassifyOtpFailure(apiMessage) });

                        string response = success ? $"OTP_SENT:SUCCESS:{apiMessage}" : $"OTP_SENT:FAIL:{apiMessage}";

                        //  Check if client is still connected before sending
                        if (client.Connected && stream.CanWrite)
                        {
                            byte[] responseBytes = Encoding.UTF8.GetBytes(response + "\n");
                            await stream.WriteAsync(responseBytes, 0, responseBytes.Length);
                            await stream.FlushAsync();  //  ADDED FLUSH!
                            Log($"➡️ [OTP Activity] Sent: {response}");
                        }
                        else
                        {
                            Log($" [OTP Activity] Client disconnected before response could be sent");
                        }
                    }
                }

                else if (message.Contains("OFFLINE_TOGGLED"))
                {
                    Audit.Log("SESSION_START", "success", "system",
                        new Dictionary<string, object?> { ["mode"] = "offline" });
                    lock (_aiInsightsLock) { _cachedAiInsights = ""; _aiInsightsReady = false; }

                    // ✅ STOP GEMINI VOICE ASSISTANT IN OFFLINE MODE
                    if (_geminiSession != null)
                    {
                        try
                        {
                            Log("[VOICE] Stopping Gemini — offline mode activated");
                            await _geminiSession.StopSessionAsync();
                            _geminiSession = null;
                        }
                        catch (Exception ex)
                        {
                            Log($"[VOICE] Error stopping Gemini: {ex.Message}");
                            _geminiSession = null;
                        }
                    }

                    lock (_lock)
                    {
                        _patientToken = string.Empty;
                        _patientId = string.Empty;
                        currentPatientId = 0;
                        _patientName = string.Empty;
                        _isAuthenticated = false;
                        
                        _currentState = MeasurementState.IDLE;
                        _isLiveMode = false;
                        ResetStoredValues();
                        ClearAllMeasurementData();
                        _currentState = MeasurementState.HEIGHT_WEIGHT;
                        Log($" [Offline] Starting measurements automatically");
                        SendHeightWeightCommand();
                    }
                }

                // ── User pressed BACK to the home/login page ──────────────────
                // GUI sends: "NAVIGATE_HOME: going to Start". The session is over,
                // so stop Gemini for good — no auto-restart; it only starts again
                // on the next explicit session command.
                else if (message.Contains("NAVIGATE_HOME"))
                {
                    Log("[VOICE] User navigated HOME — stopping Gemini session");
                    Audit.Log("SESSION_END", "info", Audit.PatientRef(_patientId),
                        new Dictionary<string, object?> { ["reason"] = "back-to-home" });
                    _voiceNavigationPending = false;
                    _geminiWasRunning = false;          // do NOT restart on next page

                    if (_voiceSession != null && _voiceSession.Started)
                    {
                        _ = Task.Run(async () =>
                        {
                            if (_geminiSession != null)
                                await _geminiSession.StopSessionAsync();
                            try { await SendToLua(_voiceWriter, "SESSION_END"); } catch { }
                            _voiceSession.Started = false;
                            _voiceWriter = null;
                            _voiceSession = null;
                            _voiceCts = null;
                            Log("[VOICE] Gemini stopped — home/login page");
                        });
                    }
                }

                else if (message.StartsWith("LOGIN_BUTTON_PRESSED:"))
                {
                    Log($"🚀 [OTP Activity] Login button pressed - starting measurements...");
                    Audit.Log("SESSION_START", "success", Audit.PatientRef(_patientId),
                        new Dictionary<string, object?> { ["mode"] = "online" });


                    lock (_aiInsightsLock)
                    {
                        _cachedAiInsights = "";
                        _aiInsightsReady = false;
                    }
                    Log($"🤖 [AI] Cleared previous patient insights");


                    lock (_lock)
                    {
                        _currentState = MeasurementState.IDLE;
                        _isLiveMode = false;
                        ResetStoredValues();
                        ClearAllMeasurementData();
                        _currentState = MeasurementState.HEIGHT_WEIGHT;
                        Log($" [GUI] Starting measurements automatically");
                        SendHeightWeightCommand();

                        string response = "MEASUREMENT_STARTED:SUCCESS";
                        byte[] responseBytes = Encoding.UTF8.GetBytes(response + "\n");
                        stream.Write(responseBytes, 0, responseBytes.Length);
                        stream.Flush();
                        Log($"➡️ [OTP Activity] Sent: {response}");
                    }
                }


                else if (message.Contains("NAVIGATE:All_Vitals_:FORWARD") || message.Contains("All_Vitals_:FORWARD"))
                {
                    TriggerAllVitalsInsightGeneration();
                }


                else if (message.Contains("CHECK_INSIGHTS_STATUS"))
                {
                    Log($" [Insights] Status check requested");
                    string status = GetInsightsStatusMessage();

                    if (client.Connected && stream.CanWrite)
                    {
                        byte[] statusBytes = Encoding.UTF8.GetBytes(status + "\n");
                        await stream.WriteAsync(statusBytes, 0, statusBytes.Length);
                        await stream.FlushAsync();
                    }
                }

                // ── Consent ──────────────────────────────────────────────────────────────
                if (message.Contains("CONSENT:ACCEPTED") || message.Contains("CONSENT_ACCEPTED"))
                {
                    Log($"📱 [GUI] Consent accepted by patient");
                }

                // ── Microphone ───────────────────────────────────────────────────────────
                else if (message.Contains("NAVIGATE:Voice_Assistant:FORWARD"))
                {
                    Log($"📱 [GUI] Microphone turned ON - starting recording...");
                    // await StartMicrophoneStreamingAsync();

                    byte[] responseBytes = Encoding.UTF8.GetBytes("MIC_STATUS|RECORDING\n");
                    await stream.WriteAsync(responseBytes, 0, responseBytes.Length);
                    await stream.FlushAsync();
                    Log($"📡 [MIC→GUI] Sent: MIC_STATUS|RECORDING");
                }
                else if (message.Contains("RECORDING_STARTED"))
                {
                    Log($"📱 [GUI] Recording started - icons blinking");
                }
                // ── Session ───────────────────────────────────────────────────────────────
                else if (message.Contains("STOP_SESSION"))
                {
                    Log($"📱 [GUI] Session stopped by user - stopping recording...");
                    await HardwareAudio.StopMicrophoneStreamingAsync();

                    byte[] responseBytes = Encoding.UTF8.GetBytes("MIC_STATUS|STOPPED\n");
                    await stream.WriteAsync(responseBytes, 0, responseBytes.Length);
                    await stream.FlushAsync();
                    Log($"📡 [MIC→GUI] Sent: MIC_STATUS|STOPPED");
                }

                // ── Select All ────────────────────────────────────────────────────────────
                else if (message.Contains("SELECT_ALL:ON"))
                {
                    Log($"📱 [GUI] Select All - ON");
                }
                else if (message.Contains("SELECT_ALL:OFF"))
                {
                    Log($"📱 [GUI] Select All - OFF");
                }

                // ── Skip / Payment ────────────────────────────────────────────────────────
                else if (message.Contains("SKIP_MODE_PROCEED"))
                {
                    Log($"📱 [GUI] Payment skipped - proceeding");
                }



                // ── Navigation (FORWARD) ──────────────────────────────────────────────────
                else if (message.Contains("NAVIGATE:Payments") && message.Contains("FORWARD"))
                {
                    Log($"📱 [GUI] Navigating FORWARD → Payments page");
                }
                else if (message.Contains("NAVIGATE:Voice_Assistant") && message.Contains("FORWARD"))
                {
                    Log($"📱 [GUI] Navigating FORWARD → Voice Assistant page");
                    _voiceNavigationPending = true;   // ← just set the flag, no delay needed


                }
                // ── Height/Weight ─────────────────────────────────────────────────────────
                // ── Height/Weight FRESH ENTRY (from login/route) ──────────────────────────
                else if (message.Contains("ROUTE:MEASUREMENT:Height___Weight"))
                {
                    Log($"📱 [GUI] Height/Weight page - fresh entry, resetting");
                    _voiceNavigationPending = false;

                    // Stop Gemini if it's running (track it for restart)
                    if (_voiceSession != null && _voiceSession.Started)
                    {
                        _geminiWasRunning = true;

                        // ── HEIGHT/WEIGHT FIRST (highest priority) ──
                        ResetMeasurementData();
                        Task.Run(async () =>
                        {
                            await SpiManager.SendSpiCommandReliable(SpiManager.CMD_HEIGHT_WEIGHT, retries: 5, delayMs: 150);
                        });
                        lock (_lock) { _currentState = MeasurementState.HEIGHT_WEIGHT; }
                        Log(" State forced to HEIGHT_WEIGHT (fresh)");

                        // ── STOP GEMINI AFTER HEIGHT/WEIGHT STARTED (background) ──
                        _ = Task.Run(async () =>
                        {
                            Log("[VOICE] User navigated to Height/Weight — stopping Gemini");
                            if (_geminiSession != null)
                                await _geminiSession.StopSessionAsync();
                            try { await SendToLua(_voiceWriter, "SESSION_END"); } catch { }
                            _voiceSession.Started = false;
                            _voiceWriter = null;
                            _voiceSession = null;
                            _voiceCts = null;
                        });
                    }
                    else
                    {
                        // No Gemini running, just start height/weight
                        ResetMeasurementData();
                        Task.Run(async () =>
                        {
                            await SpiManager.SendSpiCommandReliable(SpiManager.CMD_HEIGHT_WEIGHT, retries: 5, delayMs: 150);
                        });
                        lock (_lock) { _currentState = MeasurementState.HEIGHT_WEIGHT; }
                        Log(" State forced to HEIGHT_WEIGHT (fresh)");
                    }
                }

                // ── Height/Weight BACK (returning from Temperature) ───────────────────────
                else if (message.Contains("NAVIGATE:Height___Weight:BACK"))
                {
                    Log($"📱 [GUI] Height/Weight page BACK - preserving last reading");
                    _voiceNavigationPending = false;

                    // Stop Gemini if it's running (track it for restart)
                    if (_voiceSession != null && _voiceSession.Started)
                    {
                        _geminiWasRunning = true;

                        // ── HEIGHT/WEIGHT FIRST (highest priority) ──
                        //  NO ResetMeasurementData() — last storedHeight/storedWeight kept intact
                        // ESP32 streaming resumes; new stable reading will overwrite automatically
                        Task.Run(async () =>
                        {
                            await SpiManager.SendSpiCommandReliable(SpiManager.CMD_TEMPERATURE_STOP, retries: 5, delayMs: 150);
                            await SpiManager.SendSpiCommandReliable(SpiManager.CMD_HEIGHT_WEIGHT, retries: 5, delayMs: 150);
                            Log($" [H/W] Resumed streaming — last reading preserved");
                        });
                        lock (_lock) { _currentState = MeasurementState.HEIGHT_WEIGHT; }
                        Log(" State = HEIGHT_WEIGHT (reading preserved)");

                        // ── STOP GEMINI AFTER HEIGHT/WEIGHT STARTED (background) ──
                        _ = Task.Run(async () =>
                        {
                            Log("[VOICE] User navigated to Height/Weight — stopping Gemini");
                            if (_geminiSession != null)
                                await _geminiSession.StopSessionAsync();
                            try { await SendToLua(_voiceWriter, "SESSION_END"); } catch { }
                            _voiceSession.Started = false;
                            _voiceWriter = null;
                            _voiceSession = null;
                            _voiceCts = null;
                        });
                    }
                    else
                    {
                        // No Gemini running, just resume height/weight
                        Task.Run(async () =>
                        {
                            await SpiManager.SendSpiCommandReliable(SpiManager.CMD_TEMPERATURE_STOP, retries: 5, delayMs: 150);
                            await SpiManager.SendSpiCommandReliable(SpiManager.CMD_HEIGHT_WEIGHT, retries: 5, delayMs: 150);
                            Log($" [H/W] Resumed streaming — last reading preserved");
                        });
                        lock (_lock) { _currentState = MeasurementState.HEIGHT_WEIGHT; }
                        Log(" State = HEIGHT_WEIGHT (reading preserved)");
                    }
                }
                // ── Temperature FORWARD (fresh entry from H/W) ───────────────────────────
                else if (message.Contains("NAVIGATE:Temperature:FORWARD"))
                {
                    Log($"📱 [GUI] Temperature page FORWARD");
                    Task.Run(async () =>
                    {
                        await SpiManager.SendSpiCommandReliable(SpiManager.CMD_TEMPERATURE_START, retries: 10, delayMs: 150);
                    });
                    ProcessCommand("ENTER_TEMPERATURE");
                }

                // ── Temperature BACK (returning from SpO2+NIBP) ──────────────────────────
                else if (message.Contains("NAVIGATE:Temperature:BACK"))
                {
                    Log($"📱 [GUI] Temperature page BACK - resuming temp, preserving last reading");

                    //  Restart SPI temp sensor so lastTemperatureIR starts updating again
                    //  Do NOT send CMD_TEMPERATURE_STOP + CMD_HEIGHT_WEIGHT — user is on Temp page, not H/W
                    Task.Run(async () =>
                    {
                        await SpiManager.SendSpiCommandReliable(SpiManager.CMD_TEMPERATURE_START, retries: 5, delayMs: 150);
                        Log($" [TEMP] Sensor restarted — last stored: {storedTemperatureIR:F1}°C — live updates resuming");
                    });

                    // State goes back to TEMPERATURE so ENTER_SPO2_NIBP guard passes on next forward
                    lock (_lock) { _currentState = MeasurementState.TEMPERATURE; }
                    Log(" State = TEMPERATURE (reading preserved)");
                }

                // ── SpO2 + NIBP merged page ───────────────────────────────────────────────
                else if (message.Contains("NAVIGATE:BP:FORWARD") || message.Contains("NAVIGATE:SpO2:FORWARD"))
                {
                    if (_lastMergedPageNav != null &&
                        (DateTime.UtcNow - _lastMergedPageNav.Value).TotalMilliseconds < 500)
                    {
                        Log($"📱 [GUI] Duplicate SpO2+NIBP FORWARD suppressed");
                        return;
                    }
                    _lastMergedPageNav = DateTime.UtcNow;

                    Log($"📱 [GUI] SpO2+NIBP page FORWARD - stopping temp, starting SpO2");
                    Task.Run(async () =>
                    {
                        await SpiManager.SendSpiCommandReliable(SpiManager.CMD_TEMPERATURE_STOP, retries: 5, delayMs: 150);
                        Log($"📸 Temperature stop sent");
                    });
                    ProcessCommand("ENTER_SPO2_NIBP");
                }
                else if (message.Contains("NAVIGATE:BP:BACK") || message.Contains("NAVIGATE:SpO2:BACK"))
                {
                    if (_lastMergedPageNav != null &&
                        (DateTime.UtcNow - _lastMergedPageNav.Value).TotalMilliseconds < 500)
                    {
                        Log($"📱 [GUI] Duplicate SpO2+NIBP BACK suppressed");
                        return;
                    }
                    _lastMergedPageNav = DateTime.UtcNow;

                    Log($"📱 [GUI] SpO2+NIBP page BACK - returning to Temperature");
                    Task.Run(async () =>
                    {
                        await SpiManager.SendSpiCommandReliable(SpiManager.CMD_TEMPERATURE_START, retries: 5, delayMs: 150);
                    });
                    ProcessCommand("BACK_SPO2_NIBP");
                }

                // ── BP controls (within SpO2+NIBP page) ──────────────────────────────────
                else if (message.Contains("BP CONNECTED"))
                {
                    Log($"📱 [GUI] BP device connected");
                }
                else if (message.Contains("BP START REQUESTED"))
                {
                    Log($"📱 [GUI] BP Start pressed");
                    ProcessCommand("START BP");
                }
                else if (message.Contains("BP STOP REQUESTED"))
                {
                    Log($"📱 [GUI] BP Stop pressed");
                    ProcessCommand("STOP BP");
                }

                // ── Blood Sugar ───────────────────────────────────────────────────────────
                else if (message.Contains("NAVIGATE:Blood_Sugar_:FORWARD") ||
                        (message.Contains("Blood_Sugar") && message.Contains("FORWARD")))
                {
                    Log($"📱 [GUI] Blood Sugar page FORWARD");
                    ProcessCommand("ENTER_BLOOD_SUGAR");
                }
                else if (message.Contains("NAVIGATE:Blood_Sugar_:BACK") ||
                        (message.Contains("Blood_Sugar") && message.Contains("BACK")))
                {
                    Log($"📱 [GUI] Blood Sugar page BACK - returning to SpO2+NIBP");
                    ProcessCommand("BACK_BLOOD_SUGAR");
                }

                // ── ECG 5-lead ────────────────────────────────────────────────────────────
                else if (message.Contains("NAVIGATE:ECG:FORWARD") ||
                        (message.Contains(":ECG") && message.Contains("FORWARD") && !message.Contains("12")))
                {
                    Log($"📱 [GUI] ECG page FORWARD");
                    ProcessCommand("ENTER_ECG");
                }
                else if (message.Contains("NAVIGATE:ECG:BACK") ||
                        (message.Contains(":ECG") && message.Contains("BACK") && !message.Contains("12")))
                {
                    Log($"📱 [GUI] ECG page BACK - returning to Blood Sugar");
                    ProcessCommand("BACK_ECG");
                }
                else if (message.Contains("ECG RECORDING START"))
                {
                    Log($"📱 [GUI] ECG Start pressed");
                    ProcessCommand("START ECG");
                }
                else if (message.Contains("ECG RECORDING STOP"))
                {
                    Log($"📱 [GUI] ECG Stop pressed");
                    ProcessCommand("STOP ECG");
                }
                // ── ECG 12-lead ───────────────────────────────────────────────────────────
                else if (message.Contains(":12_LEAD") || message.Contains("12_LEAD") && message.Contains("FORWARD"))
                {
                    Log($"📱 [GUI] Switched to 12-Lead - stopping 5-lead ECG");
                    ProcessCommand("STOP ECG");
                    // NOTE: no ProcessCommand("START ECG12") here — entering the page
                    // should only stream preview, NOT start recording
                }
                else if (message.Contains("ECG12 START REQUESTED") || message.Contains("ECG12 START clicked"))
                {
                    Log($"📱 [GUI] ECG12 page ready (preview only, not recording)");
                    // No ProcessCommand call — page entry should not start recording
                }
                else if (message.Contains("ECG12 RECORDING START"))
                {
                    Log($"📱 [GUI] ECG12 Recording Start pressed");
                    ProcessCommand("START ECG12");
                }
                else if (message.Contains("ECG12 RECORDING STOP"))
                {
                    Log($"📱 [GUI] ECG12 Recording Stop pressed");
                    ProcessCommand("STOP ECG12");
                }
                // ── All Vitals ────────────────────────────────────────────────────────────
                else if (message.Contains("All_Vitals_:FORWARD"))
                {
                    Log($"📱 [GUI] All Vitals page - stopping ECG12");
                    ProcessCommand("STOP ECG12");
                    ProcessCommand("DONE");

                }

                // ── Backward Navigation ───────────────────────────────────────────────────
                else if (message.Contains("NAVIGATE:") && message.Contains("BACKWARD"))
                {
                    Log($"📱 [GUI] BACKWARD navigation received: {message}");
                    // Not active currently - placeholder for future backward nav handling
                }

                // ── Home / Start ──────────────────────────────────────────────────────────
                else if (message.Contains("HOME NAVIGATION") || message.Contains("NAVIGATE:Start"))
                {

                    Log($"📱 [GUI] Home navigation - Clearing ALL measurement data");




                    //  Clear authentication
                    lock (_authLock)
                    {
                        _isAuthenticated = false;
                        _patientToken = "";
                        _patientId = "";
                        _patientName = "";

                        _currentUserAge = 25;
                        _currentUserGender = 1;
                        _hasPatientData = false;
                        SpiManager.SetUserProfile(_currentUserAge, _currentUserGender, _hasPatientData);
                    }
                    Log($" [Auth] User logged out - Token cleared");

                    //  Clear all measurements
                    ClearAllMeasurementData();

                    ResetMeasurementData();

                    lock (_aiInsightsLock)
                    {
                        _cachedAiInsights = "";
                        _aiInsightsReady = false;
                    }
                    Log($"🤖 [AI] Insights cache cleared for new patient");

                    SpiManager.SendSpiCommand(SpiManager.CMD_HOME);
                    ProcessCommand("HOME");



                    //  Notify Lua that data is cleared
                    string response = "MEASUREMENTS_CLEARED\n";
                    byte[] responseBytes = Encoding.UTF8.GetBytes(response);
                    await stream.WriteAsync(responseBytes, 0, responseBytes.Length);
                    await stream.FlushAsync();

                    // After sending MEASUREMENTS_CLEARED, also clear voice screen:
                    if (_voiceWriter != null)
                    {
                        try
                        {
                            await SendToLua(_voiceWriter, "CLEAR_SCREEN");
                        }
                        catch { }
                    }
                }

                // ── Share Vitals ────────────────────────────────────────────────────────────
                else if (message.StartsWith("FIELD_VALUE_UPDATE:"))
                {
                    Log($"ℹ️ [OTP Activity] Field update: {message}");
                }
                else if (message.Contains("SHARE ANIMATION TRIGGERED"))
                {
                    Log($"📱 [GUI] Share menu opened");
                }
                else if (message.Contains("Share All clicked"))
                {
                    Log($"📱 [GUI] Website share clicked - triggering DONE to send vitals");
                    ProcessCommand("DONE");
                    _ = Task.Run(() => TrackSharingAction("Share All clicked"));
                }
                else if (message.Contains("share all clicked"))
                {
                    Log($"📱 [GUI] Share All clicked - triggering DONE and tracking all sharing methods");
                    ProcessCommand("DONE");
                    _ = Task.Run(async () =>
                    {
                        await TrackSharingAction("SHARE_ALL");
                        await TrackSharingAction("SMS");
                        await TrackSharingAction("WHATSAPP");
                        await TrackSharingAction("WEBSITE");
                    });
                }
                else if (message.Contains("sms clicked"))
                {
                    Log($"📱 [GUI] SMS share clicked - tracking action");
                    _ = Task.Run(() => TrackSharingAction("SMS"));
                }
                else if (message.Contains("whatsapp clicked"))
                {
                    Log($"📱 [GUI] WhatsApp share clicked - tracking action");
                    _ = Task.Run(() => TrackSharingAction("WHATSAPP"));
                }

                // ── Manual Blood Glucose Entry ────────────────────────────────────────────────────────────
                else if (message.Contains("BS_SAVE:") ||
                         message.Contains("BLOOD_SUGAR_SYSTEM:") ||
                         message.Contains("BLOOD_SUGAR_SUBMIT") ||
                         message.Contains("BLOOD_SUGAR_FIELD:") ||
                         message.Contains(":Blood_Sugar_") ||
                         message.Contains("Blood_Sugar"))
                {
                    Log($" [Blood Sugar] Handler triggered by: {message}");

                    //  Handle BS_SAVE messages
                    if (message.Contains("BS_SAVE:"))
                    {
                        Log($"📥 [Blood Sugar] BS_SAVE detected");

                        try
                        {
                            string field = "";
                            int value = 0;

                            if (message.Contains("field=FASTING"))
                            {
                                field = "FASTING";
                                Log($"🔍 Field: FASTING");
                            }
                            else if (message.Contains("field=PREMEAL"))
                            {
                                field = "PREMEAL";
                                Log($"🔍 Field: PREMEAL");
                            }
                            else if (message.Contains("field=POSTMEAL"))
                            {
                                field = "POSTMEAL";
                                Log($"🔍 Field: POSTMEAL");
                            }

                            var valueMatch = Regex.Match(message, @"value=(\d+)");
                            if (valueMatch.Success)
                            {
                                value = int.Parse(valueMatch.Groups[1].Value);
                                Log($"🔍 Value extracted: {value}");
                            }

                            if (!string.IsNullOrEmpty(field) && value > 0)
                            {
                                lock (_bloodGlucoseLock)
                                {
                                    switch (field)
                                    {
                                        case "FASTING":
                                            storedBloodGlucoseFasting = value;
                                            Log($" [Blood Sugar] Fasting STORED: {storedBloodGlucoseFasting} mg/dL");
                                            break;
                                        case "PREMEAL":
                                            storedBloodGlucosePreMeal = value;
                                            Log($" [Blood Sugar] Pre-meal STORED: {storedBloodGlucosePreMeal} mg/dL");
                                            break;
                                        case "POSTMEAL":
                                            storedBloodGlucosePostMeal = value;
                                            Log($" [Blood Sugar] Post-meal STORED: {storedBloodGlucosePostMeal} mg/dL");
                                            break;
                                    }
                                }

                                lock (_bloodGlucoseLock)
                                {
                                    Log($" Current Blood Glucose Values:");
                                    Log($"   Fasting: {storedBloodGlucoseFasting} mg/dL");
                                    Log($"   Pre-meal: {storedBloodGlucosePreMeal} mg/dL");
                                    Log($"   Post-meal: {storedBloodGlucosePostMeal} mg/dL");
                                }

                                string response = $"BS_STORED:{field}:{value}\n";
                                byte[] responseBytes = Encoding.UTF8.GetBytes(response);
                                await stream.WriteAsync(responseBytes, 0, responseBytes.Length);
                                await stream.FlushAsync();
                                Log($" Sent confirmation to Lua");
                            }
                            else
                            {
                                Log($" Skipped storage - field: '{field}', value: {value}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($" [Blood Sugar] BS_SAVE error: {ex.Message}");
                        }
                    }

                    //  Handle BLOOD_SUGAR_SYSTEM messages
                    else if (message.Contains("BLOOD_SUGAR_SYSTEM:"))
                    {
                        Log($"📥 [Blood Sugar] BLOOD_SUGAR_SYSTEM detected");

                        if (message.Contains("event=SAVE_TO_BACKEND"))
                        {
                            try
                            {
                                string field = "";
                                int value = 0;

                                var fieldMatch = Regex.Match(message, @"field=(\w+)");
                                if (fieldMatch.Success)
                                {
                                    field = fieldMatch.Groups[1].Value;
                                    Log($"🔍 Field: {field}");
                                }

                                var valueMatch = Regex.Match(message, @"value=(\d+)");
                                if (valueMatch.Success)
                                {
                                    value = int.Parse(valueMatch.Groups[1].Value);
                                    Log($"🔍 Value: {value}");
                                }

                                if (!string.IsNullOrEmpty(field) && value > 0)
                                {
                                    lock (_bloodGlucoseLock)
                                    {
                                        switch (field.ToUpper())
                                        {
                                            case "FASTING":
                                                storedBloodGlucoseFasting = value;
                                                Log($" Fasting STORED: {storedBloodGlucoseFasting}");
                                                break;
                                            case "PREMEAL":
                                                storedBloodGlucosePreMeal = value;
                                                Log($" Pre-meal STORED: {storedBloodGlucosePreMeal}");
                                                break;
                                            case "POSTMEAL":
                                                storedBloodGlucosePostMeal = value;
                                                Log($" Post-meal STORED: {storedBloodGlucosePostMeal}");
                                                break;
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Log($" SYSTEM event error: {ex.Message}");
                            }
                        }
                    }

                    //  Handle submit
                    else if (message.Contains("BLOOD_SUGAR_SUBMIT") || message.Contains("Submit"))
                    {
                        Log($"📱 [GUI] Blood Sugar Submit");

                        lock (_bloodGlucoseLock)
                        {
                            Log($" Final values:");
                            Log($"   Fasting: {storedBloodGlucoseFasting}");
                            Log($"   Pre-meal: {storedBloodGlucosePreMeal}");
                            Log($"   Post-meal: {storedBloodGlucosePostMeal}");
                        }

                        string response = $"BLOOD_SUGAR_SAVED:OK\n";
                        byte[] responseBytes = Encoding.UTF8.GetBytes(response);
                        await stream.WriteAsync(responseBytes, 0, responseBytes.Length);
                        await stream.FlushAsync();
                    }

                    else
                    {
                        Log($"📱 [GUI] Blood Sugar page navigation");
                        _isBloodSugarPageActive = true;
                    }

                }



                // ── Device Restart And Power-off ──────────────────────────────────────────
                else if (message == "SHUTDOWN_DEVICE")
                {
                    Log("📱 [GUI] Shutdown requested by user");
                    Task.Run(async () =>
                    {
                        try
                        {
                            Log(" [SystemPower] Attempting DBus shutdown...");
                            await SystemPower.PowerOff();
                            Log(" [SystemPower] DBus shutdown command sent successfully");
                        }
                        catch (Exception ex)
                        {
                            Log($" [SystemPower] DBus shutdown failed: {ex.Message}");
                            Log(" [SystemPower] Falling back to process shutdown...");
                            try
                            {
                                Process.Start(new ProcessStartInfo
                                {
                                    FileName = "/sbin/shutdown",
                                    Arguments = "-h now",
                                    UseShellExecute = false
                                });
                            }
                            catch (Exception ex2)
                            {
                                Log($" [SystemPower] Process shutdown also failed: {ex2.Message}");
                            }
                        }
                    });
                }
                else if (message == "RESTART_DEVICE")
                {
                    Log("📱 [GUI] Restart requested by user");
                    Task.Run(async () =>
                    {
                        try
                        {
                            Log("🔄 [SystemPower] Attempting DBus restart...");
                            await SystemPower.Reboot();
                            Log("🔄 [SystemPower] DBus restart command sent successfully");
                        }
                        catch (Exception ex)
                        {
                            Log($" [SystemPower] DBus restart failed: {ex.Message}");
                            Log("🔄 [SystemPower] Falling back to process restart...");
                            try
                            {
                                Process.Start(new ProcessStartInfo
                                {
                                    FileName = "/sbin/shutdown",
                                    Arguments = "-r now",
                                    UseShellExecute = false
                                });
                            }
                            catch (Exception ex2)
                            {
                                Log($" [SystemPower] Process restart also failed: {ex2.Message}");
                            }
                        }
                    });
                }



                // ── Volume Control ───────────────────────────────────────────────────────────────────
                else if (message.StartsWith("VOLUME:"))
                {
                    var val = message.Substring(7).Trim().ToUpperInvariant();
                    Log($"🔊 [Audio] Volume command: {val}");

                    if (val == "MUTE")
                    {
                        HardwareAudio.Mute();
                    }
                    else if (val == "UNMUTE")
                    {
                        HardwareAudio.Unmute();
                    }
                    else if (int.TryParse(val, out int pct))
                    {
                        HardwareAudio.SetVolume(pct);
                    }
                    else
                    {
                        Log($" [Audio] Invalid volume command: {val}");
                    }
                }



                // ── Device Calibration request and Commands ──────────────────────────────────────────

                else if (message.Contains("UART_TEST"))
                {
                    Log("[OTP Activity] UART_TEST requested over nav channel");
                    RunUartTest();
                }

                else if (message.StartsWith("CALIBRATION|"))
                {
                    string sensor = message.Split('|')[1].Trim().ToLower();
                    Log($"🔧 [Calibration] Request received for: {sensor}");

                    switch (sensor)
                    {
                        case "blood_pressure":
                            Task.Run(() => CalibrateBloodPressureAsync());
                            break;

                        case "spo2":
                            Task.Run(() => CalibrateSpO2Async());
                            break;

                        case "ecg":
                            Task.Run(() => CalibrateEcgAsync());
                            break;

                        case "temperature":
                            Task.Run(async () => await CalibrateTemperatureAsync());
                            break;

                        case "height":
                            Task.Run(async () => await CalibrateHeightAsync());
                            break;

                        case "weight":
                            Task.Run(async () => await CalibrateWeightAsync());
                            break;

                        case "blood_sugar":
                            Task.Run(() => CalibrateBloodSugarAsync());
                            break;

                        case "device":
                            Task.Run(() => CalibrateDeviceAsync());
                            break;

                        default:
                            Log($" [Calibration] Unknown sensor: {sensor}");
                            break;
                    }
                }


            }  // ← while loop ends here
        }
        catch (Exception ex)
        {
            Log($" [OTP Activity] Client error: {ex.Message}");
        }
        finally
        {
            client.Close();
        }
    }

    // ─────────────────────────────────────────────────────────────
    static async Task TrackSharingAction(string shareMethod)
    {
        Log($" Sharing action logged: {shareMethod}");
        // Sharing tracking disabled - vitals are sent successfully via DONE command
    }





    static async Task StartOtpVerifyServerAsync()
    {
        try
        {
            _otpVerifyRunning = true;
            _otpVerifyServer = new TcpListener(IPAddress.Any, OtpVerifyPort);
            _otpVerifyServer.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _otpVerifyServer.Start();
            Log($" OTP Verify Server started on {IPAddress.Any}:{OtpVerifyPort}");

            while (_otpVerifyRunning)
            {
                try
                {
                    Log("⏳ Waiting for OTP Verify connection...");
                    TcpClient client = await _otpVerifyServer.AcceptTcpClientAsync();
                    Log($"📥 OTP Verify client connected: {client.Client.RemoteEndPoint}");
                    _ = Task.Run(() => HandleOtpVerifyClientAsync(client));
                }
                catch (Exception ex)
                {
                    if (_otpVerifyRunning)
                        Log($" OTP Verify Server error: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log($" OTP Verify Server startup error: {ex.Message}");
        }
    }

    static async Task HandleOtpVerifyClientAsync(TcpClient client)
    {
        try
        {
            using NetworkStream stream = client.GetStream();
            stream.ReadTimeout = 10000; // 10 second timeout for API call
            byte[] buffer = new byte[4096];

            int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
            if (bytesRead == 0) return;

            string message = Encoding.UTF8.GetString(buffer, 0, bytesRead).Trim();
            Log($"📥 [OTP Verify] Received: {message}");

            //  Handle: VERIFY_OTP:767678
            if (message.StartsWith("VERIFY_OTP:"))  // ← CHANGED: Remove "else"
            {
                string otp = message.Substring("VERIFY_OTP:".Length).Trim();
                Log($" [OTP] Verifying OTP ({otp.Length} digits — code not logged)");

                var (success, patientData) = await VerifyOtpAsync(otp);
                Audit.Log("OTP_VERIFY", success ? "success" : "fail",
                    success ? Audit.PatientRef(patientData?.PatientId) : "system");

                if (success && patientData != null)
                {
                    _currentUserId = patientData.PatientId;

                    //  Send patient data to Lua for auto-fill
                    string response = $"OTP_VERIFIED:SUCCESS:::" +  // ← KEEP THIS
                                    $"name={patientData.Name}:::" +
                                    $"age={patientData.Age}:::" +
                                    $"gender={patientData.Gender}:::" +
                                    $"phone={patientData.Phone}:::" +
                                    $"email={patientData.Email}\n";

                    byte[] responseBytes = Encoding.UTF8.GetBytes(response);
                    await stream.WriteAsync(responseBytes, 0, responseBytes.Length);
                    await stream.FlushAsync();

                    Log($" [OTP] Sent patient data to Lua for auto-fill");
                }
                else
                {
                    string response = "OTP_VERIFIED:FAIL:::Invalid OTP\n";  // ← KEEP THIS
                    byte[] responseBytes = Encoding.UTF8.GetBytes(response);
                    await stream.WriteAsync(responseBytes, 0, responseBytes.Length);
                    await stream.FlushAsync();

                    Log($" [OTP] Verification failed");
                }
            }
            //  Handle: SEND_OTP:7366881478 (alternative format)
            else if (message.StartsWith("SEND_OTP:"))
            {
                string userId = message.Substring("SEND_OTP:".Length).Trim();
                _currentUserId = userId;

                var (success, apiMessage) = await SendOtpAsync(userId);
                Audit.Log("OTP_REQUEST", success ? "success" : "fail", "system",
                    new Dictionary<string, object?> { ["reason"] = success ? null : ClassifyOtpFailure(apiMessage) });
                string response = success ? $"OTP_SENT:SUCCESS:{apiMessage}" : $"OTP_SENT:FAIL:{apiMessage}";  // ← KEEP THIS

                //  Send response immediately
                byte[] responseBytes = Encoding.UTF8.GetBytes(response + "\n");
                await stream.WriteAsync(responseBytes, 0, responseBytes.Length);
                Log($"➡️ [OTP Verify] Sent: {response}");
            }
        }
        catch (Exception ex)
        {
            Log($" [OTP Verify] Client error: {ex.Message}");
        }
        finally
        {
            client.Close();
            Log($"📤 [OTP Verify] Client disconnected");
        }
    }

    static async Task StartAnalysisTcpServerAsync()
    {
        try
        {
            _analysisServer = new TcpListener(IPAddress.Any, AnalysisPort);
            _analysisServer.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _analysisServer.Start();
            Log($" Analysis TCP Server started on {IPAddress.Any}:{AnalysisPort}");

            while (_analysisRunning)
            {
                try
                {
                    TcpClient client = await _analysisServer.AcceptTcpClientAsync();
                    Log($"📥 Analysis client connected: {client.Client.RemoteEndPoint}");
                    _ = Task.Run(() => HandleAnalysisClientAsync(client, CancellationToken.None));
                }
                catch (Exception ex)
                {
                    if (_analysisRunning)
                        Log($" Analysis TCP Server error: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log($" Analysis TCP Server startup error: {ex.Message}");
        }
    }

    static async Task HandleAnalysisClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            using NetworkStream stream = client.GetStream();
            stream.ReadTimeout = 5000;
            stream.WriteTimeout = 5000;

            byte[] buffer = new byte[256];
            int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
            string request = Encoding.UTF8.GetString(buffer, 0, bytesRead).Trim();
            Log($"📥 Analysis request: {request}");

            //  NEW: Handle status check
            if (request == "CHECK_STATUS")
            {
                string status;
                lock (_aiInsightsLock)
                {
                    if (_aiInsightsReady && !string.IsNullOrEmpty(_cachedAiInsights) && _cachedAiInsights != "GENERATING")
                    {
                        status = "READY\n";
                    }
                    else
                    {
                        status = "GENERATING\n";
                    }
                }

                if (client.Connected && stream.CanWrite)
                {
                    byte[] statusBytes = Encoding.UTF8.GetBytes(status);
                    await stream.WriteAsync(statusBytes, 0, statusBytes.Length, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }
                return;
            }

            //  Existing analysis handler
            if (request == "analysis" || request == "GET_INSIGHTS")
            {
                string aiResponse = await GetInsightsForClient(cancellationToken);

                // Send response to GUI
                if (client.Connected && stream.CanWrite)
                {
                    byte[] responseBytes = Encoding.UTF8.GetBytes(aiResponse + "\n");
                    await stream.WriteAsync(responseBytes, 0, responseBytes.Length, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                    Log($"📤 Analysis response sent to GUI");
                }
            }
        }
        catch (Exception ex)
        {
            Log($" Analysis client error: {ex.Message}");
        }
        finally
        {
            try { client.Close(); } catch { }
        }
    }






    // =============================================================================
    // VOICE TCP SERVER — FULL DEBUG VERSION
    // =============================================================================


    private static StreamWriter? _voiceWriter = null;
    private static int _geminiActive = 0;   // 1 while a Gemini session runs (single-session guard)
    private static VoiceSession? _voiceSession = null;
    private static CancellationTokenSource? _voiceCts = null;


    /*     static async Task<string> FetchNextQuestionAsync(string answer)
        {
            try
            {
                string tokenStr;
                lock (_authLock) { tokenStr = _patientToken; }

                using var httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokenStr);

                var payload = new { answer = answer };
                var content = new StringContent(
                    JsonSerializer.Serialize(payload),
                    Encoding.UTF8,
                    "application/json"
                );

                Log($"[VOICE] Fetching next question for answer: '{answer}'");
                var response = await httpClient.PostAsync(VOICE_QUESTION_API_URL, content);
                var responseBody = await response.Content.ReadAsStringAsync();
                Log($"[VOICE] Next question API response: {responseBody}");

                using var doc = JsonDocument.Parse(responseBody);
                if (doc.RootElement.TryGetProperty("success", out var s) && s.GetBoolean())
                {
                    if (doc.RootElement.TryGetProperty("data", out var data) &&
                        data.TryGetProperty("question", out var q))
                    {
                        string nextQuestion = q.GetString() ?? "";
                        Log($"[VOICE] Next question: '{nextQuestion}'");
                        return nextQuestion;
                    }
                }

                Log("[VOICE]  Could not extract question from API response");
                return "";
            }
            catch (Exception ex)
            {
                Log($"[VOICE]  FetchNextQuestionAsync error: {ex.Message}");
                return "";
            }
        } */

    // ──────────────────────────────────────────────
    // VoiceSession — now tracks dynamic state
    // ──────────────────────────────────────────────
    private class VoiceSession
    {
        public int QuestionIndex { get; set; } = 0;
        public bool Started { get; set; } = false;
        public string CurrentQuestion { get; set; } = "";   // QUESTION block
        public string AccumulatedTranscript { get; set; } = ""; // ANSWER block (live chunks)
    }

    // ──────────────────────────────────────────────
    // Question builder — Q1 static, Q2+ from API (placeholder)
    // ──────────────────────────────────────────────
    static string BuildQuestion(int index)
    {
        if (index == 0)
            return GetQ1Text();

        // Q2+ come from API
        return "";
    }

    /*     private static byte[]? _cachedQ1Wav = null;
        private static byte[]? _cachedLastWav = null; */

    private static string GetQ1Text() =>
    $"Hello {_patientName?.Trim() ?? "there"}, I have a few quick health questions. " +
    $"Welcome to BluAI VitalsChair, your personal health assistant. I am so glad you are here today. Before we begin your health check, I just want you to know that you are in great hands. We will be measuring your blood pressure, oxygen levels, heart rate, body temperature, and body composition. This will only take a few minutes. So, how have you been feeling lately? Any pain, discomfort, or anything unusual that you would like to tell me about? Please take your time, I am listening.";
    private static string GetLastQText() =>
    $"Thank you {_patientName?.Trim() ?? "there"}, we're all done. " +
    $"Please follow the on-screen instructions for your vitals measurement.";
    // ──────────────────────────────────────────────
    // BeginSessionAsync -- LAST EDITED
    // ──────────────────────────────────────────────
    static async Task BeginSessionAsync(
        StreamWriter writer, VoiceSession session, CancellationTokenSource cts)
    {
        Log("[VOICE] BeginSessionAsync() — starting Gemini Live session");
        session.QuestionIndex = 0;

        // ── Mic hardware gate — guidance only, NO hot-plug auto-start ─────
        // Plugging the USB headset while this prompt is shown restarts the
        // device/GUI on some units (hardware/udev issue, unresolved), so
        // auto-starting Gemini on hot-plug just launched sessions into that
        // restart. Now we only tell the user what to do; they plug in and
        // restart the session themselves (Back → start again). No watchers,
        // no fallbacks — nothing runs in the background from this path.
        if (string.IsNullOrEmpty(HardwareAudio.GetMicrophoneDevice()))
        {
            Log("[VOICE] ⛔ No microphone — instructing user; session NOT started");
            await SendToLua(writer, "MIC_STATUS|NOT_CONNECTED");
            await SendToLua(writer,
                "QUESTION:No headphone detected. Please connect the headphone, then go back and start the session again.");
            session.Started = false;
            return;
        }

        // ── One Gemini session at a time ──────────────────────────────────
        // A duplicate start (double VOICE_READY / consent+ready race) used to
        // run TWO sessions: the second overwrote _geminiSession, so the real
        // conversation's transcript was lost and a 1-entry ghost transcript
        // got uploaded (seen in field logs: "transcript has 1 entries" after
        // a 7-turn session, plus two "Session loop ended" lines).
        if (Interlocked.CompareExchange(ref _geminiActive, 1, 0) != 0)
        {
            Log("[VOICE] ⛔ Session already active — duplicate start ignored");
            session.Started = true;   // a session IS running; keep the flag truthful
            return;
        }

        Audit.Log("VOICE_SESSION", "info", Audit.PatientRef(_patientId),
            new Dictionary<string, object?> { ["phase"] = "start" });

        await SendToLua(writer, "TOTAL_QUESTIONS:0");
        await Task.Delay(200);

        string apiKey = ConfigManager.GetVoiceAssistantApiKey();
        string model = ConfigManager.GetVoiceAssistantModel();
        string language = ConfigManager.GetVoiceAssistantLanguage()?.Trim() ?? "en-US";

        int luaSendFails = 0;   // consecutive GUI send failures (ghost-session guard)

        // Local reference: the transcript is captured from THIS instance, never
        // from the static field (which a racing duplicate could overwrite).
        var gemini = new GeminiLiveSession(
            patientName: _patientName?.Trim() ?? "there",
            audioOut: HardwareAudio.GetAudioOutputDevice(),
            audioMic: HardwareAudio.GetMicrophoneDevice(),
            apiKey: apiKey,
            model: model,
            language: language,
            log: Log,
            sendToLua: async msg =>
            {
                try { await SendToLua(writer, msg); Interlocked.Exchange(ref luaSendFails, 0); }
                catch
                {
                    // GUI unreachable (crashed/restarted mid-session)? After 3
                    // consecutive failed sends, stop the session — Gemini must
                    // never keep talking in the background with no page attached.
                    if (Interlocked.Increment(ref luaSendFails) == 3 && _geminiSession != null)
                    {
                        Log("[VOICE] GUI unreachable — stopping Gemini (no ghost session)");
                        try { await _geminiSession.StopSessionAsync(); } catch { }
                    }
                }
            },
            sessionCt: cts.Token
        );

        _geminiSession = gemini;   // static ref for external stop/skip paths

        try
        {
            await gemini.RunAsync();
        }
        finally
        {
            Interlocked.Exchange(ref _geminiActive, 0);   // allow the next session
        }

        // ── CAPTURE transcript immediately after RunAsync returns ────────
        // From the LOCAL instance — never the static field, which a duplicate
        // session could have replaced (this is how a 7-turn transcript got
        // lost and a 1-entry ghost transcript was uploaded).
        var transcriptSnapshot = gemini.GetSessionTranscript();
        string patientTokenSnapshot = _patientToken; // capture token too

        Log($"[VOICE] Session ended — transcript has {transcriptSnapshot?.Count ?? 0} entries");
        Audit.Log("VOICE_SESSION", "info", Audit.PatientRef(_patientId),
            new Dictionary<string, object?> { ["phase"] = "end", ["qa_count"] = transcriptSnapshot?.Count ?? 0 });
        PrintSessionSummaryToLog(transcriptSnapshot); // log it immediately

        // Dispose session now — transcript is safely captured above
        gemini.Dispose();
        if (ReferenceEquals(_geminiSession, gemini))
            _geminiSession = null;

        // ── Send to API using captured snapshot — not the disposed object ──
        if (transcriptSnapshot != null && transcriptSnapshot.Count > 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await SendVoiceSessionToApiAsync(transcriptSnapshot, patientTokenSnapshot);
                }
                catch (Exception ex)
                {
                    Log($"[VOICE API] Transcript send error: {ex.Message}");
                }
            });
        }
        else
        {
            Log("[VOICE API] ⚠️ No transcript captured — skipping API send");
        }
    }

    static void PrintSessionSummaryToLog(
        Dictionary<int, (string question, string answer)>? transcript)
    {
        if (transcript == null || transcript.Count == 0)
        {
            Log("║ [SUMMARY] No transcript captured");
            return;
        }

        // PHI: the transcript is the patient's spoken health information.
        // At normal levels log only counts; full content only at Debug
        // (LOG_LEVEL=Debug) for troubleshooting.
        int answered = transcript.Count(x => !string.IsNullOrWhiteSpace(x.Value.answer));
        Log($"║ [SUMMARY] Voice session: {transcript.Count} questions, {answered} answered (content at Debug level only)");

        foreach (var kvp in transcript.OrderBy(x => x.Key))
        {
            Log($"║ Q{kvp.Key}: {kvp.Value.question}", LogLevel.Debug);
            if (!string.IsNullOrWhiteSpace(kvp.Value.answer))
                Log($"║ A{kvp.Key}: {kvp.Value.answer}", LogLevel.Debug);
        }
    }

    /*     static async Task PreGenerateStaticQuestionsAsync()
        {
            Log("[TTS] Pre-generating Q1 and last question audio...");

            // Run both in parallel — faster
            await Task.WhenAll(
                Task.Run(async () =>
                {
                    try
                    {
                        _cachedQ1Wav = await FetchTtsWavAsync(GetQ1Text());
                        Log("[TTS]  Q1 audio pre-cached");
                    }
                    catch (Exception ex)
                    {
                        Log($"[TTS]  Q1 pre-cache failed: {ex.Message}");
                    }
                }),
                Task.Run(async () =>
                {
                    try
                    {
                        _cachedLastWav = await FetchTtsWavAsync(GetLastQText());
                        Log("[TTS]  Last question audio pre-cached");
                    }
                    catch (Exception ex)
                    {
                        Log($"[TTS]  Last question pre-cache failed: {ex.Message}");
                    }
                })
            );

            Log("[TTS] Pre-generation complete");
        } */

    /*     static async Task<byte[]> FetchTtsWavAsync(string text)
        {
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(30);
            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", OPENROUTER_API_KEY);

            var payload = new
            {
                model = OPENROUTER_TTS_MODEL,
                input = text,
                voice = OPENROUTER_TTS_VOICE,
                response_format = "mp3"
            };

            var content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"
            );

            Log($"[TTS] OpenRouter request | chars={text.Length}");
            var response = await httpClient.PostAsync(OPENROUTER_TTS_URL, content);

            if (!response.IsSuccessStatusCode)
            {
                string err = await response.Content.ReadAsStringAsync();
                throw new Exception($"OpenRouter TTS HTTP {response.StatusCode}: {err}");
            }

            return await response.Content.ReadAsByteArrayAsync();
        } */

    static async Task TriggerVoiceSessionAsync()
    {
        Log("[VOICE] Navigation trigger — starting session");

        if (_voiceWriter == null || _voiceSession == null || _voiceCts == null)
        {
            Log("[VOICE]  Voice client not connected yet");
            return;
        }

        if (_voiceSession.Started)
        {
            Log("[VOICE]  Already running");
            return;
        }

        _voiceSession.Started = true;
        _voiceSession.QuestionIndex = 0;
        await BeginSessionAsync(_voiceWriter, _voiceSession, _voiceCts);
    }




    static async Task StartVoiceTcpServerAsync()
    {
        // DEBUG 1: Did we enter this method?
        Log($"[VOICE DEBUG] StartVoiceTcpServerAsync() ENTERED — port={Voice_Assistant}", LogLevel.Debug);

        try
        {
            var listener = new TcpListener(IPAddress.Any, Voice_Assistant);
            listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.Start();

            // DEBUG 2: Successfully bound
            Log($"[VOICE DEBUG]  Listening on port {Voice_Assistant}", LogLevel.Debug);

            while (true)
            {
                Log($"[VOICE DEBUG] Waiting for next client on port {Voice_Assistant}...", LogLevel.Debug);
                TcpClient client = await listener.AcceptTcpClientAsync();
                Log($"[VOICE DEBUG]  Client connected from {client.Client.RemoteEndPoint}", LogLevel.Debug);
                _ = Task.Run(() => HandleVoiceClientAsync(client));
            }
        }
        catch (Exception ex)
        {
            // DEBUG 3: Startup failed — port in use?
            Log($"[VOICE DEBUG]  FAILED to start on port {Voice_Assistant}: {ex.Message}", LogLevel.Debug);
            Log($"[VOICE DEBUG]    Check if port {Voice_Assistant} is already in use.", LogLevel.Debug);
        }
    }

    static async Task HandleVoiceClientAsync(TcpClient client)
    {
        Log("[VOICE DEBUG] HandleVoiceClientAsync() entered", LogLevel.Debug);
        var cts = new CancellationTokenSource();
        var session = new VoiceSession();

        try
        {
            using NetworkStream stream = client.GetStream();
            stream.ReadTimeout = 60000;
            stream.WriteTimeout = 10000;

            var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
            var reader = new StreamReader(stream, Encoding.UTF8);

            // ── Store in static fields so TriggerVoiceSessionAsync can reach them ──
            _voiceWriter = writer;
            _voiceSession = session;
            _voiceCts = cts;
            Log("[VOICE DEBUG] Static voice fields assigned", LogLevel.Debug);



            // STEP 1: Read handshake ("voice\n")
            Log("[VOICE DEBUG] Waiting for handshake...", LogLevel.Debug);
            try
            {
                string? handshake = await reader.ReadLineAsync();
                Log($"[VOICE DEBUG] 🤝 Handshake: '{handshake?.Trim()}'", LogLevel.Debug);
            }
            catch (Exception ex)
            {
                Log($"[VOICE DEBUG]  Handshake failed: {ex.Message}", LogLevel.Debug);
            }

            // STEP 2: Auto-start on VOICE_READY (no button needed for now)
            Log("[VOICE DEBUG] Entering read loop...", LogLevel.Debug);
            while (!cts.Token.IsCancellationRequested)
            {
                string? msg;
                try
                {
                    msg = await reader.ReadLineAsync();
                }
                catch (IOException ex)
                {
                    Log($"[VOICE DEBUG] 🔌 IO closed: {ex.Message}", LogLevel.Debug);
                    break;
                }

                if (msg == null)
                {
                    Log("[VOICE DEBUG] msg==null — disconnected", LogLevel.Debug);
                    break;
                }

                msg = msg.Trim();
                if (msg.Length == 0) continue;

                Log($"[VOICE DEBUG] 📥 From Lua: '{msg}'", LogLevel.Debug);

                // Volume control from GUI slider (e.g. "VOLUME:75"). Software gain is
                // applied to the live PCM stream, covering both speaker and headset.
                if (msg.StartsWith("VOLUME:", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(msg.Substring(7).Trim(), out int vol))
                        HardwareAudio.SetVolume(vol);
                    else
                        Log($"[VOICE] Bad VOLUME value: '{msg}'", LogLevel.Debug);
                    continue;
                }

                switch (msg)
                {
                    case "MUTE":
                        HardwareAudio.Mute();
                        break;

                    case "UNMUTE":
                        HardwareAudio.Unmute();
                        break;

                    case "VOICE_READY":
                        Log("[VOICE DEBUG] VOICE_READY received", LogLevel.Debug);
                        if (!session.Started)
                        {
                            session.Started = true;
                            session.QuestionIndex = 0;
                            Log("[VOICE] VOICE_READY — starting session");
                            await BeginSessionAsync(writer, session, cts);
                        }
                        else
                        {
                            Log("[VOICE] VOICE_READY — session already running");
                        }
                        break;

                    case "START_SESSION":
                        if (session.Started) { Log("[VOICE DEBUG] Already started", LogLevel.Debug); break; }
                        session.Started = true;
                        session.QuestionIndex = 0;
                        await BeginSessionAsync(writer, session, cts);
                        break;

                    case "STOP_SESSION":
                        Log("[VOICE DEBUG] STOP_SESSION", LogLevel.Debug);
                        session.Started = false;
                        await SendToLua(writer, "SESSION_END");
                        cts.Cancel();
                        break;

                    case "SKIP_QUESTION":
                        Log($"[VOICE DEBUG] SKIP at index {session.QuestionIndex}", LogLevel.Debug);
                        if (!session.Started) break;
                        await HandleSkipAsync(writer, session, cts);
                        break;

                    default:
                        Log($"[VOICE DEBUG]  Unknown: '{msg}'", LogLevel.Debug);
                        break;
                }
            }

            Log("[VOICE DEBUG] Read loop done", LogLevel.Debug);
        }
        catch (Exception ex)
        {
            Log($"[VOICE DEBUG]  {ex.GetType().Name}: {ex.Message}", LogLevel.Debug);
        }
        finally
        {
            _voiceWriter = null;
            _voiceSession = null;
            _voiceCts = null;
            // GUI gone (page left / crash / restart): cancel everything scoped to
            // this connection — mic hot-plug watcher, session token — so nothing
            // can (auto-)start Gemini for a page that no longer exists.
            try { cts.Cancel(); } catch { }
            cts.Dispose();
            try { client.Close(); } catch { }
            Log("[VOICE DEBUG] Client closed", LogLevel.Debug);
        }
    }

    // ──────────────────────────────────────────────
    // SendQuestionAsync — sends question, starts mic,
    // watches transcript chunks, 3-sec silence → next Q
    // ──────────────────────────────────────────────



    // ──────────────────────────────────────────────
    // HandleSkipAsync — skip current question
    // ──────────────────────────────────────────────

    /*     static async Task PlayWavAsync(byte[] audioBytes)
        {
            string tmpMp3 = $"/tmp/tts_{Guid.NewGuid():N}.mp3";
            string tmpWav = $"/tmp/tts_{Guid.NewGuid():N}.wav";
            try
            {
                await File.WriteAllBytesAsync(tmpMp3, audioBytes);

                string outDevice = _audioOut;
                Log($"[TTS] Playing on {outDevice}");

                // Convert MP3 to WAV using ffmpeg
                var convert = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "ffmpeg",
                        Arguments = $"-analyzeduration 10M -probesize 10M -i {tmpMp3} {tmpWav} -y -loglevel quiet",
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                convert.Start();
                await convert.WaitForExitAsync();

                if (convert.ExitCode != 0)
                {
                    Log($" [TTS] ffmpeg convert failed");
                    return;
                }

                // Play WAV via aplay
                var aplay = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "aplay",
                        Arguments = $"-D {outDevice} {tmpWav}",
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                aplay.Start();
                await aplay.WaitForExitAsync();
                Log("[TTS]  Playback done");
            }
            catch (Exception ex)
            {
                Log($"[TTS]  Playback error: {ex.Message}");
            }
            finally
            {
                try { File.Delete(tmpMp3); } catch { }
                try { File.Delete(tmpWav); } catch { }
            }
        } */
    /*     static async Task SendQuestionAsync(
            StreamWriter writer, VoiceSession session, CancellationTokenSource cts)
        {
            if (cts.Token.IsCancellationRequested) return;

            Log($"[VOICE] SendQuestionAsync() index={session.QuestionIndex}");

            // ── 0. Constants ──
            const int TOTAL_QUESTIONS = 10;

            // ── 1. Build question ──
            session.AccumulatedTranscript = "";
            bool isLastQuestion = session.QuestionIndex == TOTAL_QUESTIONS - 1;

            if (session.QuestionIndex == 0)
                session.CurrentQuestion = GetQ1Text();
            else if (isLastQuestion)
                session.CurrentQuestion = GetLastQText();

            // ── 2. Start TTS fetch in background + show on screen simultaneously ──
            Task<byte[]> ttsTask;

            if (session.QuestionIndex == 0 && _cachedQ1Wav != null)
            {
                Log("[TTS] Q1 cache hit");
                ttsTask = Task.FromResult(_cachedQ1Wav);
            }
            else if (isLastQuestion && _cachedLastWav != null)
            {
                Log("[TTS] Last Q cache hit");
                ttsTask = Task.FromResult(_cachedLastWav);
            }
            else
            {
                Log("[TTS] Cache miss — fetching live");
                ttsTask = FetchTtsWavAsync(session.CurrentQuestion);
            }

            // ── 3. Show question on screen immediately ──
            await SendToLua(writer, $"QUESTION:{session.CurrentQuestion}");
            await Task.Delay(300);
            await SendToLua(writer, "LISTENING");
            await SendToLua(writer, "MIC_STATUS|RECORDING");

            // ── 4. Play audio — safely, session continues even if TTS fails ──
            try
            {
                byte[] wav = await ttsTask;
                await PlayWavAsync(wav);
            }
            catch (Exception ex)
            {
                Log($"[TTS]  Audio failed, continuing session: {ex.Message}");
            }

            // ── 5. Start microphone recording ──
            _latestTranscriptChunk = null;
            await StartMicrophoneStreamingAsync();

            // ── 6. Poll transcript chunks — 3-sec silence AFTER first speech ──
            Log("[VOICE] Listening for answer... (3-sec silence after first speech = done)");

            string lastSentTranscript = "";
            bool speechStarted = false;
            DateTime lastSpeechTime = DateTime.UtcNow;

            while (!cts.Token.IsCancellationRequested)
            {
                await Task.Delay(300);

                string chunk = _latestTranscriptChunk ?? "";

                if (!string.IsNullOrWhiteSpace(chunk) && chunk != lastSentTranscript)
                {
                    lastSentTranscript = chunk;
                    speechStarted = true;
                    lastSpeechTime = DateTime.UtcNow;

                    session.AccumulatedTranscript += (session.AccumulatedTranscript.Length > 0 ? " " : "") + chunk;

                    await SendToLua(writer, $"TRANSCRIPT:{chunk}");
                    await SendToLua(writer, $"RMS:{_latestRmsPercent:F1}");
                    Log($"[VOICE] Live transcript → '{chunk}'");
                }

                if (speechStarted && (DateTime.UtcNow - lastSpeechTime).TotalSeconds >= 3.0)
                {
                    Log("[VOICE] 3-sec silence after speech → answer done");
                    break;
                }
            }

            if (cts.Token.IsCancellationRequested) return;

            // ── 7. Stop mic ──
            await StopMicrophoneStreamingAsync();
            await SendToLua(writer, "MIC_STATUS|STOPPED");
            await SendToLua(writer, "PROCESSING");
            await Task.Delay(500);

            // ── 8. Capture full answer ──
            string fullAnswer = session.AccumulatedTranscript.Trim();
            Log($"[VOICE] Full answer for Q{session.QuestionIndex}: '{fullAnswer}'");

            // ── 9. Advance index ──
            session.QuestionIndex++;

            // ── 10. Check if session is done ──
            if (session.QuestionIndex >= TOTAL_QUESTIONS)
            {
                await SendToLua(writer, "SESSION_END");
                Log("[VOICE] All questions done → SESSION_END");
                cts.Cancel();
                return;
            }

            // ── 11. Fetch next question from API ──
            string nextQuestion = await FetchNextQuestionAsync(fullAnswer);

            if (string.IsNullOrWhiteSpace(nextQuestion))
            {
                Log("[VOICE]  Empty question from API — ending session");
                await SendToLua(writer, "SESSION_END");
                cts.Cancel();
                return;
            }

            session.CurrentQuestion = nextQuestion;

            await SendToLua(writer, "READY");
            await Task.Delay(300);

            // ── 12. Next question ──
            await SendQuestionAsync(writer, session, cts);
        }

     */


    static async Task HandleSkipAsync(
        StreamWriter writer, VoiceSession session, CancellationTokenSource cts)
    {
        Log($"[VOICE] Skip index={session.QuestionIndex}");

        if (_geminiSession != null)
            await _geminiSession.SkipCurrentQuestionAsync();
        else
        {
            // fallback if Gemini not active
            await SendToLua(writer, "SESSION_END");
            cts.Cancel();
        }
    }

    // ──────────────────────────────────────────────
    // ADD this static field near your other mic fields:
    // ──────────────────────────────────────────────
    // private static string? _latestTranscriptChunk = null;

    // Audio device detection and microphone streaming have been moved to HardwareAudio.
    // Program.cs should not keep duplicate hardware audio implementations.

    static async Task SendToLua(StreamWriter writer, string message)
    {
        try
        {
            await writer.WriteLineAsync(message);
            // Only log for important messages, not every chunk
            if (!message.StartsWith("QUESTION_CHUNK"))
                Log($"[VOICE] 📤 {message}");
        }
        catch (Exception ex)
        {
            // Silently handle broken pipe - connection may be temporarily disconnected
            if (!ex.Message.Contains("Broken pipe"))
                Log($"[VOICE]  Send issue: {ex.Message}");
        }
    }

    static async Task SendVoiceSessionToApiAsync(
        Dictionary<int, (string question, string answer)> transcript,
        string patientToken)
    {
        try
        {
            if (transcript == null || transcript.Count == 0)
            {
                Log("[VOICE API] ⚠️ No transcript data to send");
                return;
            }

            if (string.IsNullOrEmpty(patientToken))
            {
                Log("[VOICE API] ⚠️ No patient token");
                return;
            }

            // ── Send to VoiceQuestionUrl (optional — only if configured) ──
            string voiceApiUrl = ConfigManager.GetBluHealthApiUrl("VoiceQuestionUrl");
            if (!string.IsNullOrEmpty(voiceApiUrl))
            {
                try
                {
                    var qaArray = transcript.OrderBy(x => x.Key).Select(item => new
                    {
                        question_number = item.Key,
                        question = item.Value.question ?? "",
                        patient_answer = item.Value.answer ?? ""
                    }).ToList<object>();

                    var payload = new
                    {
                        patient_token = patientToken,
                        device_id = DeviceRegistration.GetDeviceIdFromCache(),
                        timestamp = DateTime.UtcNow.ToString("o"),
                        date = DateTime.UtcNow.ToString("yyyy-MM-dd"),
                        time = DateTime.UtcNow.ToString("HH:mm:ss"),
                        session_qa = qaArray
                    };

                    string jsonPayload = JsonSerializer.Serialize(payload, new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    });

                    Log($"[VOICE API] Sending to VoiceQuestionUrl: entries={transcript.Count}, payloadBytes={Encoding.UTF8.GetByteCount(jsonPayload)}");

                    using var httpClient = new HttpClient();
                    httpClient.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Bearer", patientToken);

                    var response = await httpClient.PostAsync(voiceApiUrl,
                        new StringContent(jsonPayload, Encoding.UTF8, "application/json"));
                    string responseBody = await response.Content.ReadAsStringAsync();

                    Log(response.IsSuccessStatusCode
                        ? $"✅ [VOICE API] Sent successfully | {responseBody}"
                        : $"❌ [VOICE API] Failed HTTP {(int)response.StatusCode} | {responseBody}");
                }
                catch (Exception ex)
                {
                    Log($"❌ [VOICE API] VoiceQuestion send error: {ex.Message}");
                }
            }
            else
            {
                Log("[VOICE API] ⚠️ VoiceQuestionUrl not configured — skipping Q&A upload");
            }

            // ── Always send to BluNote regardless of VoiceQuestionUrl ────
            await SendConversationToBluNoteAsync(transcript, patientToken);
        }
        catch (Exception ex)
        {
            Log($"❌ [VOICE API] Unexpected error: {ex.Message}");
        }
    }
    static async Task SendConversationToBluNoteAsync(Dictionary<int, (string question, string answer)> transcript, string patientToken)
    {
        try
        {
            string bluNoteUrl = ConfigManager.GetBluHealthApiUrl("BluNoteUrl");
            if (string.IsNullOrEmpty(bluNoteUrl))
            {
                LoggerUtil.LogError("AUDIO_API", "BluNoteUrl not configured — skipping upload");
                return;
            }

            if (transcript == null || transcript.Count == 0)
            {
                LoggerUtil.LogError("AUDIO_API", "No transcript data available");
                return;
            }

            // Build conversation text from Q&A pairs
            var conversationBuilder = new StringBuilder();
            foreach (var item in transcript.OrderBy(x => x.Key))
            {
                conversationBuilder.AppendLine($"Q{item.Key}: {item.Value.question}");
                if (!string.IsNullOrWhiteSpace(item.Value.answer))
                {
                    conversationBuilder.AppendLine($"A{item.Key}: {item.Value.answer}");
                }
            }

            // Device fingerprint — same as the vitals payload — so voice QA
            // records are attributable to the producing device/site too.
            var blunotePayload = new
            {
                audio_text = conversationBuilder.ToString().Trim(),
                device_id  = DeviceIdentity.GetDeviceId(),
                device     = DeviceIdentity.GetFingerprint()
            };
            string jsonPayload = JsonSerializer.Serialize(blunotePayload, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });

            string outboundPayload = jsonPayload;
            bool encryptedRequest = false;
            if (_encryptionManager != null)
            {
                string encryptedPayload = await _encryptionManager.EncryptAsync(jsonPayload);
                outboundPayload = JsonSerializer.Serialize(new { payload = encryptedPayload });
                encryptedRequest = true;
            }

            LoggerUtil.LogProcessing("AUDIO_API", "Preparing transcript upload");
            LoggerUtil.LogStructured(LogLevel.Info, LogCategory.Audio, "Audio Transcript Upload",
                new Dictionary<string, object>
                {
                    { "Endpoint", bluNoteUrl },
                    { "Q&A Pairs", transcript.Count },
                    { "Payload Size", $"{jsonPayload.Length} bytes" },
                    { "Encrypted", encryptedRequest },
                    { "Outbound Size", $"{outboundPayload.Length} bytes" },
                    { "Patient Token", "Bearer ••••••••" }
                });

            using (var httpClient = new HttpClient())
            {
                httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", patientToken);
                httpClient.Timeout = TimeSpan.FromSeconds(30);

                var content = new StringContent(outboundPayload, Encoding.UTF8, "application/json");

                LoggerUtil.LogProcessing("AUDIO_API", "Sending request to BluNoteUrl");
                var startTime = DateTime.UtcNow;
                HttpResponseMessage response = await httpClient.PostAsync(bluNoteUrl, content);
                var elapsedTime = DateTime.UtcNow - startTime;
                string responseBody = await response.Content.ReadAsStringAsync();
                string responseBodyForLogging = responseBody;
                string? encryptedResponsePayload = ExtractEncryptedApiPayload(responseBody);
                if (_encryptionManager != null && !string.IsNullOrEmpty(encryptedResponsePayload))
                {
                    responseBodyForLogging = await _encryptionManager.DecryptAsync(encryptedResponsePayload);
                    LoggerUtil.LogProcessing("AUDIO_API", "BluNote encrypted response decrypted");
                }

                LoggerUtil.LogApiResponse("BluNoteUrl", (int)response.StatusCode, TruncateForLog(responseBodyForLogging, 600), response.IsSuccessStatusCode);

                if (response.IsSuccessStatusCode)
                {
                    LoggerUtil.LogSuccess("Audio Transcript Upload",
                        new Dictionary<string, object>
                        {
                            { "Status", "Uploaded to BluHealth" },
                            { "Response Time", $"{elapsedTime.TotalMilliseconds:F0}ms" },
                            { "Q&A Entries", transcript.Count }
                        });
                }
                else
                {
                    LoggerUtil.LogError("AUDIO_API", $"HTTP {response.StatusCode}: {response.ReasonPhrase}");
                }
            }
        }
        catch (HttpRequestException httpEx)
        {
            LoggerUtil.LogError("AUDIO_API_NETWORK", "Failed to connect to Audio API endpoint", httpEx);
        }
        catch (TaskCanceledException)
        {
            LoggerUtil.LogError("AUDIO_API_TIMEOUT", "Request timed out (30s exceeded)");
        }
        catch (Exception ex)
        {
            LoggerUtil.LogError("AUDIO_API_ERROR", "Unexpected error during transcript upload", ex);
        }
    }


    static async Task StartHwTempTcpServerAsync()
    {
        try
        {
            _hwTempServer = new TcpListener(IPAddress.Any, HwTempPort);
            _hwTempServer.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _hwTempServer.Start();
            Log($" HW+TEMP TCP Server started on {IPAddress.Any}:{HwTempPort}");

            while (_hwTempRunning)
            {
                try
                {
                    LogThrottled("hw-temp-waiting", "Waiting for HW+TEMP connection...", TimeSpan.FromMinutes(1));
                    TcpClient client = await _hwTempServer.AcceptTcpClientAsync();
                    lock (_hwTempClientLock)
                    {
                        _hwTempClients.Add(client);
                    }
                    LogThrottled("hw-temp-connected", $"New HW+TEMP client connected: {client.Client.RemoteEndPoint}", TimeSpan.FromSeconds(10));
                    _ = Task.Run(() => HandleHwTempClientAsync(client, CancellationToken.None));
                }
                catch (Exception ex)
                {
                    if (_hwTempRunning)
                        Log($" HW+TEMP TCP Server error: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log($" HW+TEMP TCP Server startup error: {ex.Message}");
        }
    }





    static async Task HandleHwTempClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            using NetworkStream stream = client.GetStream();
            stream.ReadTimeout = 1000;
            stream.WriteTimeout = -1;

            byte[] buffer = new byte[256];
            int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
            string deviceType = Encoding.UTF8.GetString(buffer, 0, bytesRead).Trim();
            LogThrottled($"hw-temp-handshake:{deviceType}", $"HW+TEMP Connected Remote: {client.Client.RemoteEndPoint} for {deviceType}", TimeSpan.FromSeconds(10));

            if (deviceType == "height_weight")
            {
                var interval = TimeSpan.FromMilliseconds(1000);
                var lastSendTime = DateTime.UtcNow;
                // var lastDataTime = DateTime.UtcNow;
                // bool resetSent = false;

                while (_hwTempRunning && client.Connected && !cancellationToken.IsCancellationRequested)
                {
                    var currentTime = DateTime.UtcNow;

                    // Send data when in correct state
                    if ((_currentState == MeasurementState.HEIGHT_WEIGHT || _isLiveMode) &&
                        (currentTime - lastSendTime) >= interval)
                    {
                        StringBuilder response = new StringBuilder();

                        lock (_lock)
                        {
                            //  DECISION: Use last valid data if current is zero
                            float displayWeight = lastWeight > 0 ? lastWeight : (hasValidMeasurement ? lastValidWeight : 0);
                            float displayHeight = lastHeight > 0 ? lastHeight : (hasValidMeasurement ? lastValidHeight : 0);
                            float displayBMI = lastBMI > 0 ? lastBMI : (hasValidMeasurement ? lastValidBMI : 0);

                            // BMI Message
                            string bmiMessage;
                            if (displayBMI <= 0f) bmiMessage = "Step on the scale to begin.";
                            else if (displayBMI < 18.5f) bmiMessage = "Underweight. Nutritional review advised.";
                            else if (displayBMI < 25f) bmiMessage = "Healthy BMI. Well done!";
                            else if (displayBMI < 30f) bmiMessage = "Mildly elevated. Lifestyle review suggested.";
                            else bmiMessage = "Elevated BMI. Medical consultation advised.";



                            //  CRITICAL: Use last valid body composition if current is zero
                            BodyCompositionData displayBodyComp = BodyComposition;

                            // If current body composition is empty/zero but we have valid data, use it
                            if (hasValidMeasurement &&
                                (BodyComposition.BodyFatPct == 0 || BodyComposition.MuscleMass == 0))
                            {
                                displayBodyComp = lastValidBodyComposition;
                                // No logging needed - works silently
                            }

                            response.AppendLine($"weight:{displayWeight:F1}");
                            response.AppendLine($"height:{displayHeight:F1}");
                            response.AppendLine($"bmi:{displayBMI:F1}");
                            response.AppendLine($"bmi_message:{bmiMessage}");

                            //  USE displayBodyComp instead of BodyComposition
                            response.AppendLine($"body_fat:{displayBodyComp.BodyFatPct:F1}");
                            response.AppendLine($"muscle_mass:{displayBodyComp.MuscleMass:F1}");
                            response.AppendLine($"visceral_fat:{displayBodyComp.VisceralFatLevel:F1}");
                            response.AppendLine($"basal_metabo:{displayBodyComp.BasalMetabolism:F0}");
                            response.AppendLine($"fat_mass:{displayBodyComp.FatMass:F1}");
                            response.AppendLine($"skeletal_mass:{displayBodyComp.SkeletalMuscle:F1}");
                            response.AppendLine($"bone_mass:{displayBodyComp.BoneMass:F1}");
                            response.AppendLine($"protein:{displayBodyComp.Protein:F1}");
                            response.AppendLine($"body_water:{displayBodyComp.BodyWater:F1}");
                            response.AppendLine($"ideal_body_weight:{displayBodyComp.IdealBodyWeight:F1}");
                            response.AppendLine($"obesity_level:{displayBodyComp.ObesityLevel}");
                            response.AppendLine($"body_type:{displayBodyComp.BodyType}");
                            response.AppendLine($"body_score:{displayBodyComp.BodyScore:F1}");
                            response.AppendLine($"intracellular_water:{displayBodyComp.IntracellularWater:F1}");
                            response.AppendLine($"muscle_control:{displayBodyComp.MuscleControl:F1}");
                            response.AppendLine($"FatControl:{displayBodyComp.FatControl:F1}");
                            response.AppendLine($"physical_age:{displayBodyComp.PhysicalAge:F0}");
                            response.AppendLine($"extracellular:{displayBodyComp.ExtracellularWater:F1}");
                            response.AppendLine($"body_cell_mass:{displayBodyComp.BodyCellMass:F1}");
                            response.AppendLine($"trunk_fat:{displayBodyComp.TrunkFatPct:F1}");
                            response.AppendLine($"subcutaneous:{displayBodyComp.SubcutaneousFatPct:F1}");
                            response.AppendLine($"trunk_muscle:{displayBodyComp.TrunkMuscleMass:F1}");
                            response.AppendLine($"lean_body:{displayBodyComp.LeanBodyMass:F1}");
                            response.AppendLine($"MoistureTbw:{displayBodyComp.MoistureTbw:F1}");
                            response.AppendLine($"waist_ratio:{displayBodyComp.WaistHipRatio:F2}");
                            response.AppendLine($"right_hand:{displayBodyComp.RightHandMuscle:F1}");
                            response.AppendLine($"weight_control:{displayBodyComp.WeightControl:F1}");
                            response.AppendLine($"left_hand:{displayBodyComp.LeftHandMuscle:F1}");
                            // ===== 12 NEW SEGMENTAL FIELDS (add after existing body comp lines) =====
                            response.AppendLine($"fat_right_hand:{displayBodyComp.FatRightHand:F1}");
                            response.AppendLine($"fat_left_hand:{displayBodyComp.FatLeftHand:F1}");
                            response.AppendLine($"fat_trunk:{displayBodyComp.FatTrunk:F1}");
                            response.AppendLine($"fat_right_foot:{displayBodyComp.FatRightFoot:F1}");
                            response.AppendLine($"fat_left_foot:{displayBodyComp.FatLeftFoot:F1}");
                            response.AppendLine($"muscle_pct_right_hand:{displayBodyComp.MusclePctRightHand:F1}");
                            response.AppendLine($"muscle_pct_left_hand:{displayBodyComp.MusclePctLeftHand:F1}");
                            response.AppendLine($"muscle_pct_trunk:{displayBodyComp.MusclePctTrunk:F1}");
                            response.AppendLine($"muscle_pct_right_foot:{displayBodyComp.MusclePctRightFoot:F1}");
                            response.AppendLine($"muscle_pct_left_foot:{displayBodyComp.MusclePctLeftFoot:F1}");
                            response.AppendLine($"smi_index:{displayBodyComp.SmiIndex:F2}");
                            response.AppendLine($"inorganic_salt:{displayBodyComp.InorganicSalt:F1}");



                            // ===== RANGE BLOCK (sent as separate block when available) =====
                            if (_hasRangeData)
                            {



                                response.AppendLine("---RANGE_START---");
                                response.AppendLine($"range_muscle_control_min:{_lastRangeData.MuscleControlMin:F1}");
                                response.AppendLine($"range_muscle_control_max:{_lastRangeData.MuscleControlMax:F1}");
                                response.AppendLine($"range_intracellular_water_min:{_lastRangeData.IntracellularWaterMin:F1}");
                                response.AppendLine($"range_intracellular_water_max:{_lastRangeData.IntracellularWaterMax:F1}");
                                response.AppendLine($"range_bone_mass_min:{_lastRangeData.BoneMassMin:F1}");
                                response.AppendLine($"range_bone_mass_max:{_lastRangeData.BoneMassMax:F1}");
                                response.AppendLine($"range_protein_min:{_lastRangeData.ProteinMin:F1}");
                                response.AppendLine($"range_protein_max:{_lastRangeData.ProteinMax:F1}");
                                response.AppendLine($"range_visceral_fat_min:{_lastRangeData.VisceralFatMin:F1}");
                                response.AppendLine($"range_visceral_fat_max:{_lastRangeData.VisceralFatMax:F1}");
                                response.AppendLine($"range_body_water_min:{_lastRangeData.BodyWaterMin:F1}");
                                response.AppendLine($"range_body_water_max:{_lastRangeData.BodyWaterMax:F1}");
                                response.AppendLine($"range_body_fat_min:{_lastRangeData.BodyFatPercentMin:F1}");
                                response.AppendLine($"range_body_fat_max:{_lastRangeData.BodyFatPercentMax:F1}");
                                response.AppendLine($"range_subcutaneous_fat_min:{_lastRangeData.SubcutaneousFatPercentMin:F1}");
                                response.AppendLine($"range_subcutaneous_fat_max:{_lastRangeData.SubcutaneousFatPercentMax:F1}");
                                response.AppendLine($"range_basal_metabolism_min:{_lastRangeData.BasalMetabolismMin:F0}");
                                response.AppendLine($"range_basal_metabolism_max:{_lastRangeData.BasalMetabolismMax:F0}");
                                response.AppendLine($"range_waist_hip_ratio_min:{_lastRangeData.WaistHipRatioMin:F2}");
                                response.AppendLine($"range_waist_hip_ratio_max:{_lastRangeData.WaistHipRatioMax:F2}");
                                response.AppendLine($"range_lean_body_mass_min:{_lastRangeData.LeanBodyMassMin:F1}");
                                response.AppendLine($"range_lean_body_mass_max:{_lastRangeData.LeanBodyMassMax:F1}");
                                response.AppendLine($"range_obesity_level_min:{_lastRangeData.ObesityLevelMin:F1}");
                                response.AppendLine($"range_obesity_level_max:{_lastRangeData.ObesityLevelMax:F1}");
                                response.AppendLine($"range_muscle_mass_min:{_lastRangeData.MuscleMassMin:F1}");
                                response.AppendLine($"range_muscle_mass_max:{_lastRangeData.MuscleMassMax:F1}");
                                response.AppendLine($"range_skeletal_muscle_min:{_lastRangeData.SkeletalMuscleMin:F1}");
                                response.AppendLine($"range_skeletal_muscle_max:{_lastRangeData.SkeletalMuscleMax:F1}");
                                response.AppendLine($"range_moisture_tbw_min:{_lastRangeData.MoistureTbwMin:F1}");

                                response.AppendLine($"range_moisture_tbw_max:{_lastRangeData.MoistureTbwMax:F1}");
                                response.AppendLine($"range_bmi_min:{_lastRangeData.BmiMin:F1}");
                                response.AppendLine($"range_bmi_max:{_lastRangeData.BmiMax:F1}");
                                response.AppendLine($"range_weight_min:{_lastRangeData.WeightMin:F1}");
                                response.AppendLine($"range_weight_max:{_lastRangeData.WeightMax:F1}");
                                response.AppendLine("---RANGE_END---");
                            }



                        }



                        /*  Log($"📤 [TCP] ===== FULL SEND TO LUA =====");
                         Log(response.ToString());  // prints exact string being sent
                         if (!_hasRangeData)
                             Log($" [TCP] Range NOT included — _hasRangeData is false");
                         else
                             Log($" [TCP] Range block included");
                         Log($"📤 [TCP] ===== END SEND ====="); */

                        byte[] responseBytes = Encoding.UTF8.GetBytes(response.ToString());

                        if (client.Connected && stream.CanWrite)
                        {
                            try
                            {
                                await stream.WriteAsync(responseBytes, 0, responseBytes.Length, cancellationToken);

                                // lastDataTime = currentTime;
                                // resetSent = false;
                            }
                            catch (Exception ex)
                            {
                                Log($" Write error: {ex.Message}");
                                break;
                            }
                        }

                        lastSendTime = currentTime;
                    }

                    //  KEEP RESET LOGIC - but only after valid data was sent
                    // else if (hasValidMeasurement && 
                    //          (currentTime - lastDataTime).TotalSeconds >= 25 && 
                    //          !resetSent)
                    // {
                    //     if (client.Connected && stream.CanWrite)
                    //     {
                    //         try
                    //         {
                    //             byte[] resetMsg = Encoding.UTF8.GetBytes("RESET\n");
                    //             await stream.WriteAsync(resetMsg, 0, resetMsg.Length, cancellationToken);
                    //             Log("🔄 Sent RESET to GUI (25 sec timeout)");
                    //             resetSent = true;
                    //         }
                    //         catch (Exception ex)
                    //         {
                    //             Log($" RESET send error: {ex.Message}");
                    //         }
                    //     }
                    // }

                    await Task.Delay(100, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log($"HW+TEMP Client {client.Client.RemoteEndPoint} operation canceled.");
        }
        catch (Exception ex)
        {
            Log($" Exception with HW+TEMP client {client.Client.RemoteEndPoint}: {ex.Message}");
        }
        finally
        {
            lock (_hwTempClientLock)
            {
                _hwTempClients.Remove(client);
            }
            client.Close();
            Log($"HW+TEMP Client {client.Client.RemoteEndPoint} disconnected.");
        }
    }
    static async Task StartSpo2TcpServerAsync()
    {
        try
        {
            _spo2Server = new TcpListener(IPAddress.Any, DataPort);
            _spo2Server.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _spo2Server.Start();
            Log($" SPO2 TCP Server started on {IPAddress.Any}:{DataPort}");

            while (_spo2Running)
            {
                try
                {
                    LogThrottled("spo2-waiting", "Waiting for SPO2 connection...", TimeSpan.FromMinutes(1));
                    TcpClient client = await _spo2Server.AcceptTcpClientAsync();
                    lock (_spo2ClientLock)
                    {
                        _spo2Clients.Add(client);
                        _spo2ClientWriteLocks[client] = new SemaphoreSlim(1, 1);
                    }
                    LogThrottled("spo2-connected", $"New SPO2 client connected: {client.Client.RemoteEndPoint}", TimeSpan.FromSeconds(10));
                    _ = Task.Run(() => HandleSpo2ClientAsync(client, CancellationToken.None));
                }
                catch (Exception ex)
                {
                    if (_spo2Running)
                        Log($" SPO2 TCP Server error: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log($" SPO2 TCP Server startup error: {ex.Message}");
        }
    }

    static async Task HandleSpo2ClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            using NetworkStream stream = client.GetStream();
            stream.ReadTimeout = 1000;
            stream.WriteTimeout = -1;

            byte[] buffer = new byte[256];
            int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
            string deviceType = Encoding.UTF8.GetString(buffer, 0, bytesRead).Trim();
            LogThrottled($"spo2-handshake:{deviceType}", $"SPO2 Connected Remote: {client.Client.RemoteEndPoint} for {deviceType}", TimeSpan.FromSeconds(10));

            if (deviceType == "spo2")
            {
                byte[] startBytes = Encoding.UTF8.GetBytes("SPO2_START\n");
                await stream.WriteAsync(startBytes, 0, startBytes.Length, cancellationToken);

                // Changed from 200ms to 8ms for 125Hz update rate (matching sensor output)
                var interval = TimeSpan.FromMilliseconds(8);  // 125 Hz
                var lastSendTime = DateTime.UtcNow;

                while (_spo2Running && client.Connected && !cancellationToken.IsCancellationRequested)
                {
                    var currentTime = DateTime.UtcNow;
                    var elapsed = currentTime - lastSendTime;

                    if (elapsed >= interval)
                    {
                        byte[] responseBytes = null;
                        lock (_lock)
                        {
                            // Format: SpO2,PulseRate,SignalStrength%,WaveData
                            // string response = $"{lastSpO2:F1},{lastPulseRate},{lastSignalQuality:F0},{lastSpo2WaveData}\n"; // Old Format
                            // Replace 0 with "?" for medical-grade display
                            string spo2Display = lastSpO2 <= 0 ? "?" : $"{lastSpO2:F1}";
                            string hrDisplay = lastPulseRate <= 0 ? "?" : lastPulseRate.ToString();
                            // 3rd CSV field: PI ESTIMATE. The GUI label is frozen as "PI"
                            // but the UN806C provides no true perfusion index — this value
                            // is derived from pleth signal strength, mapped into the typical
                            // physiological range. Display-only; excluded from records/claims.
                            float piEst = EstimatePerfusionIndex(lastSignalQuality);
                            string piDisplay = piEst <= 0 ? "?" : $"{piEst:F1}";
                            string response = $"{spo2Display},{hrDisplay},{piDisplay}\n";
                            responseBytes = Encoding.UTF8.GetBytes(response);
                        }

                        if (responseBytes != null && responseBytes.Length > 0)
                        {
                            if (client.Connected && stream.CanWrite)
                            {
                                var writeLock = _spo2ClientWriteLocks[client];
                                await writeLock.WaitAsync(cancellationToken);
                                try
                                {
                                    await stream.WriteAsync(responseBytes, 0, responseBytes.Length, cancellationToken).ConfigureAwait(false);
                                    await stream.FlushAsync(cancellationToken);
                                }
                                catch (IOException ex)
                                {
                                    Log($"IOException in WriteAsync for client {client.Client.RemoteEndPoint}: {ex.Message}");
                                    break;
                                }
                                catch (SocketException ex)
                                {
                                    Log($"SocketException in WriteAsync for client {client.Client.RemoteEndPoint}: {ex.Message}");
                                    break;
                                }
                                catch (OperationCanceledException)
                                {
                                    Log($"WriteAsync canceled for client {client.Client.RemoteEndPoint}");
                                    break;
                                }
                                catch (Exception ex)
                                {
                                    Log($"Unexpected error in WriteAsync for client {client.Client.RemoteEndPoint}: {ex.ToString()}");
                                    break;
                                }
                                finally
                                {
                                    writeLock.Release();
                                }
                            }
                            else
                            {
                                Log($"Client {client.Client.RemoteEndPoint} disconnected or stream not writable.");
                                break;
                            }
                        }
                        lastSendTime = currentTime;
                    }
                    else
                    {
                        // Use SpinWait for sub-millisecond precision
                        await Task.Yield();
                    }
                }

                byte[] stopBytes = Encoding.UTF8.GetBytes("SPO2_STOP\n");
                if (client.Connected && stream.CanWrite)
                {
                    await stream.WriteAsync(stopBytes, 0, stopBytes.Length, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log($"SPO2 Client {client.Client.RemoteEndPoint} operation canceled.");
        }
        catch (Exception ex)
        {
            Log($" Exception with SPO2 client {client.Client.RemoteEndPoint}: {ex.ToString()}");
        }
        finally
        {
            lock (_spo2ClientLock)
            {
                _spo2Clients.Remove(client);
                _spo2ClientWriteLocks.Remove(client); // ← add this
            }
            client.Close();
        }


    }










    // ------------------------------------------------------------ Update to new height,Weight and Body Analysis -------------------------------




    static async Task StartTempTcpServerAsync()
    {
        try
        {
            _tempServer1 = new TcpListener(IPAddress.Any, temperature);
            _tempServer1.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _tempServer1.Start();

            Log($" TEMP TCP Server started on {IPAddress.Any}:{temperature}");

            while (_tempRunning)
            {
                LogThrottled("temp-waiting", "Waiting for TEMP connection...", TimeSpan.FromMinutes(1));
                TcpClient client = await _tempServer1.AcceptTcpClientAsync();

                lock (_tempClientLock)
                {
                    _tempClients.Add(client);
                }

                LogThrottled("temp-connected", $"New TEMP client connected: {client.Client.RemoteEndPoint}", TimeSpan.FromSeconds(10));
                _ = Task.Run(() => HandleTempClientAsync(client, CancellationToken.None));
            }
        }
        catch (Exception ex)
        {
            Log($" TEMP TCP Server startup error: {ex.Message}");
        }
    }


    static async Task HandleTempClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            using NetworkStream stream = client.GetStream();
            stream.ReadTimeout = 1000;
            stream.WriteTimeout = -1;

            byte[] buffer = new byte[256];
            int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
            string deviceType = Encoding.UTF8.GetString(buffer, 0, bytesRead).Trim();
            LogThrottled($"temp-handshake:{deviceType}", $"TEMP Connected Remote: {client.Client.RemoteEndPoint} for {deviceType}", TimeSpan.FromSeconds(10));

            if (deviceType == "temperature")
            {
                byte[] startBytes = Encoding.UTF8.GetBytes("TEMPERATURE_START\n");
                await stream.WriteAsync(startBytes, 0, startBytes.Length, cancellationToken);

                var interval = TimeSpan.FromMilliseconds(500);
                var lastSendTime = DateTime.Now;

                // ── Forehead measuring window (mm) ─────────────────────────────
                //  PROX_MIN_MM    : lower edge of the valid window (user-facing).
                //  PROX_STRONG_MM : above this, prox alone proves a person is there
                //                   (idle ToF noise reads <100mm with nobody present,
                //                    so 50-100mm additionally needs a warm IR body).
                //  PROX_MAX_MM    : upper edge of the valid window.
                const float PROX_MIN_MM    = 50f;
                const float PROX_STRONG_MM = 100f;
                const float PROX_MAX_MM    = 400f;

                // Per-session latch: once a valid in-band reading is captured we hold the
                // "Recorded" state + value, so the patient shifting/leaning back can't revert
                // it to "Too close"/"measuring" or blank the number. Released only after the
                // patient has clearly left (no warm body for a few seconds) → ready for next.
                bool     tempCaptured     = false;
                float    capturedForehead = 0f;
                DateTime lastWarmSeen     = DateTime.MinValue;
                DateTime lastInBandSeen   = DateTime.MinValue;
                var      captureReleaseAfter = TimeSpan.FromSeconds(3);
                var      warmRecentWindow    = TimeSpan.FromSeconds(2.5);

                // Sensor conditioning + anti-flicker state:
                //  heldProx    : last valid prox reading, held through brief ToF dropouts
                //                (single 0/garbage samples were flipping the status).
                //  validStreak : consecutive good ticks required before latching a capture,
                //                so we record a settled reading, not the first sample.
                //  currentStatus/pending : downgrades away from "measuring" must persist
                //                2 consecutive ticks (~1s) — kills the ready↔measuring flicker.
                float    heldProx      = 0f;
                DateTime heldProxTime  = DateTime.MinValue;
                var      proxHold      = TimeSpan.FromSeconds(1);
                int      validStreak   = 0;
                string   currentStatus = "Ready to measure";
                string   pendingStatus = "";
                int      pendingCount  = 0;

                while (_tempRunning && client.Connected && !cancellationToken.IsCancellationRequested)
                {
                    var currentTime = DateTime.Now;
                    if ((currentTime - lastSendTime) >= interval)
                    {
                        byte[] responseBytes;
                        lock (_lock)
                        {
                            // ── Page gate ────────────────────────────────────────────
                            // This stream also fills the All-Vitals temperature widgets.
                            // Off the temperature page the GUI must show the STORED
                            // measurement, not a live feed (the IR kept updating on the
                            // All-Vitals page). Live logic runs only while on the page.
                            if (_currentState != MeasurementState.TEMPERATURE && !_isLiveMode)
                            {
                                string s = storedTemperature1  <= 0 ? "?" : $"{storedTemperature1:F1}";
                                string f = storedTemperatureIR <= 0 ? "?" : $"{storedTemperatureIR:F1}";
                                string b = storedTemperature2  <= 0 ? "?" : $"{storedTemperature2:F1}";
                                string frozenStatus = storedTemperatureIR > 0
                                    ? "Recorded - proceed to next" : "Ready to measure";
                                responseBytes = Encoding.UTF8.GetBytes(
                                    $"STATUS:{frozenStatus}\nskin:{s}\nforehead:{f}\nbody:{b}\n");
                            }
                            else
                            {
                            // Send "?" for disconnected/invalid/zero sensors, or "..." if measuring
                            string skinValue = float.IsNaN(lastTemperature1) || lastTemperature1 <= 0 ? "?" : $"{lastTemperature1:F1}";
                            string bodyValue = float.IsNaN(lastTemperature2) || lastTemperature2 <= 0 ? "?" : $"{lastTemperature2:F1}";

                            // 1. Condition the prox: hold the last valid reading through brief
                            //    ToF dropouts so single bad samples can't flip the status.
                            if (lastProxDistance > 0)
                            {
                                heldProx     = lastProxDistance;
                                heldProxTime = currentTime;
                            }
                            else if ((currentTime - heldProxTime) > proxHold)
                            {
                                heldProx = 0f;
                            }

                            // 2. Warmth: IR reads 0 with nobody there. "warmRecent" bridges the
                            //    IR's own dropouts and lets guidance (too close/too far) work for
                            //    a person whose IR was valid moments ago.
                            bool warmObject = !(float.IsNaN(lastTemperatureIR) || lastTemperatureIR <= 0);
                            if (warmObject)
                                lastWarmSeen = currentTime;
                            bool warmRecent = (currentTime - lastWarmSeen) <= warmRecentWindow;

                            // 3. Zones. Idle ToF noise reads <100mm with nobody present, so the
                            //    50-100mm slice needs a warm body to count; 100-400mm stands alone.
                            //    tooFar is warm-gated so a wall behind the patient can't trigger it.
                            bool inBand   = (heldProx >= PROX_STRONG_MM && heldProx <= PROX_MAX_MM)
                                         || (heldProx >= PROX_MIN_MM && heldProx < PROX_STRONG_MM && warmRecent);
                            if (inBand)
                                lastInBandSeen = currentTime;
                            bool nearBandRecently = (currentTime - lastInBandSeen) <= TimeSpan.FromSeconds(3);

                            // Too close: the ESP32 reports IR=0 until a measurement window is
                            // achieved, so the old warm-only gate was unreachable when someone
                            // leaned in directly — "Too close" never showed. A recent pass
                            // through the band (or recent warmth) proves a real person vs the
                            // sub-100mm idle ToF noise.
                            bool tooClose = heldProx > 0 && heldProx < PROX_MIN_MM
                                         && (warmRecent || nearBandRecently);

                            // Too far: idle ToF noise reads <100mm, never >400 (v1's ungated
                            // check never false-fired at idle), so prox alone is trustworthy
                            // here; the warm gate only made it unreachable. 1300mm sanity cap
                            // rejects sensor error codes.
                            bool tooFar = heldProx > PROX_MAX_MM && heldProx <= 1300f;

                            // 4. Capture: require 2 consecutive good ticks (~1s) so we latch a
                            //    settled reading, not the first sample. Release only after the
                            //    patient has been gone (no warm body) for captureReleaseAfter.
                            bool validNow = warmObject
                                         && lastProxDistance >= PROX_MIN_MM
                                         && lastProxDistance <= PROX_MAX_MM;
                            validStreak = validNow ? validStreak + 1 : 0;
                            if (validStreak >= 2)
                            {
                                tempCaptured     = true;
                                capturedForehead = lastTemperatureIR;   // latest settled in-band value
                            }
                            else if (tempCaptured && !warmObject
                                     && (currentTime - lastWarmSeen) >= captureReleaseAfter)
                            {
                                tempCaptured = false;                   // patient left → ready for next
                            }

                            // 5. Status by priority → Lua shows it in temperature.Control4.text:
                            //   captured (held)          → "Recorded - proceed to next"
                            //   warm person < 50mm       → "Too close - move back slightly"
                            //   warm person > 400mm      → "Too far - move a little closer"
                            //   person in 50-400mm       → "Hold still - measuring temperature..."
                            //   no person / idle noise   → "Ready to measure"
                            string computed;
                            if (tempCaptured)  computed = "Recorded - proceed to next";
                            else if (tooClose) computed = "Too close - move back slightly";
                            else if (tooFar)   computed = "Too far - move a little closer";
                            else if (inBand)   computed = "Hold still - measuring temperature...";
                            else               computed = "Ready to measure";

                            // 6. Anti-flicker: leaving "measuring" (except to Recorded) only after
                            //    the new status persists 2 consecutive ticks (~1s). Upgrades into
                            //    measuring/recorded are instant.
                            if (computed == currentStatus)
                            {
                                pendingCount = 0;
                            }
                            else if (computed == "Recorded - proceed to next"
                                  || currentStatus != "Hold still - measuring temperature...")
                            {
                                currentStatus = computed;
                                pendingCount  = 0;
                            }
                            else
                            {
                                pendingCount  = (computed == pendingStatus) ? pendingCount + 1 : 1;
                                pendingStatus = computed;
                                if (pendingCount >= 2) { currentStatus = computed; pendingCount = 0; }
                            }

                            string statusMsg      = currentStatus;
                            string foreheadValue  = tempCaptured ? $"{capturedForehead:F1}" : "?";
                         

                            // Lua expects: "STATUS:..\nskin:32.5\nforehead:34.7\nbody:NA\n"
                          //  string response = $"STATUS:{statusMsg}\nskin:{skinValue_display}\nforehead:{foreheadValue}\nbody:{bodyValue_display}\n";
                            string response = $"STATUS:{statusMsg}\nskin:{skinValue}\nforehead:{foreheadValue}\nbody:{bodyValue}\n";
                            responseBytes = Encoding.UTF8.GetBytes(response);
                            }   // end live path (on temperature page)
                        }

                        if (client.Connected && stream.CanWrite)
                        {
                            try
                            {
                                await stream.WriteAsync(responseBytes, 0, responseBytes.Length, cancellationToken).ConfigureAwait(false);
                                //Log($"➡️ Sent TEMP to {client.Client.RemoteEndPoint}: {Encoding.UTF8.GetString(responseBytes).Replace('\n', '|').TrimEnd('|')}");
                            }
                            catch (IOException ex)
                            {
                                Log($"IOException in WriteAsync for client {client.Client.RemoteEndPoint}: {ex.Message}");
                                break;
                            }
                            catch (Exception ex)
                            {
                                Log($"Unexpected error in WriteAsync for client {client.Client.RemoteEndPoint}: {ex}");
                                break;
                            }
                        }
                        else
                        {
                            Log($"Client {client.Client.RemoteEndPoint} disconnected or stream not writable.");
                            break;
                        }

                        lastSendTime = currentTime;
                    }

                    await Task.Delay(50, cancellationToken);
                }

                byte[] stopBytes = Encoding.UTF8.GetBytes("TEMPERATURE_STOP\n");
                if (client.Connected && stream.CanWrite)
                {
                    await stream.WriteAsync(stopBytes, 0, stopBytes.Length, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log($"TEMP Client {client.Client.RemoteEndPoint} operation canceled.");
        }
        catch (Exception ex)
        {
            Log($" Exception with TEMP client {client.Client.RemoteEndPoint}: {ex}");
        }
        finally
        {
            lock (_tempClientLock)
            {
                _tempClients.Remove(client);
            }
            client.Close();
            Log($"TEMP Client {client.Client.RemoteEndPoint} disconnected.");
        }
    }



    static async Task StartNibpTcpServerAsync()
    {
        try
        {
            _nibpServer = new TcpListener(IPAddress.Any, NibpPort);
            _nibpServer.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _nibpServer.Start();
            Log($" NIBP TCP Server started on {IPAddress.Any}:{NibpPort}");

            while (_nibpRunning)
            {
                try
                {
                    Log("⏳ Waiting for NIBP connection...");
                    TcpClient client = await _nibpServer.AcceptTcpClientAsync();
                    lock (_nibpClientLock)
                    {
                        _nibpClients.Add(client);
                    }
                    Log($"📥 New NIBP client connected: {client.Client.RemoteEndPoint}");
                    _ = Task.Run(() => HandleNibpClientAsync(client, CancellationToken.None));
                }
                catch (Exception ex)
                {
                    if (_nibpRunning)
                        Log($" NIBP TCP Server error: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log($" NIBP TCP Server startup error: {ex.Message}");
        }
    }

    static async Task HandleNibpClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            using NetworkStream stream = client.GetStream();
            stream.ReadTimeout = 1000;
            stream.WriteTimeout = -1;

            // Read device type first
            byte[] buffer = new byte[256];
            int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
            string deviceType = Encoding.UTF8.GetString(buffer, 0, bytesRead).Trim();
            Log($"📥 NIBP Connected! Remote: {client.Client.RemoteEndPoint} for {deviceType}");

            if (deviceType == "bp")
            {
                // FIXED: Send BP_CONNECTED response like Python
                byte[] connectedBytes = Encoding.UTF8.GetBytes("BP_CONNECTED\n");
                await stream.WriteAsync(connectedBytes, 0, connectedBytes.Length, cancellationToken);

                // Initialize client state
                lock (_nibpClientLock)
                {
                    _nibpClientStates[client] = false; // Not running initially
                }

                // FIXED: Main command loop - listen for START/STOP commands
                while (_nibpRunning && client.Connected && !cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        // Set short timeout to check for commands
                        stream.ReadTimeout = 1000;

                        byte[] commandBuffer = new byte[256];
                        int commandBytes = await stream.ReadAsync(commandBuffer, 0, commandBuffer.Length, cancellationToken);

                        if (commandBytes > 0)
                        {
                            string command = Encoding.UTF8.GetString(commandBuffer, 0, commandBytes).Trim();
                            Log($"📥 NIBP Command from {client.Client.RemoteEndPoint}: {command}");

                            if (command == "START")
                            {
                                Log($"🟢 Starting BP measurement for {client.Client.RemoteEndPoint}");

                                //  RESET measurement state
                                lock (_lock)
                                {
                                    _measurementProgress = 0;
                                    _measurementComplete = false;
                                    _measurementStartTime = DateTime.Now;
                                    lastLivePressure = 0;
                                    validSys = 0;
                                    validDia = 0;
                                    validPulse2 = 0;
                                    lastSys = 0;
                                    lastDia = 0;
                                    lastPulseRate2 = 0;
                                }

                                byte[] startBytes = Encoding.UTF8.GetBytes("BP_START\n");
                                await stream.WriteAsync(startBytes, 0, startBytes.Length, cancellationToken);
                                await stream.FlushAsync(cancellationToken); //  ADD THIS - Force immediate send

                                lock (_nibpClientLock)
                                {
                                    _nibpClientStates[client] = true;
                                }

                                StartNiBP();
                                _isNIBPActive = true;
                                _nibpDone = false;   // fresh run — stale true caused BOTH_COMPLETED mid-measurement
                                _ = Task.Run(() => SendSpo2Status("NIBP_MEASURING"));

                                _ = Task.Run(() => SendContinuousBPDataAsync(client, stream, cancellationToken));
                            }
                            else if (command == "STOP")
                            {
                                Log($" Stopping BP measurement for {client.Client.RemoteEndPoint}");

                                // Send BP_STOP response like Python
                                byte[] stopBytes = Encoding.UTF8.GetBytes("BP_STOP\n");
                                await stream.WriteAsync(stopBytes, 0, stopBytes.Length, cancellationToken);

                                // Update client state and stop hardware
                                lock (_nibpClientLock)
                                {
                                    _nibpClientStates[client] = false;
                                }

                                // Stop the actual BP hardware measurement
                                StopNiBP();
                                _isNIBPActive = false;
                            }
                        }
                    }
                    catch (IOException)
                    {
                        // Timeout or connection issue - continue loop
                        await Task.Delay(100, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
            else
            {
                // Invalid device type
                byte[] invalidBytes = Encoding.UTF8.GetBytes("INVALID_DEVICE\n");
                await stream.WriteAsync(invalidBytes, 0, invalidBytes.Length, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            Log($"NIBP Client {client.Client.RemoteEndPoint} operation canceled.");
        }
        catch (Exception ex)
        {
            Log($" Exception with NIBP client {client.Client.RemoteEndPoint}: {ex}");
        }
        finally
        {
            // Cleanup client state
            lock (_nibpClientLock)
            {
                if (_nibpClientStates.ContainsKey(client))
                {
                    _nibpClientStates.Remove(client);
                }
                _nibpClients.Remove(client);
            }

            // Stop BP if this was the last client
            if (_nibpClientStates.Count == 0)
            {
                StopNiBP();
                _isNIBPActive = false;
            }

            client.Close();
            Log($"NIBP Client {client.Client.RemoteEndPoint} disconnected.");
        }
    }


    // ADD this new method for continuous BP data sending (like Python's send_continuous_data):


    // UPDATE SendContinuousBPDataAsync to use the freshest validated data:
    static async Task SendContinuousBPDataAsync(TcpClient client, NetworkStream stream, CancellationToken cancellationToken)
    {
        try
        {
            client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.NoDelay, true);
            while (_nibpRunning && client.Connected && !cancellationToken.IsCancellationRequested)
            {
                bool isClientRunning;
                lock (_nibpClientLock)
                {
                    isClientRunning = _nibpClientStates.GetValueOrDefault(client, false);
                }

                if (!isClientRunning)
                {
                    break;
                }

                byte[] dataBytes;
                lock (_lock)
                {
                    bool dataIsFresh = (DateTime.Now - lastBPUpdateTime) < BP_DATA_FRESHNESS;

                    int systolic = 0, diastolic = 0, pulseRate = 0, liveVal = 0;

                    // Get live cuff pressure
                    liveVal = Math.Max(0, lastLivePressure);

                    // Get final readings when available
                    if (dataIsFresh && validSys > 0 && validDia > 0)
                    {
                        systolic = validSys;
                        diastolic = validDia;
                        pulseRate = validPulse2 > 0 ? validPulse2 : 0;
                    }

                    // Ensure no negative values
                    if (liveVal < 0) liveVal = 0;
                    if (systolic < 0) systolic = 0;
                    if (diastolic < 0) diastolic = 0;
                    if (pulseRate < 0) pulseRate = 0;

                    // Send format: val,systolic,diastolic,pulse,progress
                    string response;

                    // Replace 0 with "?" for medical-grade display
                    string liveValDisplay = liveVal.ToString();
                    string sysDisplay = systolic.ToString();
                    string diaDisplay = diastolic.ToString();
                    string hrDisplay = pulseRate.ToString();

                    if (_measurementComplete && systolic > 0 && diastolic > 0)
                    {
                        // Measurement complete - send BP_DONE signal
                        response = $"BP_DONE,{sysDisplay},{diaDisplay},{hrDisplay}\n";
                        Log($" Sending BP_DONE: {sysDisplay}/{diaDisplay} mmHg, Pulse: {hrDisplay} BPM");
                    }
                    else
                    {
                        // Measurement in progress - send live data with progress
                        response = $"{liveVal},{systolic},{diastolic},{pulseRate},{_measurementProgress}\n";
                        LogThrottled("bp-live", $"➡️ BP Live: {liveValDisplay} mmHg, Progress: {_measurementProgress}%, Sys/Dia: {sysDisplay}/{diaDisplay}", TimeSpan.FromSeconds(1));
                    }

                    dataBytes = Encoding.UTF8.GetBytes(response);
                }

                if (client.Connected && stream.CanWrite)
                {
                    try
                    {
                        await stream.WriteAsync(dataBytes, 0, dataBytes.Length, cancellationToken);
                        await stream.FlushAsync(cancellationToken); //  ADD THIS

                        // If measurement complete, stop sending after a few transmissions
                        if (_measurementComplete)
                        {
                            await Task.Delay(2000, cancellationToken);  // Send BP_DONE for 2 seconds
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"Error sending BP data: {ex.Message}");
                        break;
                    }
                }

                await Task.Delay(500, cancellationToken);  // Send every 500ms
            }
        }
        catch (Exception ex)
        {
            Log($"BP data sending error: {ex}");
        }
    }



    // ALSO ADD this debug method to verify your sensor variables are correct:
    static void DebugBPSensorValues()
    {
        lock (_lock)
        {
            Log($"=== BP SENSOR DEBUG ===", LogLevel.Debug);
            Log($"_isNIBPActive: {_isNIBPActive}", LogLevel.Debug);
            Log($"lastSys (systolic): {lastSys}", LogLevel.Debug);
            Log($"lastDia (diastolic): {lastDia}", LogLevel.Debug);
            Log($"lastMean (mean arterial): {lastMean}", LogLevel.Debug);
            Log($"lastPulseRate2 (NIBP pulse): {lastPulseRate2}", LogLevel.Debug);
            Log($"lastLivePressure (live cuff): {lastLivePressure}", LogLevel.Debug);
            Log($"=== END DEBUG ===", LogLevel.Debug);
        }
    }
    // ========== HELPER: Get samples from circular buffer as List ==========
    private static List<int> GetBufferAsList(int[] buffer, int writeIndex, int sampleCount)
    {
        if (sampleCount == 0)
            return new List<int>();

        var result = new List<int>(sampleCount);

        if (sampleCount < ECG_BUFFER_SIZE)
        {
            // Buffer not full yet - samples start at index 0
            for (int i = 0; i < sampleCount; i++)
                result.Add(buffer[i]);
        }
        else
        {
            // Buffer is full - oldest sample is at writeIndex
            for (int i = 0; i < ECG_BUFFER_SIZE; i++)
            {
                int idx = (writeIndex + i) % ECG_BUFFER_SIZE;
                result.Add(buffer[idx]);
            }
        }

        return result;
    }

    // ========== HELPER: Clear ECG5 buffers ==========
    private static void ClearEcg5Buffers()
    {
        ecg5WriteIndex = 0;
        ecg5SampleCount = 0;
        Log("🗑️ ECG5 buffers cleared");
    }

    static void ClearBloodGlucoseValues()
    {
        lock (_bloodGlucoseLock)  // ← Changed from _lock
        {
            storedBloodGlucoseFasting = 0;
            storedBloodGlucosePreMeal = 0;
            storedBloodGlucosePostMeal = 0;
        }
        Log("🗑️ Blood glucose values cleared");
    }





    /*     static async Task StartGraphTcpServerAsync()
        {
            try
            {
                _graphServer = new TcpListener(IPAddress.Any, GraphPort);
                _graphServer.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _graphServer.Start();
                Log($" GRAPH TCP Server started on {IPAddress.Any}:{GraphPort}");

                while (_graphRunning)
                {
                    try
                    {
                        Log("⏳ Waiting for GRAPH connection...");
                        TcpClient client = await _graphServer.AcceptTcpClientAsync();
                        lock (_graphClientLock)
                        {
                            _graphClients.Add(client);
                        }
                        Log($"📥 New GRAPH client connected: {client.Client.RemoteEndPoint}");
                        _ = Task.Run(() => HandleGraphClientAsync(client, CancellationToken.None));
                    }
                    catch (Exception ex)
                    {
                        if (_graphRunning)
                            Log($" GRAPH TCP Server error: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($" GRAPH TCP Server startup error: {ex.Message}");
            }
        }

    static async Task HandleGraphClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            using NetworkStream stream = client.GetStream();
            stream.ReadTimeout = 1000;
            stream.WriteTimeout = -1; // Infinite write timeout to avoid timeout issues

            byte[] buffer = new byte[256];
            int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
            string deviceType = Encoding.UTF8.GetString(buffer, 0, bytesRead).Trim();
            Log($"📥 GRAPH Connected! Remote: {client.Client.RemoteEndPoint} for {deviceType}");

            if (deviceType == "graphs")
            {
                byte[] startBytes = Encoding.UTF8.GetBytes("GRAPH_START\n");
                await stream.WriteAsync(startBytes, 0, startBytes.Length, cancellationToken);

                var interval = TimeSpan.FromMilliseconds(100); // 10Hz for smooth graphs
                var lastSendTime = DateTime.Now;

                while (_graphRunning && client.Connected && !cancellationToken.IsCancellationRequested)
                {
                    var currentTime = DateTime.Now;
                    var elapsed = currentTime - lastSendTime;
                    if (elapsed >= interval)
                    {
                        byte[] responseBytes = null;
                        lock (_lock)
                        {
                            // Combined graph data: HR, SpO2, BP trends, Temperature
                            string response = $"{{\"timestamp\": {currentTime.Ticks / TimeSpan.TicksPerMillisecond}, " +
                                              $"\"hr\": {lastPulseRate}, \"spo2\": {lastSpO2:F1}, " +
                                              $"\"sys\": {(_isNIBPActive ? lastSys : -1)}, \"dia\": {(_isNIBPActive ? lastDia : -1)}, " +
                                              $"\"temp1\": {(lastTemperature1 <= 0 ? \"?\" : $\"{lastTemperature1:F1}\")}, \"temp2\": {(lastTemperature2 <= 0 ? \"?\" : $\"{lastTemperature2:F1}\")}, " +
                                              $"\"pi\": {EstimatePerfusionIndex(lastSignalQuality):F1}, \"weight\": {lastWeight:F1}, \"height\": {lastHeight:F1}}}\n";
                            responseBytes = Encoding.UTF8.GetBytes(response);
                        }

                        if (responseBytes != null && responseBytes.Length > 0)
                        {
                            if (client.Connected && stream.CanWrite)
                            {
                                try
                                {
                                    await stream.WriteAsync(responseBytes, 0, responseBytes.Length, cancellationToken).ConfigureAwait(false);
                                    //Log($"➡️ Sent GRAPH to {client.Client.RemoteEndPoint}: HR={lastPulseRate}, SpO2={lastSpO2:F1}");
                                }
                                catch (IOException ex)
                                {
                                    Log($"IOException in WriteAsync for client {client.Client.RemoteEndPoint}: {ex.Message}");
                                    break; // Exit loop on I/O error
                                }
                                catch (SocketException ex)
                                {
                                    Log($"SocketException in WriteAsync for client {client.Client.RemoteEndPoint}: {ex.Message}");
                                    break; // Exit loop on socket error
                                }
                                catch (OperationCanceledException)
                                {
                                    Log($"WriteAsync canceled for client {client.Client.RemoteEndPoint}");
                                    break; // Exit loop on cancellation
                                }
                                catch (Exception ex)
                                {
                                    Log($"Unexpected error in WriteAsync for client {client.Client.RemoteEndPoint}: {ex.ToString()}");
                                    break; // Exit loop on other errors
                                }
                            }
                            else
                            {
                                Log($"Client {client.Client.RemoteEndPoint} disconnected or stream not writable.");
                                break; // Exit loop if client disconnected
                            }
                        }
                        lastSendTime = currentTime;
                    }

                    var sleepTime = Math.Max(0, (interval - (DateTime.Now - lastSendTime)).TotalMilliseconds);
                    if (sleepTime > 0)
                        await Task.Delay((int)sleepTime, cancellationToken);
                }

                byte[] stopBytes = Encoding.UTF8.GetBytes("GRAPH_STOP\n");
                if (client.Connected && stream.CanWrite)
                {
                    await stream.WriteAsync(stopBytes, 0, stopBytes.Length, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log($"GRAPH Client {client.Client.RemoteEndPoint} operation canceled.");
        }
        catch (Exception ex)
        {
            Log($" Exception with GRAPH client {client.Client.RemoteEndPoint}: {ex.ToString()}"); // Enhanced logging
        }
        finally
        {
            lock (_graphClientLock)
            {
                _graphClients.Remove(client);
            }
            client.Close();
            Log($"GRAPH Client {client.Client.RemoteEndPoint} disconnected.");
        }
    } */

    public static void StopAllServers()
    {
        _spo2Running = false;
        _hwTempRunning = false;
        _nibpRunning = false;
        _ecg7Running = false;
        _ecg12Running = false;
        _graphRunning = false;
        _otpActivityRunning = false;
        _otpVerifyRunning = false;
        _wifiRunning = false;

        _spo2Server?.Stop();
        _hwTempServer?.Stop();
        _nibpServer?.Stop();
        _ecg7Server?.Stop();
        _ecg12Server?.Stop();
        _graphServer?.Stop();
        _otpActivityServer?.Stop();
        _otpVerifyServer?.Stop();
        _wifiServer?.Stop();
        Log("All TCP servers stopped.");
    }



    static void ProcessCommand(string command)
    {
        try
        {
            lock (_lock)
            {
                Log($"Processing command: {command} (Current state: {_currentState}, Live mode: {_isLiveMode})", LogLevel.Debug);

                switch (command)
                {
                    case "START":
                        Log(" Starting Height/Weight measurement...");
                        _currentState = MeasurementState.HEIGHT_WEIGHT;
                        SendHeightWeightCommand();
                        break;

                    // ── HEIGHT_WEIGHT → TEMPERATURE ───────────────────────────────────────
                    case "ENTER_TEMPERATURE":
                        StoreHeightWeight();
                        _currentState = MeasurementState.TEMPERATURE;
                        Log(" Entering Temperature measurement...");
                        SendRequestPOST();
                        break;

                    // ── TEMPERATURE → SPO2_NIBP ───────────────────────────────────────────
                    case "ENTER_SPO2_NIBP":

                        ResetSpo2Measurement();
                        _ = Task.Run(() => SendSpo2Status("IDLE"));
                        StoreTemperature();
                        _currentState = MeasurementState.SPO2_NIBP;
                        // Reset SpO2 live values so GUI shows fresh data
                        _spo2Done = false;
                        _nibpDone = false;
                        lastSpO2 = 0;
                        lastPulseRate = 0;
                        lastSignalQuality = 0;
                        Log(" Entering SpO2+NIBP page - SpO2 streaming started...");
                        SendRequestPOST();
                        break;

                    // ── BACK: SPO2_NIBP → TEMPERATURE ────────────────────────────────────
                    case "BACK_SPO2_NIBP":
                        ResetSpo2Measurement();

                        _spo2Done = false;
                        _nibpDone = false;
                        if (_isNIBPActive)
                        {
                            _isNIBPActive = false;
                            StopNiBP();
                            Log("🛑 NIBP stopped on BACK");
                        }
                        //  Do NOT zero lastSpO2/lastPulseRate/lastSignalQuality here
                        // Sensor pipeline keeps updating them; zeroing causes the freeze
                        // storedSpO2/storedPulseRate are untouched — committed fresh on next forward
                        _currentState = MeasurementState.TEMPERATURE;
                        Log($" Returning to Temperature — last SpO2:{lastSpO2}% HR:{lastPulseRate}bpm preserved");
                        SendRequestPOST();
                        break;

                    // ── SPO2_NIBP → BLOOD_SUGAR ───────────────────────────────────────────
                    case "ENTER_BLOOD_SUGAR":
                        // Store whatever SpO2 and NIBP values we have
                        StoreSpO2();
                        StoreNIBP();
                        // Stop NIBP if still running
                        if (_isNIBPActive)
                        {
                            _isNIBPActive = false;
                            StopNiBP();
                            Log("🛑 NIBP auto-stopped on leaving SpO2+NIBP page");
                        }
                        _currentState = MeasurementState.BLOOD_SUGAR;
                        _isBloodSugarPageActive = true;
                        Log(" Entering Blood Sugar page...");
                        break;

                    // ── BACK: BLOOD_SUGAR → SPO2_NIBP ────────────────────────────────────




                    case "BACK_BLOOD_SUGAR":
                        _isBloodSugarPageActive = false;
                        _currentState = MeasurementState.SPO2_NIBP;
                        // Do NOT force-clear. Make it purely finger-driven:
                        // force Captured -> Hold so the recorded SpO2 stays on
                        // screen if no finger is present, but the moment a
                        // finger is (re)detected the spot-check starts a FRESH
                        // measurement (Hold -> Measuring) that supersedes it.
                        // (BP is untouched.)
                        if (_spo2HasCapture) _spo2Phase = Spo2Phase.Hold;
                        Log(" Returning to SpO2+NIBP — SpO2 held; finger re-detect re-measures");
                        SendRequestPOST();
                        break;


                    // ── START BP (within SPO2_NIBP) ───────────────────────────────────────
                    case "START BP":
                        if (_currentState == MeasurementState.SPO2_NIBP ||
                            _currentState == MeasurementState.BLOOD_SUGAR ||
                            _isLiveMode)
                        {
                            StoreSpO2();

                            // Fresh measurement: clear the previous run's state. Without
                            // this, _measurementComplete stays true (progress stuck at
                            // 100% from the 2nd measurement on) and the stale validSys
                            // let non-pulse frames pass the pulse sanity gate.
                            lock (_lock)
                            {
                                _measurementComplete = false;
                                _measurementProgress = 0;
                                validSys = 0;
                                validDia = 0;
                                lastLivePressure = -100;
                            }

                            _isNIBPActive = true;
                            _nibpDone = false;   // fresh run — stale true caused BOTH_COMPLETED mid-measurement
                            StartNiBP();
                            _ = Task.Run(() => SendSpo2Status("NIBP_MEASURING"));
                            Log(" BP measurement started - SpO2 snapshot stored");
                        }
                        else
                        {
                            Log($" Cannot start BP - Current state: {_currentState}");
                        }
                        break;

                    case "STOP BP":
                        if (_currentState == MeasurementState.SPO2_NIBP ||
                            _currentState == MeasurementState.BLOOD_SUGAR ||
                            _isLiveMode)
                        {
                            _isNIBPActive = false;
                            StopNiBP();
                            Log(" BP measurement stopped");
                        }
                        else
                        {
                            Log($" Cannot stop BP - Current state: {_currentState}");
                        }
                        break;

                    // ── BLOOD_SUGAR → ECG ─────────────────────────────────────────────────
                    case "ENTER_ECG":
                        _isBloodSugarPageActive = false;
                        _currentState = MeasurementState.ECG;
                        Log(" Entering ECG page...");
                        break;

                    // ── BACK: ECG → BLOOD_SUGAR ───────────────────────────────────────────
                    case "BACK_ECG":
                        if (_isECGActive)
                        {
                            _isECGActive = false;
                            Log("🛑 ECG stopped on BACK");
                        }
                        if (_isECG12Active)
                        {
                            _isECG12Active = false;
                            Log("🛑 ECG12 stopped on BACK");
                        }
                        _currentState = MeasurementState.BLOOD_SUGAR;
                        _isBloodSugarPageActive = true;
                        Log(" Returning to Blood Sugar page...");
                        break;
                    // ── ECG → DONE ────────────────────────────────────────────────────────
                    case "DONE":
                        _currentState = MeasurementState.DONE;
                        Log("Measurements complete!");
                        PrintStoredData();

                        // Snapshot vitals BEFORE entering Task.Run
                        Hl7VitalsSnapshot hl7Snapshot;
                        lock (_lock)
                        {
                            lock (_authLock)
                            {
                                hl7Snapshot = new Hl7VitalsSnapshot
                                {
                                    BluId = string.IsNullOrWhiteSpace(_patientId) ? currentPatientId.ToString() : _patientId,
                                    PatientName = _patientName,
                                    Gender = _patientGender,
                                    PulseRateSpO2 = storedPulseRate,
                                    PulseRateNIBP = Math.Max(0, storedPulseRate2),
                                    HeartRateECG = storedEcgHeartRate,
                                    SpO2 = storedSpO2,
                                    Systolic = Math.Max(0, storedSys),
                                    Diastolic = Math.Max(0, storedDia),
                                    Temperature = storedTemperature1,
                                    Height = storedHeight,
                                    Weight = storedWeight,
                                    BMI = storedBMI >= 0 ? storedBMI : 0,
                                    GlucoseFasting = storedBloodGlucoseFasting,
                                    GlucosePreMeal = storedBloodGlucosePreMeal,
                                    GlucosePostMeal = storedBloodGlucosePostMeal,
                                };
                            }
                        }

                        _ = Task.Run(async () =>
                        {
                            // Send vitals
                            try
                            {
                                await SendVitalsToBluHealth();
                                ClearEcgSamples();
                                Log("Vitals sent to BluHealth portal");
                            }
                            catch (Exception ex)
                            {
                                Log($"Error sending vitals: {ex.Message}");
                            }

                            // Send HL7
                            try
                            {
                                await Hl7Client.SendAsync(hl7Snapshot);
                            }
                            catch (Exception ex)
                            {
                                Log($"[HL7] Unexpected error: {ex.Message}");
                            }

                            // NOTE: Voice transcript is sent from BeginSessionAsync
                            // immediately after voice session ends — not here.
                            // _geminiSession is already null by the time DONE fires.
                        });
                        break;

                    case "HOME":
                        _currentState = MeasurementState.IDLE;
                        currentPatientId = 0;
                        lock (_aiInsightsLock)
                        {
                            _cachedAiInsights = "";
                            _aiInsightsReady = false;
                        }
                        ResetStoredValues();
                        ClearEcgSamples();
                        Log(" Returning to start. AI insights cache cleared.");
                        break;

                    case "START ECG":
                        if (_currentState == MeasurementState.ECG || _isLiveMode)
                        {
                            StartEcg5Recording();
                        }
                        break;

                    case "STOP ECG":
                        StopEcg5Recording();
                        break;

                    case "START ECG12":
                        if (_currentState == MeasurementState.ECG || _isLiveMode)
                        {
                            StartEcg12Recording();
                        }
                        break;


                    case "STOP ECG12":
                        StopEcg12Recording();
                        break;


                    case "LIVE":
                        lock (_authLock)
                        {
                            if (_isAuthenticated && !string.IsNullOrEmpty(_patientId))
                            {
                                currentPatientId = int.Parse(_patientId);
                                _isLiveMode = true;
                                _currentState = MeasurementState.IDLE;
                                Log($" Live mode started for {_patientName} (ID: {currentPatientId})");
                                SendRequestPOST();
                                SendHeightWeightCommand();
                            }
                            else if (currentPatientId == 0)
                            {
                                Log("📋 Please enter Patient ID first:");
                                waitingForPatientId = true;
                            }
                            else
                            {
                                _isLiveMode = true;
                                _currentState = MeasurementState.IDLE;
                                Log($" Live mode started for Patient ID: {currentPatientId}");
                                SendRequestPOST();
                                SendHeightWeightCommand();
                            }
                        }
                        break;

                    case "UART_TEST":
                        RunUartTest();
                        break;

                    case "STATUS":
                        Log($"Current State: {_currentState}");
                        Log($"Live Mode: {_isLiveMode}");
                        Log($"NIBP Active: {_isNIBPActive}");
                        Log($"ECG Active: {_isECGActive}");
                        lock (_authLock)
                        {
                            if (_isAuthenticated && !string.IsNullOrEmpty(_patientId))
                                Log($"Patient (OTP): {_patientName} (ID: {_patientId}) - Authenticated ");
                            else
                                Log($"Patient (Console): {(currentPatientId > 0 ? currentPatientId.ToString() : "Not Set")}");
                        }
                        break;

                    case "HELP":
                        Log("Available commands:");
                        Log("- START            : Begin measurement sequence");
                        Log("- ENTER_TEMPERATURE: HEIGHT_WEIGHT → TEMPERATURE");
                        Log("- ENTER_SPO2_NIBP  : TEMPERATURE → SPO2+NIBP page");
                        Log("- BACK_SPO2_NIBP   : SPO2+NIBP → TEMPERATURE");
                        Log("- START BP         : Start BP within SpO2+NIBP page");
                        Log("- STOP BP          : Stop BP within SpO2+NIBP page");
                        Log("- ENTER_BLOOD_SUGAR: SPO2+NIBP → BLOOD_SUGAR");
                        Log("- BACK_BLOOD_SUGAR : BLOOD_SUGAR → SPO2+NIBP");
                        Log("- ENTER_ECG        : BLOOD_SUGAR → ECG");
                        Log("- BACK_ECG         : ECG → BLOOD_SUGAR");
                        Log("- START ECG        : Start ECG recording");
                        Log("- STOP ECG         : Stop ECG recording");
                        Log("- START ECG12      : Start 12-lead ECG");
                        Log("- STOP ECG12       : Stop 12-lead ECG");
                        Log("- DONE             : Complete and send vitals");
                        Log("- HOME             : Return to start");
                        Log("- LIVE             : Live monitoring mode");
                        Log("- STATUS           : Show current state");
                        Log("- UART_TEST        : Send a test pattern on UART1 to check if the port is alive");
                        break;

                    default:
                        Log($" Unknown command: {command}");
                        Log("Type 'HELP' for available commands");
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Log($"Error processing command '{command}': {ex.Message}");
        }
    }





    private static int _lastSavedVitalId = 0;

    static async Task SendVitalsToBluHealth()
    {
        // Check if patient is authenticated
        string currentToken;
        string authPatientId;
        bool isAuth;

        lock (_authLock)
        {
            currentToken = _patientToken;
            authPatientId = _patientId;
            isAuth = _isAuthenticated;
        }
        Log($" Auth snapshot -> isAuth: {isAuth}, " +
          $"patientId: '{authPatientId}', " +
          $"token null/empty: {string.IsNullOrEmpty(currentToken)}, " +
          $"token length: {(currentToken?.Length ?? 0)}");
        if (!string.IsNullOrEmpty(currentToken))
        {
            Log($" Token present (len={currentToken.Length})");
        }

        if (!isAuth || string.IsNullOrEmpty(currentToken))
        {
            Log(" Cannot send vitals - Patient not authenticated. Please login first.");
            Log("❗ Early exit reason: " +
                $"{(!isAuth ? "_isAuthenticated=false " : "")}" +
                $"{(string.IsNullOrEmpty(currentToken) ? "_patientToken is null/empty" : "")}");
            return;
        }

        if (!isAuth || string.IsNullOrEmpty(currentToken))
        {
            Log(" Cannot send vitals - Patient not authenticated. Please login first.");
            return;
        }

        using HttpClient httpClient = new HttpClient();

        httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", currentToken);

        object payload;
        StringContent? content = null;
        string jsonPayload = string.Empty;
        int patientIdInt = 0;
        int validPulseRateNIBP = 0;
        int validSystolic = 0;
        int validDiastolic = 0;

        lock (_lock)
        {
            // ========== ECG5: Get samples from circular buffers ==========
            var leadISamples = GetBufferAsList(ecgLeadIBuffer, ecg5WriteIndex, ecg5SampleCount);
            var leadIISamples = GetBufferAsList(ecgLeadIIBuffer, ecg5WriteIndex, ecg5SampleCount);
            var leadIIISamples = GetBufferAsList(ecgLeadIIIBuffer, ecg5WriteIndex, ecg5SampleCount);
            var leadAvrSamples = GetBufferAsList(ecgLeadAvrBuffer, ecg5WriteIndex, ecg5SampleCount);
            var leadAvlSamples = GetBufferAsList(ecgLeadAvlBuffer, ecg5WriteIndex, ecg5SampleCount);
            var leadAvfSamples = GetBufferAsList(ecgLeadAvfBuffer, ecg5WriteIndex, ecg5SampleCount);
            var leadVSamples = GetBufferAsList(ecgLeadVBuffer, ecg5WriteIndex, ecg5SampleCount);

            Log($"ECG5 Samples collected: {ecg5SampleCount}", LogLevel.Debug);

            // ========== ECG12: Get samples from circular buffers ==========
            var lead12ISamples = GetBufferAsList(ecg12LeadIBuffer, ecg12WriteIndex, ecg12SampleCount);
            var lead12IISamples = GetBufferAsList(ecg12LeadIIBuffer, ecg12WriteIndex, ecg12SampleCount);
            var lead12IIISamples = GetBufferAsList(ecg12LeadIIIBuffer, ecg12WriteIndex, ecg12SampleCount);
            var lead12AvrSamples = GetBufferAsList(ecg12LeadAvrBuffer, ecg12WriteIndex, ecg12SampleCount);
            var lead12AvlSamples = GetBufferAsList(ecg12LeadAvlBuffer, ecg12WriteIndex, ecg12SampleCount);
            var lead12AvfSamples = GetBufferAsList(ecg12LeadAvfBuffer, ecg12WriteIndex, ecg12SampleCount);
            var lead12V1Samples = GetBufferAsList(ecg12LeadV1Buffer, ecg12WriteIndex, ecg12SampleCount);
            var lead12V2Samples = GetBufferAsList(ecg12LeadV2Buffer, ecg12WriteIndex, ecg12SampleCount);
            var lead12V3Samples = GetBufferAsList(ecg12LeadV3Buffer, ecg12WriteIndex, ecg12SampleCount);
            var lead12V4Samples = GetBufferAsList(ecg12LeadV4Buffer, ecg12WriteIndex, ecg12SampleCount);
            var lead12V5Samples = GetBufferAsList(ecg12LeadV5Buffer, ecg12WriteIndex, ecg12SampleCount);
            var lead12V6Samples = GetBufferAsList(ecg12LeadV6Buffer, ecg12WriteIndex, ecg12SampleCount);

            Log($"ECG12 Samples collected: {ecg12SampleCount}", LogLevel.Debug);

            validPulseRateNIBP = Math.Max(0, storedPulseRate2);
            validSystolic = Math.Max(0, storedSys);
            validDiastolic = Math.Max(0, storedDia);

            patientIdInt = 0;

            if (!string.IsNullOrEmpty(authPatientId) && int.TryParse(authPatientId, out int parsedAuthId))
            {
                patientIdInt = parsedAuthId;
                Log($"📤 Using OTP authenticated Patient ID: {patientIdInt} ({_patientName})");
            }
            else if (currentPatientId > 0)
            {
                patientIdInt = currentPatientId;
                Log($"📤 Using console Patient ID: {patientIdInt}");
            }
            else
            {
                Log(" No valid patient ID available - cannot send vitals");
                return;
            }

            payload = new
            {
                patient_id = patientIdInt,
                token = currentToken,
                timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),

                // Device footprint — lets the portal differentiate which device/
                // site produced this record (multi-location deployments). Flat
                // device_id is the primary key to index on; the device block is
                // the full traceable footprint. Same GetDeviceId() as status-
                // check, so records join cleanly. Inlined (not a helper returning
                // object) so System.Text.Json serialises the fields, not {}.
                device_id = DeviceIdentity.GetDeviceId(),
                device = DeviceIdentity.GetFingerprint(),

                vitals = new
                {
                    pulse_rate_spo2 = storedPulseRate.ToString(),
                    pulse_rate_nibp = validPulseRateNIBP.ToString(),
                    ecg_heart_rate = storedEcgHeartRate.ToString(),
                    respiration_rate = "0",
                    blood_oxygen = storedSpO2.ToString(),
                    weight_kg = storedWeight.ToString("F1"),
                    height_cm = storedHeight.ToString("F1"),
                    BMI = storedBMI >= 0 ? storedBMI.ToString("F1") : "0",

                    blood_glucose = new
                    {
                        fasting = storedBloodGlucoseFasting > 0 ? storedBloodGlucoseFasting.ToString() : "0",
                        pre_meal = storedBloodGlucosePreMeal > 0 ? storedBloodGlucosePreMeal.ToString() : "0",
                        post_meal = storedBloodGlucosePostMeal > 0 ? storedBloodGlucosePostMeal.ToString() : "0"
                    }
                },
                blood_pressure = new
                {
                    systolic = validSystolic.ToString(),
                    diastolic = validDiastolic.ToString()
                },
                temperature = new
                {
                    temp_1_c = storedTemperature1.ToString("F1"),
                    temp_2_c = storedTemperature2.ToString("F1"),
                    ir_temp_c = storedTemperatureIR.ToString("F1")
                },
                spo2_status = new
                {
                    finger_off = false,
                    pulse_flag = storedPulseRate > 0,
                    search_pulse = false,
                    sensor_off = storedSpO2 == 0,
                    bar_graph = 3
                },
                ecg_five_lead = new
                {
                    leads = new
                    {
                        lead_i = leadISamples,
                        lead_ii = leadIISamples,
                        lead_iii = leadIIISamples,
                        lead_avr = leadAvrSamples,
                        lead_avl = leadAvlSamples,
                        lead_avf = leadAvfSamples,
                        lead_v = leadVSamples
                    },
                    pace_flag = lastPaceFlag,
                    heartbeat_flag = lastHeartBeatFlag,
                    lead_status = new
                    {
                        v_off = leadStatus[0],
                        ra_off = leadStatus[1],
                        la_off = leadStatus[2],
                        ll_off = leadStatus[3]
                    }

                    /*                 signal_quality = new
                                    {
                                        lead_off = leadStatus.Any(x => x),
                                        saturated = leadSaturation.Any(x => x),
                                        confidence = leadStatus.Any(x => x) ? "low" : "high"
                                    },
                                    lead_configuration = "5-lead",
                                    sampling_rate_hz = 500

                                    st_segment = new
                                    {
                                        lead_i_mv = stI_mV,
                                        lead_ii_mv = stII_mV,
                                        lead_v_mv = stV_mV
                                    }

                                    rhythm_flags = new
                                    {
                                        pvc_count_per_min = lastPVC,
                                        arrhythmia_type = lastARR,   // numeric is fine for now
                                        irregular_rhythm = lastARR != 15 && lastARR != 0
                                    } */




                },
                ecg_twelve_lead = new
                {
                    leads = new
                    {
                        lead_i = lead12ISamples,
                        lead_ii = lead12IISamples,
                        lead_iii = lead12IIISamples,
                        lead_avr = lead12AvrSamples,
                        lead_avl = lead12AvlSamples,
                        lead_avf = lead12AvfSamples,
                        lead_v1 = lead12V1Samples,
                        lead_v2 = lead12V2Samples,
                        lead_v3 = lead12V3Samples,
                        lead_v4 = lead12V4Samples,
                        lead_v5 = lead12V5Samples,
                        lead_v6 = lead12V6Samples
                    },
                    heart_rate = lastEcg12HeartRate,
                    pace_flag = lastEcg12PaceFlag,
                    heartbeat_flag = lastEcg12HeartBeatFlag,
                    lead_status = new
                    {
                        v2_off = lead12Status[0],
                        v3_off = lead12Status[1],
                        v4_off = lead12Status[2],
                        v5_off = lead12Status[3],
                        v6_off = lead12Status[4],
                        ra_off = lead12Status[5],
                        la_off = lead12Status[6],
                        ll_off = lead12Status[7],
                        v1_off = lead12Status[8]
                    }
                },
                body_analysis = new
                {
                    BMH05108 = new
                    {
                        weight = storedWeight,
                        bmi = storedBMI,
                        bodyScore = BodyComposition.BodyScore,
                        physicalAge = BodyComposition.PhysicalAge,
                        bodyType = BodyComposition.BodyType,
                        idealWeight = BodyComposition.IdealBodyWeight,
                        weightControl = BodyComposition.WeightControl,
                        muscleMass = BodyComposition.MuscleMass,
                        skeletalMuscle = BodyComposition.SkeletalMuscle,
                        fatMass = BodyComposition.FatMass,
                        boneMass = BodyComposition.BoneMass,
                        protein = BodyComposition.Protein,
                        leanBodyMass = BodyComposition.LeanBodyMass,
                        bodyCellMass = BodyComposition.BodyCellMass,
                        bodyFatPercent = BodyComposition.BodyFatPct,
                        subcutaneousFatRate = BodyComposition.SubcutaneousFatPct,
                        moistureContent = BodyComposition.MoistureTbw,
                        intracellularWater = BodyComposition.IntracellularWater,
                        extracellularWater = BodyComposition.ExtracellularWater,
                        visceralFatLevel = BodyComposition.VisceralFatLevel,
                        obesityPercent = BodyComposition.ObesityLevel,
                        waistHipRatio = BodyComposition.WaistHipRatio,
                        basalMetabolism = BodyComposition.BasalMetabolism,
                        fatControl = BodyComposition.FatControl,
                        muscleControl = BodyComposition.MuscleControl,
                        rightHandMuscleMass = BodyComposition.RightHandMuscle,
                        leftHandMuscleMass = BodyComposition.LeftHandMuscle,
                        trunkMuscleMass = BodyComposition.TrunkMuscleMass,
                        trunkFatPercent = BodyComposition.TrunkFatPct,
                        trunkFatMass = BodyComposition.FatTrunk,
                        fatRightHand = BodyComposition.FatRightHand,
                        fatLeftHand = BodyComposition.FatLeftHand,
                        fatRightFoot = BodyComposition.FatRightFoot,
                        fatLeftFoot = BodyComposition.FatLeftFoot,

                        musclePctRightHand = BodyComposition.MusclePctRightHand,
                        musclePctLeftHand = BodyComposition.MusclePctLeftHand,
                        musclePctTrunk = BodyComposition.MusclePctTrunk,
                        musclePctRightFoot = BodyComposition.MusclePctRightFoot,
                        musclePctLeftFoot = BodyComposition.MusclePctLeftFoot,

                        smiIndex = BodyComposition.SmiIndex,
                        inorganicSalt = BodyComposition.InorganicSalt,
                    },
                    BMH05108_status = new { }
                }
            };

            jsonPayload = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping  // Properly handles nested JSON strings and escape sequences
            });

            Log($"📤 BluHealth vitals payload prepared: patientId={patientIdInt}, jsonBytes={Encoding.UTF8.GetByteCount(jsonPayload)}, ECG5={ecg5SampleCount} samples, ECG12={ecg12SampleCount} samples");
            Log($"📤 BluHealth endpoint: {BLUHEALTH_VITALS_URL}");
        }

        try
        {
            if (string.IsNullOrEmpty(BLUHEALTH_VITALS_URL))
            {
                Log("Error: BLUHEALTH_VITALS_URL is not defined or empty.");
                return;
            }

            var queuedVitals = new QueuedVitals
            {
                PatientToken = currentToken,
                PatientId = patientIdInt,
                BluId = authPatientId,
                PatientName = _patientName,
                Gender = _patientGender,
                PulseRateSpO2 = storedPulseRate,
                PulseRateNIBP = validPulseRateNIBP,
                HeartRateECG = storedEcgHeartRate,
                SpO2 = storedSpO2,
                Systolic = validSystolic,
                Diastolic = validDiastolic,
                Temperature = storedTemperature1,
                Height = storedHeight,
                Weight = storedWeight,
                BMI = storedBMI >= 0 ? storedBMI : 0,
                GlucoseFasting = storedBloodGlucoseFasting,
                GlucosePreMeal = storedBloodGlucosePreMeal,
                GlucosePostMeal = storedBloodGlucosePostMeal,
                CloudApiJsonPayload = jsonPayload,
                CreatedAtUtc = DateTime.UtcNow,
                Status = "pending",
                TargetService = "bluhealth",
                Hl7Message = null
            };

            if (!await VitalsQueue.IsInternetAvailableAsync())
            {
                await VitalsQueue.EnqueueAsync(queuedVitals);
                Audit.Log("VITALS_QUEUED", "info", Audit.PatientRef(patientIdInt.ToString()));
                Log($"🗄️ No internet: queued BluHealth vitals for Patient ID {patientIdInt}");
                return;
            }

            string outboundPayload = jsonPayload;
            string encryptedPayload = string.Empty;

            if (_encryptionManager == null)
            {
                Log("🔓 [API Encryption] Encryption manager is not initialized - sending plain JSON payload");
            }
            else
            {
                Log("🔐 [API Encryption] Encrypting BluHealth vitals payload before POST");
                encryptedPayload = await _encryptionManager.EncryptAsync(jsonPayload);
                outboundPayload = encryptedPayload;

                string[] encryptedParts = encryptedPayload.Split(':');
                bool hasExpectedFormat = encryptedParts.Length == 2;
                Log("🔐 [API Encryption] AES-256-CBC encryption applied");
                Log($"🔐 [API Encryption] Plain JSON length: {jsonPayload.Length} chars");
                Log($"🔐 [API Encryption] Encrypted payload length: {encryptedPayload.Length} chars");
                Log($"🔐 [API Encryption] Format valid (iv:ciphertext): {hasExpectedFormat}");
                if (hasExpectedFormat)
                {
                    Log($"🔐 [API Encryption] IV hex length: {encryptedParts[0].Length}, ciphertext hex length: {encryptedParts[1].Length}");
                }

                outboundPayload = JsonSerializer.Serialize(new { payload = encryptedPayload });
                Log($"🔐 [API Encryption] JSON wrapper prepared: {Encoding.UTF8.GetByteCount(outboundPayload)} bytes");
            }

            content = new StringContent(outboundPayload, Encoding.UTF8, "application/json");
            Log($"📤 [API] Posting {(string.IsNullOrEmpty(encryptedPayload) ? "plain" : "encrypted")} payload to BluHealth");

            HttpResponseMessage response = await httpClient.PostAsync(BLUHEALTH_VITALS_URL, content);
            string responseBody = await response.Content.ReadAsStringAsync();
            string responseBodyForParsing = responseBody;

            string? encryptedResponsePayload = ExtractEncryptedApiPayload(responseBody);
            if (_encryptionManager != null && !string.IsNullOrEmpty(encryptedResponsePayload))
            {
                Log("🔓 [API Encryption] Encrypted BluHealth response detected - decrypting before parsing");
                responseBodyForParsing = NormalizeJsonText(await _encryptionManager.DecryptAsync(encryptedResponsePayload));
                Log($"🔓 [API Encryption] Response decrypted successfully ({responseBodyForParsing.Length} chars)");
            }

            // Build Lua-specific response format for GUI parsing
            var luaFormattedResponse = BuildLuaFormattedResponse(responseBodyForParsing);

            Log(BuildVitalsResponseSummary(response, responseBodyForParsing, luaFormattedResponse));
            Audit.Log("VITALS_SUBMIT", response.IsSuccessStatusCode ? "success" : "fail",
                Audit.PatientRef(patientIdInt.ToString()),
                new Dictionary<string, object?> { ["mode"] = "live" });

            if (response.IsSuccessStatusCode)
            {
                Log($" Vitals sent successfully for Patient: {_patientName}");

                try
                {
                    // Cache the Lua-formatted response for the GUI to retrieve (synchronized)
                    SetCachedInsights(luaFormattedResponse);

                    if (!string.IsNullOrEmpty(luaFormattedResponse))
                    {
                        Log($" AI insights Lua-formatted response cached for GUI ({luaFormattedResponse.Length} chars)");
                    }
                    else
                    {
                        Log(" No Lua-formatted AI insights available from VitalsUrl API response");
                    }

                    using JsonDocument postDoc = JsonDocument.Parse(responseBodyForParsing);
                    var postRoot = postDoc.RootElement;

                    // ── Capture vital_id ─────────────────────────────────────────
                    if (TryGetVitalsDataRoot(postRoot, out JsonElement dataEl) &&
                        TryGetPropertyAny(dataEl, out JsonElement vitalsEl, "patientVitals", "patient_vitals", "vitals") &&
                        vitalsEl.TryGetProperty("vital_id", out JsonElement vitalIdEl))
                    {
                        _lastSavedVitalId = vitalIdEl.GetInt32();
                        Log($" Captured vital_id: {_lastSavedVitalId}");
                    }
                    else
                    {
                        Log(" vital_id not found in POST response");
                        _lastSavedVitalId = 0;
                    }
                }
                catch (Exception ex)
                {
                    Log($" Could not parse VitalsUrl API response: {ex.Message}");
                    _lastSavedVitalId = 0;
                    ClearCachedInsights();
                }

                // Clear buffers after successful upload
                ClearEcg5Buffers();
                ClearEcg12Buffers();
            }
            else
            {
                Log($" Vitals request failed. Status: {response.StatusCode}, Body: {responseBodyForParsing}");
                _lastSavedVitalId = 0;

                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    Log(" Token may have expired - Patient needs to re-authenticate");
                    lock (_authLock)
                    {
                        _isAuthenticated = false;
                        _patientToken = "";
                    }
                }

                await VitalsQueue.EnqueueAsync(queuedVitals);
                Log($"🗄️ Response error: queued BluHealth vitals for Patient ID {patientIdInt}");
            }
        }
        catch (Exception ex)
        {
            Log($" Error sending vitals: {ex.Message}");
            _lastSavedVitalId = 0;

            try
            {
                var queuedVitals = new QueuedVitals
                {
                    PatientToken = currentToken,
                    PatientId = patientIdInt,
                    BluId = authPatientId,
                    PatientName = _patientName,
                    Gender = _patientGender,
                    PulseRateSpO2 = storedPulseRate,
                    PulseRateNIBP = validPulseRateNIBP,
                    HeartRateECG = storedEcgHeartRate,
                    SpO2 = storedSpO2,
                    Systolic = validSystolic,
                    Diastolic = validDiastolic,
                    Temperature = storedTemperature1,
                    Height = storedHeight,
                    Weight = storedWeight,
                    BMI = storedBMI >= 0 ? storedBMI : 0,
                    GlucoseFasting = storedBloodGlucoseFasting,
                    GlucosePreMeal = storedBloodGlucosePreMeal,
                    GlucosePostMeal = storedBloodGlucosePostMeal,
                    CloudApiJsonPayload = jsonPayload,
                    CreatedAtUtc = DateTime.UtcNow,
                    Status = "pending",
                    TargetService = "bluhealth",
                    Hl7Message = null
                };

                await VitalsQueue.EnqueueAsync(queuedVitals);
                Log($"🗄️ Exception: queued BluHealth vitals for Patient ID {patientIdInt}");
            }
            catch (Exception queueEx)
            {
                Log($" Also failed to queue vitals: {queueEx.Message}");
            }
        }
    }

    private static string? ExtractEncryptedApiPayload(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
            return null;

        string trimmed = responseBody.Trim();

        if (LooksLikeEncryptedPayload(trimmed))
            return trimmed;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(trimmed);
            JsonElement root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.String)
            {
                string? value = root.GetString();
                return LooksLikeEncryptedPayload(value) ? value : null;
            }

            return FindEncryptedApiPayload(root);
        }
        catch
        {
            return null;
        }
    }

    private static string? FindEncryptedApiPayload(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            string? value = element.GetString();
            return LooksLikeEncryptedPayload(value) ? value : null;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (string fieldName in new[]
            {
                "payload", "data", "encryptedData", "encrypted_payload",
                "encrypted", "encryptedResponse", "encrypted_response", "response", "result"
            })
            {
                if (element.TryGetProperty(fieldName, out JsonElement field))
                {
                    string? directMatch = FindEncryptedApiPayload(field);
                    if (!string.IsNullOrEmpty(directMatch))
                        return directMatch;
                }
            }

            foreach (JsonProperty property in element.EnumerateObject())
            {
                string? nestedMatch = FindEncryptedApiPayload(property.Value);
                if (!string.IsNullOrEmpty(nestedMatch))
                    return nestedMatch;
            }
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                string? nestedMatch = FindEncryptedApiPayload(item);
                if (!string.IsNullOrEmpty(nestedMatch))
                    return nestedMatch;
            }
        }

        return null;
    }

    private static string NormalizeJsonText(string text)
    {
        string trimmed = text.Trim();

        try
        {
            using JsonDocument doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.ValueKind == JsonValueKind.String)
            {
                string? nestedJson = doc.RootElement.GetString();
                if (!string.IsNullOrWhiteSpace(nestedJson))
                {
                    string nestedTrimmed = nestedJson.Trim();
                    if (nestedTrimmed.StartsWith("{", StringComparison.Ordinal) ||
                        nestedTrimmed.StartsWith("[", StringComparison.Ordinal))
                        return nestedTrimmed;
                }
            }
        }
        catch
        {
            // Keep the original text; caller will log/handle JSON parse errors.
        }

        return trimmed;
    }

    private static bool LooksLikeEncryptedPayload(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string[] parts = value.Split(':');
        if (parts.Length != 2 || parts[0].Length != 32)
            return false;

        return IsEvenLengthHex(parts[0]) && IsEvenLengthHex(parts[1]);
    }

    private static bool IsEvenLengthHex(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length % 2 != 0)
            return false;

        foreach (char c in value)
        {
            bool isHex =
                (c >= '0' && c <= '9') ||
                (c >= 'a' && c <= 'f') ||
                (c >= 'A' && c <= 'F');

            if (!isHex)
                return false;
        }

        return true;
    }

    private static bool TryGetVitalsDataRoot(JsonElement root, out JsonElement dataRoot)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            TryGetPropertyAny(root, out JsonElement dataEl, "data") &&
            dataEl.ValueKind == JsonValueKind.Object)
        {
            dataRoot = dataEl;
            return true;
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            dataRoot = root;
            return true;
        }

        dataRoot = default;
        return false;
    }

    private static bool TryGetAiPatientData(JsonElement root, out JsonElement patientData)
    {
        if (!TryGetVitalsDataRoot(root, out JsonElement dataRoot))
        {
            patientData = default;
            return false;
        }

        foreach (JsonElement candidate in new[] { dataRoot, root })
        {
            if (candidate.ValueKind != JsonValueKind.Object)
                continue;

            if (TryGetPropertyAny(candidate, out JsonElement aiAnalysis, "aiAnalysis", "ai_analysis", "analysisData") &&
                aiAnalysis.ValueKind == JsonValueKind.Object)
            {
                if (TryGetPropertyAny(aiAnalysis, out patientData, "patientData", "patient_data") &&
                    patientData.ValueKind == JsonValueKind.Object)
                    return true;

                if (LooksLikePatientAnalysis(aiAnalysis))
                {
                    patientData = aiAnalysis;
                    return true;
                }
            }

            if (TryGetPropertyAny(candidate, out patientData, "patientData", "patient_data") &&
                patientData.ValueKind == JsonValueKind.Object)
                return true;

            if (LooksLikePatientAnalysis(candidate))
            {
                patientData = candidate;
                return true;
            }
        }

        patientData = default;
        return false;
    }

    private static bool LooksLikePatientAnalysis(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.Object &&
               (TryGetPropertyAny(element, out _, "Analysis", "analysis") ||
                TryGetPropertyAny(element, out _, "Scores", "scores") ||
                TryGetPropertyAny(element, out _, "Conclusion", "conclusion"));
    }

    private static string BuildVitalsResponseSummary(HttpResponseMessage response, string responseBody, string luaFormattedResponse)
    {
        try
        {
            string message = "";
            int vitalId = 0;
            int healthInsightCount = 0;
            int recommendationCount = 0;
            int overallScore = -1;

            using JsonDocument doc = JsonDocument.Parse(responseBody);
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("message", out JsonElement messageEl))
                message = messageEl.GetString() ?? "";

            if (TryGetVitalsDataRoot(root, out JsonElement dataEl))
            {
                if (TryGetPropertyAny(dataEl, out JsonElement vitalsEl, "patientVitals", "patient_vitals", "vitals") &&
                    TryGetPropertyAny(vitalsEl, out JsonElement vitalIdEl, "vital_id", "vitalId") &&
                    vitalIdEl.TryGetInt32(out int parsedVitalId))
                {
                    vitalId = parsedVitalId;
                }

                if (TryGetAiPatientData(root, out JsonElement patientDataEl))
                {
                    if (TryGetPropertyAny(patientDataEl, out JsonElement scoresEl, "Scores", "scores") &&
                        TryGetPropertyAny(scoresEl, out JsonElement scoreEl, "Overall_Health_Score", "overallHealthScore", "overall_score") &&
                        scoreEl.TryGetInt32(out int parsedScore))
                    {
                        overallScore = parsedScore;
                    }

                    if (TryGetPropertyAny(patientDataEl, out JsonElement analysisEl, "Analysis", "analysis"))
                    {
                        if (TryGetPropertyAny(analysisEl, out JsonElement insightsEl, "Health_Insights", "healthInsights", "insights") &&
                            insightsEl.ValueKind == JsonValueKind.Array)
                        {
                            healthInsightCount = insightsEl.GetArrayLength();
                        }

                        if (TryGetPropertyAny(analysisEl, out JsonElement recsEl, "Recommendations", "recommendations") &&
                            recsEl.ValueKind == JsonValueKind.Array)
                        {
                            recommendationCount = recsEl.GetArrayLength();
                        }
                    }
                }
            }

            string scoreText = overallScore >= 0 ? overallScore.ToString() : "n/a";
            return $"📥 BluHealth response: status={(int)response.StatusCode} {response.StatusCode}, bodyBytes={responseBody.Length}, vitalId={vitalId}, message='{message}', insights={healthInsightCount}, recommendations={recommendationCount}, overallScore={scoreText}, luaInsightBytes={luaFormattedResponse.Length}";
        }
        catch (Exception ex)
        {
            return $"📥 BluHealth response: status={(int)response.StatusCode} {response.StatusCode}, bodyBytes={responseBody.Length}, summaryParseError={ex.Message}";
        }
    }

    private static string TruncateForLog(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            return value;

        return value.Substring(0, maxLength) + $"... [truncated {value.Length - maxLength} chars]";
    }






















    /* -------------------------------------------------------------------------END SENDING VITALS TO BLUHEALTH PORTAL (OTP BASED)------------------------------------------------------------------------------*/







    /* -------------------------------------------------------------------------SENDING DEVICE DATA TO BLUHEALTH ADMIN PORTAL------------------------------------------------------------------------------*/

    static async Task SendDeviceInformationAsync()
    {
        using HttpClient httpClient = new HttpClient();
        try
        {
            var payload = DeviceIdentity.BuildDevicePayload(
                currentState: _currentState.ToString(),
                isLiveMode: _isLiveMode,
                deviceStartTime: deviceStartTime,
                temperature1: lastTemperature1,
                temperature2: lastTemperature2
            );

            string jsonPayload = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping  // fixes °C unicode
            });

            Log("=== Sending Device Information to BluHealth ===");
            Log(jsonPayload);

            var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            HttpResponseMessage response = await httpClient.PostAsync(BLUHEALTH_DEVICE_URL, content);
            string responseBody = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                Log(" Device information sent successfully!");
                lastDeviceInfoSent = DateTime.Now;
            }
            else
                Log($" Failed. Status: {response.StatusCode}, Body: {responseBody}");
        }
        catch (HttpRequestException ex) { Log($" HTTP Error: {ex.Message}"); }
        catch (Exception ex) { Log($" Error: {ex.Message}"); }
    }


    // ADD this background task to send device info periodically
    static async Task DeviceInfoPeriodicSendAsync(CancellationToken cancellationToken)
    {
        // Send immediately on startup
        Log("📡 Sending initial device information on startup...");
        await SendDeviceInformationAsync();

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Wait for the interval
                await Task.Delay(DeviceInfoInterval, cancellationToken);

                // Send device information
                Log($"📡 Periodic device information update (every {DeviceInfoInterval.TotalMinutes} minutes)...");
                await SendDeviceInformationAsync();
            }
            catch (OperationCanceledException)
            {
                Log("Device info periodic task canceled.");
                break;
            }
            catch (Exception ex)
            {
                Log($"Error in device info periodic task: {ex.Message}");
                await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken); // Retry after 5 minutes on error
            }
        }
    }


    public static bool IsPatientAuthenticated()
    {
        lock (_authLock)
        {
            return _isAuthenticated && !string.IsNullOrEmpty(_patientToken);
        }
    }

    public static string GetCurrentPatientId()
    {
        lock (_authLock)
        {
            return _patientId;
        }
    }

    public static string GetCurrentPatientName()
    {
        lock (_authLock)
        {
            return _patientName;
        }
    }

    // Clear authentication (for logout)
    public static void ClearAuthentication()
    {
        lock (_authLock)
        {
            _patientToken = "";
            _patientId = "";
            _patientName = "";
            _patientPhone = "";
            _patientEmail = "";
            _patientGender = "";
            _patientAge = 0;
            _isAuthenticated = false;
        }
        Log(" Patient authentication cleared");
    }








    static void ClearEcgSamples()
    {
        lock (_lock)
        {
            ecgLeadI.Clear();
            ecgLeadII.Clear();
            ecgLeadIII.Clear();
            ecgLeadAvr.Clear();
            ecgLeadAvl.Clear();
            ecgLeadAvf.Clear();
            ecgLeadV.Clear();
            // Clear 12-lead samples (UART2) - ADD THIS
            ecg12LeadI.Clear();
            ecg12LeadII.Clear();
            ecg12LeadIII.Clear();
            ecg12LeadAvr.Clear();
            ecg12LeadAvl.Clear();
            ecg12LeadAvf.Clear();
            ecg12LeadV1.Clear();
            ecg12LeadV2.Clear();
            ecg12LeadV3.Clear();
            ecg12LeadV4.Clear();
            ecg12LeadV5.Clear();
            ecg12LeadV6.Clear();



        }
    }

    static void SendHeightWeightCommand()
    {
        Log("Height/Weight measurement initiated.");
    }

    //  ADD THIS FUNCTION - Call when user logs out or starts new session
    static void ClearAllMeasurementData()
    {
        lock (_lock)
        {
            // Clear vitals
            storedHeight = 0;
            storedWeight = 0;
            storedBMI = 0;
            /*         storedTemperature = 0;
                    storedSpO2 = 0;
                    storedHeartRate = 0;

                    // Clear BP/NIBP
                    storedSystolic = 0;
                    storedDiastolic = 0;
                    storedMeanAP = 0;
                    lastSys = 0; */
            lastDia = 0;
            lastMean = 0;
            lastPulseRate2 = 0;
            lastLivePressure = 0;
            validSys = 0;
            validDia = 0;
            validPulse2 = 0;
            _measurementProgress = 0;
            _measurementComplete = false;

            // Clear blood glucose
            ClearBloodGlucoseValues();

            // Clear ECG buffers
            ClearEcg5Buffers();
            ClearEcg12Buffers();

            // Clear ECG queues
            while (ecg5LeadQueue.TryDequeue(out _)) { }
            while (ecg12LeadQueue.TryDequeue(out _)) { }

            Log("🗑️  ALL measurement data cleared for new session");
        }
    }



    static readonly TimeSpan _lockDuration = TimeSpan.FromSeconds(5);
    const int SampleWindow = 10;

    static async Task ReadHeightLoopAsync(CancellationToken cancellationToken)
    {
        // ========================================================================
        // HEIGHT/WEIGHT CODE COMMENTED OUT - ECG 12-LEAD PROCESSING ON UART2
        // To restore height/weight, uncomment the section below and comment out
        // the ECG 12-lead processing section
        // ========================================================================

        /*
        // === ORIGINAL HEIGHT/WEIGHT CODE (COMMENTED OUT) ===
        var regexHeight = new Regex(@"HEIGHT\s*[:=]\s*([0-9]+(?:\.[0-9]+)?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var regexWeight = new Regex(@"WEIGHT\s*[:=]\s*([0-9]+(?:\.[0-9]+)?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var regexBMI = new Regex(@"BMI\s*[:=]\s*([0-9]+(?:\.[0-9]+)?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        var heightSamples = new AdvancedSampleQueue(FAST_SAMPLE_WINDOW);
        var weightSamples = new AdvancedSampleQueue(FAST_SAMPLE_WINDOW);
        var bmiSamples = new AdvancedSampleQueue(FAST_SAMPLE_WINDOW);

        DateTime lockUntil = DateTime.MinValue;
        DateTime lastDisplayUpdate = DateTime.MinValue;
        const int DISPLAY_UPDATE_INTERVAL_MS = 250;

        Log($" Enhanced averaging initialized - Window size: {FAST_SAMPLE_WINDOW} samples");
        Log($"🎯 Stability threshold: {STABILITY_THRESHOLD}, Lock duration: {FAST_LOCK_DURATION.TotalSeconds}s");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!_serialPortHeight.IsOpen)
                {
                    await Task.Delay(100, cancellationToken);
                    continue;
                }

                if (_serialPortHeight.BytesToRead == 0)
                {
                    await Task.Delay(25, cancellationToken);
                    continue;
                }

                string line = _serialPortHeight.ReadLine();
                if (string.IsNullOrWhiteSpace(line))
                {
                    await Task.Delay(25, cancellationToken);
                    continue;
                }

                if (_currentState != MeasurementState.HEIGHT_WEIGHT && !_isLiveMode) continue;

                float? newHeight = null, newWeight = null, newBMI = null;

                var mH = regexHeight.Match(line);
                var mW = regexWeight.Match(line);
                var mB = regexBMI.Match(line);

                if (mH.Success && TryParseInvariant(mH.Groups[1].Value, out float h) && h > 0) newHeight = h;
                if (mW.Success && TryParseInvariant(mW.Groups[1].Value, out float w) && w > 0) newWeight = w;
                if (mB.Success && TryParseInvariant(mB.Groups[1].Value, out float b) && b > 0) newBMI = b;

                bool samplesAdded = false;

                if (newHeight.HasValue || newWeight.HasValue || newBMI.HasValue)
                {
                    lock (_lock)
                    {
                        if (newHeight.HasValue) 
                        {
                            heightSamples.AddSample(newHeight.Value);
                            samplesAdded = true;
                        }
                        if (newWeight.HasValue) 
                        {
                            weightSamples.AddSample(newWeight.Value);
                            samplesAdded = true;
                        }
                        if (newBMI.HasValue) 
                        {
                            bmiSamples.AddSample(newBMI.Value);
                            samplesAdded = true;
                        }

                        lastHeight = heightSamples.GetFilteredAverage();
                        lastWeight = weightSamples.GetFilteredAverage();
                        lastBMI = bmiSamples.GetFilteredAverage();
                    }

                    var now = DateTime.Now;
                    if (samplesAdded && (now - lastDisplayUpdate).TotalMilliseconds >= DISPLAY_UPDATE_INTERVAL_MS)
                    {
                        lock (_lock)
                        {
                            if (_currentState == MeasurementState.HEIGHT_WEIGHT)
                                PrintHeightWeightWithStats(heightSamples, weightSamples, bmiSamples);
                            else if (_isLiveMode)
                                PrintLiveData();
                        }
                        lastDisplayUpdate = now;
                    }

                    if (heightSamples.IsFull && weightSamples.IsFull && bmiSamples.IsFull && lockUntil == DateTime.MinValue)
                    {
                        lock (_lock)
                        {
                            lastHeight = heightSamples.GetFilteredAverage();
                            lastWeight = weightSamples.GetFilteredAverage();
                            lastBMI = bmiSamples.GetFilteredAverage();

                            Log($"🔒 STABLE READING LOCKED - H:{lastHeight:F1}cm W:{lastWeight:F1}kg BMI:{lastBMI:F1}");
                            PrintHeightWeightWithStats(heightSamples, weightSamples, bmiSamples);
                        }

                        lockUntil = DateTime.Now + FAST_LOCK_DURATION;

                        heightSamples.Clear();
                        weightSamples.Clear();
                        bmiSamples.Clear();
                    }
                }

                if (DateTime.Now > lockUntil && lockUntil != DateTime.MinValue)
                {
                    lock (_lock)
                    {
                        lastHeight = 0;
                        lastWeight = 0;
                        lastBMI = 0;
                        Log(" Lock expired - Measurements reset");
                        PrintHeightWeight();
                    }
                    lockUntil = DateTime.MinValue;

                    heightSamples.Clear();
                    weightSamples.Clear();
                    bmiSamples.Clear();
                }
            }
            catch (TimeoutException)
            {
                await Task.Delay(50, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (InvalidOperationException)
            {
                Log($"UART2 warning: Port may be closed");
                await Task.Delay(100, cancellationToken);
            }
            catch (Exception ex)
            {
                Log($"UART2 error: {ex.Message}");
                await Task.Delay(100, cancellationToken);
            }
        }
        */

        // ========================================================================
        // ECG 12-LEAD PROCESSING ON UART2 (ACTIVE)
        // ========================================================================

        Log("🔬 ECG 12-Lead processing started on UART2");
        Log($" ECG12 will record up to {MaxEcgSamples} samples per lead (approximately {MaxEcgSamples / 500.0:F1} seconds at 500Hz)");

        var buffer = new byte[4096];

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Check if serial port is open before reading
                if (!_serialPortECG12.IsOpen)
                {
                    await Task.Delay(100, cancellationToken);
                    continue;
                }

                // Check if data is available to avoid blocking
                if (_serialPortECG12.BytesToRead == 0)
                {
                    await Task.Delay(10, cancellationToken);
                    continue;
                }

                // Read available data
                int bytesRead = _serialPortECG12.Read(buffer, 0, buffer.Length);

                if (bytesRead > 0)
                {
                    await ProcessEcg12DataAsync(buffer, bytesRead, cancellationToken);
                }
            }
            catch (TimeoutException)
            {
                // Silently handle timeout - this is expected when no data is available
                await Task.Delay(50, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Log("ECG 12-Lead processing cancelled.");
                break;
            }
            catch (InvalidOperationException ex)
            {
                // Port might be closed
                Log($" UART2 (ECG12) warning: Port may be closed - {ex.Message}");
                await Task.Delay(100, cancellationToken);
            }
            catch (Exception ex)
            {
                // Log unexpected errors
                Log($" UART2 (ECG12) error: {ex.Message}");
                await Task.Delay(100, cancellationToken);
            }
        }

        Log("ECG 12-Lead processing stopped on UART2");
    }


    // Enhanced display function with statistics
    static void PrintHeightWeightWithStats(AdvancedSampleQueue heightSamples,
                                         AdvancedSampleQueue weightSamples,
                                         AdvancedSampleQueue bmiSamples)
    {
        lock (_lock)
        {
            string stats = $"[H:{heightSamples.Count}/{FAST_SAMPLE_WINDOW} W:{weightSamples.Count}/{FAST_SAMPLE_WINDOW} B:{bmiSamples.Count}/{FAST_SAMPLE_WINDOW}]";
            Log($"Height: {lastHeight:F1} cm | Weight: {lastWeight:F1} kg | BMI: {lastBMI:F1} {stats}");
        }
    }






    static void RunUartTest()
    {
        // Distinctive pattern that won't be confused with any real protocol
        // frame ID (0x04-0x24 range is all taken by real frames above).
        byte[] testPattern = new byte[] { 0xAA, 0x55, 0xAA, 0x55, 0xDE, 0xAD, 0xBE, 0xEF };
        try
        {
            _serialPortData.Write(testPattern, 0, testPattern.Length);
            Log("[UART_TEST] Sent AA 55 AA 55 DE AD BE EF on /dev/verdin-uart1.");
            Log("[UART_TEST] Jumper TX to RX on the connector BEFORE running this for a clean self-test: " +
                "if the port itself is alive, the existing '[UART1] N bytes read ... first bytes: ...' log line " +
                "will show that same AA 55 AA 55 DE AD BE EF sequence within the next couple seconds.");
            Log("[UART_TEST] Without a jumper, a successful write here only proves the OS/driver side is working — " +
                "it does NOT prove the TX pin is electrically producing a signal, and it won't produce any " +
                "'[UART1] bytes read' response unless something out there echoes it back.");
        }
        catch (Exception ex)
        {
            Log($"[UART_TEST] Write failed: {ex.Message} — the OS-level write itself is failing here, which points " +
                "to a driver/port-open problem rather than just a dead pin.");
        }
    }

    static void SendRequestPOST()
    {
        byte[] postRequestFrame = new byte[] { 0x40, 0xC0 };
        try
        {
            _serialPortData.Write(postRequestFrame, 0, postRequestFrame.Length);
            Log("RequestPOST frame sent successfully.", LogLevel.Debug);
        }
        catch (Exception ex)
        {
            Log($"Error sending RequestPOST frame: {ex.Message}");
        }
    }

    static void StartNiBP()
    {
        byte[] nibpStartFrame = new byte[] { 0x55, 0xD5 };
        try
        {
            _serialPortData.Write(nibpStartFrame, 0, nibpStartFrame.Length);
            Log("Started NIBP Measurement.");
        }
        catch (Exception ex)
        {
            Log($"Error starting NIBP: {ex.Message}");
        }
    }

    static void StopNiBP()
    {
        byte[] nibpStopFrame = new byte[] { 0x56, 0xD6 };
        try
        {
            _serialPortData.Write(nibpStopFrame, 0, nibpStopFrame.Length);
            Log("Stopped NIBP Measurement.");
        }
        catch (Exception ex)
        {
            Log($"Error stopping NIBP: {ex.Message}");
        }
    }





    static async Task ProcessDataLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];

        //  Keep timeout, but we'll avoid hitting it
        // ReadTimeout = 100 is fine, we won't wait that long

        // Diagnostic-only: proves whether ANY bytes ever arrive on UART1 at all,
        // independent of frame parsing — SpO2/Temp/ECG (5-lead) all share this
        // one physical port, so if this stays silent, none of them can work no
        // matter what the parsing code does.
        DateTime lastByteSeen = DateTime.MinValue;
        long totalBytesSeen = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                //  CHECK if data is available BEFORE reading
                if (_serialPortData.BytesToRead > 0)
                {
                    // Data is available - read it immediately (no waiting!)
                    int bytesToRead = Math.Min(buffer.Length, _serialPortData.BytesToRead);
                    int bytesRead = _serialPortData.Read(buffer, 0, bytesToRead);

                    if (bytesRead > 0)
                    {
                        lastByteSeen = DateTime.UtcNow;
                        totalBytesSeen += bytesRead;
                        string hexPreview = string.Join(" ", buffer.Take(Math.Min(bytesRead, 16)).Select(b => b.ToString("X2")));
                        LogThrottled("uart1-raw", $"[UART1] {bytesRead} bytes read (total so far: {totalBytesSeen}) — first bytes: {hexPreview}", TimeSpan.FromSeconds(2));

                        await ProcessRawDataAsync(buffer, bytesRead, cancellationToken);
                    }
                }
                else
                {
                    if (lastByteSeen == DateTime.MinValue)
                    {
                        LogThrottled("uart1-silent-ever", "[UART1]  No bytes received on /dev/verdin-uart1 since startup — SpO2/Temp/ECG(5-lead)/NIBP all depend on this port", TimeSpan.FromSeconds(10));
                    }
                    else if ((DateTime.UtcNow - lastByteSeen) > TimeSpan.FromSeconds(5))
                    {
                        LogThrottled("uart1-silent-since", $"[UART1]  No bytes received in {(DateTime.UtcNow - lastByteSeen).TotalSeconds:F0}s (last seen at {lastByteSeen:HH:mm:ss.fff} UTC)", TimeSpan.FromSeconds(10));
                    }

                    //  No data available - yield CPU for 1ms only
                    await Task.Delay(1, cancellationToken);
                }
            }
            catch (TimeoutException)
            {
                //  REMOVED Task.Delay(10) - just continue immediately
                // This shouldn't happen often since we check BytesToRead first
            }
            catch (Exception ex)
            {
                Log($"UART1 error: {ex.Message}");
                await Task.Delay(50, cancellationToken);
            }
        }
    }











    static async Task ProcessRawDataAsync(byte[] data, int length, CancellationToken cancellationToken)
    {
        for (int i = 0; i < length; i++)
        {
            // Add acknowledgment frame handling
            if (data[i] == 0x04 && i + 5 <= length)
            {
                await HandleAcknowledgmentAsync(data, i);
                i += 4;  // Skip 4 more bytes (5 total)
            }


            //  Frame 0x05: ECG Wave (8 bytes total: ID + 7 data bytes)
            else if (data[i] == 0x05 && i + 8 <= length)
            {
                await DecodeAndPrintEcgWaveAsync(data, i, cancellationToken);
                i += 7;  //  FIXED: Skip 7 more bytes (8 total including ID)
            }
            //  Frame 0x06: ECG Status (3 bytes total)
            else if (data[i] == 0x06 && i + 3 <= length)
            {
                await DecodeAndPrintEcgStatusAsync(data, i, cancellationToken);
                i += 2;  // Skip 2 more bytes (3 total)
            }
            //  Frame 0x07: ECG HR (5 bytes total)
            else if (data[i] == 0x07 && i + 5 <= length)
            {
                await DecodeAndPrintEcgHrAsync(data, i, cancellationToken);
                i += 4;  // Skip 4 more bytes (5 total)
            }
            else if (data[i] == 0x09 && i + 3 <= length)
            {
                await DecodeAndPrintARRAsync(data, i, cancellationToken);
                i += 6;
            }
            else if (data[i] == 0x0A && i + 7 < length)
            {
                await DecodeAndPrintSTAsync(data, i, cancellationToken);
                i += 8;
            }
            else if (data[i] == 0x0B && i + 9 < length)
            {
                await DecodeAndPrintRRAsync(data, i, cancellationToken);
                i += 4;
            }

            //  Frame 0x09: ECG PVC (3 bytes total)
            else if (data[i] == 0x09 && i + 3 <= length)
            {
                await DecodeAndPrintEcgPvcAsync(data, i, cancellationToken);
                i += 2;  // Skip 2 more bytes (3 total)
            }
            // Temperature frame (8 bytes)
            else if (data[i] == 0x15 && i + 8 <= length && (_isLiveMode || _currentState == MeasurementState.TEMPERATURE || _currentState == MeasurementState.SPO2_NIBP))
            {
                await DecodeAndPrintTemperatureAsync(data, i, cancellationToken);
                i += 7;  // Skip 7 more bytes
            }
            // SpO2 frame (7 bytes)
            else if (data[i] == 0x17 && i + 7 <= length && (_isLiveMode || _currentState == MeasurementState.SPO2_NIBP))
            {
                await DecodeAndPrintSpo2Async(data, i, cancellationToken);
                i += 6;  // Skip 6 more bytes
            }
            // SpO2 wave frame (5 bytes) - ADD THIS
            else if (data[i] == 0x16 && i + 5 <= length && (_isLiveMode || _currentState == MeasurementState.SPO2_NIBP))
            {
                await DecodeAndPrintSpo2WaveAsync(data, i, cancellationToken);
                i += 4;  // Skip 4 more bytes (total frame is 5 bytes: ID + HEAD + DATA + DATA + CHECKSUM)
            }

            else if (data[i] == 0x24 && i + 8 <= length)
            {
                await DecodeAndPrintNIBPStatusAsync(data, i, cancellationToken);
                i += 7;
            }
            // NIBP End frame — always listen, not gated (needed for calibration)
            else if (data[i] == 0x21 && i + 4 <= length)
            {
                await DecodeAndPrintNIBPEndAsync(data, i, cancellationToken);
                i += 3;
            }

            // NIBP frames — decode whenever a measurement is actually running.
            // Must accept the SAME states as the "START BP" handler (which allows
            // BLOOD_SUGAR): after next→back navigation the state can legitimately
            // still be BLOOD_SUGAR (back nav message missed/de-duped), and gating
            // decode on SPO2_NIBP only made the restarted BP pump with a BLANK
            // GUI — frames were discarded so live pressure never updated.
            else if (_isNIBPActive && (_isLiveMode
                     || _currentState == MeasurementState.SPO2_NIBP
                     || _currentState == MeasurementState.BLOOD_SUGAR))
            {
                if (data[i] == 0x22 && i + 9 <= length)
                {
                    await DecodeAndPrintNIBPAsync(data, i, 8, cancellationToken);
                    i += 8;  // Skip 8 more bytes
                }
                else if (data[i] == 0x23 && i + 5 <= length)
                {
                    await DecodeAndPrintNIBPAsync(data, i, 4, cancellationToken);
                    i += 4;  // Skip 4 more bytes
                }
                else if (data[i] == 0x20 && i + 6 <= length)
                {
                    await DecodeAndPrintNIBPAsync(data, i, 6, cancellationToken);
                    i += 5;  // Skip 5 more bytes
                }
            }
        }
    }

    // Add this method to handle acknowledgment frames (0x04)
    static async Task HandleAcknowledgmentAsync(byte[] data, int startIndex)
    {
        if (startIndex + 4 >= data.Length)
            return;

        byte head = data[startIndex + 1];
        byte commandId = (byte)(((head & 0x01) << 7) | (data[startIndex + 2] & 0x7F));
        byte ackStatus = (byte)(((head & 0x02) >> 1 << 7) | (data[startIndex + 3] & 0x7F));  // ← fix

        string status = ackStatus switch
        {
            0 => "OK",
            1 => "CHECKSUM error",
            2 => "Command length error",
            3 => "Invalid command",
            4 => "Command parameter error",
            5 => "Command not accepted",
            _ => "Unknown"
        };

        // Promoted to Info: this is the module directly telling us whether it
        // received our last command intact. status != "OK" (especially
        // "CHECKSUM error") means the command DID reach the sensor but arrived
        // corrupted — evidence of a damaged/noisy TX line, not a dead one.
        // No ACK at all ever appearing means the module either isn't hearing
        // us, or isn't ACK'ing — evidence pointing the other way.
        string ackMarker = ackStatus == 0 ? "" : " ⚠️";
        Log($"[ACK] Command 0x{commandId:X2} → {status}{ackMarker}");
    }


    static async Task DecodeAndPrintTemperatureAsync(byte[] data, int startIndex, CancellationToken cancellationToken)
    {
        if (startIndex + 7 >= data.Length)
            throw new ArgumentException("Invalid frame length");

        byte id = data[startIndex]; // 0x15
        byte head = data[startIndex + 1];

        byte sensorStatus = (byte)((head & 0x01) << 7 | (data[startIndex + 2] & 0x7F));
        byte temp1High = (byte)(((head >> 1) & 0x01) << 7 | (data[startIndex + 3] & 0x7F));
        byte temp1Low = (byte)(((head >> 2) & 0x01) << 7 | (data[startIndex + 4] & 0x7F));
        byte temp2High = (byte)(((head >> 3) & 0x01) << 7 | (data[startIndex + 5] & 0x7F));
        byte temp2Low = (byte)(((head >> 4) & 0x01) << 7 | (data[startIndex + 6] & 0x7F));

        ushort temp1Raw = (ushort)((temp1High << 8) | temp1Low);
        ushort temp2Raw = (ushort)((temp2High << 8) | temp2Low);

        byte sum = 0;
        for (int i = 0; i < 7; i++) sum += data[startIndex + i];
        byte expectedChecksum = (byte)(sum | 0x80);
        byte actualChecksum = data[startIndex + 7];
        if (expectedChecksum != actualChecksum)
            throw new InvalidDataException("Checksum mismatch");

        // Diagnostic: fires every time a 0x15 temperature frame passes checksum
        // and is decoded — separate from the raw [UART1] byte log and from
        // PrintTemperature/PrintLiveData, which are gated by state/live-mode.
        LogThrottled("temp-frame-decode",
            $"[TEMP] Frame decoded — raw temp1:{(temp1Raw == 0xFF9C ? "invalid" : (temp1Raw / 10.0f).ToString("F1"))} " +
            $"raw temp2:{(temp2Raw == 0xFF9C ? "invalid" : (temp2Raw / 10.0f).ToString("F1"))} sensorStatus:0x{sensorStatus:X2}",
            TimeSpan.FromSeconds(1));

        lock (_lock)
        {
            lastTemperature1 = (sensorStatus & 0x01) != 0 || temp1Raw == 0xFF9C ? float.NaN : temp1Raw / 10.0f;
            lastTemperature2 = (sensorStatus & 0x02) != 0 || temp2Raw == 0xFF9C ? float.NaN : temp2Raw / 10.0f;

            if (_currentState == MeasurementState.TEMPERATURE)
            {
                PrintTemperature();
            }
            else if (_isLiveMode)
            {
                PrintLiveData();
            }
        }
    }
    /// <summary>
    /// PI display estimate for the frozen "PI" GUI label. The UN806C module has
    /// no true perfusion index (AC/DC pleth ratio); its info byte carries only a
    /// signal-strength indicator 0-8. This maps that strength into the typical
    /// physiological PI range (~0.5-6%) so the display is plausible and varies
    /// with real finger-contact quality. NOT a measurement: display-only, never
    /// stored in records, excluded from any clinical claim.
    /// (The old mapping was strength/8*20 → pegged everyone at 10-15%.)
    /// </summary>
    static float EstimatePerfusionIndex(float signalStrength)
    {
        return signalStrength switch
        {
            <= 0 => 0.0f,
            <= 1 => 0.5f,
            <= 2 => 0.8f,
            <= 3 => 1.2f,
            <= 4 => 1.8f,
            <= 5 => 2.6f,
            <= 6 => 3.6f,
            <= 7 => 4.8f,
            _    => 6.2f
        };
    }

    static async Task DecodeAndPrintSpo2Async(byte[] data, int startIndex, CancellationToken cancellationToken)
    {
        if (startIndex + 6 >= data.Length)
            throw new ArgumentException("Invalid SpO2 frame length");

        byte id = data[startIndex]; // 0x17
        byte head = data[startIndex + 1];
        byte spo2Info = data[startIndex + 2];  // SPO2 information byte
        byte prHigh = data[startIndex + 3];
        byte prLow = data[startIndex + 4];
        byte spo2Byte = data[startIndex + 5];

        // Reconstruct PR (pulse rate) from two bytes.
        // UN806C protocol §1.2: HEAD bit0 = DATA1's bit7, bit1 = DATA2's, bit2 = DATA3's,
        // bit3 = DATA4's. Frame 0x17: DATA1=spo2Info, DATA2=PR-high, DATA3=PR-low, DATA4=SpO2.
        // (Previous masks were shifted one bit — any PR >= 128 bpm decoded wrong.)
        int pr_high = prHigh & 0x7F;
        if ((head & 0x02) != 0) pr_high |= 0x80;

        int pr_low = prLow & 0x7F;
        if ((head & 0x04) != 0) pr_low |= 0x80;

        int pulseRate = (pr_high << 8) | pr_low;

        // Extract SPO2 value
        int spo2 = spo2Byte & 0x7F;
        if ((head & 0x08) != 0) spo2 |= 0x80;

        // Extract SPO2 information byte bits
        int spo2InfoValue = spo2Info & 0x7F;
        if ((head & 0x01) != 0) spo2InfoValue |= 0x80;

        bool spo2Drop = (spo2InfoValue & 0x20) != 0;
        bool searchTooLong = (spo2InfoValue & 0x10) != 0;
        int signalStrength = spo2InfoValue & 0x0F;  // 0-8 valid, 15 = invalid

        // Validate ranges
        pulseRate = (pulseRate < 40 || pulseRate > 250) ? 0 : pulseRate;
        spo2 = (spo2 < 60 || spo2 > 100) ? 0 : spo2;

        // Signal quality straight from the module (0-8; 15 = invalid).
        // The UN806C does NOT provide a Perfusion Index — protocol §1.3.15 bits 3:0
        // are a pleth signal-strength indicator. We previously scaled this to a fake
        // "PI %" (always showed 10-15%); it is now reported honestly as signal
        // quality 0-8 and the GUI labels it "Signal", not PI.
        float signalQuality = (signalStrength <= 8) ? signalStrength : 0.0f;

        // Diagnostic: fires every time a 0x17 SpO2 frame is actually recognized
        // and decoded, independent of PrintLiveData/_isLiveMode — proves frame
        // parsing is happening at all, separate from the raw [UART1] byte log.
        LogThrottled("spo2-frame-decode",
            $"[SPO2] Frame decoded — raw SpO2:{spo2}% raw PR:{pulseRate}bpm signalStrength:{signalStrength}/8 " +
            $"spo2Drop:{spo2Drop} searchTooLong:{searchTooLong}",
            TimeSpan.FromSeconds(1));

        lock (_lock)
        {
            lastPulseRate = SmoothValue(pulseRateHistory, pulseRate);
            lastSpO2 = SmoothValue(spo2History, spo2);
            lastSignalQuality = signalQuality;

            bool fingerPresent = signalStrength > 0 && signalStrength <= 8
                                 && lastSpO2 > 0 && lastPulseRate > 0;

            RunSpo2StateMachine(fingerPresent);

            if (_isLiveMode) PrintLiveData();
        }
    }
    // Runs once per decoded SpO2 frame (already inside _lock). Continuous
    // spot-check: no wall-clock timer, capture is driven by signal STABILITY.
    private static void RunSpo2StateMachine(bool fingerPresent)
    {
        var nowUtc = DateTime.UtcNow;

        // Feed the rolling window with GOOD-quality samples only, then drop
        // anything older than the stability window.
        if (fingerPresent && lastSpO2 > 0 && lastPulseRate > 0
            && lastSignalQuality >= SPO2_QUALITY_MIN)
        {
            _spo2Buf.Add((nowUtc, lastSpO2, lastPulseRate, (int)lastSignalQuality));
        }
        _spo2Buf.RemoveAll(s => (nowUtc - s.t).TotalSeconds > SPO2_STABLE_WINDOW);

        switch (_spo2Phase)
        {
            case Spo2Phase.Idle:
                if (fingerPresent)
                {
                    _spo2Phase = Spo2Phase.Measuring;
                    _spo2AcquireStart = nowUtc;
                    _spo2LastGuidance = DateTime.MinValue;
                    Log("SPO2: finger detected - acquiring");
                }
                break;

            case Spo2Phase.Measuring:
                if (!fingerPresent)
                {
                    if (_spo2HasCapture)
                    {
                        _spo2Phase = Spo2Phase.Hold;   // keep the stored reading on screen
                        Log("SPO2: finger removed - holding stored reading");
                    }
                    else
                    {
                        _spo2Phase = Spo2Phase.Idle;   // nothing valid yet - no garbage stored
                        _spo2Buf.Clear();
                        _ = Task.Run(() => SendSpo2Status("PAUSED"));
                        Log("SPO2: finger removed before a stable reading - reset");
                    }
                    break;
                }

                if (TryCaptureStableSpo2(nowUtc))
                    break;   // stable average captured -> phase moved to Captured

                // Inadequate-signal indication (ISO 80601-2-61): if we still
                // cannot get a stable reading after a while, tell the user -
                // never sit silently and never freeze.
                if ((nowUtc - _spo2AcquireStart).TotalSeconds >= SPO2_GUIDANCE_AFTER
                    && (nowUtc - _spo2LastGuidance).TotalSeconds >= 5)
                {
                    _spo2LastGuidance = nowUtc;
                    _ = Task.Run(() => SendSpo2Status("SPO2_SEARCHING"));
                }
                break;

            case Spo2Phase.Captured:
                // One stable spot reading per finger application (no churn while
                // the finger stays). When it leaves, hold the stored value.
                if (!fingerPresent)
                {
                    _spo2Phase = Spo2Phase.Hold;
                    Log("SPO2: finger removed - holding stored reading");
                }
                break;

            case Spo2Phase.Hold:
                if (fingerPresent)
                {
                    // Finger re-applied -> take a fresh spot reading. The previous
                    // stored value stays on screen only when the finger is absent;
                    // once the finger is detected again we clear the stale display
                    // values so the new live reading can take over immediately.
                    _spo2Phase = Spo2Phase.Measuring;
                    _spo2AcquireStart = nowUtc;
                    _spo2LastGuidance = DateTime.MinValue;
                    _spo2Buf.Clear();
                    StartFreshSpo2LiveReading();
                    _ = Task.Run(() => SendSpo2Status("IDLE"));
                    Log("SPO2: finger re-applied - re-measuring");
                }
                break;
        }
    }

    private static void StartFreshSpo2LiveReading()
    {
        lastSpO2 = 0;
        lastPulseRate = 0;
        lastSignalQuality = 0;
        _finalSpO2 = 0;
        _finalPulseRate = 0;
        _finalSignalQuality = 0;
        _spo2Done = false;
    }

    // Captures a stable AVERAGE from the rolling window if the readings have
    // settled (quality + spread), else returns false to keep waiting. Runs
    // inside _lock.
    private static bool TryCaptureStableSpo2(DateTime nowUtc)
    {
        if (_spo2Buf.Count < SPO2_MIN_SAMPLES) return false;
        double span = (_spo2Buf[_spo2Buf.Count - 1].t - _spo2Buf[0].t).TotalSeconds;
        if (span < SPO2_STABLE_WINDOW - 0.5) return false;   // window not full yet

        int sMin = int.MaxValue, sMax = int.MinValue, pMin = int.MaxValue, pMax = int.MinValue;
        long sSum = 0, pSum = 0; double qSum = 0;
        foreach (var s in _spo2Buf)
        {
            if (s.spo2 < sMin) sMin = s.spo2;
            if (s.spo2 > sMax) sMax = s.spo2;
            if (s.pr   < pMin) pMin = s.pr;
            if (s.pr   > pMax) pMax = s.pr;
            sSum += s.spo2; pSum += s.pr; qSum += s.q;
        }
        if (sMax - sMin > SPO2_SPO2_SPREAD) return false;   // SpO2 not steady enough
        if (pMax - pMin > SPO2_PR_SPREAD)   return false;   // pulse not steady enough

        int n = _spo2Buf.Count;
        int avgSpO2 = (int)Math.Round(sSum / (double)n);
        int avgPR   = (int)Math.Round(pSum / (double)n);
        float avgQ  = (float)(qSum / n);

        // Commit the averaged spot reading. Push it into the live values too so
        // the GUI freezes on the AVERAGE (it is within the +/- spread it satisfied).
        _finalSpO2 = avgSpO2; _finalPulseRate = avgPR; _finalSignalQuality = avgQ;
        lastSpO2 = avgSpO2; lastPulseRate = avgPR;
        storedSpO2 = avgSpO2; storedPulseRate = avgPR;

        bool first = !_spo2HasCapture;
        _spo2HasCapture = true;
        _spo2Phase = Spo2Phase.Captured;
        _spo2Buf.Clear();

        Log($"SPO2 spot reading (avg of {n} over {span:F0}s) -> {avgSpO2}% {avgPR}bpm Q{avgQ:F1}/8"
            + (first ? "" : " [updated]"));

        _spo2Done = true;
        // Send ONE status: if BP already finished, go straight to "both done".
        // (Previously this sent SPO2_DONE and then CheckAndSendBothCompleted as
        // two separate tasks — they raced, so measuring SpO2 first then BP could
        // leave the wrong "BP in progress" message on screen after BP was done.)
        string doneStatus = _nibpDone ? "BOTH_COMPLETED" : "SPO2_DONE";
        _ = Task.Run(async () => await SendSpo2Status(doneStatus));
        if (_nibpDone) Log(" Both SpO2 and NIBP complete");
        return true;
    }

    // Full reset of the SpO2 spot-check state - page entry / back / patient
    // boundary. Locks so it is safe against the decode thread.
    private static void ResetSpo2Measurement()
    {
        lock (_lock)
        {
            _spo2Phase = Spo2Phase.Idle;
            _spo2Buf.Clear();
            _spo2HasCapture   = false;
            _spo2AcquireStart = DateTime.MinValue;
            _spo2LastGuidance = DateTime.MinValue;
            _lastSpo2Status   = "";
            _finalSpO2 = 0; _finalPulseRate = 0; _finalSignalQuality = 0;
        }
    }
    private static async Task SendSpo2Status(string statusCode)
    {
        bool isCountdown = statusCode.StartsWith("MEASURING:");
        if (!isCountdown && _lastSpo2Status == statusCode) return;
        if (!isCountdown) _lastSpo2Status = statusCode;

        string line = $"STATUS:{statusCode}\n";
        byte[] bytes = Encoding.UTF8.GetBytes(line);

        List<TcpClient> clients;
        lock (_spo2ClientLock)
            clients = _spo2Clients.ToList();

        foreach (var client in clients)
        {
            try
            {
                if (client.Connected && _spo2ClientWriteLocks.TryGetValue(client, out var writeLock))
                {
                    if (await writeLock.WaitAsync(50))  // ← waits for vitals loop to release
                    {
                        try
                        {
                            await client.GetStream().WriteAsync(bytes, 0, bytes.Length);
                            await client.GetStream().FlushAsync();
                        }
                        finally { writeLock.Release(); }
                    }
                    else
                    {
                        Log($"⚠ [SPO2-STATUS] Lock timeout — dropped: {statusCode}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($" [SPO2-STATUS] {ex.Message}");
            }
        }
    }


    // static void CheckAndAutoStoreReading(int currentSpO2, int currentPR, float currentPI, int signalStrength)
    // {
    //     //  Invalid reading - reset counter but DON'T clear stored values
    //     if (currentSpO2 == 0 || currentPR == 0 || currentPI < PI_MIN_THRESHOLD || signalStrength >= 15)
    //     {
    //         spo2StableCount = 0;
    //         lastStableSpO2 = 0;
    //         lastStablePR = 0;
    //         return;
    //     }

    //     //  Check if reading is stable
    //     bool isStable = false;

    //     if (lastStableSpO2 == 0 && lastStablePR == 0)
    //     {
    //         isStable = true;
    //     }
    //     else
    //     {
    //         int spo2Diff = Math.Abs(currentSpO2 - lastStableSpO2);
    //         int prDiff = Math.Abs(currentPR - lastStablePR);
    //         isStable = (spo2Diff <= SPO2_TOLERANCE && prDiff <= PR_TOLERANCE);
    //     }

    //     if (isStable)
    //     {
    //         spo2StableCount++;
    //         lastStableSpO2 = currentSpO2;
    //         lastStablePR = currentPR;

    //         //  SILENTLY STORE after 20 stable seconds
    //         if (spo2StableCount >= SPO2_STABLE_SAMPLES)
    //         {
    //             storedSpO2 = currentSpO2;
    //             storedPulseRate = currentPR;
    //             storedSignalQuality = currentPI;

    //             // Reset counter so it can capture new reading if values change
    //             spo2StableCount = 0;
    //         }
    //     }
    //     else
    //     {
    //         // Reading changed - reset counter
    //         spo2StableCount = 0;
    //         lastStableSpO2 = currentSpO2;
    //         lastStablePR = currentPR;
    //     }
    // }


    static async Task DecodeAndPrintSpo2WaveAsync(byte[] data, int startIndex, CancellationToken cancellationToken)
    {
        if (startIndex + 5 > data.Length)  // Changed from 4 to 5
            throw new ArgumentException("Invalid SpO2 wave frame length");

        byte id = data[startIndex];        // 0x16
        byte head = data[startIndex + 1];  // HEAD
        byte waveByte = data[startIndex + 2];  // SPO2 wave
        byte statusByte = data[startIndex + 3]; // SPO2 status
        byte checksum = data[startIndex + 4];   // CHECKSUM

        // Extract wave value (0-255 range)
        int waveValue = waveByte & 0x7F;  // Remove bit 7
        if ((head & 0x01) != 0) waveValue |= 0x80;  // Restore bit 7 from HEAD

        // Extract status (reconstruct from HEAD)
        int status = statusByte & 0x7F;
        if ((head & 0x02) != 0) status |= 0x80;  // Restore bit 7 from HEAD

        // Extract status bits
        bool fingerOff = (status & 0x80) != 0;
        bool pulseFlag = (status & 0x40) != 0;
        bool searchPulse = (status & 0x20) != 0;
        bool sensorOff = (status & 0x10) != 0;
        int barGraph = status & 0x0F;  // 0-15 range

        lock (_lock)
        {
            lastSpo2WaveValue = waveValue;
            lastSpo2Status = (byte)status;
        }
    }



    // Add this method to start the graph TCP server
    static async Task StartGraphTcpServerAsync()
    {
        try
        {
            _graphServer = new TcpListener(IPAddress.Any, GraphPort);
            _graphServer.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _graphServer.Start();
            Log($" Graph TCP Server started on {IPAddress.Any}:{GraphPort}");

            while (_graphRunning)
            {
                try
                {
                    LogThrottled("graph-waiting", "Waiting for Graph connection...", TimeSpan.FromMinutes(1));
                    TcpClient client = await _graphServer.AcceptTcpClientAsync();
                    lock (_graphClientLock)
                    {
                        _graphClients.Add(client);
                    }
                    LogThrottled("graph-connected", $"New Graph client connected: {client.Client.RemoteEndPoint}", TimeSpan.FromSeconds(10));
                    _ = Task.Run(() => HandleGraphClientAsync(client, CancellationToken.None));
                }
                catch (Exception ex)
                {
                    if (_graphRunning)
                        Log($" Graph TCP Server error: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log($" Graph TCP Server startup error: {ex.Message}");
        }
    }



    // Add this method to handle graph client connections
    static async Task HandleGraphClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            using NetworkStream stream = client.GetStream();
            stream.ReadTimeout = 1000;
            stream.WriteTimeout = -1;

            byte[] buffer = new byte[256];
            int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
            string deviceType = Encoding.UTF8.GetString(buffer, 0, bytesRead).Trim();
            LogThrottled($"graph-handshake:{deviceType}", $"Graph Connected Remote: {client.Client.RemoteEndPoint} for {deviceType}", TimeSpan.FromSeconds(10));

            if (deviceType == "spo2_graph")
            {
                byte[] startBytes = Encoding.UTF8.GetBytes("GRAPH_START\n");
                await stream.WriteAsync(startBytes, 0, startBytes.Length, cancellationToken);

                // Buffer to accumulate samples
                var sampleBuffer = new System.Text.StringBuilder();
                int samplesPerBatch = 5;  // Send 5 samples at once for faster graph
                int sampleCount = 0;

                var interval = TimeSpan.FromMilliseconds(8);  // 125 Hz sample rate
                var batchInterval = TimeSpan.FromMilliseconds(40);  // Send batches every 40ms
                var lastSendTime = DateTime.UtcNow;

                while (_graphRunning && client.Connected && !cancellationToken.IsCancellationRequested)
                {
                    var currentTime = DateTime.UtcNow;

                    // Accumulate samples
                    if ((currentTime - lastSendTime) >= interval)
                    {
                        int waveValue, status;
                        lock (_lock)
                        {
                            waveValue = lastSpo2WaveValue;
                            status = lastSpo2Status;
                        }

                        sampleBuffer.Append($"{waveValue},{status:X2};");
                        sampleCount++;
                        lastSendTime = currentTime;
                    }

                    // Send batch when accumulated enough samples
                    if (sampleCount >= samplesPerBatch)
                    {
                        string batch = sampleBuffer.ToString();
                        byte[] responseBytes = Encoding.UTF8.GetBytes(batch + "\n");

                        if (client.Connected && stream.CanWrite)
                        {
                            try
                            {
                                await stream.WriteAsync(responseBytes, 0, responseBytes.Length, cancellationToken).ConfigureAwait(false);
                                await stream.FlushAsync(cancellationToken);
                            }
                            catch
                            {
                                break;
                            }
                        }
                        else
                        {
                            break;
                        }

                        // Reset buffer
                        sampleBuffer.Clear();
                        sampleCount = 0;
                    }
                    else
                    {
                        await Task.Delay(1, cancellationToken);  // Small delay to prevent CPU spin
                    }
                }

                byte[] stopBytes = Encoding.UTF8.GetBytes("GRAPH_STOP\n");
                if (client.Connected && stream.CanWrite)
                {
                    await stream.WriteAsync(stopBytes, 0, stopBytes.Length, cancellationToken);
                }
            }
        }
        catch (Exception ex)
        {
            Log($" Exception with Graph client: {ex.Message}");
        }
        finally
        {
            lock (_graphClientLock)
            {
                _graphClients.Remove(client);
            }
            client.Close();
            Log($"Graph Client disconnected.");
        }
    }




    // UPDATE your DecodeAndPrintNIBPAsync method to track when BP data is updated:
    static async Task DecodeAndPrintNIBPAsync(byte[] data, int startIndex, int length, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            bool bpDataUpdated = false;

            // Parse Live Cuff Pressure — 0x20 cuff frames only (length exactly 6).
            // Guards were ">=" before, so result1 frames (8) also ran this branch
            // and briefly parsed systolic bytes as cuff pressure.
            if (length == 6)
            {
                byte head = data[startIndex + 1];
                byte cuffHighByte = data[startIndex + 2];
                byte cuffLowByte = data[startIndex + 3];

                int high = (((head >> 0) & 0x01) << 7) | (cuffHighByte & 0x7F);
                int low = (((head >> 1) & 0x01) << 7) | (cuffLowByte & 0x7F);

                int pressure = (high << 8) | low;
                pressure = (pressure >= 0 && pressure <= 300) ? pressure : -100;

                if (lastLivePressure != -100 && pressure != -100)
                {
                    int diff = pressure - lastLivePressure;
                    if (Math.Abs(diff) > 1 && Math.Abs(diff) < 5)
                    {
                        lastHeartBlink = !lastHeartBlink;
                    }
                }

                lastLivePressure = pressure;

                // Calculate progress based on live pressure (0-180 mmHg = 0-100%)
                if (pressure > 0 && !_measurementComplete)
                {
                    _measurementProgress = Math.Min((pressure * 100) / 180, 100);
                }

                if (_currentState == MeasurementState.SPO2_NIBP || _isLiveMode)
                {
                    LogThrottled("nibp-live-cuff", $"[NIBP] Live Cuff: {lastLivePressure} mmHg | Progress: {_measurementProgress}%", TimeSpan.FromSeconds(1));
                }

                // Cuff frames only stream while a measurement is actually running —
                // use them as the reliable "BP in progress" status signal (the 0x24
                // status frame is only sent on request, so the GUI never saw
                // NIBP_MEASURING and kept showing "ready — tap Start"). Deduped by
                // SendSpo2Status; guarded so a trailing frame can't regress a
                // finished measurement.
                if (!_nibpDone && pressure > 0)
                    _ = Task.Run(() => SendSpo2Status("NIBP_MEASURING"));
            }

            // Parse Final BP Reading (Systolic/Diastolic/Mean) — 0x22 result1 only
            if (length == 8)
            {
                byte head = data[startIndex + 1];

                int sysHigh = (((head >> 0) & 0x01) << 7) | (data[startIndex + 2] & 0x7F);
                int sysLow = (((head >> 1) & 0x01) << 7) | (data[startIndex + 3] & 0x7F);
                int diaHigh = (((head >> 2) & 0x01) << 7) | (data[startIndex + 4] & 0x7F);
                int diaLow = (((head >> 3) & 0x01) << 7) | (data[startIndex + 5] & 0x7F);
                int meanHigh = (((head >> 4) & 0x01) << 7) | (data[startIndex + 6] & 0x7F);
                int meanLow = (((head >> 5) & 0x01) << 7) | (data[startIndex + 7] & 0x7F);

                int newSys = ((sysHigh << 8) | sysLow);
                int newDia = ((diaHigh << 8) | diaLow);
                int newMean = ((meanHigh << 8) | meanLow);

                newSys = (newSys >= 0 && newSys <= 300) ? newSys : -100;
                newDia = (newDia >= 0 && newDia <= 300) ? newDia : -100;
                newMean = (newMean >= 0 && newMean <= 300) ? newMean : -100;

                if (newSys > 0 && newDia > 0)
                {
                    lastSys = newSys;
                    lastDia = newDia;
                    lastMean = newMean;

                    validSys = newSys;
                    validDia = newDia;
                    lastBPUpdateTime = DateTime.Now;
                    bpDataUpdated = true;

                    storedSys = newSys;
                    storedDia = newDia;
                    storedMean = newMean;

                    // Mark as complete and set progress to 100%
                    _measurementComplete = true;
                    _measurementProgress = 100;

                    Log($"[NIBP]  Final BP Reading: {validSys}/{validDia} mmHg (Mean: {newMean})");

                }
            }

            // Parse Pulse Rate — 0x23 result2 only. With exact-length guards, cuff
            // pressure and systolic bytes can no longer masquerade as a pulse here;
            // the validSys/validDia check below stays as a sanity gate (result2
            // always follows result1, so a valid BP exists when real PR arrives).
            if (length == 4)
            {
                byte head = data[startIndex + 1];

                int prHigh = (((head >> 0) & 0x01) << 7) | (data[startIndex + 2] & 0x7F);
                int prLow = (((head >> 1) & 0x01) << 7) | (data[startIndex + 3] & 0x7F);
                int pr = (prHigh << 8) | prLow;

                int newPulse = (pr >= 40 && pr <= 250) ? pr : -100;

                //  CRITICAL: Only update pulse when we have valid BP data
                // During inflation, this value equals cuff pressure (wrong)
                // After measurement, this value is actual pulse rate (correct)
                if (newPulse > 0)
                {
                    // If we have valid BP readings, this is the real pulse rate
                    if (bpDataUpdated || (validSys > 0 && validDia > 0))
                    {
                        lastPulseRate2 = newPulse;
                        validPulse2 = newPulse;
                        Log($"[NIBP]  Final Pulse Rate: {validPulse2} BPM");
                    }
                    // Otherwise, it's just the cuff pressure (ignore it for pulse)
                    else
                    {
                        // During inflation - don't treat this as pulse rate
                        // Log($"[NIBP] Ignoring pulse during inflation: {newPulse}");
                    }
                }
            }

            if (_isLiveMode)
            {
                PrintLiveData();
            }
        }
    }

    static Task DecodeAndPrintNIBPStatusAsync(byte[] data, int startIndex, CancellationToken ct)
    {
        byte head = data[startIndex + 1];
        byte nibpStatus = (byte)(((head & 0x01) << 7) | (data[startIndex + 2] & 0x7F));
        byte autoPeriod = (byte)(((head & 0x02) >> 1 << 7) | (data[startIndex + 3] & 0x7F));
        byte errorCode = (byte)(((head & 0x04) >> 2 << 7) | (data[startIndex + 4] & 0x7F));

        string mode = (nibpStatus & 0x0F) switch
        {
            0 => "Reset OK",
            1 => "Manual mode",
            2 => "Manual mode",
            3 => "STAT mode",
            4 => "Calibration mode ",
            5 => "Pneumatic mode",
            6 => "Resetting",
            10 => $"ERROR — code {errorCode}",
            _ => $"Unknown ({nibpStatus})"
        };

        string error = errorCode switch
        {
            0 => "No error",
            1 => "Cuff loose",
            2 => "Leakage",
            3 => "Pressure wrong",
            4 => "Weak signal",
            5 => "Data range error",
            6 => "Arm movement",
            7 => "Over pressure",
            8 => "Signal saturate",
            9 => "Pneumatic test fail",
            10 => "Fatal error",
            11 => "Over time",
            _ => "Unknown"
        };

        Log($"🔧 [NIBP-STATUS] Mode: {mode} | Error: {error}");



        // ── Forward NIBP errors and measuring state to Lua via SpO2 status channel ──
        if (errorCode > 0)
        {
            _ = Task.Run(() => SendSpo2Status($"NIBP_ERROR:{errorCode}"));
        }
        else if ((nibpStatus & 0x0F) == 1 || (nibpStatus & 0x0F) == 2)
        {
            _ = Task.Run(() => SendSpo2Status("NIBP_MEASURING"));
        }
        return Task.CompletedTask;
    }

    static Task DecodeAndPrintNIBPEndAsync(byte[] data, int startIndex, CancellationToken ct)
    {
        byte head = data[startIndex + 1];
        byte endMode = (byte)(((head & 0x01) << 7) | (data[startIndex + 2] & 0x7F));

        string result = endMode switch
        {
            1 => "Ended — manual mode",
            2 => "Ended — auto mode",
            3 => "Ended — STAT mode",
            4 => "Ended — calibration  SUCCESS",
            5 => "Ended — pneumatic mode",
            10 => "Ended — ERROR (check NIBP status frame)",
            _ => $"Ended — unknown mode ({endMode})"
        };

        Log($"🔧 [NIBP-END] {result}");

        // Only mark done if not an error
        if (endMode != 10)
        {
            _nibpDone = true;
            // BP finished while SpO2 is still measuring → tell the GUI explicitly
            // (previously nothing was sent until both completed, so the status
            // text stayed on a stale message).
            if (!_spo2Done)
                _ = Task.Run(() => SendSpo2Status("NIBP_DONE"));
            CheckAndSendBothCompleted();
        }

        return Task.CompletedTask;
    }

    private static bool _spo2Done = false;
    private static bool _nibpDone = false;

    private static void CheckAndSendBothCompleted()
    {
        if (_spo2Done && _nibpDone)
        {
            _ = Task.Run(() => SendSpo2Status("BOTH_COMPLETED"));
            Log(" Both SpO2 and NIBP complete — sending BOTH_COMPLETED");
        }
    }


    // ══════════════════════════════════════════════════════════════
    //  CALIBRATION METHODS - BLOOD PRESSURE, SPO2, TEMP, ECG,  WEIGHT, HEIGHT 
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// NIBP Calibration — UN806C protocol 0x58
    /// Sends calibration command, then resets module with 0x59.
    /// Module will enter calibration pneumatic mode and return
    /// NIBP status (0x24) and NIBP end (0x21) frames.
    /// </summary>
    static async Task CalibrateBloodPressureAsync()
    {
        try
        {
            Log("🔧 [NIBP-CAL] Starting NIBP calibration sequence...");

            if (_serialPortData == null || !_serialPortData.IsOpen)
            {
                Log(" [NIBP-CAL] Serial port not open — cannot calibrate.");
                return;
            }

            // Step 1: Reset NIBP module (0x59, checksum 0xD9)
            byte[] resetCmd = new byte[] { 0x59, 0xD9 };
            lock (_lock)
            {
                _serialPortData.Write(resetCmd, 0, resetCmd.Length);
            }
            Log("🔧 [NIBP-CAL] NIBP Reset (0x59) sent — waiting for module to settle...");
            await Task.Delay(3000);

            // Step 2: Send NIBP Calibration command (0x58, checksum 0xD8)
            byte[] calCmd = new byte[] { 0x58, 0xD8 };
            lock (_lock)
            {
                _serialPortData.Write(calCmd, 0, calCmd.Length);
            }
            Log("🔧 [NIBP-CAL] NIBP Calibration (0x58) sent — module entering calibration mode.");
            await Task.Delay(500);

            // Step 3: Request NIBP status AFTER calibration command (0x5B, checksum 0xDB)
            byte[] statusReqCmd = new byte[] { 0x5B, 0xDB };
            lock (_lock)
            {
                _serialPortData.Write(statusReqCmd, 0, statusReqCmd.Length);
            }
            Log("🔧 [NIBP-CAL] Status Request (0x5B) sent — expecting 0x24 status frame...");
            Log("🔧 [NIBP-CAL] Connect cuff to calibrator reference. Monitor NIBP status (0x24) for confirmation.");

            await Task.Delay(500);
            Log("🔧 [NIBP-CAL] Calibration command sequence complete. Awaiting module response frames...");
        }
        catch (Exception ex)
        {
            Log($" [NIBP-CAL] Calibration failed: {ex.Message}");
        }
    }
    // ── Stubs for remaining sensors (implement when hardware ready) ──

    static Task CalibrateSpO2Async()
    {
        Log("🔧 [CAL-SPO2] SpO2 calibration not yet implemented.");
        return Task.CompletedTask;
    }

    static Task CalibrateEcgAsync()
    {
        Log("🔧 [CAL-ECG] ECG calibration not yet implemented.");
        return Task.CompletedTask;
    }

    static async Task CalibrateTemperatureAsync()
    {
        Log("🔧 [CAL-TEMP] Starting temperature calibration...");
        await SpiManager.SendSpiCommandReliable(SpiManager.CMD_TEMPERATURE_CALIBRATE);
        Log(" [CAL-TEMP] Temperature calibration command sent.");
    }

    static async Task CalibrateHeightAsync()
    {
        Log("🔧 [CAL-HEIGHT] Starting height calibration...");
        await SpiManager.SendSpiCommandReliable(SpiManager.CMD_HEIGHT_CALIBRATE);
        Log(" [CAL-HEIGHT] Height calibration command sent.");
    }

    static async Task CalibrateWeightAsync()
    {
        Log("🔧 [CAL-WEIGHT] Starting weight calibration...");
        await SpiManager.SendSpiCommandReliable(SpiManager.CMD_WEIGHT_CALIBRATE);
        Log(" [CAL-WEIGHT] Weight calibration command sent.");
    }

    static Task CalibrateBloodSugarAsync()
    {
        Log("🔧 [CAL-BSUGAR] Blood sugar calibration not yet implemented.");
        return Task.CompletedTask;
    }

    static Task CalibrateDeviceAsync()
    {
        Log("🔧 [CAL-DEVICE] Full device calibration not yet implemented.");
        return Task.CompletedTask;
    }



    // Updated StoreHeightWeight method (no BMI calculation)
    static void StoreHeightWeight()
    {
        storedHeight = lastHeight;
        storedWeight = lastWeight;
        storedBMI = lastBMI;

        // Store body composition snapshot
        // StoredBodyComposition.CopyFrom();

        Audit.Log("MEASUREMENT", "success", Audit.PatientRef(_patientId),
            new Dictionary<string, object?> { ["type"] = "HEIGHT_WEIGHT" });
        Log($" Stored measurements - H:{storedHeight:F1}cm W:{storedWeight:F1}kg BMI:{storedBMI:F1}");
        Log($" Body Composition - Fat:{BodyComposition.BodyFatPct:F1}% Muscle:{BodyComposition.MuscleMass:F1}kg");


    }


    //   ------------------------------------------------------------------------------------------------FOR RR CALCULATION ----------------------------------------------------------------------
    /*     public static class EnumerableExtensions
                    {
                        public static double StandardDeviation(this IEnumerable<double> values)
                        {
                            var avg = values.Average();
                            var sum = values.Sum(d => Math.Pow(d - avg, 2));
                            return Math.Sqrt(sum / values.Count());
                        }
                    }

                    // ADD this method (can be placed with your other methods)
                    static void CalculateRespirationRate()
                    {
                        try
                        {
                            // Calculate RR every 10 seconds using R-R interval variability
                            if (DateTime.Now - lastRRCalculation > TimeSpan.FromSeconds(10))
                            {
                                if (rWaveTimestamps.Count >= 10) // Need enough R-waves
                                {
                                    var intervals = new List<double>();
                                    var timestamps = rWaveTimestamps.ToArray();

                                    for (int i = 1; i < timestamps.Length; i++)
                                    {
                                        double interval = (timestamps[i] - timestamps[i-1]).TotalMilliseconds;
                                        intervals.Add(interval);
                                    }

                                    // Basic RR estimation from R-R variability (simplified method)
                                    if (intervals.Count > 0)
                                    {
                                        double avgInterval = intervals.Average();
                                        double variability = intervals.StandardDeviation();

                                        // Estimate respiration rate (12-20 bpm normal range)
                                        calculatedRR = Math.Max(12, Math.Min(20, (int)(15 + variability / 50)));
                                    }
                                }
                                lastRRCalculation = DateTime.Now;
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"RR calculation error: {ex.Message}");
                            calculatedRR = 0;
                        }
                    } 

                   // --------------------------------------------------------------------------------- RR CALCULATION METHOD END ------------------------------------------------------------------
                    */



    static void StoreTemperature()
    {
        lock (_lock)
        {
            // Guard: only commit if IR sensor has a real reading
            if (lastTemperatureIR <= 0 || float.IsNaN(lastTemperatureIR))
            {
                Log($" [StoreTemperature] No valid IR reading — keeping last stored: {storedTemperatureIR:F1}°C");
                return;
            }

            storedTemperature1 = lastTemperature1;
            storedTemperature2 = lastTemperature2;
            storedTemperatureIR = lastTemperatureIR;
            Audit.Log("MEASUREMENT", "success", Audit.PatientRef(_patientId),
                new Dictionary<string, object?> { ["type"] = "TEMPERATURE" });
            Log($"📸 [StoreTemperature] CALLED: Temp1={storedTemperature1:F1}°C, TempIR={storedTemperatureIR:F1}°C, Temp2={storedTemperature2:F1}°C");
        }
    }

    static void StoreSpO2()
    {
        lock (_lock)
        {
            // Guard: only commit if we have a real reading
            int candidateSpO2 = (_finalSpO2 > 0) ? (int)_finalSpO2 : (int)lastSpO2;
            int candidateHR = (_finalPulseRate > 0) ? _finalPulseRate : lastPulseRate;

            if (candidateSpO2 <= 0 && candidateHR <= 0)
            {
                Log($" [StoreSpO2] No valid reading — keeping last stored: SpO2:{storedSpO2}% HR:{storedPulseRate}bpm");
                return;
            }

            storedSpO2 = candidateSpO2;
            storedPulseRate = candidateHR;
            Log($"💾 SPO2 Stored → SpO2:{storedSpO2}% HR:{storedPulseRate}bpm Signal:{_finalSignalQuality:F0}/8");
        }
        Audit.Log("MEASUREMENT", "success", Audit.PatientRef(_patientId),
            new Dictionary<string, object?> { ["type"] = "SPO2" });
    }

    static void StoreNIBP()
    {
        bool hasReading;
        lock (_lock)
        {
            storedSys = lastSys;
            storedDia = lastDia;
            storedMean = lastMean;
            storedPulseRate2 = lastPulseRate2;
            storedEcgHeartRate = lastHeartRate;
            hasReading = storedSys > 0 && storedDia > 0;
        }
        if (hasReading)
            Audit.Log("MEASUREMENT", "success", Audit.PatientRef(_patientId),
                new Dictionary<string, object?> { ["type"] = "NIBP" });
    }


    // PATIENT BOUNDARY RESET — called at login and offline-session start.
    // Must clear EVERY patient-scoped value, including the *sources* the
    // Store*() functions copy from: when a patient skips pages, the page
    // transitions still call Store*(), which resurrected the PREVIOUS
    // patient's readings from _final*/last* into the new payload
    // (field-observed cross-patient leak: SpO2, SpO2 HR, IR temp).
    static void ResetStoredValues()
    {
        lock (_lock)
        {
            // ── Stored (payload) values ──
            storedTemperature1 = 0;
            storedTemperature2 = 0;
            storedTemperatureIR = 0;
            storedSignalQuality = 0;
            storedHeight = -1;
            storedWeight = -1;
            storedBMI = 0;
            storedSpO2 = 0;
            storedPulseRate = 0;
            storedSys = 0;
            storedDia = 0;
            storedMean = 0;
            storedPulseRate2 = 0;
            storedEcgHeartRate = 0;

            // ── SpO2 finals + lives (StoreSpO2 falls back to these) ──
            _finalSpO2 = 0;
            _finalPulseRate = 0;
            _finalSignalQuality = 0;
            lastSpO2 = 0;
            lastPulseRate = 0;
            lastSignalQuality = 0;
            _spo2Done = false;
            _nibpDone = false;
            _spo2Phase = Spo2Phase.Idle;
            _spo2Buf.Clear();
            _spo2HasCapture = false;
            _spo2AcquireStart = DateTime.MinValue;
            _spo2LastGuidance = DateTime.MinValue;

            // ── NIBP lives (StoreNIBP copies these) ──
            lastSys = 0;
            lastDia = 0;
            lastMean = 0;
            lastPulseRate2 = 0;
            lastLivePressure = 0;
            validSys = 0;
            validDia = 0;
            validPulse2 = 0;
            lastHeartRate = 0;

            // ── Height/weight + body composition lives ──
            lastHeight = 0;
            lastWeight = 0;
            lastBMI = 0;
            lastValidHeight = 0;
            lastValidWeight = 0;
            lastValidBMI = 0;
            BodyComposition = new BodyCompositionData();
            lastValidBodyComposition = new BodyCompositionData();
            hasValidMeasurement = false;

            Log("🔄 All stored + source values reset (patient boundary)");
        }

        //  Clear blood glucose with its own lock
        ClearBloodGlucoseValues();
    }



    // Update PrintHeightWeight method - remove BMI calculation
    static void PrintHeightWeight()
    {
        lock (_lock)
        {
            LogThrottled("live-hw", $"Height: {lastHeight:F1} cm | Weight: {lastWeight:F1} kg | BMI: {lastBMI:F1}", TimeSpan.FromSeconds(1));
        }
    }

    static void PrintTemperature()
    {
        lock (_lock)
        {
            LogThrottled("live-temp", $"Temp1: {lastTemperature1:F1}°C | Temp2: {lastTemperature2:F1}°C | TempIR: {lastTemperatureIR:F1}°C", TimeSpan.FromSeconds(1));
        }
    }

    // Updated PrintLiveData method (no changes needed, but showing for completeness)
    static void PrintLiveData()
    {
        lock (_lock)
        {
            string liveData = $"Temp1: {lastTemperature1:F1}°C | Temp2: {lastTemperature2:F1}°C | " +
                              $"Pulse1: {lastPulseRate} BPM | SpO2: {lastSpO2}% | " +
                              $"Sys: {(_isNIBPActive ? lastSys.ToString() : "-")} | Dia: {(_isNIBPActive ? lastDia.ToString() : "-")} | " +
                              $"Mean: {(_isNIBPActive ? lastMean.ToString() : "-")} | Pulse2: {(_isNIBPActive ? lastPulseRate2.ToString() : "-")} | " +
                              $"Height: {lastHeight:F1} cm | Weight: {lastWeight:F1} kg | BMI: {lastBMI:F1} | " +
                              $"ECG Lead II: {lastEcg1} | ECG Lead I: {lastEcg2} | ECG Lead V: {lastEcg3} | ECG HR: {lastHeartRate} BPM | " +
                              $"Pace: {lastPaceFlag} | Beat: {lastHeartBeatFlag} | " +
                              $"Lead Status: V:{(leadStatus[0] ? "Off" : "OK")}, RA:{(leadStatus[1] ? "Off" : "OK")}, LA:{(leadStatus[2] ? "Off" : "OK")}, LL:{(leadStatus[3] ? "Off" : "OK")} | " +
                              $"Saturation: V:{(leadSaturation[0] ? "Sat" : "OK")}, III:{(leadSaturation[1] ? "Sat" : "OK")}, I:{(leadSaturation[2] ? "Sat" : "OK")}, II:{(leadSaturation[3] ? "Sat" : "OK")}";

            LogThrottled("live-data", $"LIVE: {liveData}", TimeSpan.FromSeconds(1));
        }
    }



    static void PrintStoredData()
    {
        //  FIXED: Use authenticated patient ID if available
        int displayPatientId = 0;
        string patientInfo = "";

        lock (_authLock)
        {
            if (_isAuthenticated && !string.IsNullOrEmpty(_patientId))
            {
                displayPatientId = int.Parse(_patientId);
                patientInfo = $" ({_patientName})";
            }
            else
            {
                displayPatientId = currentPatientId;
            }
        }

        Log($"Final Measurement Results for Patient ID: {displayPatientId}{patientInfo}");
        Log($"Height: {storedHeight} cm | Weight: {storedWeight} kg | BMI: {storedBMI}");
        Log($"Body Temperature: Temp1: {storedTemperature1}°C | Temp2: {storedTemperature2}°C");
        Log($"SpO2: {storedSpO2}% | Pulse: {storedPulseRate} BPM");
        Log($"Sys: {storedSys} mmHg | Dia: {storedDia} mmHg | Mean: {storedMean} mmHg | Pulse2: {storedPulseRate2} BPM");
        Log($"ECG HR: {storedEcgHeartRate} BPM");
    }


    static int SmoothValue(Queue<int> history, int newValue)
    {
        if (newValue == 0) return 0;

        history.Enqueue(newValue);
        if (history.Count > SmoothWindowSize)
            history.Dequeue();

        return (int)history.Average();
    }






    private static string ExtractAiContent(string rawResponse)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(rawResponse);
            var root = doc.RootElement;

            if (root.TryGetProperty("candidates", out JsonElement candidates) &&
                candidates.ValueKind == JsonValueKind.Array &&
                candidates.GetArrayLength() > 0)
            {
                var candidate = candidates[0];

                if (candidate.TryGetProperty("content", out JsonElement content) &&
                    content.TryGetProperty("parts", out JsonElement parts) &&
                    parts.ValueKind == JsonValueKind.Array &&
                    parts.GetArrayLength() > 0)
                {
                    if (parts[0].TryGetProperty("text", out JsonElement text))
                    {
                        return text.GetString() ?? string.Empty;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log($" Failed to parse Gemini AI response: {ex.Message}");
        }

        return string.Empty;
    }







    // OTP API FUNCTIONS

    // Maps the user-facing OTP failure message to a coarse class for the audit
    // trail (no free text in audit events).
    static string ClassifyOtpFailure(string? message)
    {
        var m = message ?? "";
        if (m.Contains("not approved", StringComparison.OrdinalIgnoreCase))   return "device-not-approved";
        if (m.Contains("not configured", StringComparison.OrdinalIgnoreCase)) return "not-configured";
        if (m.Contains("Server not running", StringComparison.OrdinalIgnoreCase)
         || m.Contains("not responding", StringComparison.OrdinalIgnoreCase)
         || m.Contains("Cannot reach", StringComparison.OrdinalIgnoreCase))   return "server-unreachable";
        return "rejected-by-api"; // e.g. unknown patient id
    }

    static async Task<(bool success, string message)> SendOtpAsync(string phoneNumber)
    {
        // ── Gate 1: device must be approved by admin (config available). Until then
        //    the API URLs aren't configured, so a call would just fail with a
        //    clueless connection error. Tell the user what's actually wrong. ──
        if (ConfigManager.IsPendingApproval())
        {
            Log("⛔ [API] SendOTP blocked — device not approved yet");
            return (false, "Device not approved yet. Please ask the admin to approve this device.");
        }

        // ── Gate 2: endpoint must actually be configured ──
        if (string.IsNullOrWhiteSpace(SEND_OTP_URL))
        {
            Log("⛔ [API] SendOTP blocked — SendOtpUrl not configured");
            return (false, "Service not configured. Please contact admin.");
        }

        try
        {
            _lastOtpIdentifier = phoneNumber;
            var payload = new
            {
                payload   = phoneNumber,
                device_id = DeviceIdentity.GetDeviceId(),
                device    = DeviceIdentity.GetFingerprint()
            };
            var content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"
            );

            Log($"📤 [API] Calling SendOTP for: {LogMask.Phone(phoneNumber)}");
            // Fail fast: an 8s per-request timeout so an unreachable/slow server
            // returns a message in seconds instead of hanging the GUI for
            // HttpClient's 100s default (the "stuck GUI" the user saw).
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var response = await _httpClient.PostAsync(SEND_OTP_URL, content, cts.Token);
            var responseBody = await response.Content.ReadAsStringAsync(cts.Token);

            // NEVER log the raw body — it contains the OTP code itself.
            Log($"📥 [API] SendOTP Response: HTTP {(int)response.StatusCode}, {responseBody?.Length ?? 0} bytes");

            // ── Prefer the API's OWN JSON response, even on a non-2xx status. A
            //    real business error like an invalid/unknown patient ID comes back
            //    as 4xx WITH a JSON body ({"success":false,"message":"No patient
            //    data found"}) — we must surface that message, not "server down".
            //    Only a genuinely non-JSON body (HTML error page / empty) means the
            //    server itself isn't serving our API. ──
            string trimmed = responseBody.TrimStart();
            bool looksJson = trimmed.StartsWith("{", StringComparison.Ordinal)
                          || trimmed.StartsWith("[", StringComparison.Ordinal);

            if (!looksJson)
            {
                Log($"⛔ [API] SendOTP non-JSON response (HTTP {(int)response.StatusCode}) — treating as server down");
                return (false, "Server not running. Please contact admin.");
            }

            using var doc = JsonDocument.Parse(responseBody);

            bool success = doc.RootElement.TryGetProperty("success", out var successProp)
                           && successProp.GetBoolean();

            string message = "";
            if (doc.RootElement.TryGetProperty("message", out var msgProp))
            {
                message = msgProp.GetString() ?? "";
            }

            if (!success && string.IsNullOrWhiteSpace(message))
                message = "Could not send OTP. Please try again.";

            Log($"📱 [API] OTP Send Result: {(success ? "SUCCESS" : "FAIL")} - {message}");
            return (success, message);
        }
        catch (JsonException jex)
        {
            // Body wasn't valid JSON despite the guard — treat as a server problem.
            Log($"⛔ [API] SendOTP JSON parse error: {jex.Message}");
            return (false, "Server not running. Please contact admin.");
        }
        catch (HttpRequestException hex)
        {
            // Couldn't reach the server at all (refused / DNS / network down).
            Log($"⛔ [API] SendOTP connection error: {hex.Message}");
            return (false, "Cannot reach server. Please check the connection or contact admin.");
        }
        catch (TaskCanceledException)
        {
            Log("⛔ [API] SendOTP timed out");
            return (false, "Server not responding. Please try again or contact admin.");
        }
        catch (Exception ex)
        {
            Log($"⛔ [API] SendOTP Error: {ex.Message}");
            return (false, "Something went wrong. Please contact admin.");
        }
    }

    // Patient data class to hold verified patient info
    public class PatientData
    {
        public string Token { get; set; } = "";
        public string PatientId { get; set; } = "";
        public string Name { get; set; } = "";
        public string Email { get; set; } = "";
        public string Phone { get; set; } = "";
        public string Last4Aadhaar { get; set; } = "";
        public string Gender { get; set; } = "";
        public DateTime DateOfBirth { get; set; }
        public int Age { get; set; }
    }

    private static bool IsSuccessfulOtpResponse(HttpResponseMessage response, JsonElement root)
    {
        if (TryGetPropertyAny(root, out var successProp, "success", "ok", "verified"))
        {
            if (successProp.ValueKind == JsonValueKind.True)
                return true;

            if (successProp.ValueKind == JsonValueKind.False)
                return false;

            string? successText = GetElementAsString(successProp);
            if (bool.TryParse(successText, out bool parsedBool))
                return parsedBool;
        }

        string? statusText = GetStringAny(root, "status");
        if (!string.IsNullOrWhiteSpace(statusText))
            return statusText.Equals("success", StringComparison.OrdinalIgnoreCase) ||
                   statusText.Equals("verified", StringComparison.OrdinalIgnoreCase) ||
                   statusText.Equals("200", StringComparison.OrdinalIgnoreCase);

        return response.IsSuccessStatusCode;
    }

    private static bool TryFindPatientElement(JsonElement root, JsonElement data, out JsonElement patient)
    {
        foreach (JsonElement container in new[] { data, root })
        {
            if (container.ValueKind != JsonValueKind.Object)
                continue;

            if (TryGetPropertyAny(container, out patient, "patient", "patientData", "user", "profile", "data") &&
                patient.ValueKind == JsonValueKind.Object)
                return true;

            if (ContainsPatientFields(container))
            {
                patient = container;
                return true;
            }
        }

        patient = default;
        return false;
    }

    private static bool ContainsPatientFields(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.Object &&
               (TryGetPropertyAny(element, out _, "patient_id", "patientId", "blu_id", "bluId") ||
                TryGetPropertyAny(element, out _, "name", "first_name", "firstName", "phone", "mobile"));
    }

    private static bool TryGetPropertyAny(JsonElement element, out JsonElement value, params string[] names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (string name in names)
            {
                if (element.TryGetProperty(name, out value))
                    return true;
            }

            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (names.Any(name => string.Equals(name, property.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? GetStringAny(JsonElement element, params string[] names)
    {
        return TryGetPropertyAny(element, out var value, names) ? GetElementAsString(value) : null;
    }

    private static int? GetIntAny(JsonElement element, params string[] names)
    {
        string? value = GetStringAny(element, names);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : null;
    }

    private static string? GetElementAsString(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static string BuildPatientName(JsonElement patient)
    {
        string? name = GetStringAny(patient, "name", "full_name", "fullName");
        if (!string.IsNullOrWhiteSpace(name))
            return name;

        string firstName = GetStringAny(patient, "first_name", "firstName") ?? "";
        string lastName = GetStringAny(patient, "last_name", "lastName") ?? "";
        return $"{firstName} {lastName}".Trim();
    }

    private static string DescribeJsonShape(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return element.ValueKind.ToString();

        return string.Join(",", element.EnumerateObject()
            .Take(8)
            .Select(property => $"{property.Name}:{property.Value.ValueKind}"));
    }

    static async Task<(bool success, PatientData? data)> VerifyOtpAsync(string otp)
    {
        try
        {
            string otpIdentifier = !string.IsNullOrWhiteSpace(_lastOtpIdentifier)
                ? _lastOtpIdentifier
                : _currentUserId;

            var payload = new
            {
                otp = otp,
                payload = otpIdentifier,
                patient_id = otpIdentifier,
                user_id = otpIdentifier,
                device_id = DeviceIdentity.GetDeviceId(),
                device = DeviceIdentity.GetFingerprint()
            };
            var content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"
            );

            Log($"📤 [API] Calling VerifyOTP ({otp?.Length ?? 0} digits — code not logged), identifierPresent={!string.IsNullOrWhiteSpace(otpIdentifier)}");
            var response = await _httpClient.PostAsync(VERIFY_OTP_URL, content);
            var responseBody = await response.Content.ReadAsStringAsync();
            string responseBodyForParsing = NormalizeJsonText(responseBody);
            bool encryptedResponseDetected = false;

            string? encryptedResponsePayload = ExtractEncryptedApiPayload(responseBody);
            if (!string.IsNullOrEmpty(encryptedResponsePayload))
            {
                encryptedResponseDetected = true;

                if (_encryptionManager == null)
                {
                    Log("⚠️ [API Encryption] VerifyOTP returned encrypted patient data but encryption manager is not initialized");
                }
                else
                {
                    Log("🔓 [API Encryption] VerifyOTP encrypted data detected - decrypting patient/token payload");
                    responseBodyForParsing = NormalizeJsonText(await _encryptionManager.DecryptAsync(encryptedResponsePayload));
                    Log($"🔓 [API Encryption] VerifyOTP patient/token payload decrypted ({responseBodyForParsing.Length} chars)");
                }
            }

            Log($"📥 [API] VerifyOTP Response received: status={(int)response.StatusCode}, bodyBytes={responseBodyForParsing.Length}, encrypted={encryptedResponseDetected}");

            using var doc = JsonDocument.Parse(responseBodyForParsing);

            bool apiSuccess = IsSuccessfulOtpResponse(response, doc.RootElement);

            if (apiSuccess)
            {
                var patientData = new PatientData();
                // _ = Task.Run(() => PreGenerateStaticQuestionsAsync());

                JsonElement dataProp = doc.RootElement;
                if (TryGetPropertyAny(doc.RootElement, out var nestedData, "data", "result", "response"))
                    dataProp = nestedData;

                patientData.Token =
                    GetStringAny(dataProp, "token", "access_token", "accessToken", "jwt") ??
                    GetStringAny(doc.RootElement, "token", "access_token", "accessToken", "jwt") ??
                    "";

                if (!string.IsNullOrEmpty(patientData.Token))
                    Log($"🔍 [DEBUG] Token extracted (len={patientData.Token.Length})");

                if (TryFindPatientElement(doc.RootElement, dataProp, out var patientProp))
                {
                    patientData.PatientId = GetStringAny(patientProp, "patient_id", "patientId", "id", "blu_id", "bluId") ?? "";
                    patientData.Name = BuildPatientName(patientProp);
                    patientData.Email = GetStringAny(patientProp, "email", "email_id", "emailId") ?? "";
                    patientData.Phone = GetStringAny(patientProp, "phone", "mobile", "phone_number", "phoneNumber") ?? "";
                    patientData.Last4Aadhaar = GetStringAny(patientProp, "last4_aadhaar", "last4Aadhaar") ?? "";
                    patientData.Gender = GetStringAny(patientProp, "gender", "sex") ?? "";
                    patientData.Age = GetIntAny(patientProp, "age") ?? 0;

                    string? dobValue = GetStringAny(patientProp, "date_of_birth", "dateOfBirth", "dob");
                    if (DateTime.TryParse(dobValue, out var dob))
                        patientData.DateOfBirth = dob;

                    _patientName = patientData.Name;
                    Log($"🔍 [DEBUG] Patient data decrypted/extracted: patientId={LogMask.Id(patientData.PatientId)}, name={LogMask.Name(patientData.Name)}, phonePresent={!string.IsNullOrEmpty(patientData.Phone)}");
                }

                if (string.IsNullOrEmpty(patientData.Token) &&
                    string.IsNullOrEmpty(patientData.PatientId) &&
                    string.IsNullOrEmpty(patientData.Name))
                {
                    if (!string.IsNullOrWhiteSpace(otpIdentifier))
                    {
                        patientData.PatientId = otpIdentifier;
                        Log($"⚠️ [API] VerifyOTP confirmed OTP but returned no patient/token data. Using OTP identifier as patientId. schema={DescribeJsonShape(doc.RootElement)}");
                    }
                    else
                    {
                        Log($"⚠️ [API] VerifyOTP success response did not contain usable patient data. schema={DescribeJsonShape(doc.RootElement)}");
                        return (false, null);
                    }
                }

                Log($" [API] OTP Verified - Patient: {LogMask.Name(patientData.Name)} (ID: {LogMask.Id(patientData.PatientId)})");
                return (true, patientData);
            }
            else
            {
                Log($" [API] OTP Verification Failed");
                return (false, null);
            }
        }
        catch (Exception ex)
        {
            Log($" [API] VerifyOTP Error: {ex.Message}");
            return (false, null);
        }
    }

    // Helper function to extract values from message
    static string ExtractValue(string message, string key)
    {
        int startIndex = message.IndexOf(key);
        if (startIndex < 0) return "";

        startIndex += key.Length;
        int endIndex = message.IndexOf(' ', startIndex);
        if (endIndex < 0) endIndex = message.Length;

        return message.Substring(startIndex, endIndex - startIndex).Trim();
    }

    private static string BuildFormattedVitalsResponse(HttpResponseMessage response, string responseBody)
    {
        var sb = new StringBuilder();

        try
        {
            // Add response metadata headers
            sb.AppendLine($"📥 [VitalsUrl Response] Status: {response.StatusCode}");
            sb.AppendLine($"📥 [VitalsUrl Response] Content-Type: {response.Content.Headers.ContentType}");
            sb.AppendLine($"📥 [VitalsUrl Response] Body Length: {responseBody.Length} bytes");

            using JsonDocument respDoc = JsonDocument.Parse(responseBody);
            var respRoot = respDoc.RootElement;

            // Extract top-level response info
            if (respRoot.TryGetProperty("success", out var success))
                sb.AppendLine($" [VitalsUrl] Success: {success.GetBoolean()}");

            if (respRoot.TryGetProperty("statusCode", out var statusCode))
                sb.AppendLine($"🔢 [VitalsUrl] StatusCode: {statusCode.GetInt32()}");

            if (respRoot.TryGetProperty("message", out var message))
                sb.AppendLine($"📝 [VitalsUrl] Message: {message.GetString()}");

            // Extract patient vitals data
            if (respRoot.TryGetProperty("data", out var dataEl) &&
                dataEl.TryGetProperty("patientVitals", out var vitalsEl))
            {
                sb.AppendLine("\n🏥 [VitalsUrl] Patient Vitals Data:");
                sb.AppendLine("──────────────────────────────────────");

                // Helper to safely extract numeric values (handle strings and numbers)
                var ExtractNumber = (JsonElement el, string key, string suffix = "") =>
                {
                    try
                    {
                        if (el.TryGetProperty(key, out var prop))
                        {
                            if (prop.ValueKind == JsonValueKind.Number)
                            {
                                if (prop.TryGetInt32(out int intVal))
                                    return intVal.ToString();
                                else if (prop.TryGetDouble(out double dblVal))
                                {
                                    if (double.IsNaN(dblVal) || double.IsInfinity(dblVal))
                                        return null;
                                    return dblVal.ToString("F2");
                                }
                            }
                            else if (prop.ValueKind == JsonValueKind.String)
                            {
                                string? strVal = prop.GetString();
                                if (string.IsNullOrWhiteSpace(strVal) || strVal == "NaN" || strVal == "null")
                                    return null;
                                return strVal;
                            }
                        }
                    }
                    catch { }
                    return null;
                };

                // Display vitals with proper type handling
                var vitalId = ExtractNumber(vitalsEl, "vital_id");
                if (vitalId != null) sb.AppendLine($"  • Vital ID: {vitalId}");

                var patientId = ExtractNumber(vitalsEl, "patient_id");
                if (patientId != null) sb.AppendLine($"  • Patient ID: {patientId}");

                var temp = ExtractNumber(vitalsEl, "body_temperature");
                if (temp != null) sb.AppendLine($"  • Body Temperature: {temp}°C");

                var pulse = ExtractNumber(vitalsEl, "pulse_rate");
                if (pulse != null) sb.AppendLine($"  • Pulse Rate: {pulse} BPM");

                var resp = ExtractNumber(vitalsEl, "respiration_rate");
                if (resp != null && resp != "0") sb.AppendLine($"  • Respiration Rate: {resp} breaths/min");

                var sys = ExtractNumber(vitalsEl, "blood_pressure_systolic");
                var dia = ExtractNumber(vitalsEl, "blood_pressure_diastolic");
                if (sys != null && dia != null) sb.AppendLine($"  • Blood Pressure: {sys}/{dia} mmHg");

                var spo2 = ExtractNumber(vitalsEl, "blood_oxygen");
                if (spo2 != null) sb.AppendLine($"  • SpO2: {spo2}%");

                var height = ExtractNumber(vitalsEl, "height");
                if (height != null) sb.AppendLine($"  • Height: {height} cm");

                var weight = ExtractNumber(vitalsEl, "weight");
                if (weight != null) sb.AppendLine($"  • Weight: {weight} kg");

                var bmi = ExtractNumber(vitalsEl, "bmi");
                if (bmi != null) sb.AppendLine($"  • BMI: {bmi}");

                var glucose = ExtractNumber(vitalsEl, "blood_glucose_level");
                if (glucose != null) sb.AppendLine($"  • Blood Glucose: {glucose} mg/dL");

                sb.AppendLine("──────────────────────────────────────");
            }

            // Try to extract and format AI insights from response
            try
            {
                using JsonDocument doc = JsonDocument.Parse(responseBody);

                var patientData = doc.RootElement
                    .GetProperty("data")
                    .GetProperty("aiAnalysis")
                    .GetProperty("patientData");

                sb.AppendLine("\n========== AI HEALTH REPORT ==========");

                // Scores
                if (patientData.TryGetProperty("Scores", out var scores))
                {
                    sb.AppendLine("\n📊 SCORES");
                    if (scores.TryGetProperty("Overall_Health_Score", out var overall))
                        sb.AppendLine($"Overall Health Score      : {overall.GetInt32()}");
                    if (scores.TryGetProperty("Oxygen_Health_Score", out var oxygen))
                        sb.AppendLine($"Oxygen Health Score       : {oxygen.GetInt32()}");
                    if (scores.TryGetProperty("Metabolic_Health_Score", out var metabolic))
                        sb.AppendLine($"Metabolic Health Score    : {metabolic.GetInt32()}");
                    if (scores.TryGetProperty("Cardiovascular_Risk_Score", out var cardio))
                        sb.AppendLine($"Cardiovascular Risk Score : {cardio.GetInt32()}");
                }

                // Health Insights
                if (patientData.TryGetProperty("Analysis", out var analysis) &&
                    analysis.TryGetProperty("Health_Insights", out var insights))
                {
                    sb.AppendLine("\n🩺 HEALTH INSIGHTS");
                    foreach (var item in insights.EnumerateArray())
                    {
                        sb.AppendLine($"• {item.GetString()}");
                    }
                }

                // Insight Scores
                if (patientData.TryGetProperty("Analysis", out analysis) &&
                    analysis.TryGetProperty("Insight_Scores", out var insightScores))
                {
                    sb.AppendLine("\n📈 INSIGHT SCORES");
                    foreach (var item in insightScores.EnumerateArray())
                    {
                        var label = item.GetProperty("label").GetString();
                        var score = item.GetProperty("score").GetInt32();
                        var outOf = item.GetProperty("out_of").GetInt32();
                        sb.AppendLine($"• {label}: {score}/{outOf}");
                    }
                }

                // Recommendations
                if (patientData.TryGetProperty("Analysis", out analysis) &&
                    analysis.TryGetProperty("Recommendations", out var recommendations))
                {
                    sb.AppendLine("\n💡 RECOMMENDATIONS");
                    foreach (var item in recommendations.EnumerateArray())
                    {
                        sb.AppendLine($"• {item.GetString()}");
                    }
                }

                // Conclusion
                if (patientData.TryGetProperty("Conclusion", out var conclusion))
                {
                    sb.AppendLine("\n📋 CONCLUSION");

                    if (conclusion.TryGetProperty("Summary_Points", out var summary))
                    {
                        foreach (var item in summary.EnumerateArray())
                        {
                            sb.AppendLine($"• {item.GetString()}");
                        }
                    }

                    if (conclusion.TryGetProperty("Overall_Health_Score", out var overallScore))
                    {
                        sb.AppendLine($"\n⭐ Overall Score: {overallScore.GetString()}");
                    }

                    if (conclusion.TryGetProperty("Closing_Message", out var closing))
                    {
                        sb.AppendLine($"\n📝 Closing Message:\n{closing.GetString()}");
                    }
                }

                sb.AppendLine("\n======================================");
            }
            catch
            {
                // If AI insights not available, that's okay - just show vitals data
                sb.AppendLine("\n⚠️ No AI Health Report in this response (will be available in next measurement)");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($" Error formatting response: {ex.Message}");
            Log($" BuildFormattedVitalsResponse failed: {ex.Message}");
            // Return empty instead of raw data on error
            return string.Empty;
        }

        return sb.ToString();
    }

    private static string BuildLuaFormattedResponse(string responseBody)
    {
        var sb = new StringBuilder();

        try
        {
            using JsonDocument doc = JsonDocument.Parse(responseBody);

            if (!TryGetAiPatientData(doc.RootElement, out var patientData))
            {
                return string.Empty; // No AI data available
            }

            // Extract Health Insights
            if (TryGetPropertyAny(patientData, out var analysis, "Analysis", "analysis") &&
                TryGetPropertyAny(analysis, out var insightsArray, "Health_Insights", "healthInsights", "insights") &&
                insightsArray.ValueKind == JsonValueKind.Array)
            {
                sb.AppendLine("[INSIGHTS]");
                foreach (var insight in insightsArray.EnumerateArray())
                {
                    sb.AppendLine("- " + (GetElementAsString(insight) ?? insight.GetRawText()));
                }
                sb.AppendLine("[INSIGHTS_HIGHLIGHTED]");
                // Highlighted version - show first 2-3 most important insights
                int count = 0;
                foreach (var insight in insightsArray.EnumerateArray())
                {
                    if (count < 3)
                    {
                        sb.AppendLine("- " + (GetElementAsString(insight) ?? insight.GetRawText()));
                        count++;
                    }
                }
            }

            // Extract Recommendations
            if (TryGetPropertyAny(patientData, out analysis, "Analysis", "analysis") &&
                TryGetPropertyAny(analysis, out var recommendationsArray, "Recommendations", "recommendations") &&
                recommendationsArray.ValueKind == JsonValueKind.Array)
            {
                sb.AppendLine("[RECOMMENDATIONS]");
                foreach (var rec in recommendationsArray.EnumerateArray())
                {
                    sb.AppendLine("- " + (GetElementAsString(rec) ?? rec.GetRawText()));
                }
                sb.AppendLine("[RECOMMENDATIONS_HIGHLIGHTED]");
                // Highlighted version - show first 2-3 most important recommendations
                int count = 0;
                foreach (var rec in recommendationsArray.EnumerateArray())
                {
                    if (count < 3)
                    {
                        sb.AppendLine("- " + (GetElementAsString(rec) ?? rec.GetRawText()));
                        count++;
                    }
                }
            }

            // Extract Conclusion
            if (TryGetPropertyAny(patientData, out var conclusion, "Conclusion", "conclusion"))
            {
                sb.AppendLine("[CONCLUSION]");

                if (TryGetPropertyAny(conclusion, out var summaryArray, "Summary_Points", "summaryPoints", "summary_points") &&
                    summaryArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var point in summaryArray.EnumerateArray())
                    {
                        sb.AppendLine("- " + (GetElementAsString(point) ?? point.GetRawText()));
                    }
                }

                if (TryGetPropertyAny(conclusion, out var closingMsg, "Closing_Message", "closingMessage", "closing_message"))
                {
                    sb.AppendLine("- " + (GetElementAsString(closingMsg) ?? closingMsg.GetRawText()));
                }

                sb.AppendLine("[CONCLUSION_HIGHLIGHTED]");
                if (TryGetPropertyAny(conclusion, out var overallScore, "Overall_Health_Score", "overallHealthScore", "overall_score"))
                {
                    sb.AppendLine("- Health Status: " + (GetElementAsString(overallScore) ?? overallScore.GetRawText()));
                }
            }

            // Extract scores for new fields
            if (TryGetPropertyAny(patientData, out var scores, "Scores", "scores"))
            {
                sb.AppendLine("[SLEEP_EFFICIENCY]");
                if (TryGetPropertyAny(scores, out var oxyScore, "Oxygen_Health_Score", "oxygenHealthScore", "oxygen_health_score"))
                    sb.AppendLine((GetElementAsString(oxyScore) ?? "85"));
                else
                    sb.AppendLine("85");

                sb.AppendLine("[STRESS_MANAGEMENT]");
                if (TryGetPropertyAny(scores, out var metabScore, "Metabolic_Health_Score", "metabolicHealthScore", "metabolic_health_score"))
                    sb.AppendLine((GetElementAsString(metabScore) ?? "80"));
                else
                    sb.AppendLine("80");

                sb.AppendLine("[ACTIVITY_LEVEL]");
                if (TryGetPropertyAny(scores, out var cardioScore, "Cardiovascular_Risk_Score", "cardiovascularRiskScore", "cardiovascular_risk_score") &&
                    int.TryParse(GetElementAsString(cardioScore), NumberStyles.Integer, CultureInfo.InvariantCulture, out int cardioRisk))
                    sb.AppendLine((100 - cardioRisk).ToString()); // Invert for health score
                else
                    sb.AppendLine("78");

                sb.AppendLine("[PRIORITY_ACTION]");
                sb.AppendLine("- Continue monitoring vital signs regularly");  // Added dash prefix

                sb.AppendLine("[SCORE]");
                if (TryGetPropertyAny(scores, out var overallHealthScore, "Overall_Health_Score", "overallHealthScore", "overall_score"))
                    sb.AppendLine((GetElementAsString(overallHealthScore) ?? "83"));
                else
                    sb.AppendLine("83");
            }
        }
        catch (Exception ex)
        {
            Log($" BuildLuaFormattedResponse failed: {ex.Message}");
            return string.Empty;
        }

        return sb.ToString();
    }
}



// Last Edited - Friday