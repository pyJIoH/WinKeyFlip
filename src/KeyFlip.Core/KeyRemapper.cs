using System.Collections.Generic;

namespace KeyFlip.Core
{
    /// A synthetic key event to send before the event that is being handled.
    public struct KeyStroke
    {
        public readonly int Key;
        public readonly bool Up;

        public KeyStroke(int key, bool up)
        {
            Key = key;
            Up = up;
        }
    }

    /// Turns the Mac shortcuts of `MacShortcuts` into what Windows expects, one keyboard
    /// event at a time. For each event it says whether the event is passed on or
    /// swallowed, and which synthetic events go to the system before it.
    ///
    /// It never trusts its own memory of what the system believes: the state of the
    /// modifier keys as Windows reports it comes in with every event. It remembers only
    /// where it made that state differ from the keys that are physically held:
    /// - a hidden key is held, but the system was told it is up: Alt, which is Command
    ///   and reaches the system only where a shortcut needs a real Alt, and any
    ///   modifier that a shortcut must not contain;
    /// - a lent key is down for the system, but nobody holds it: the Ctrl of Ctrl+C
    ///   while Alt+C is pressed.
    /// Lent keys go up again when the key of the shortcut is released, hidden Ctrl and
    /// Shift come back then, and a hidden Alt or Win stays hidden until it is needed.
    public sealed class KeyRemapper
    {
        /// An unassigned virtual key. A press of any key between the press and the
        /// release of Alt or Win keeps the release from opening a menu.
        public const int MaskKey = 0xE8;
        /// The scan code of the left Ctrl that Windows adds to AltGr.
        public const int FakeControlScanCode = 0x21D;
        /// Auto-repeat never pauses this long. As for Caps Lock.
        public const int LostReleaseMilliseconds = 1500;
        /// After this long without any key event, a hidden key is taken to be released.
        public const int IdleMilliseconds = 10000;

        public const int LeftShiftBit = 1;
        public const int RightShiftBit = 2;
        public const int LeftControlBit = 4;
        public const int RightControlBit = 8;
        public const int LeftAltBit = 16;
        public const int RightAltBit = 32;
        public const int LeftWinBit = 64;
        public const int RightWinBit = 128;
        public const int ShiftBits = LeftShiftBit | RightShiftBit;
        public const int ControlBits = LeftControlBit | RightControlBit;
        public const int AltBits = LeftAltBit | RightAltBit;
        public const int WinBits = LeftWinBit | RightWinBit;

        /// The key of each bit, lowest bit first.
        public static readonly int[] ModifierKeys =
        {
            VirtualKeys.LeftShift, VirtualKeys.RightShift, VirtualKeys.LeftControl, VirtualKeys.RightControl,
            VirtualKeys.LeftAlt, VirtualKeys.RightAlt, VirtualKeys.LeftWin, VirtualKeys.RightWin
        };

        /// The synthetic events of the last call, in the order to send them.
        public readonly List<KeyStroke> Output = new List<KeyStroke>();

        private readonly List<int> swallowed = new List<int>();
        private readonly List<int> passed = new List<int>();
        private int os;
        private int hidden;
        private int lent;
        private bool altUnused;
        private bool winUnused;
        /// A modifier went down for the system, and no key has followed it.
        private bool bare;
        private bool switching;
        private bool fakeControl;
        private bool altGr;
        private bool modifiersRepeat;
        private bool started;
        private int lastTick;
        private int lastDown;

        /// The command that the last key press asked for.
        public ShortcutCommand Command { get; private set; }

        /// For a modifier key: whether the last event changed what is physically held.
        /// Auto-repeat does not, and neither does a release that had no press.
        public bool Transition { get; private set; }

        /// The modifier keys that are physically held after the last event.
        public int Held { get; private set; }

        /// What the last key press became, when that is a single key press; otherwise 0.
        public int EffectiveKey { get; private set; }
        public Modifiers EffectiveModifiers { get; private set; }

        public static int BitOf(int key)
        {
            for (int i = 0; i < ModifierKeys.Length; i++)
            {
                if (ModifierKeys[i] == key) return 1 << i;
            }
            return 0;
        }

        private static int KeyOf(int bit)
        {
            for (int i = 0; i < ModifierKeys.Length; i++)
            {
                if (bit == 1 << i) return ModifierKeys[i];
            }
            return 0;
        }

        /// Side-agnostic modifiers of a set of modifier keys.
        public static Modifiers ModifiersOf(int keys)
        {
            Modifiers modifiers = Modifiers.None;
            if ((keys & ShiftBits) != 0) modifiers |= Modifiers.Shift;
            if ((keys & ControlBits) != 0) modifiers |= Modifiers.Control;
            if ((keys & AltBits) != 0) modifiers |= Modifiers.Alt;
            if ((keys & WinBits) != 0) modifiers |= Modifiers.Win;
            return modifiers;
        }

        /// The modifier keys that are physically held now, given what the system reports.
        public int HeldGiven(int system)
        {
            return (system & ~lent) | hidden;
        }

        /// A keyboard event. `system` holds the modifier keys that are down as Windows
        /// reports them, before this event; `tick` is a millisecond clock that may wrap.
        /// Returns true when the event must not be passed on.
        public bool Key(int key, int scanCode, bool down, int system, int tick, IShortcutContext context)
        {
            Begin(system);
            int bit = BitOf(key);
            int gap = started ? unchecked(tick - lastTick) : 0;
            if (down) DropLostHolds(gap);
            bool swallow;
            if (bit != 0)
            {
                swallow = down ? ModifierDown(key, bit, scanCode, gap, context) : ModifierUp(key, bit, scanCode);
            }
            else
            {
                swallow = down ? KeyDown(key, context) : KeyUp(key);
            }
            if (down) lastDown = key;
            if (down && bit != 0 && !swallow) bare = true;
            lastTick = tick;
            started = true;
            // An event that is passed on reaches the system after the synthetic ones.
            int after = os;
            if (bit != 0 && !swallow) after = down ? after | bit : after & ~bit;
            Held = Physical(after);
            return swallow;
        }

        /// A mouse button went down: Command+click is Ctrl+click. However long ago the
        /// last key event was: files are picked one by one with Command held all along.
        public void MouseDown(int system, IShortcutContext context)
        {
            Begin(system);
            Modifiers held = Logical(Physical(os));
            if (!switching && (held & Modifiers.Alt) != 0 && (held & Modifiers.Control) == 0 && context.Active)
            {
                Reconcile(Modifiers.Control | (held & Modifiers.Shift)
                    | ((held & Modifiers.Win) != 0 ? Modifiers.Alt : Modifiers.None));
                // No key follows these modifiers. To Windows, two of them pressed and
                // released with nothing in between can be the shortcut that changes the
                // input language.
                if (Output.Count > 0) Mask();
            }
            Held = Physical(os);
        }

        /// The releases of keys were or will be missed: lets go of every lent key and
        /// forgets what was held.
        public void Reset(int system)
        {
            Begin(system);
            ReleaseLent();
            hidden = 0;
            swallowed.Clear();
            passed.Clear();
            switching = false;
            fakeControl = false;
            altGr = false;
            altUnused = false;
            winUnused = false;
            bare = false;
            Held = Physical(os);
        }

        private void Begin(int system)
        {
            Output.Clear();
            Command = ShortcutCommand.None;
            Transition = false;
            EffectiveKey = 0;
            EffectiveModifiers = Modifiers.None;
            os = system;
            // The system knows best: a lent key that is up is not lent any more, and a
            // hidden key that is down is not hidden.
            lent &= os;
            hidden &= ~os;
        }

        private int Physical(int system)
        {
            return (system & ~lent) | hidden;
        }

        private Modifiers Logical(int keys)
        {
            // AltGr is a letter key's business, not Command.
            if (altGr) keys &= ~(RightAltBit | LeftControlBit);
            return ModifiersOf(keys);
        }

        private bool InSession(int keys)
        {
            return (Logical(keys) & (Modifiers.Alt | Modifiers.Win)) != 0;
        }

        /// A hidden Alt or Win whose release never arrived (lock screen, a window that
        /// runs as administrator) would turn everything typed into shortcuts.
        private void DropLostHolds(int gap)
        {
            int held = hidden & (AltBits | WinBits);
            if (held == 0) return;
            int lost = 0;
            if (gap > IdleMilliseconds)
            {
                lost = held;
            }
            else if (modifiersRepeat && gap > LostReleaseMilliseconds)
            {
                // The key pressed last repeats while it is held, and this one did not.
                lost = held & BitOf(lastDown);
            }
            if (lost == 0) return;
            hidden &= ~lost;
            if (!InSession(Physical(os))) EndSession();
        }

        private bool ModifierDown(int key, int bit, int scanCode, int gap, IShortcutContext context)
        {
            bool was = (Physical(os) & bit) != 0;
            Transition = !was;
            if (key == VirtualKeys.LeftControl && scanCode == FakeControlScanCode)
            {
                fakeControl = true;
                return false;
            }
            if (was && key == lastDown && gap <= LostReleaseMilliseconds) modifiersRepeat = true;
            if ((bit & AltBits) != 0)
            {
                if (key == VirtualKeys.RightAlt && fakeControl)
                {
                    altGr = true;
                    return false;
                }
                if ((hidden & bit) != 0) return true; // Auto-repeat of the hidden key.
                if (was) return false;
                if (!context.Active)
                {
                    altUnused = true;
                    return false;
                }
                // Command: the system hears of it only where a shortcut needs a real Alt.
                hidden |= bit;
                return true;
            }
            if ((hidden & bit) != 0) return true;
            if (!was && (bit & WinBits) != 0) winUnused = true;
            // If this key was lent, it is the user's now.
            lent &= ~bit;
            return false;
        }

        private bool ModifierUp(int key, int bit, int scanCode)
        {
            Transition = (Physical(os) & bit) != 0;
            if (key == VirtualKeys.LeftControl && scanCode == FakeControlScanCode)
            {
                fakeControl = false;
                return false;
            }
            if (key == VirtualKeys.RightAlt && altGr)
            {
                altGr = false;
                return false;
            }
            // The system already has a hidden key as up.
            bool swallow = (hidden & bit) != 0;
            hidden &= ~bit;
            lent &= ~bit;
            int after = swallow ? os : os & ~bit;
            if (!InSession(Physical(after))) EndSession();
            return swallow;
        }

        private bool KeyDown(int key, IShortcutContext context)
        {
            int physical = Physical(os);
            Shortcut shortcut = null;
            if (InSession(physical))
            {
                Modifiers held = Logical(physical);
                shortcut = switching || !context.Active
                    ? MacShortcuts.Unchanged(held, key)
                    : MacShortcuts.Find(key, held, context);
            }
            if (shortcut == null)
            {
                swallowed.Remove(key);
                KeySent();
                return false;
            }
            if (shortcut.Switcher) switching = true;
            if (shortcut.Command != ShortcutCommand.None)
            {
                Command = shortcut.Command;
                Remember(swallowed, key);
                if (Command == ShortcutCommand.Lock)
                {
                    // The lock screen takes the releases of the keys that are down now.
                    EndSession();
                    hidden = 0;
                }
                return true;
            }
            Chord[] chords = shortcut.Chords;
            if (chords.Length == 1)
            {
                EffectiveKey = chords[0].Key;
                EffectiveModifiers = chords[0].Modifiers;
            }
            if (chords.Length == 1 && chords[0].Key == key)
            {
                // The key itself goes on, with other modifiers around it. They stay
                // until it is released, as they would under a finger.
                Reconcile(chords[0].Modifiers);
                KeySent();
                swallowed.Remove(key);
                Remember(passed, key);
                return false;
            }
            foreach (Chord chord in chords)
            {
                Reconcile(chord.Modifiers);
                Output.Add(new KeyStroke(chord.Key, false));
                Output.Add(new KeyStroke(chord.Key, true));
                KeySent();
            }
            Settle();
            Remember(swallowed, key);
            passed.Remove(key);
            return true;
        }

        private bool KeyUp(int key)
        {
            if (swallowed.Remove(key)) return true;
            if (passed.Remove(key) && passed.Count == 0) Settle();
            return false;
        }

        private static void Remember(List<int> keys, int key)
        {
            if (!keys.Contains(key)) keys.Add(key);
        }

        private void KeySent()
        {
            altUnused = false;
            winUnused = false;
            bare = false;
        }

        /// Shift with Ctrl or Alt, pressed and released with no key between them, is to
        /// Windows the shortcut that changes the input language.
        private bool LanguagePair()
        {
            return bare && (os & ShiftBits) != 0 && (os & (ControlBits | AltBits)) != 0;
        }

        private void EndSession()
        {
            switching = false;
            passed.Clear();
            Settle();
        }

        private void Mask()
        {
            Output.Add(new KeyStroke(MaskKey, false));
            Output.Add(new KeyStroke(MaskKey, true));
            KeySent();
        }

        /// Makes the system's modifiers exactly `want`. Releases come first: Ctrl and
        /// Shift, or Alt and Shift, held together with no key between them can be the
        /// shortcut that changes the input language.
        private void Reconcile(Modifiers want)
        {
            if ((want & Modifiers.Shift) == 0) Release(ShiftBits);
            if ((want & Modifiers.Control) == 0) Release(ControlBits);
            if ((want & Modifiers.Alt) == 0) Release(AltBits);
            if ((want & Modifiers.Win) == 0) Release(WinBits);
            if ((want & Modifiers.Shift) != 0) Hold(ShiftBits, LeftShiftBit);
            if ((want & Modifiers.Control) != 0) Hold(ControlBits, LeftControlBit);
            if ((want & Modifiers.Alt) != 0) Hold(AltBits, LeftAltBit);
            if ((want & Modifiers.Win) != 0) Hold(WinBits, LeftWinBit);
        }

        private void Release(int bits)
        {
            for (int bit = 1; bit <= RightWinBit; bit <<= 1)
            {
                if ((bits & bit & os) != 0) Lift(bit);
            }
        }

        private void Lift(int bit)
        {
            if (((bit & AltBits) != 0 && altUnused) || ((bit & WinBits) != 0 && winUnused)
                || ((bit & WinBits) == 0 && LanguagePair()))
            {
                Mask();
            }
            Output.Add(new KeyStroke(KeyOf(bit), true));
            os &= ~bit;
            // A key that was not lent is under a finger: it is hidden from now on.
            if ((lent & bit) != 0) lent &= ~bit;
            else hidden |= bit;
        }

        private void Hold(int bits, int leftBit)
        {
            if ((os & bits) != 0) return;
            // A key of this kind that is held but hidden comes back first.
            int own = hidden & bits;
            int bit = own != 0 ? own & -own : leftBit;
            Output.Add(new KeyStroke(KeyOf(bit), false));
            os |= bit;
            if (own != 0) hidden &= ~bit;
            else lent |= bit;
            if ((bit & AltBits) != 0) altUnused = true;
            if ((bit & WinBits) != 0) winUnused = true;
            bare = true;
        }

        private void ReleaseLent()
        {
            for (int bit = 1; bit <= RightWinBit; bit <<= 1)
            {
                if ((lent & bit) != 0) Lift(bit);
            }
        }

        /// Between shortcuts: nothing is lent, and Ctrl and Shift are as they are held.
        private void Settle()
        {
            ReleaseLent();
            bool restored = false;
            for (int bit = 1; bit <= RightControlBit; bit <<= 1)
            {
                if ((hidden & bit) == 0) continue;
                Output.Add(new KeyStroke(KeyOf(bit), false));
                os |= bit;
                hidden &= ~bit;
                bare = true;
                restored = true;
            }
            // No key follows a key that comes back.
            if (restored && LanguagePair()) Mask();
        }
    }
}
