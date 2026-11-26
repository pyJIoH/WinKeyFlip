using System;
using System.Windows.Forms;
using KeyFlip.Core;
using Microsoft.Win32;

namespace KeyFlip
{
    /// Stored state: the two layout choices, the conversion key, the Caps Lock choice
    /// and a first-run flag, in the registry.
    internal static class Settings
    {
        private const string KeyPath = @"Software\KeyFlip";
        private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValue = "KeyFlip";

        public static string Get(string name)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    return key == null ? null : key.GetValue(name) as string;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static void Set(string name, string value)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    if (key != null) key.SetValue(name, value, RegistryValueKind.String);
                }
            }
            catch (Exception)
            {
                // Settings are a convenience; the app works with defaults.
            }
        }

        public static bool HasOpened
        {
            get { return Get("HasOpened") == "1"; }
            set { Set("HasOpened", value ? "1" : "0"); }
        }

        /// The key whose lone tap converts the layout. Alt unless Win was chosen.
        public static LayoutTapKey LayoutTapKey
        {
            get { return Get("LayoutTapKey") == "win" ? LayoutTapKey.Win : LayoutTapKey.Alt; }
            set { Set("LayoutTapKey", value == LayoutTapKey.Win ? "win" : "alt"); }
        }

        /// Caps Lock, pressed alone, only switches the keyboard layout. On unless turned off.
        public static bool CapsLockSwitchesLayout
        {
            get { return Get("CapsLockSwitchesLayout") != "0"; }
            set { Set("CapsLockSwitchesLayout", value ? "1" : "0"); }
        }

        private static string RunCommand
        {
            get { return "\"" + Application.ExecutablePath + "\""; }
        }

        /// Whether this copy of KeyFlip starts when the user signs in.
        public static bool LaunchAtLogin
        {
            get
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunPath))
                    {
                        string value = key == null ? null : key.GetValue(RunValue) as string;
                        return string.Equals(value, RunCommand, StringComparison.OrdinalIgnoreCase);
                    }
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        public static void SetLaunchAtLogin(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunPath))
            {
                if (key == null) throw new InvalidOperationException("The startup list is not available.");
                if (enabled) key.SetValue(RunValue, RunCommand, RegistryValueKind.String);
                else key.DeleteValue(RunValue, false);
            }
        }
    }
}
