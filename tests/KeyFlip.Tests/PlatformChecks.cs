using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;
using KeyFlip.Core;

namespace KeyFlip.Tests
{
    /// Checks of the Windows integration that need a desktop session. They post no
    /// input, never take the focus and never touch the user's clipboard or settings.
    internal static class PlatformChecks
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetProcessWindowStation();
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetUserObjectInformation(IntPtr handle, int index, byte[] buffer, int size, out int needed);

        public static int Run(bool platform, bool layouts)
        {
            if (platform)
            {
                Section("Synthetic input", CheckSyntheticInput,
                    "structure layout, Backspace and Unicode typing, Ctrl+Insert and Shift+Insert, menu mask, event tags. No events posted.");
                Section("Clipboard", CheckClipboardInChild,
                    "real clipboard code in a private window station; the desktop clipboard was not touched.");
                Section("UI Automation", CheckTextAccess,
                    "word and selection targets, selection requests, replacement checks, caret restoration, typed-word probes, control classification. No window took the focus.");
            }
            if (layouts)
            {
                Section("Installed layouts", CheckInstalledLayouts,
                    "typed characters with Shift and Caps Lock, both directions, mixed case, punctuation, multiline text, emoji, mixed-alphabet rejection. No layout was switched.");
            }
            Console.WriteLine(Expect.Failures == 0 ? "Platform checks passed." : Expect.Failures + " failed assertions.");
            return Expect.Failures == 0 ? 0 : 1;
        }

        private static void Section(string name, Action check, string covered)
        {
            int before = Expect.Failures;
            try
            {
                check();
            }
            catch (Exception error)
            {
                Expect.Failures++;
                Console.WriteLine("FAIL " + name + ": " + error);
            }
            Console.WriteLine(before == Expect.Failures ? "PASS " + name + ": " + covered : "FAIL " + name);
        }

        // ---- Synthetic input ----

        private static void ExpectKey(Native.INPUT input, int virtualKey, int scanCode, uint flags)
        {
            Expect.Equal(input.type, Native.INPUT_KEYBOARD);
            Expect.Equal((int)input.u.ki.wVk, virtualKey);
            Expect.Equal((int)input.u.ki.wScan, scanCode);
            Expect.Equal(input.u.ki.dwFlags, flags);
            Expect.Equal(input.u.ki.dwExtraInfo, KeySender.Tag);
            Expect.Equal(input.u.ki.time, 0u);
        }

        /// Constructs and inspects events only; nothing is sent.
        private static void CheckSyntheticInput()
        {
            bool wide = IntPtr.Size == 8;
            Expect.Equal(KeySender.InputSize, wide ? 40 : 28);
            Expect.Equal(Marshal.OffsetOf(typeof(Native.INPUT), "u").ToInt32(), wide ? 8 : 4);
            Expect.Equal(Marshal.OffsetOf(typeof(Native.KBDLLHOOKSTRUCT), "dwExtraInfo").ToInt32(), 16);
            Expect.Equal(Marshal.SizeOf(typeof(Native.GUITHREADINFO)), wide ? 72 : 48);
            Expect.Equal(Marshal.SizeOf(typeof(Native.LASTINPUTINFO)), 8);

            Expect.Equal(KeySender.Backspaces(0).Length, 0);
            Native.INPUT[] deletions = KeySender.Backspaces(2);
            Expect.Equal(deletions.Length, 4);
            for (int i = 0; i < deletions.Length; i++)
            {
                ExpectKey(deletions[i], Native.VK_BACK, 0x0E, i % 2 == 0 ? 0u : Native.KEYEVENTF_KEYUP);
            }

            // One event pair per UTF-16 unit, including both halves of a surrogate pair.
            string text = "пР x\xD83D\xDC4B";
            Native.INPUT[] typing = KeySender.Typing(text);
            Expect.Equal(typing.Length, text.Length * 2);
            for (int i = 0; i < typing.Length; i++)
            {
                ExpectKey(typing[i], 0, text[i / 2],
                    Native.KEYEVENTF_UNICODE | (i % 2 == 0 ? 0u : Native.KEYEVENTF_KEYUP));
            }
            Expect.Equal(KeySender.Typing("").Length, 0);

            foreach (bool copy in new[] { true, false })
            {
                Native.INPUT[] chord = copy ? KeySender.Copy() : KeySender.Paste();
                int modifier = copy ? VirtualKeys.LeftControl : VirtualKeys.LeftShift;
                int modifierScan = copy ? 0x1D : 0x2A;
                Expect.Equal(chord.Length, 4);
                ExpectKey(chord[0], modifier, modifierScan, 0);
                ExpectKey(chord[1], Native.VK_INSERT, 0x52, Native.KEYEVENTF_EXTENDEDKEY);
                ExpectKey(chord[2], Native.VK_INSERT, 0x52, Native.KEYEVENTF_EXTENDEDKEY | Native.KEYEVENTF_KEYUP);
                ExpectKey(chord[3], modifier, modifierScan, Native.KEYEVENTF_KEYUP);
            }

            Native.INPUT[] mask = KeySender.MenuMask();
            Expect.Equal(mask.Length, 2);
            ExpectKey(mask[0], 0xE8, 0, 0);
            ExpectKey(mask[1], 0xE8, 0, Native.KEYEVENTF_KEYUP);
        }

        // ---- Clipboard ----

        private static void CheckClipboardInChild()
        {
            uint before = Native.GetClipboardSequenceNumber();
            ProcessStartInfo info = new ProcessStartInfo(Application.ExecutablePath, "--clipboard-child");
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardOutput = true;
            info.StandardOutputEncoding = Encoding.UTF8;
            using (Process child = Process.Start(info))
            {
                string output = child.StandardOutput.ReadToEnd();
                child.WaitForExit();
                Console.Write(output);
                Expect.Equal(child.ExitCode, 0);
            }
            Expect.True(Native.GetClipboardSequenceNumber() == before,
                "the desktop clipboard changed during the check (was something copied meanwhile?)");
        }

        private static string StationName(IntPtr station)
        {
            byte[] buffer = new byte[512];
            int needed;
            // UOI_NAME
            if (!GetUserObjectInformation(station, 2, buffer, buffer.Length, out needed)) return null;
            return Encoding.Unicode.GetString(buffer, 0, Math.Min(needed, buffer.Length)).TrimEnd('\0');
        }

        /// Runs in a child process that moves itself to a new, invisible window station.
        /// A window station has its own clipboard, so the real clipboard code can be
        /// exercised without touching the user's.
        public static int RunClipboardChild()
        {
            StreamWriter output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
            output.AutoFlush = true;
            Console.SetOut(output);

            IntPtr station = Native.CreateWindowStation(null, 0, 0x37F, IntPtr.Zero);
            if (station == IntPtr.Zero || !Native.SetProcessWindowStation(station))
            {
                Console.WriteLine("FAIL could not create a private window station (error " + Marshal.GetLastWin32Error() + ")");
                return 1;
            }
            string name = StationName(GetProcessWindowStation());
            if (name == null || name.Equals("WinSta0", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("FAIL still on the interactive window station; the clipboard was not touched");
                return 1;
            }
            int result = 1;
            // A thread is bound to a desktop by its first window-manager call.
            Thread thread = new Thread(delegate()
            {
                IntPtr desktop = Native.CreateDesktop("KeyFlipChecks", IntPtr.Zero, IntPtr.Zero, 0, 0x10000000, IntPtr.Zero);
                if (desktop == IntPtr.Zero || !Native.SetThreadDesktop(desktop))
                {
                    Console.WriteLine("FAIL could not create a private desktop (error " + Marshal.GetLastWin32Error() + ")");
                    return;
                }
                try
                {
                    ClipboardScenarios();
                    result = Expect.Failures == 0 ? 0 : 1;
                }
                catch (Exception error)
                {
                    Console.WriteLine("FAIL clipboard: " + error);
                }
            });
            thread.Start();
            thread.Join();
            return result;
        }

        private static KeyValuePair<uint, byte[]> TextFormat(string text)
        {
            return new KeyValuePair<uint, byte[]>(Native.CF_UNICODETEXT, Encoding.Unicode.GetBytes(text + "\0"));
        }

        private static void UserCopies(string text)
        {
            WinClipboard.SetRaw(new[] { TextFormat(text) });
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length < b.Length) return false;
            // Global memory blocks may be rounded up; compare the meaningful prefix.
            for (int i = 0; i < b.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        private static void ClipboardScenarios()
        {
            WinClipboard clipboard = new WinClipboard();
            uint custom = Native.RegisterClipboardFormat("KeyFlip.Check.Data");
            uint exclude = Native.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
            uint history = Native.RegisterClipboardFormat("CanIncludeInClipboardHistory");
            uint cloud = Native.RegisterClipboardFormat("CanUploadToCloudClipboard");
            byte[] bytes = { 0, 1, 2, 255 };
            // A 1x1, 32-bit device-independent bitmap: Windows synthesizes other bitmap formats from it.
            byte[] bitmap = new byte[44];
            bitmap[0] = 40; bitmap[4] = 1; bitmap[8] = 1; bitmap[12] = 1; bitmap[14] = 32; bitmap[20] = 4;
            bitmap[40] = 0x10; bitmap[41] = 0x20; bitmap[42] = 0x30; bitmap[43] = 0xFF;

            // Several formats, staged and restored.
            WinClipboard.SetRaw(new[]
            {
                TextFormat("original привет"),
                new KeyValuePair<uint, byte[]>(custom, bytes),
                new KeyValuePair<uint, byte[]>(8, bitmap) // CF_DIB
            });
            uint start = clipboard.SequenceNumber;
            ClipboardLease lease = new ClipboardLease(clipboard, "temporary");
            Expect.True(lease.SavedSequenceNumber == start, "saving the clipboard changed its sequence number");
            Expect.Equal(clipboard.GetText(), "temporary");
            List<uint> staged = WinClipboard.CurrentFormats();
            Expect.True(staged.Contains(exclude) && staged.Contains(history) && staged.Contains(cloud),
                "temporary text is not marked to stay out of clipboard history");
            Expect.True(SameBytes(WinClipboard.GetRaw(history), new byte[4]), "history marker must be zero");
            Expect.True(lease.HoldsTemporaryContents, "lease should hold temporary contents");
            Expect.True(lease.Restore(), "restore failed");
            Expect.Equal(clipboard.GetText(), "original привет");
            Expect.True(SameBytes(WinClipboard.GetRaw(custom), bytes), "custom format was not restored");
            Expect.True(SameBytes(WinClipboard.GetRaw(8), bitmap), "bitmap was not restored");
            Expect.True(WinClipboard.CurrentFormats().Contains(2), "bitmap handle format should be synthesized again");
            Expect.True(WinClipboard.CurrentFormats().Contains(exclude), "restored contents should stay out of clipboard history");

            // Restoring twice changes nothing.
            uint restored = clipboard.SequenceNumber;
            Expect.True(lease.Restore(), "second restore failed");
            Expect.Equal(clipboard.SequenceNumber, restored);

            // A newer copy is never overwritten.
            ClipboardLease interrupted = new ClipboardLease(clipboard, "temporary again");
            UserCopies("new user copy");
            interrupted.Restore();
            Expect.Equal(clipboard.GetText(), "new user copy");

            // An empty clipboard stays empty.
            WinClipboard.SetRaw(null);
            ClipboardLease empty = new ClipboardLease(clipboard, "temporary");
            Expect.Equal(clipboard.GetText(), "temporary");
            empty.Restore();
            Expect.Equal(WinClipboard.CurrentFormats().Count, 0);
            Expect.True(clipboard.GetText() == null, "empty clipboard should have no text");

            // Snapshot, then a synthetic copy replaces the clipboard before the result is staged.
            UserCopies("before copy");
            ClipboardLease copyLease = new ClipboardLease(clipboard);
            Expect.Equal(clipboard.GetText(), "before copy");
            Expect.True(!copyLease.ChangedSinceSaved, "saving the clipboard must not change it");
            UserCopies("selected text");
            Expect.True(copyLease.ChangedSinceSaved, "a copy must be noticed");
            copyLease.Claim();
            copyLease.Stage("converted text");
            Expect.Equal(clipboard.GetText(), "converted text");
            copyLease.Restore();
            Expect.Equal(clipboard.GetText(), "before copy");

            // A copy by the user after the synthetic copy wins.
            ClipboardLease raced = new ClipboardLease(clipboard);
            UserCopies("selected text");
            raced.Claim();
            UserCopies("new user copy");
            Expect.Throws(ClipboardLease.ChangedMessage, delegate { raced.Stage("converted text"); });
            raced.Restore();
            Expect.Equal(clipboard.GetText(), "new user copy");

            // Disposal restores.
            UserCopies("restore on release");
            using (new ClipboardLease(clipboard, "temporary"))
            {
                Expect.Equal(clipboard.GetText(), "temporary");
            }
            Expect.Equal(clipboard.GetText(), "restore on release");

            // The largest selection survives a round trip.
            string large = new string('я', TextRules.MaximumSelectionLength);
            UserCopies("small");
            using (new ClipboardLease(clipboard, large))
            {
                Expect.True(clipboard.GetText() == large, "large text was not staged intact");
            }
            Expect.Equal(clipboard.GetText(), "small");
            WinClipboard.SetRaw(null);
            if (Expect.Failures == 0) Console.WriteLine("     clipboard child: all scenarios passed in a private window station");
        }

        // ---- UI Automation ----

        private static bool WaitFor(Func<bool> condition)
        {
            for (int attempt = 0; attempt < 50; attempt++)
            {
                if (condition()) return true;
                Thread.Sleep(20);
            }
            return false;
        }

        private static void ExpectNoWord(string what, Action action)
        {
            try
            {
                action();
            }
            catch (TextTargetFailure failure)
            {
                Expect.True(failure.Kind == TargetFailureKind.NoWord, what + ": expected no word, got " + failure.Message);
                return;
            }
            Expect.True(false, what + ": expected no word, but a target was found");
        }

        private static TypedWordBuffer Typed(string text)
        {
            TypedWordBuffer buffer = new TypedWordBuffer();
            foreach (char character in text) buffer.Insert(character.ToString());
            return buffer;
        }

        /// Uses the test's own edit controls, which never have the focus.
        private static void CheckTextAccess()
        {
            using (EditorHost host = new EditorHost(true, "KeyFlip checks"))
            {
                TextAccess access = new TextAccess();
                CheckEditor(host, access, host.Plain, "edit", false);
                CheckEditor(host, access, host.Multiline, "multiline edit", true);
                CheckEditor(host, access, host.Rich, "rich edit", true);

                Expect.Equal(access.Classify(AutomationElement.FromHandle(host.HandleOf(host.Plain))), "Usable");
                Expect.Equal(access.Classify(AutomationElement.FromHandle(host.HandleOf(host.Password))), "Secure");
                Expect.Equal(access.Classify(AutomationElement.FromHandle(host.HandleOf(host.ReadOnly))), "ReadOnly");
                Expect.Equal(access.Classify(AutomationElement.FromHandle(host.HandleOf(host.Button))), "NotText");
                Expect.True(Engine.IsPasswordControl(host.HandleOf(host.Password)), "password style not detected");
                Expect.True(!Engine.IsPasswordControl(host.HandleOf(host.Plain)), "plain edit taken for a password field");
                Expect.True(!Engine.IsPasswordControl(host.HandleOf(host.Button)), "button taken for a password field");
            }
        }

        private static void CheckEditor(EditorHost host, TextAccess access, TextBoxBase box, string kind, bool multiline)
        {
            AutomationElement element = AutomationElement.FromHandle(host.HandleOf(box));
            object raw;
            if (!element.TryGetCurrentPattern(TextPattern.Pattern, out raw))
            {
                Expect.True(false, kind + ": no text pattern");
                return;
            }
            TextPattern pattern = (TextPattern)raw;
            Func<TextTarget> find = delegate { return access.TargetFor(element, pattern, host.Handle); };
            Func<int> end = delegate { return host.TextOf(box).Length; };

            // The word before the caret, selected and replaced.
            host.Set(box, "first ghbdtn", 12, 0);
            TextTarget target = find();
            Expect.Equal(target.Text, "ghbdtn");
            Expect.Equal(target.Trailing, "");
            Expect.True(!target.WasSelected, kind + ": a caret is not a selection");
            Expect.True(access.SelectionIs(target, target.OriginalSelection), kind + ": original selection");
            Expect.True(access.Select(target.Range), kind + ": selection request refused");
            Expect.True(WaitFor(delegate { return access.SelectionIs(target, target.Range); }), kind + ": selection not confirmed");
            Expect.Equal(host.SelectionStartOf(box), 6);
            Expect.Equal(host.SelectionLengthOf(box), 6);
            Expect.True(access.TextIs(target.Range, "ghbdtn"), kind + ": range text");
            Expect.True(!access.Replaced(target, "привет"), kind + ": replacement reported before it happened");
            host.Do(delegate { box.SelectedText = "привет"; });
            Expect.True(WaitFor(delegate { return access.Replaced(target, "привет"); }), kind + ": replacement not confirmed");
            Expect.True(!access.Replaced(target, "другое"), kind + ": wrong replacement confirmed");
            Expect.Equal(host.TextOf(box), "first привет");

            // Trailing spaces stay, and the caret returns behind them.
            host.Set(box, "first ghbdtn  ", 14, 0);
            target = find();
            Expect.Equal(target.Text, "ghbdtn");
            Expect.Equal(target.Trailing, "  ");
            Expect.True(access.Select(target.Range), kind + ": selection request refused");
            Expect.True(WaitFor(delegate { return access.SelectionIs(target, target.Range); }), kind + ": selection not confirmed");
            Expect.Equal(host.SelectionStartOf(box), 6);
            Expect.Equal(host.SelectionLengthOf(box), 6);
            host.Do(delegate { box.SelectedText = "GHBDTN"; });
            Expect.True(WaitFor(delegate { return access.Replaced(target, "GHBDTN"); }), kind + ": replacement not confirmed");
            Expect.True(access.MoveCaretForward(target, 2, "GHBDTN  "), kind + ": caret not moved behind the spaces");
            Expect.Equal(host.SelectionStartOf(box), 14);
            Expect.Equal(host.SelectionLengthOf(box), 0);
            Expect.Equal(host.TextOf(box), "first GHBDTN  ");

            // A selection is used as it is.
            host.Set(box, "one ghbdtn two", 4, 6);
            target = find();
            Expect.True(target.WasSelected, kind + ": selection not seen");
            Expect.Equal(target.Text, "ghbdtn");
            Expect.True(access.SelectionIs(target, target.Range), kind + ": selection range");
            host.Do(delegate { box.SelectedText = "привет"; });
            Expect.True(WaitFor(delegate { return access.Replaced(target, "привет"); }), kind + ": selection replacement not confirmed");

            // Word boundaries.
            host.Set(box, "ghbdtn", 6, 0);
            Expect.Equal(find().Text, "ghbdtn");
            host.Set(box, "ghbdtn", 3, 0);
            Expect.Equal(find().Text, "ghb");
            host.Set(box, "a,b.c!", 6, 0);
            Expect.Equal(find().Text, "a,b.c!");
            host.Set(box, "привет \xD83D\xDC4Bмир", 12, 0);
            target = find();
            Expect.Equal(target.Text, "\xD83D\xDC4Bмир");
            Expect.True(access.Select(target.Range), kind + ": selection request refused");
            Expect.True(WaitFor(delegate { return access.SelectionIs(target, target.Range); }), kind + ": emoji selection not confirmed");
            Expect.Equal(host.Get(delegate { return box.SelectedText; }), "\xD83D\xDC4Bмир");

            host.Set(box, "", 0, 0);
            ExpectNoWord(kind + " empty", delegate { find(); });
            host.Set(box, "word\t", 5, 0);
            ExpectNoWord(kind + " after a tab", delegate { find(); });
            host.Set(box, "word         ", 13, 0);
            ExpectNoWord(kind + " after nine spaces", delegate { find(); });
            host.Set(box, new string('x', 300), 300, 0);
            ExpectNoWord(kind + " word too long", delegate { find(); });
            host.Set(box, "word " + new string('x', 256), 261, 0);
            Expect.Equal(find().Text.Length, 256);

            if (multiline)
            {
                host.Set(box, "line one\r\nghbdtn", 0, 0);
                host.Do(delegate { box.Select(box.Text.Length, 0); });
                Expect.Equal(find().Text, "ghbdtn");
                host.Set(box, "line one\r\n", 0, 0);
                host.Do(delegate { box.Select(box.Text.Length, 0); });
                ExpectNoWord(kind + " at the start of a line", delegate { find(); });
            }

            // The typed-word probe.
            host.Set(box, "first ghbdtn", 12, 0);
            Expect.Equal(access.ProbeCaretFor(pattern, Typed("ghbdtn")), CaretProbe.Matches);
            Expect.Equal(access.ProbeCaretFor(pattern, Typed("ghbdtx")), CaretProbe.Contradicts);
            host.Set(box, "ghbdtn", 6, 0);
            Expect.Equal(access.ProbeCaretFor(pattern, Typed("ghbdtn")), CaretProbe.Matches);
            host.Set(box, "firstghbdtn", 11, 0);
            Expect.Equal(access.ProbeCaretFor(pattern, Typed("ghbdtn")), CaretProbe.Contradicts);
            host.Set(box, "first ghbdtn ", 13, 0);
            Expect.Equal(access.ProbeCaretFor(pattern, Typed("ghbdtn ")), CaretProbe.Matches);
            Expect.Equal(access.ProbeCaretFor(pattern, Typed("ghbdtn")), CaretProbe.Contradicts);
            host.Set(box, "ghbdtn.com", 6, 4);
            Expect.Equal(access.ProbeCaretFor(pattern, Typed("ghbdtn")), CaretProbe.CompletionSelected);
            GC.KeepAlive(end);
        }

        // ---- Installed layouts ----

        /// Uses the installed layouts without switching the active one or posting keys.
        private static void CheckInstalledLayouts()
        {
            Layouts layouts = new Layouts(false);
            KeyboardLayout english = layouts.Find(layouts.EnglishId);
            KeyboardLayout russian = layouts.Find(layouts.RussianId);
            if (english == null || russian == null)
            {
                Expect.True(false, "add an English and a Russian keyboard layout first");
                return;
            }
            Console.WriteLine("     English: " + english.Name + " [" + english.IdText + "], Russian: " + russian.Name + " [" + russian.IdText + "]");
            // Scan code 0x22 is G/П, 0x39 Space, 0x1C Enter, 0x0E Backspace, 0x0F Tab, 0x4B Left.
            foreach (KeyboardLayout layout in new[] { english, russian })
            {
                string plain = layout == english ? "g" : "п";
                string upper = layout == english ? "G" : "П";
                Expect.Equal(layout.TypedCharacter(0x47, 0x22, false, false), plain);
                Expect.Equal(layout.TypedCharacter(0x47, 0x22, true, false), upper);
                Expect.Equal(layout.TypedCharacter(0x47, 0x22, false, true), upper);
                Expect.Equal(layout.TypedCharacter(0x47, 0x22, true, true), plain);
                Expect.Equal(layout.TypedCharacter(0x20, 0x39, false, false), " ");
                Expect.Equal(layout.TypedCharacter(0x31, 0x02, false, false), "1");
                Expect.True(layout.TypedCharacter(0x0D, 0x1C, false, false) == null, "Enter is not a character");
                Expect.True(layout.TypedCharacter(0x08, 0x0E, false, false) == null, "Backspace is not a character");
                Expect.True(layout.TypedCharacter(0x09, 0x0F, false, false) == null, "Tab is not a character");
                Expect.True(layout.TypedCharacter(0x1B, 0x01, false, false) == null, "Escape is not a character");
                Expect.True(layout.TypedCharacter(0x25, 0x4B, false, false) == null, "Left is not a character");
                Expect.True(layout.TypedCharacter(0x70, 0x3B, false, false) == null, "F1 is not a character");
                Expect.Equal(layout.Characters(0x22, false), plain);
                Expect.Equal(layout.Characters(0x22, true), upper);
            }
            string[][] pairs =
            {
                new[] { "ghbdtn", "привет" }, new[] { "привет", "ghbdtn" },
                new[] { "GhBdTn", "ПрИвЕт" }, new[] { "ПрИвЕт", "GhBdTn" },
                new[] { "ghbdtn\nGhbdtn \xD83D\xDC4B", "привет\nПривет \xD83D\xDC4B" },
                new[] { "Ghbdtn? rfr ltkf&", "Привет, как дела?" }, new[] { "Привет, как дела?", "Ghbdtn? rfr ltkf&" },
new[] { "Ёжик", "~;br" }
            };
            foreach (string[] pair in pairs)
            {
                Conversion conversion = layouts.ConversionFor(pair[0]);
                Expect.Equal(conversion.Text, pair[1]);
                Expect.True(conversion.Target == (Transforms.AlphabetOf(pair[0]) == Alphabet.English ? russian : english),
                    "wrong target layout for " + pair[0]);
            }
            Expect.Throws("Select text in one language to convert its layout.", delegate { layouts.ConversionFor("hello мир"); });
            Expect.True(layouts.CurrentName.Length > 0, "the active layout has no name");
        }

        // ---- Settings window ----

        /// Draws the settings window into a picture without showing it on screen, at the
        /// display's scale (as the app does) or at 100%.
        public static int RenderSettings(string path, bool scaled)
        {
            Exception failure = null;
            // The app declares this in its manifest.
            if (scaled) SetProcessDPIAware();
            Thread thread = new Thread(delegate()
            {
                try
                {
                    Application.EnableVisualStyles();
                    Engine engine = new Engine(new Layouts(false));
                    using (SettingsForm form = new SettingsForm(engine, delegate { }))
                    {
                        form.ShowInactive = true;
                        form.ShowInTaskbar = false;
                        form.StartPosition = FormStartPosition.Manual;
                        form.Location = new Point(-30000, -30000);
                        form.Show();
                        Application.DoEvents();
                        using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
                        {
                            form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
                            bitmap.Save(path, ImageFormat.Png);
                        }
                        form.Close();
                    }
                }
                catch (Exception error)
                {
                    failure = error;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (failure != null)
            {
                Console.WriteLine("FAIL " + failure);
                return 1;
            }
            Console.WriteLine("Settings window drawn to " + path);
            return 0;
        }
    }
}
