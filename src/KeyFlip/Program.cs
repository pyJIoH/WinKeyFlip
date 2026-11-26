using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace KeyFlip
{
    internal static class Program
    {
        private const string InstanceName = @"Local\KeyFlip.SingleInstance";
        private const string ShowSettingsName = @"Local\KeyFlip.ShowSettings";
        private const string QuitName = @"Local\KeyFlip.Quit";

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--diagnose") return Diagnose();
            bool quit = args.Length > 0 && args[0] == "--quit";

            bool first;
            using (Mutex instance = new Mutex(true, InstanceName, out first))
            {
                if (!first || quit)
                {
                    // Ask the running copy to quit, or to show its settings window.
                    try
                    {
                        using (EventWaitHandle signal = EventWaitHandle.OpenExisting(quit ? QuitName : ShowSettingsName))
                        {
                            signal.Set();
                        }
                    }
                    catch (Exception)
                    {
                        // No running copy, or it is just starting or stopping.
                    }
                    return 0;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
                {
                    MessageBox.Show(e.Exception.Message, "KeyFlip", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                };
                using (EventWaitHandle show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSettingsName))
                using (EventWaitHandle stop = new EventWaitHandle(false, EventResetMode.AutoReset, QuitName))
                using (TrayApp app = new TrayApp(show, stop))
                {
                    Application.Run(app);
                }
                GC.KeepAlive(instance);
            }
            return 0;
        }

        public static string Version
        {
            get
            {
                Version version = Assembly.GetExecutingAssembly().GetName().Version;
                return version.Major + "." + version.Minor + "." + version.Build;
            }
        }

        /// Prints the environment as JSON. It never prints editor text.
        private static int Diagnose()
        {
            Layouts layouts = new Layouts(false);
            uint english;
            uint russian;
            // Show the saved choice without writing anything.
            uint.TryParse(Settings.Get("EnglishLayout") ?? "", NumberStyles.HexNumber, CultureInfo.InvariantCulture, out english);
            uint.TryParse(Settings.Get("RussianLayout") ?? "", NumberStyles.HexNumber, CultureInfo.InvariantCulture, out russian);
            if (layouts.Find(english) == null) english = layouts.EnglishId;
            if (layouts.Find(russian) == null) russian = layouts.RussianId;

            StringBuilder json = new StringBuilder();
            json.Append("{\n  \"version\": ").Append(Quote(Version));
            json.Append(",\n  \"elevated\": ").Append(IsElevated() ? "true" : "false");
            json.Append(",\n  \"layouts\": [");
            bool firstItem = true;
            foreach (KeyboardLayout layout in layouts.Available)
            {
                json.Append(firstItem ? "\n" : ",\n");
                firstItem = false;
                json.Append("    {\"id\": ").Append(Quote(layout.IdText));
                json.Append(", \"language\": ").Append(Quote(layout.Language));
                json.Append(", \"name\": ").Append(Quote(layout.Name));
                json.Append(", \"selected\": ").Append(layout.Id == english || layout.Id == russian ? "true" : "false");
                json.Append("}");
            }
            json.Append(firstItem ? "]" : "\n  ]");
            uint current = layouts.CurrentId;
            json.Append(",\n  \"activeLayout\": ").Append(Quote(current.ToString("X8", CultureInfo.InvariantCulture)));
            json.Append(",\n  \"activeLayoutName\": ").Append(Quote(layouts.CurrentName));
            json.Append("\n}");
            WriteLine(json.ToString());
            return 0;
        }

        private static bool IsElevated()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// A JSON string in plain ASCII, so the console code page does not matter.
        private static string Quote(string text)
        {
            StringBuilder quoted = new StringBuilder("\"");
            foreach (char character in text ?? "")
            {
                if (character == '"' || character == '\\') quoted.Append('\\').Append(character);
                else if (character < ' ' || character > '~')
                {
                    quoted.Append('\\').Append('u').Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                }
                else quoted.Append(character);
            }
            return quoted.Append('"').ToString();
        }

        /// A windowed program has no console of its own: write to a redirected
        /// output if there is one, otherwise to the console that started it.
        private static void WriteLine(string text)
        {
            IntPtr output = Native.GetStdHandle(Native.STD_OUTPUT_HANDLE);
            bool redirected = output != IntPtr.Zero && output != new IntPtr(-1) && Native.GetFileType(output) != 0;
            if (!redirected) Native.AttachConsole(Native.ATTACH_PARENT_PROCESS);
            try
            {
                using (Stream stream = Console.OpenStandardOutput())
                using (StreamWriter writer = new StreamWriter(stream, new ASCIIEncoding()))
                {
                    if (!redirected) writer.WriteLine();
                    writer.WriteLine(text);
                }
            }
            catch (IOException)
            {
                // No console at all, for example when started from Explorer.
            }
        }
    }
}
