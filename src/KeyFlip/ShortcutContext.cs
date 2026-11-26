using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using KeyFlip.Core;

namespace KeyFlip
{
    /// What the Mac shortcuts need to know about the program in front. It is asked from
    /// inside the keyboard hook, so it only reads what Windows already knows: it never
    /// sends a message to another program. The answers are kept for as long as the
    /// same window stays in front.
    internal sealed class ShortcutContext : IShortcutContext
    {
        private static readonly char[] Separators = { ',', ';', ' ', '\t', '\r', '\n' };

        private readonly bool ownElevated = Native.Elevation(Native.GetCurrentProcessId()) == 1;
        // Replaced as a whole when the setting changes; read on the input thread.
        private volatile HashSet<string> excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> seenExcluded;
        private IntPtr window;
        private AppKind app;
        private bool active = true;

        /// Program names as the user wrote them: "mstsc, game.exe".
        public void SetExcluded(string names)
        {
            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string part in (names ?? "").Split(Separators, StringSplitOptions.RemoveEmptyEntries))
            {
                string name = part.Trim();
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
                if (name.Length > 0) set.Add(name);
            }
            excluded = set;
        }

        /// Whether the user excluded the program with this executable name.
        public bool Excludes(string process)
        {
            return !string.IsNullOrEmpty(process) && excluded.Contains(process);
        }

        public bool Active
        {
            get
            {
                Refresh();
                return active;
            }
        }

        public AppKind App
        {
            get
            {
                Refresh();
                return app;
            }
        }

        public bool EditingText
        {
            get
            {
                IntPtr foreground = Native.GetForegroundWindow();
                if (foreground == IntPtr.Zero) return true;
                uint process;
                Native.GUITHREADINFO gui = new Native.GUITHREADINFO();
                gui.cbSize = Marshal.SizeOf(typeof(Native.GUITHREADINFO));
                // When in doubt it is text: there, Command+Backspace deletes to the
                // start of the line, and in a list of files it deletes the file.
                if (!Native.GetGUIThreadInfo(Native.GetWindowThreadProcessId(foreground, out process), ref gui)) return true;
                return gui.hwndCaret != IntPtr.Zero || !AppCategories.ListsFiles(Native.ClassName(gui.hwndFocus));
            }
        }

        private void Refresh()
        {
            IntPtr foreground = Native.GetForegroundWindow();
            HashSet<string> names = excluded;
            if (foreground == window && names == seenExcluded) return;
            window = foreground;
            seenExcluded = names;
            uint process;
            Native.GetWindowThreadProcessId(foreground, out process);
            string name = Native.ProcessName(process);
            app = AppCategories.KindOf(name, Native.ClassName(foreground));
            active = !AppCategories.ForwardsKeys(name) && !names.Contains(name);
        }

        /// A window that runs as administrator, while KeyFlip does not: its keys never
        /// reach the keyboard hook, so the release of a key that is held now will be missed.
        public bool HidesInput(IntPtr foreground)
        {
            if (ownElevated || foreground == IntPtr.Zero) return false;
            uint process;
            Native.GetWindowThreadProcessId(foreground, out process);
            return process != 0 && Native.Elevation(process) != 0;
        }
    }
}
