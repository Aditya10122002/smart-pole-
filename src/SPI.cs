using System;
using System.Device.Spi;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal static class SpiManager
{
    private const int SpiBufferSize = 512;

    private static SpiDevice? _spiDevice;
    private static byte _currentUserAge = 25;
    private static byte _currentUserGender = 1;
    private static bool _hasPatientData = false;

    public const byte CMD_NONE = 0x00;
    public const byte CMD_HOME = 0x01;
    public const byte CMD_HEIGHT_WEIGHT = 0x02;
    public const byte CMD_TEMPERATURE_START = 0x03;
    public const byte CMD_TEMPERATURE_STOP = 0x04;
    public const byte CMD_WEIGHT_CALIBRATE = 0x05;
    public const byte CMD_HEIGHT_CALIBRATE = 0x06;
    public const byte CMD_TEMPERATURE_CALIBRATE = 0x07;
    public const uint MAGIC_HEADER = 0xDEADBEEF;
    public const uint MAGIC_HEADER_RANGE = 0xABCD1234;

    public static void InitializeSpi()
    {
        try
        {
            _spiDevice?.Dispose();

            var settings = new SpiConnectionSettings(1, 0)
            {
                Mode = SpiMode.Mode0,
                ClockFrequency = 1_000_000,
                DataBitLength = 8,
                ChipSelectLineActiveState = 0
            };

            _spiDevice = SpiDevice.Create(settings);
            Log($"[OK] SPI initialized (Binary Protocol - {Marshal.SizeOf<HealthData>()} bytes)");
            SendStartupCommands();
        }
        catch (Exception ex)
        {
            Log($"❌ SPI initialization error: {ex.Message}");
        }
    }

    public static void SetUserProfile(byte age, byte gender, bool hasPatientData)
    {
        _currentUserAge = age;
        _currentUserGender = gender;
        _hasPatientData = hasPatientData;
    }

    public static void SendStartupCommands()
    {
        Log("🚀 Sending SPI startup commands...");

        if (!_hasPatientData)
        {
            _currentUserAge = 25;
            _currentUserGender = 1;
        }

        SendSpiCommand(CMD_NONE, _currentUserAge, _currentUserGender);
        Thread.Sleep(100);
        Log($"✅ SPI startup: User profile set (Age:{_currentUserAge} Gender:{(_currentUserGender == 1 ? "Male" : "Female")})");
    }

    public static void SendSpiCommand(byte command, byte age = 0, byte gender = 0)
    {
        try
        {
            if (_spiDevice == null)
            {
                Log("⚠️ SPI device not initialized");
                return;
            }

            byte actualAge = _hasPatientData ? _currentUserAge : (byte)25;
            byte actualGender = _hasPatientData ? _currentUserGender : (byte)1;

            if (age > 0) actualAge = age;
            if (gender > 0) actualGender = gender;

            HealthData commandData = new()
            {
                Magic = MAGIC_HEADER,
                CommandFromMaster = command,
                UserAge = actualAge,
                UserGender = actualGender,
                Reserved = 0,
                BodyType = new byte[16]
            };

            commandData.Checksum = CalculateChecksum(commandData);

            int structSize = Marshal.SizeOf<HealthData>();
            byte[] txData = new byte[SpiBufferSize];
            byte[] rxData = new byte[SpiBufferSize];
            IntPtr ptr = Marshal.AllocHGlobal(structSize);
            try
            {
                Marshal.StructureToPtr(commandData, ptr, false);
                Marshal.Copy(ptr, txData, 0, structSize);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }

            for (int i = 0; i < 3; i++)
            {
                _spiDevice.TransferFullDuplex(txData, rxData);
                Thread.Sleep(50);
            }

            string cmdName = command switch
            {
                CMD_NONE => "NONE",
                CMD_HOME => "HOME",
                CMD_HEIGHT_WEIGHT => "HEIGHT_WEIGHT",
                CMD_TEMPERATURE_START => "TEMPERATURE_START",
                CMD_TEMPERATURE_STOP => "TEMPERATURE_STOP",
                CMD_WEIGHT_CALIBRATE => "WEIGHT_CALIBRATE",
                CMD_HEIGHT_CALIBRATE => "HEIGHT_CALIBRATE",
                CMD_TEMPERATURE_CALIBRATE => "TEMPERATURE_CALIBRATE",
                _ => $"0x{command:X2}"
            };

            Log($"✅ SPI: {cmdName} | Age:{actualAge} Gender:{(actualGender == 1 ? "Male" : "Female")} | HasPatientData:{_hasPatientData}");
            Thread.Sleep(200);
        }
        catch (Exception ex)
        {
            Log($"❌ SPI error: {ex.Message}");
        }
    }

    public static async Task SendSpiCommandReliable(byte command, int retries = 10, int delayMs = 150)
    {
        string cmdName = command switch
        {
            CMD_NONE => "NONE",
            CMD_HOME => "HOME",
            CMD_HEIGHT_WEIGHT => "HEIGHT_WEIGHT",
            CMD_TEMPERATURE_START => "TEMPERATURE_START",
            CMD_TEMPERATURE_STOP => "TEMPERATURE_STOP",
            _ => $"0x{command:X2}"
        };

        Log($"🚀 [SPI RELIABLE] Sending {cmdName} - {retries} attempts over {(retries * delayMs) / 1000.0:F1}s");

        for (int i = 0; i < retries; i++)
        {
            SendSpiCommand(command);
            await Task.Delay(delayMs);
            if ((i + 1) % 3 == 0 || i == retries - 1)
            {
                Log($"   └─ Progress: {i + 1}/{retries} commands sent");
            }
        }

        await Task.Delay(300);
        Log($"✅ [SPI RELIABLE] Completed {cmdName}");
    }

    public static byte[]? RequestSpiHealthData()
    {
        try
        {
            if (_spiDevice == null)
            {
                Log("⚠️ SPI device not initialized");
                return null;
            }

            byte[] txData = new byte[SpiBufferSize];
            byte[] rxData = new byte[SpiBufferSize];
            _spiDevice.TransferFullDuplex(txData, rxData);
            return rxData;
        }
        catch (Exception ex)
        {
            Log($"❌ SPI request error: {ex.Message}");
            return null;
        }
    }

    public static T ByteArrayToStruct<T>(byte[] bytes) where T : struct
    {
        int structSize = Marshal.SizeOf<T>();
        IntPtr ptr = Marshal.AllocHGlobal(structSize);
        try
        {
            Marshal.Copy(bytes, 0, ptr, structSize);
            return Marshal.PtrToStructure<T>(ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    public static uint CalculateChecksum(HealthData data)
    {
        int structSize = Marshal.SizeOf<HealthData>();
        byte[] buffer = new byte[structSize];
        IntPtr ptr = Marshal.AllocHGlobal(structSize);
        try
        {
            Marshal.StructureToPtr(data, ptr, false);
            Marshal.Copy(ptr, buffer, 0, structSize);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }

        uint sum = 0;
        for (int i = 0; i < structSize - 4; i++)
            sum += buffer[i];
        return sum;
    }

    public static uint CalculateRangeChecksum(HealthRangeData data)
    {
        int structSize = Marshal.SizeOf<HealthRangeData>();
        byte[] buffer = new byte[structSize];
        IntPtr ptr = Marshal.AllocHGlobal(structSize);
        try
        {
            Marshal.StructureToPtr(data, ptr, false);
            Marshal.Copy(ptr, buffer, 0, structSize);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }

        uint sum = 0;
        for (int i = 0; i < structSize - 4; i++)
            sum += buffer[i];
        return sum;
    }

    public static string CreateJsonFromBinaryData(HealthData data)
    {
        var json = new StringBuilder();
        json.Append('{');
        json.Append($"\"height\":{data.Height},");
        json.Append($"\"weight\":{data.Weight},");
        json.Append($"\"bmi\":{data.Bmi},");
        json.Append($"\"temperature\":{data.Temperature},");
        json.Append($"\"prox_distance\":{data.prox_distance},");
        json.Append("\"body_composition\":{");
        json.Append($"\"muscle_control\":{data.MuscleControl},");
        json.Append($"\"body_score\":{data.BodyScore},");
        json.Append($"\"bone_mass\":{data.BoneMass},");
        json.Append($"\"body_fat_pct\":{data.BodyFatPercent},");
        json.Append($"\"muscle_mass\":{data.MuscleMass},");
        json.Append($"\"skeletal_muscle\":{data.SkeletalMuscle}");
        json.Append("}}");
        return json.ToString();
    }

    private static void Log(string message) => Console.WriteLine(message);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct HealthData
    {
        public uint Magic;
        public float Height;
        public float Weight;
        public float Bmi;
        public float Temperature;
        public float prox_distance;
        public float MuscleControl;
        public float IntracellularWater;
        public float BodyScore;
        public float BoneMass;
        public float FatControl;
        public float ExtracellularWater;
        public float PhysicalAge;
        public float Protein;
        public float TrunkFatPercent;
        public float BodyCellMass;
        public float VisceralFatLevel;
        public float BodyWater;
        public float BodyFatPercent;
        public float TrunkMuscleMass;
        public float SubcutaneousFatPercent;
        public float BasalMetabolism;
        public float IdealBodyWeight;
        public float FatMass;
        public float RightHandMuscle;
        public float WaistHipRatio;
        public float LeanBodyMass;
        public float ObesityLevel;
        public float MuscleMass;
        public float LeftHandMuscle;
        public float WeightControl;
        public float MoistureTbw;
        public float SkeletalMuscle;
        public float FatRightHand;
        public float FatLeftHand;
        public float FatTrunk;
        public float FatRightFoot;
        public float FatLeftFoot;
        public float MusclePctRightHand;
        public float MusclePctLeftHand;
        public float MusclePctTrunk;
        public float MusclePctRightFoot;
        public float MusclePctLeftFoot;
        public float SmiIndex;
        public float InorganicSalt;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] BodyType;
        public byte CommandFromMaster;
        public byte UserAge;
        public byte UserGender;
        public byte Reserved;
        public uint Checksum;

        public string BodyTypeString => BodyType != null ? Encoding.ASCII.GetString(BodyType).TrimEnd('\0') : "Unknown";
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct HealthRangeData
    {
        public uint Magic;
        public float MuscleControlMin;
        public float MuscleControlMax;
        public float IntracellularWaterMin;
        public float IntracellularWaterMax;
        public float BoneMassMin;
        public float BoneMassMax;
        public float ProteinMin;
        public float ProteinMax;
        public float VisceralFatMin;
        public float VisceralFatMax;
        public float BodyWaterMin;
        public float BodyWaterMax;
        public float BodyFatPercentMin;
        public float BodyFatPercentMax;
        public float SubcutaneousFatPercentMin;
        public float SubcutaneousFatPercentMax;
        public float BasalMetabolismMin;
        public float BasalMetabolismMax;
        public float WaistHipRatioMin;
        public float WaistHipRatioMax;
        public float LeanBodyMassMin;
        public float LeanBodyMassMax;
        public float ObesityLevelMin;
        public float ObesityLevelMax;
        public float MuscleMassMin;
        public float MuscleMassMax;
        public float SkeletalMuscleMin;
        public float SkeletalMuscleMax;
        public float MoistureTbwMin;
        public float MoistureTbwMax;
        public float BmiMin;
        public float BmiMax;
        public float WeightMin;
        public float WeightMax;
        public uint Checksum;
    }
}
