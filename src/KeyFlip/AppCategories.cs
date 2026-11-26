using System;
using System.Collections.Generic;

namespace KeyFlip
{
    /// Applications whose text input needs different handling.
    internal static class AppCategories
    {
        /// Selected terminal text is usually output: pasting a converted copy would insert
        /// it at the prompt instead of replacing the selection. Their text is not editable
        /// through UI Automation either, so only a word typed just now is converted.
        private static readonly HashSet<string> TerminalProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "windowsterminal", "openconsole", "conhost", "cmd", "powershell", "pwsh", "wt",
            "alacritty", "wezterm-gui", "kitty", "mintty", "conemu", "conemu64", "cmder",
            "hyper", "tabby", "putty", "mobaxterm", "securecrt", "xshell", "rio", "warp", "ghostty", "far"
        };

        private static readonly HashSet<string> TerminalClasses = new HashSet<string>(StringComparer.Ordinal)
        {
            "ConsoleWindowClass", "CASCADIA_HOSTING_WINDOW_CLASS", "PseudoConsoleWindow",
            "mintty", "VirtualConsoleClass", "PuTTY", "KiTTY"
        };

        /// Virtual machines and remote desktops forward keys to another system with its own
        /// layout; retyping or clipboard shortcuts would act inside that system.
        private static readonly HashSet<string> KeyForwardingProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "mstsc", "msrdc", "vmconnect", "vmware", "vmware-vmx", "vmplayer", "vmrc",
            "virtualboxvm", "virtualbox", "qemu-system-x86_64", "qemu-system-aarch64",
            "teamviewer", "anydesk", "vncviewer", "tvnviewer", "rustdesk", "parsecd",
            "cdviewer", "wfica32", "rdcman", "mremoteng", "nxplayer", "moonlight"
        };

        private static bool AnyProcess(FocusInfo focus, HashSet<string> names)
        {
            if (names.Contains(Native.ProcessName(focus.ProcessId))) return true;
            uint foregroundProcess;
            Native.GetWindowThreadProcessId(focus.Foreground, out foregroundProcess);
            return foregroundProcess != focus.ProcessId && names.Contains(Native.ProcessName(foregroundProcess));
        }

        public static bool IsTerminal(FocusInfo focus)
        {
            if (focus.Foreground == IntPtr.Zero) return false;
            return TerminalClasses.Contains(Native.ClassName(focus.Foreground))
                || TerminalClasses.Contains(Native.ClassName(focus.Focus))
                || AnyProcess(focus, TerminalProcesses);
        }

        public static bool ForwardsKeys(FocusInfo focus)
        {
            return focus.Foreground != IntPtr.Zero && AnyProcess(focus, KeyForwardingProcesses);
        }

        public static bool AllowsClipboardFallback(FocusInfo focus)
        {
            return !IsTerminal(focus) && !ForwardsKeys(focus);
        }

        /// Browsers and Electron applications start their accessibility support on demand.
        public static bool StartsAccessibilityOnDemand(FocusInfo focus)
        {
            string foreground = Native.ClassName(focus.Foreground);
            string control = Native.ClassName(focus.Focus);
            return foreground.StartsWith("Chrome_", StringComparison.Ordinal)
                || control.StartsWith("Chrome_", StringComparison.Ordinal)
                || foreground == "MozillaWindowClass";
        }
    }
}
