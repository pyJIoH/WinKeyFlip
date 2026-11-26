using System;
using System.Collections.Generic;
using KeyFlip.Core;

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

        /// Command-line programs. Their console is a terminal window, which its window
        /// class tells; a window of their own, such as a dialog of a script, is not.
        private static readonly HashSet<string> ShellProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "cmd", "powershell", "pwsh"
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

        // For the Mac shortcuts: programs whose shortcuts differ from the general rule.
        private static readonly HashSet<string> BrowserProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "chromium", "browser", "arc",
            "zen", "librewolf", "waterfox", "thorium", "iexplore"
        };

        private static readonly HashSet<string> CodeEditorProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "code", "code - insiders", "cursor", "windsurf", "vscodium"
        };

        private static readonly HashSet<string> JetBrainsProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "pycharm64", "pycharm", "idea64", "idea", "webstorm64", "phpstorm64", "clion64", "goland64",
            "rider64", "rubymine64", "datagrip64", "dataspell64", "rustrover64", "aqua64", "studio64"
        };

        private static readonly HashSet<string> OfficeProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "winword", "excel", "powerpnt", "outlook", "onenote"
        };

        /// Folder windows and the desktop. The taskbar and the task switcher belong to
        /// the same process and are not file managers.
        private static readonly HashSet<string> FolderClasses = new HashSet<string>(StringComparer.Ordinal)
        {
            "CabinetWClass", "ExploreWClass"
        };

        /// The desktop and the taskbars.
        private static readonly HashSet<string> DesktopClasses = new HashSet<string>(StringComparer.Ordinal)
        {
            "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"
        };

        /// The controls of a file manager that list files and folders.
        private static readonly HashSet<string> FileListClasses = new HashSet<string>(StringComparer.Ordinal)
        {
            "DirectUIHWND", "SysListView32", "SysTreeView32"
        };

        /// The kind of program a foreground window belongs to, from the name of its
        /// executable (without the extension) and its window class.
        public static AppKind KindOf(string process, string windowClass)
        {
            process = process ?? "";
            windowClass = windowClass ?? "";
            if (TerminalClasses.Contains(windowClass)) return AppKind.Terminal;
            if (TerminalProcesses.Contains(process) && !ShellProcesses.Contains(process)) return AppKind.Terminal;
            if (BrowserProcesses.Contains(process)) return AppKind.Browser;
            if (CodeEditorProcesses.Contains(process)) return AppKind.CodeEditor;
            if (JetBrainsProcesses.Contains(process)) return AppKind.JetBrains;
            if (OfficeProcesses.Contains(process)) return AppKind.Office;
            if (string.Equals(process, "explorer", StringComparison.OrdinalIgnoreCase))
            {
                if (FolderClasses.Contains(windowClass)) return AppKind.FileManager;
                if (DesktopClasses.Contains(windowClass)) return AppKind.Desktop;
            }
            return AppKind.Other;
        }

        /// Whether a control with this window class lists files, as opposed to holding text.
        public static bool ListsFiles(string controlClass)
        {
            return FileListClasses.Contains(controlClass ?? "");
        }

        /// Remote desktops and virtual machines have a keyboard of their own behind the window.
        public static bool ForwardsKeys(string process)
        {
            return KeyForwardingProcesses.Contains(process ?? "");
        }

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
