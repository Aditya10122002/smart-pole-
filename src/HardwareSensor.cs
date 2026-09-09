// ============================================================
// HardwareSensor.cs — Sensor Input Interfaces
// BluAI Pvt. Ltd. | VitalsChair™ Backend
//
// Manages hardware sensor inputs including height/weight
// scale reading via serial port and real-time data processing.
// ============================================================

using System;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Sensor hardware interface for reading anthropometric data
/// from connected measurement devices (height/weight scales).
/// </summary>
public static class HardwareSensor
{
    // ────────────────────────────────────────────────────────
    // CONFIGURATION
    // ────────────────────────────────────────────────────────
    private static readonly int SAMPLE_WINDOW = 10;
    private static readonly TimeSpan LOCK_DURATION = TimeSpan.FromSeconds(5);

    // ────────────────────────────────────────────────────────
    // READ HEIGHT LOOP
    // Continuously reads height/weight data from serial port
    // and updates internal measurement state. Runs in background.
    //
    // Protocol: Serial port receives height (cm) and weight (kg)
    // measurements formatted as text lines. Data is validated
    // and stored for retrieval by other measurement handlers.
    // ────────────────────────────────────────────────────────
    public static async Task ReadHeightLoopAsync(
        SerialPort serialPort,
        CancellationToken cancellationToken,
        Action<float, float> onMeasurementReceived)
    {
        if (serialPort == null || !serialPort.IsOpen)
        {
            ProductionLogger.Warning(LogCategory.Hardware, "Height/weight serial port not available");
            return;
        }

        try
        {
            ProductionLogger.Info(LogCategory.Hardware, "Height/weight reader started");

            string buffer = "";
            byte[] readBuffer = new byte[1024];

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (serialPort.BytesToRead > 0)
                    {
                        int bytesRead = serialPort.Read(readBuffer, 0, readBuffer.Length);
                        buffer += System.Text.Encoding.UTF8.GetString(readBuffer, 0, bytesRead);

                        // Process complete lines
                        while (buffer.Contains("\n"))
                        {
                            int newlineIndex = buffer.IndexOf('\n');
                            string line = buffer.Substring(0, newlineIndex).Trim();
                            buffer = buffer.Substring(newlineIndex + 1);

                            // Parse measurement line
                            if (!string.IsNullOrEmpty(line))
                            {
                                string[] parts = line.Split(',');
                                if (parts.Length >= 2 &&
                                    float.TryParse(parts[0], out float height) &&
                                    float.TryParse(parts[1], out float weight))
                                {
                                    Console.WriteLine($"📏 [Sensor] Height: {height}cm, Weight: {weight}kg");
                                    onMeasurementReceived?.Invoke(height, weight);
                                }
                            }
                        }
                    }

                    await Task.Delay(100, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    ProductionLogger.Warning(LogCategory.Hardware, "Sensor read error", new System.Collections.Generic.Dictionary<string, object> { ["error"] = ex.Message });
                    await Task.Delay(500, cancellationToken);
                }
            }
        }
        catch (Exception ex)
        {
            ProductionLogger.Error(LogCategory.Hardware, "Height sensor loop error", ex);
        }
        finally
        {
            ProductionLogger.Info(LogCategory.Hardware, "Height/weight reader stopped");
        }
    }

    // ────────────────────────────────────────────────────────
    // VALIDATE MEASUREMENT
    // Checks if a sensor reading is within reasonable ranges
    // for anthropometric data (height cm, weight kg).
    // ────────────────────────────────────────────────────────
    public static bool ValidateMeasurement(float height, float weight)
    {
        // Reasonable ranges:
        // Height: 50cm (infant) to 250cm (very tall adult)
        // Weight: 2kg (infant) to 250kg (extreme cases)
        bool heightValid = height >= 50f && height <= 250f;
        bool weightValid = weight >= 2f && weight <= 250f;

        return heightValid && weightValid;
    }

    // ────────────────────────────────────────────────────────
    // CALCULATE BMI
    // Computes Body Mass Index from height and weight.
    // Formula: BMI = weight(kg) / (height(m) ^ 2)
    // ────────────────────────────────────────────────────────
    public static float CalculateBMI(float heightCm, float weightKg)
    {
        if (heightCm <= 0 || weightKg <= 0)
            return 0f;

        float heightM = heightCm / 100f;
        return weightKg / (heightM * heightM);
    }

    // ────────────────────────────────────────────────────────
    // GET BMI CATEGORY
    // Returns WHO BMI classification based on calculated BMI.
    // ────────────────────────────────────────────────────────
    public static string GetBMICategory(float bmi)
    {
        if (bmi < 18.5f)
            return "Underweight";
        else if (bmi < 25f)
            return "Normal";
        else if (bmi < 30f)
            return "Overweight";
        else
            return "Obese";
    }
}
