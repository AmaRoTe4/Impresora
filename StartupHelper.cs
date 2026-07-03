using System;
using Microsoft.Win32;

namespace PrintAgent
{
    /// <summary>
    /// Auto-arranque via HKCU Run key (sin admin, reversible). No usa Windows Service
    /// ni shortcut .lnk para mantenerlo simple: una sola API, sin COM interop.
    /// </summary>
    internal static class StartupHelper
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "PrintAgent";

        public static bool IsEnabled()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) != null;
        }

        public static void Enable()
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath)) return;

            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            key?.SetValue(ValueName, $"\"{exePath}\"");
        }

        public static void Disable()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
