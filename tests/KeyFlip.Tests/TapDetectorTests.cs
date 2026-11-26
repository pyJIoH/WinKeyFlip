using System.Collections.Generic;
using KeyFlip.Core;

namespace KeyFlip.Tests
{
    internal static class TapDetectorTests
    {
        private const int LeftAlt = VirtualKeys.LeftAlt;
        private const int RightAlt = VirtualKeys.RightAlt;
        private const int LeftControl = VirtualKeys.LeftControl;
        private const int RightControl = VirtualKeys.RightControl;

        private sealed class Tap
        {
            public readonly int Key;
            public readonly Modifiers Modifiers;
            public readonly KeyAction Action;

            public Tap(int key, Modifiers modifiers, KeyAction action)
            {
                Key = key;
                Modifiers = modifiers;
                Action = action;
            }
        }

        private static readonly Tap[] Triggers =
        {
            new Tap(LeftAlt, Modifiers.Alt, KeyAction.Layout),
            new Tap(RightAlt, Modifiers.Alt, KeyAction.Layout),
            new Tap(LeftControl, Modifiers.Control, KeyAction.InvertCase)
        };

        public static void Add(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("Either Alt converts layout", AltTapOnEitherSide));
            scenarios.Add(new Scenario("Only left Ctrl inverts case", OnlyLeftControlInvertsCase));
            scenarios.Add(new Scenario("Windows keys ignored", WindowsKeysDoNotTrigger));
            scenarios.Add(new Scenario("Win as the conversion key", WinTapConvertsWhenChosen));
            scenarios.Add(new Scenario("Win shortcuts are not taps", WinShortcutsAreNotTaps));
            scenarios.Add(new Scenario("Tap after normal typing", NormalTypingDoesNotConsumeTheNextTap));
            scenarios.Add(new Scenario("Shortcut cancellation", ShortcutDoesNotTriggerOnRelease));
            scenarios.Add(new Scenario("Mixed modifiers", MixedModifiersAreNotTaps));
            scenarios.Add(new Scenario("Both Alt keys cancel", BothAltKeysDoNotTrigger));
            scenarios.Add(new Scenario("Both Ctrl keys cancel", BothControlKeysDoNotTrigger));
            scenarios.Add(new Scenario("Long hold and unmatched release", LongHoldAndUnmatchedReleaseDoNotTrigger));
            scenarios.Add(new Scenario("Reset an armed tap", ResetCancelsAnArmedTap));
            scenarios.Add(new Scenario("Held modifiers block hotkeys", HeldShiftOrWinBlocksNewHotkeys));
            scenarios.Add(new Scenario("Repeated and alternating taps", RepeatedAndAlternatingTaps));
            scenarios.Add(new Scenario("AltGr is not a tap", AltGrIsNotATap));
        }

        private static void AltTapOnEitherSide()
        {
            foreach (int key in new[] { LeftAlt, RightAlt })
            {
                TapDetector detector = new TapDetector();
                Expect.Null(detector.ModifiersChanged(key, Modifiers.Alt, 1));
                Expect.Equal(detector.ModifiersChanged(key, Modifiers.None, 1.1), KeyAction.Layout);
            }
        }

        private static void OnlyLeftControlInvertsCase()
        {
            TapDetector detector = new TapDetector();
            Expect.Null(detector.ModifiersChanged(LeftControl, Modifiers.Control, 1));
            Expect.Equal(detector.ModifiersChanged(LeftControl, Modifiers.None, 1.1), KeyAction.InvertCase);
            Expect.Null(detector.ModifiersChanged(RightControl, Modifiers.Control, 2));
            Expect.Null(detector.ModifiersChanged(RightControl, Modifiers.None, 2.1));
            detector.ModifiersChanged(LeftControl, Modifiers.Control, 3);
            Expect.Equal(detector.ModifiersChanged(LeftControl, Modifiers.None, 3.1), KeyAction.InvertCase);
        }

        private static void WindowsKeysDoNotTrigger()
        {
            foreach (int key in new[] { VirtualKeys.LeftWin, VirtualKeys.RightWin })
            {
                TapDetector detector = new TapDetector();
                Expect.Null(detector.ModifiersChanged(key, Modifiers.Win, 1));
                Expect.Null(detector.ModifiersChanged(key, Modifiers.None, 1.1));
                detector.ModifiersChanged(LeftAlt, Modifiers.Alt, 2);
                Expect.Equal(detector.ModifiersChanged(LeftAlt, Modifiers.None, 2.1), KeyAction.Layout);
            }
        }

        private static TapDetector WinDetector()
        {
            TapDetector detector = new TapDetector();
            detector.LayoutKey = LayoutTapKey.Win;
            return detector;
        }

        private static void WinTapConvertsWhenChosen()
        {
            foreach (int key in new[] { VirtualKeys.LeftWin, VirtualKeys.RightWin })
            {
                TapDetector detector = WinDetector();
                Expect.Null(detector.ModifiersChanged(key, Modifiers.Win, 1));
                Expect.Equal(detector.ModifiersChanged(key, Modifiers.None, 1.1), KeyAction.Layout);
            }
            // Alt is an ordinary key again; left Ctrl keeps its action.
            TapDetector other = WinDetector();
            foreach (int key in new[] { LeftAlt, RightAlt })
            {
                Expect.Null(other.ModifiersChanged(key, Modifiers.Alt, 2));
                Expect.Null(other.ModifiersChanged(key, Modifiers.None, 2.1));
            }
            other.ModifiersChanged(LeftControl, Modifiers.Control, 3);
            Expect.Equal(other.ModifiersChanged(LeftControl, Modifiers.None, 3.1), KeyAction.InvertCase);
            other.ModifiersChanged(VirtualKeys.LeftWin, Modifiers.Win, 4);
            Expect.Equal(other.ModifiersChanged(VirtualKeys.LeftWin, Modifiers.None, 4.1), KeyAction.Layout);
        }

        private static void WinShortcutsAreNotTaps()
        {
            const int Win = VirtualKeys.LeftWin;
            const int Shift = VirtualKeys.LeftShift;
            TapDetector detector = WinDetector();
            // Win+E, Win+V, a click with Win held.
            detector.ModifiersChanged(Win, Modifiers.Win, 1);
            detector.Cancel();
            Expect.Null(detector.ModifiersChanged(Win, Modifiers.None, 1.1));
            // Win+Shift+S.
            detector.ModifiersChanged(Win, Modifiers.Win, 2);
            detector.ModifiersChanged(Shift, Modifiers.Win | Modifiers.Shift, 2.05);
            detector.Cancel();
            Expect.Null(detector.ModifiersChanged(Shift, Modifiers.Win, 2.1));
            Expect.Null(detector.ModifiersChanged(Win, Modifiers.None, 2.2));
            // Shift first, then Win.
            detector.ModifiersChanged(Shift, Modifiers.Shift, 3);
            detector.ModifiersChanged(Win, Modifiers.Win | Modifiers.Shift, 3.05);
            Expect.Null(detector.ModifiersChanged(Win, Modifiers.Shift, 3.1));
            Expect.Null(detector.ModifiersChanged(Shift, Modifiers.None, 3.2));
            // Held for more than a second: the Start menu is wanted.
            detector.ModifiersChanged(Win, Modifiers.Win, 4);
            Expect.Null(detector.ModifiersChanged(Win, Modifiers.None, 5.5));
            // Both Windows keys.
            detector.ModifiersChanged(Win, Modifiers.Win, 6);
            detector.ModifiersChanged(VirtualKeys.RightWin, Modifiers.Win, 6.05);
            detector.ModifiersChanged(Win, Modifiers.Win, 6.1);
            Expect.Null(detector.ModifiersChanged(VirtualKeys.RightWin, Modifiers.None, 6.2));
            // A plain tap still works after all of that.
            detector.ModifiersChanged(Win, Modifiers.Win, 7);
            Expect.Equal(detector.ModifiersChanged(Win, Modifiers.None, 7.1), KeyAction.Layout);
        }

        private static void NormalTypingDoesNotConsumeTheNextTap()
        {
            TapDetector detector = new TapDetector();
            detector.Cancel(); // An ordinary letter, with no modifier held.
            detector.ModifiersChanged(LeftAlt, Modifiers.Alt, 1);
            Expect.Equal(detector.ModifiersChanged(LeftAlt, Modifiers.None, 1.1), KeyAction.Layout);
        }

        private static void ShortcutDoesNotTriggerOnRelease()
        {
            foreach (Tap tap in Triggers)
            {
                TapDetector detector = new TapDetector();
                detector.ModifiersChanged(tap.Key, tap.Modifiers, 1);
                detector.Cancel(); // Alt+Tab, Ctrl+A, a mouse click, or scrolling.
                Expect.Null(detector.ModifiersChanged(tap.Key, Modifiers.None, 1.1));
                detector.ModifiersChanged(tap.Key, tap.Modifiers, 2);
                Expect.Equal(detector.ModifiersChanged(tap.Key, Modifiers.None, 2.1), tap.Action);
            }
        }

        private static void MixedModifiersAreNotTaps()
        {
            foreach (bool releaseAltFirst in new[] { true, false })
            {
                TapDetector detector = new TapDetector();
                detector.ModifiersChanged(LeftAlt, Modifiers.Alt, 1);
                detector.ModifiersChanged(LeftControl, Modifiers.Control | Modifiers.Alt, 1.05);
                Expect.Null(detector.ModifiersChanged(releaseAltFirst ? LeftAlt : LeftControl,
                                                      releaseAltFirst ? Modifiers.Control : Modifiers.Alt, 1.1));
                Expect.Null(detector.ModifiersChanged(releaseAltFirst ? LeftControl : LeftAlt, Modifiers.None, 1.2));
            }
        }

        private static void BothAltKeysDoNotTrigger()
        {
            foreach (int first in new[] { LeftAlt, RightAlt })
            {
                int second = first == LeftAlt ? RightAlt : LeftAlt;
                TapDetector detector = new TapDetector();
                detector.ModifiersChanged(first, Modifiers.Alt, 1);
                detector.ModifiersChanged(second, Modifiers.Alt, 1.05);
                detector.ModifiersChanged(first, Modifiers.Alt, 1.1);
                Expect.Null(detector.ModifiersChanged(second, Modifiers.None, 1.2));
            }
        }

        private static void BothControlKeysDoNotTrigger()
        {
            foreach (int first in new[] { LeftControl, RightControl })
            {
                int second = first == LeftControl ? RightControl : LeftControl;
                TapDetector detector = new TapDetector();
                detector.ModifiersChanged(first, Modifiers.Control, 1);
                detector.ModifiersChanged(second, Modifiers.Control, 1.05);
                detector.ModifiersChanged(first, Modifiers.Control, 1.1);
                Expect.Null(detector.ModifiersChanged(second, Modifiers.None, 1.2));
            }
        }

        private static void LongHoldAndUnmatchedReleaseDoNotTrigger()
        {
            foreach (Tap tap in Triggers)
            {
                TapDetector detector = new TapDetector();
                Expect.Null(detector.ModifiersChanged(tap.Key, Modifiers.None, 1));
                detector.ModifiersChanged(tap.Key, tap.Modifiers, 2);
                Expect.Null(detector.ModifiersChanged(tap.Key, Modifiers.None, 3.5));
            }
        }

        private static void ResetCancelsAnArmedTap()
        {
            TapDetector detector = new TapDetector();
            detector.ModifiersChanged(LeftAlt, Modifiers.Alt, 1);
            detector.Reset();
            Expect.Null(detector.ModifiersChanged(LeftAlt, Modifiers.None, 1.1));
        }

        private static void HeldShiftOrWinBlocksNewHotkeys()
        {
            foreach (Modifiers other in new[] { Modifiers.Shift, Modifiers.Win })
            {
                foreach (Tap tap in Triggers)
                {
                    TapDetector detector = new TapDetector();
                    Expect.Null(detector.ModifiersChanged(tap.Key, tap.Modifiers | other, 1));
                    Expect.Null(detector.ModifiersChanged(tap.Key, other, 1.1));
                    Expect.Null(detector.ModifiersChanged(VirtualKeys.LeftShift, Modifiers.None, 1.2));
                }
            }
        }

        private static void RepeatedAndAlternatingTaps()
        {
            TapDetector detector = new TapDetector();
            Tap[] sequence = { Triggers[0], Triggers[1], Triggers[2], Triggers[2], Triggers[0] };
            for (int index = 0; index < sequence.Length; index++)
            {
                Tap tap = sequence[index];
                Expect.Null(detector.ModifiersChanged(tap.Key, tap.Modifiers, index));
                Expect.Equal(detector.ModifiersChanged(tap.Key, Modifiers.None, index + 0.1), tap.Action);
                Expect.Null(detector.ModifiersChanged(tap.Key, Modifiers.None, index + 0.2));
            }
        }

        /// AltGr arrives as a synthetic left Ctrl press followed by right Alt.
        private static void AltGrIsNotATap()
        {
            TapDetector detector = new TapDetector();
            Expect.Null(detector.ModifiersChanged(LeftControl, Modifiers.Control, 1));
            Expect.Null(detector.ModifiersChanged(RightAlt, Modifiers.Control | Modifiers.Alt, 1));
            Expect.Null(detector.ModifiersChanged(LeftControl, Modifiers.Alt, 1.1));
            Expect.Null(detector.ModifiersChanged(RightAlt, Modifiers.None, 1.1));
            detector.ModifiersChanged(LeftAlt, Modifiers.Alt, 2);
            Expect.Equal(detector.ModifiersChanged(LeftAlt, Modifiers.None, 2.1), KeyAction.Layout);
        }
    }
}
