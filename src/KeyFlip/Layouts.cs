using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using KeyFlip.Core;
using Microsoft.Win32;

namespace KeyFlip
{
    internal sealed class KeyboardLayout
    {
        /// The layout handle (HKL) as Windows returned it.
        public readonly IntPtr Handle;
        /// The low 32 bits of the handle: language id and layout id.
        public readonly uint Id;
        public readonly string Name;
        /// "en" or "ru".
        public readonly string Language;

        [ThreadStatic] private static byte[] keyState;
        [ThreadStatic] private static char[] characters;

        public KeyboardLayout(IntPtr handle, string name, string language)
        {
            Handle = handle;
            Id = Layouts.IdOf(handle);
            Name = name;
            Language = language;
        }

        public string IdText { get { return Id.ToString("X8", CultureInfo.InvariantCulture); } }

        /// The character a key position produces, for building conversion tables.
        public string Characters(int scanCode, bool shifted)
        {
            uint virtualKey = Native.MapVirtualKeyEx((uint)scanCode, Native.MAPVK_VSC_TO_VK, Handle);
            return virtualKey == 0 ? null : Translate((int)virtualKey, scanCode, shifted, false);
        }

        /// The character a key press inserts, or null for dead keys and control keys.
        public string TypedCharacter(int virtualKey, int scanCode, bool shifted, bool capsLock)
        {
            return Translate(virtualKey, scanCode, shifted, capsLock);
        }

        private string Translate(int virtualKey, int scanCode, bool shifted, bool capsLock)
        {
            if (keyState == null)
            {
                keyState = new byte[256];
                characters = new char[8];
            }
            Array.Clear(keyState, 0, keyState.Length);
            if (shifted)
            {
                keyState[Native.VK_SHIFT] = 0x80;
                keyState[VirtualKeys.LeftShift] = 0x80;
            }
            if (capsLock) keyState[Native.VK_CAPITAL] = 0x01;
            // Flag 4: do not change the kernel keyboard state, so a dead key the user
            // is in the middle of is left alone.
            int count = Native.ToUnicodeEx((uint)virtualKey, (uint)scanCode, keyState, characters, characters.Length, 4, Handle);
            // 0: no character. Negative: a dead key. More than one: a ligature.
            if (count != 1) return null;
            char character = characters[0];
            if (char.IsControl(character) || char.IsSurrogate(character)) return null;
            return character.ToString();
        }
    }

    internal sealed class Conversion
    {
        public string Text;
        public KeyboardLayout Target;
    }

    internal sealed class Layouts
    {
        public const string MissingMessage =
            "Add English and Russian keyboard layouts in Windows language settings, then refresh layouts.";

        private const int LanguageEnglish = 0x09;
        private const int LanguageRussian = 0x19;

        private volatile KeyboardLayout[] available = new KeyboardLayout[0];
        private readonly object tableLock = new object();
        private readonly Dictionary<ulong, Dictionary<char, char>> tables = new Dictionary<ulong, Dictionary<char, char>>();
        // Read on the input thread for every key press: kept in fields, not in the registry.
        private uint englishId;
        private uint russianId;

        private readonly bool persistent;

        public Layouts() : this(true)
        {
        }

        /// `persistent` is false in checks, which must not touch the user's settings.
        public Layouts(bool persistent)
        {
            this.persistent = persistent;
            if (persistent)
            {
                englishId = ParseId(Settings.Get("EnglishLayout"));
                russianId = ParseId(Settings.Get("RussianLayout"));
            }
            Reload();
        }

        public KeyboardLayout[] Available { get { return available; } }

        public uint EnglishId
        {
            get { return Volatile.Read(ref englishId); }
            set
            {
                if (Volatile.Read(ref englishId) == value) return;
                Volatile.Write(ref englishId, value);
                if (persistent) Settings.Set("EnglishLayout", value.ToString("X8", CultureInfo.InvariantCulture));
            }
        }

        public uint RussianId
        {
            get { return Volatile.Read(ref russianId); }
            set
            {
                if (Volatile.Read(ref russianId) == value) return;
                Volatile.Write(ref russianId, value);
                if (persistent) Settings.Set("RussianLayout", value.ToString("X8", CultureInfo.InvariantCulture));
            }
        }

        public static uint IdOf(IntPtr layout)
        {
            return unchecked((uint)layout.ToInt64());
        }

        private static uint ParseId(string text)
        {
            uint id;
            return uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id) ? id : 0;
        }

        public void Reload()
        {
            int count = Native.GetKeyboardLayoutList(0, null);
            IntPtr[] handles = new IntPtr[Math.Max(count, 0)];
            if (count > 0) count = Native.GetKeyboardLayoutList(count, handles);
            List<KeyboardLayout> found = new List<KeyboardLayout>();
            for (int i = 0; i < count; i++)
            {
                uint id = IdOf(handles[i]);
                int primaryLanguage = (int)(id & 0x3FF);
                string language = primaryLanguage == LanguageEnglish ? "en" : primaryLanguage == LanguageRussian ? "ru" : null;
                if (language == null || found.Exists(delegate(KeyboardLayout other) { return other.Id == id; })) continue;
                found.Add(new KeyboardLayout(handles[i], LayoutNames.Describe(id), language));
            }
            available = found.ToArray();
            lock (tableLock) tables.Clear();
            if (Find(EnglishId) == null || Find(EnglishId).Language != "en")
            {
                KeyboardLayout first = found.Find(delegate(KeyboardLayout layout) { return layout.Language == "en"; });
                if (first != null) EnglishId = first.Id;
            }
            if (Find(RussianId) == null || Find(RussianId).Language != "ru")
            {
                KeyboardLayout first = found.Find(delegate(KeyboardLayout layout) { return layout.Language == "ru"; });
                if (first != null) RussianId = first.Id;
            }
        }

        public KeyboardLayout Find(uint id)
        {
            foreach (KeyboardLayout layout in available)
            {
                if (layout.Id == id) return layout;
            }
            return null;
        }

        /// The layout of the thread that owns the focused control.
        public uint CurrentId
        {
            get { return IdOf(Native.GetKeyboardLayout(Native.GetFocusInfo().ThreadId)); }
        }

        public string CurrentName
        {
            get
            {
                uint id = CurrentId;
                if (id == 0) return "";
                KeyboardLayout layout = Find(id);
                return layout != null ? layout.Name : LayoutNames.Describe(id);
            }
        }

        /// The character a key press inserts with the selected English or Russian layout.
        /// Other layouts, including input methods, are not tracked.
        public string TypedCharacter(uint threadId, int virtualKey, int scanCode, bool shifted, bool capsLock)
        {
            uint id = IdOf(Native.GetKeyboardLayout(threadId));
            if (id == 0 || (id != EnglishId && id != RussianId)) return null;
            KeyboardLayout layout = Find(id);
            return layout == null ? null : layout.TypedCharacter(virtualKey, scanCode, shifted, capsLock);
        }

        public Conversion ConversionFor(string text)
        {
            KeyboardLayout english = Find(EnglishId);
            KeyboardLayout russian = Find(RussianId);
            if (english == null || russian == null) throw new AppFailure(MissingMessage);
            KeyboardLayout from;
            switch (Transforms.AlphabetOf(text))
            {
                case Alphabet.English:
                    from = english;
                    break;
                case Alphabet.Russian:
                    from = russian;
                    break;
                case Alphabet.Mixed:
                    throw new AppFailure("Select text in one language to convert its layout.");
                default:
                    uint current = CurrentId;
                    if (current != english.Id && current != russian.Id)
                    {
                        throw new AppFailure("Choose the English or Russian keyboard layout first.");
                    }
                    from = current == russian.Id ? russian : english;
                    break;
            }
            KeyboardLayout to = from == english ? russian : english;
            Conversion conversion = new Conversion();
            conversion.Text = Transforms.Convert(text, Table(from, to));
            conversion.Target = to;
            return conversion;
        }

        private Dictionary<char, char> Table(KeyboardLayout from, KeyboardLayout to)
        {
            ulong key = ((ulong)from.Id << 32) | to.Id;
            lock (tableLock)
            {
                Dictionary<char, char> table;
                if (!tables.TryGetValue(key, out table))
                {
                    table = KeyMap.Build(from.Characters, to.Characters);
                    tables[key] = table;
                }
                return table;
            }
        }

        /// A layout change that has been requested but not yet confirmed.
        public sealed class PendingSwitch
        {
            public KeyboardLayout Layout;
            public FocusInfo Focus;
        }

        /// Asks the focused window to switch layout, without waiting.
        public PendingSwitch Request(KeyboardLayout layout)
        {
            PendingSwitch pending = new PendingSwitch();
            pending.Layout = layout;
            pending.Focus = Native.GetFocusInfo();
            if (pending.Focus.Foreground != IntPtr.Zero && IdOf(Native.GetKeyboardLayout(pending.Focus.ThreadId)) != layout.Id)
            {
                Native.PostMessage(pending.Focus.Target, Native.WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, layout.Handle);
            }
            return pending;
        }

        /// Waits for a requested switch; throws when the window did not follow it.
        public void Confirm(PendingSwitch pending, bool textConverted)
        {
            FocusInfo focus = pending.Focus;
            if (focus.Foreground != IntPtr.Zero)
            {
                if (WaitFor(focus.ThreadId, pending.Layout.Id, 300)) return;
                if (focus.Target != focus.Foreground)
                {
                    Native.PostMessage(focus.Foreground, Native.WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, pending.Layout.Handle);
                    if (WaitFor(focus.ThreadId, pending.Layout.Id, 200)) return;
                }
            }
            throw new AppFailure(textConverted
                ? "Text converted, but Windows could not switch the keyboard layout."
                : "Windows could not switch the keyboard layout in this window.");
        }

        public void Select(KeyboardLayout layout, bool textConverted)
        {
            Confirm(Request(layout), textConverted);
        }

        public void Toggle()
        {
            uint targetId = CurrentId == RussianId ? EnglishId : RussianId;
            KeyboardLayout target = Find(targetId);
            if (target == null) throw new AppFailure(MissingMessage);
            Select(target, false);
        }

        private static bool WaitFor(uint threadId, uint layoutId, int timeoutMilliseconds)
        {
            for (int waited = 0; ; waited += 10)
            {
                if (IdOf(Native.GetKeyboardLayout(threadId)) == layoutId) return true;
                if (waited >= timeoutMilliseconds) return false;
                Thread.Sleep(10);
            }
        }
    }

    internal static class LayoutNames
    {
        private const string LayoutsKey = @"SYSTEM\CurrentControlSet\Control\Keyboard Layouts";

        /// "English (United States) — US".
        public static string Describe(uint id)
        {
            string language;
            try
            {
                language = new CultureInfo((int)(id & 0xFFFF)).EnglishName;
            }
            catch (Exception)
            {
                language = "Language " + (id & 0xFFFF).ToString("X4", CultureInfo.InvariantCulture);
            }
            string layout = LayoutText(id);
            return string.IsNullOrEmpty(layout) ? language : language + " — " + layout;
        }

        /// The name Windows gives the keyboard layout behind a handle, or null.
        private static string LayoutText(uint id)
        {
            try
            {
                uint device = id >> 16;
                using (RegistryKey root = Registry.LocalMachine.OpenSubKey(LayoutsKey))
                {
                    if (root == null) return null;
                    string identifier = null;
                    if ((device & 0xF000) == 0xF000)
                    {
                        // A variant layout: the handle carries its "Layout Id".
                        uint layoutId = device & 0x0FFF;
                        foreach (string name in root.GetSubKeyNames())
                        {
                            using (RegistryKey candidate = root.OpenSubKey(name))
                            {
                                string value = candidate == null ? null : candidate.GetValue("Layout Id") as string;
                                uint parsed;
                                if (value != null && uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsed)
                                    && parsed == layoutId)
                                {
                                    identifier = name;
                                    break;
                                }
                            }
                        }
                    }
                    else
                    {
                        identifier = device.ToString("X8", CultureInfo.InvariantCulture);
                    }
                    if (identifier == null) return null;
                    using (RegistryKey key = root.OpenSubKey(identifier))
                    {
                        return key == null ? null : key.GetValue("Layout Text") as string;
                    }
                }
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
