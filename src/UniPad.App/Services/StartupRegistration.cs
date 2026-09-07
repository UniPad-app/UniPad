using Microsoft.Win32;
using Serilog;
using UniPad.Core.SystemServices;

namespace UniPad.App.Services;

/// <summary>
/// Registers or removes the "run at sign-in" entry.
/// <para>
/// The per-user Run key is used rather than a scheduled task or a service, because it needs no
/// elevation and matches the portable philosophy of the application.
/// </para>
/// </summary>
public static class StartupRegistration
{
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "UniPad";

    /// <summary>True when an autostart entry pointing at this executable exists.</summary>
    public static bool IsRegistered()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            // PLATFORM: per-user Run key, no elevation required.
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string existing
                   && existing.Contains(PortablePaths.ExecutablePath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Autostart probe failed");
            return false;
        }
    }

    /// <summary>Creates or removes the autostart entry. Returns true on success.</summary>
    public static bool SetRegistered(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            // PLATFORM: per-user Run key write.
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                return false;
            }

            if (enabled)
            {
                // --tray keeps the window hidden when Windows launches it.
                key.SetValue(ValueName, $"\"{PortablePaths.ExecutablePath}\" --tray");
                Log.Information("Autostart entry created");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                Log.Information("Autostart entry removed");
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to update autostart entry");
            return false;
        }
    }
}
