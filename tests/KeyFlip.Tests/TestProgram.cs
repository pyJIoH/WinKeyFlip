using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace KeyFlip.Tests
{
    /// Dependency-free runner: no test framework or SDK required.
    internal static class Expect
    {
        public static int Failures;

        public static void Equal<T>(T actual, T expected, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        {
            if (!EqualityComparer<T>.Default.Equals(actual, expected))
            {
                Fail("expected " + Describe(expected) + ", got " + Describe(actual), file, line);
            }
        }

        public static void Null<T>(T? actual, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) where T : struct
        {
            if (actual.HasValue) Fail("expected null, got " + Describe(actual.Value), file, line);
        }

        public static void True(bool condition, string what, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        {
            if (!condition) Fail(what, file, line);
        }

        public static void Throws(string message, Action action, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        {
            try
            {
                action();
            }
            catch (Exception error)
            {
                if (error.Message != message) Fail("expected failure \"" + message + "\", got \"" + error.Message + "\"", file, line);
                return;
            }
            Fail("expected failure \"" + message + "\", but nothing was thrown", file, line);
        }

        private static void Fail(string message, string file, int line)
        {
            Failures++;
            Console.WriteLine("FAIL " + Path.GetFileName(file) + ":" + line + ": " + message);
        }

        private static string Describe(object value)
        {
            if (value == null) return "null";
            string text = value as string;
            return text != null ? "\"" + text + "\"" : value.ToString();
        }
    }

    internal sealed class Scenario
    {
        public readonly string Name;
        public readonly Action Run;

        public Scenario(string name, Action run)
        {
            Name = name;
            Run = run;
        }
    }

    internal static class TestProgram
    {
        private static int Main(string[] args)
        {
            try { Console.OutputEncoding = new UTF8Encoding(false); } catch (IOException) { }
            List<string> options = new List<string>(args);
            int e2e = options.IndexOf("--e2e");
            if (e2e >= 0)
            {
                bool trace = options.Remove("--trace");
                return EndToEnd.Run(e2e + 1 < options.Count ? options[e2e + 1] : null, trace);
            }
            int external = options.IndexOf("--external");
            if (external >= 0)
            {
                bool trace = options.Remove("--trace");
                bool installed = options.Remove("--installed");
                if (external + 2 >= options.Count)
                {
                    Console.WriteLine("Usage: KeyFlip.Tests.exe --external <window title> <script file> [--trace] [--installed]");
                    return 2;
                }
                return ExternalCheck.Run(options[external + 1], options[external + 2], trace, installed);
            }
            int render = options.IndexOf("--render-settings");
            if (render >= 0 && render + 1 < options.Count)
            {
                return PlatformChecks.RenderSettings(options[render + 1], !options.Contains("--unscaled"));
            }
            if (options.Contains("--clipboard-child")) return PlatformChecks.RunClipboardChild();
            if (options.Contains("--platform") || options.Contains("--layouts"))
            {
                return PlatformChecks.Run(options.Contains("--platform"), options.Contains("--layouts"));
            }
            if (options.Count > 0)
            {
                Console.WriteLine("Usage: KeyFlip.Tests.exe [--platform] [--layouts] | --e2e [name] [--trace] | --external <window title> <script file> [--trace] [--installed] | --render-settings <file.png> [--unscaled]");
                return 2;
            }
            return RunCore();
        }

        private static int RunCore()
        {
            List<Scenario> scenarios = new List<Scenario>();
            TapDetectorTests.Add(scenarios);
            CapsLockGestureTests.Add(scenarios);
            MacShortcutsTests.Add(scenarios);
            WordAtCaretTests.Add(scenarios);
            TransformTests.Add(scenarios);
            BoundaryTests.Add(scenarios);
            TypedWordTests.Add(scenarios);
            KeyMapTests.Add(scenarios);
            ClipboardLeaseTests.Add(scenarios);
            foreach (Scenario scenario in scenarios)
            {
                int before = Expect.Failures;
                try
                {
                    scenario.Run();
                }
                catch (Exception error)
                {
                    Expect.Failures++;
                    Console.WriteLine("FAIL " + scenario.Name + ": " + error);
                }
                Console.WriteLine((before == Expect.Failures ? "PASS " : "FAIL ") + scenario.Name);
            }
            Console.WriteLine(scenarios.Count + " scenarios, " + Expect.Failures + " failed assertions.");
            return Expect.Failures == 0 ? 0 : 1;
        }
    }
}
