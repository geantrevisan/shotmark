using System;
using Microsoft.Win32;

namespace ShotMark.Services
{
    /// <summary>Registers ShotMark to start with Windows (per-user Run key, no admin rights needed).</summary>
    public static class StartupManager
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "ShotMark";

        /// <summary>True when the per-user Run entry exists (set by the installer or by ShotMark itself).</summary>
        public static bool IsRegistered()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
                return key?.GetValue(ValueName) != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static void Apply(bool enabled)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
                if (enabled)
                {
                    string exe = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
                    if (!string.IsNullOrEmpty(exe)) key.SetValue(ValueName, $"\"{exe}\" --tray");
                }
                else if (key.GetValue(ValueName) != null)
                {
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                }
            }
            catch (Exception)
            {
                // Policy may block the Run key; the setting simply has no effect then.
            }
        }
    }
}
