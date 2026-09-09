// ============================================================
// HardwareShutdown.cs — System Power Control Interface
// BluAI Pvt. Ltd. | VitalsChair™ Backend
//
// Provides graceful shutdown and reboot capabilities for
// the VitalsChair embedded system. Uses nsenter to execute
// commands in the host namespace from the container.
// ============================================================

using System;
using System.Diagnostics;
using System.Threading.Tasks;

/// <summary>
/// System power control interface for graceful shutdown/reboot.
/// Executes nsenter commands to properly manage host system state.
/// </summary>
public static class SystemPower
{
    // ────────────────────────────────────────────────────────
    // POWER OFF
    // Initiates system shutdown and power off sequence.
    // ────────────────────────────────────────────────────────
    public static async Task PowerOff()
    {
        await RunPowerCommand("shutdown");
    }

    // ────────────────────────────────────────────────────────
    // REBOOT
    // Initiates system warm reboot.
    // ────────────────────────────────────────────────────────
    public static async Task Reboot()
    {
        await RunPowerCommand("restart");
    }

    // ────────────────────────────────────────────────────────
    // RUN POWER COMMAND
    // Internal helper to execute power commands via nsenter.
    // Ensures commands run in host namespace, not container.
    // ────────────────────────────────────────────────────────
    private static async Task RunPowerCommand(string action)
    {
        try
        {
            // Map action to system command
            string command = action == "shutdown" ? "poweroff" : "reboot";

            // Create nsenter process to access host namespace
            var psi = new ProcessStartInfo
            {
                FileName = "nsenter",
                Arguments = $"-t 1 -m -u -i -n -p -- {command}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            string output = await process.StandardOutput.ReadToEndAsync();
            string error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Console.WriteLine($"[SystemPower] {action} exit: {process.ExitCode}");
            if (!string.IsNullOrEmpty(error))
                Console.WriteLine($"[SystemPower] Error: {error}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SystemPower] Failed: {ex.Message}");
        }
    }
}
