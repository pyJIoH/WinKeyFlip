using System.Collections.Generic;
using KeyFlip.Core;

namespace KeyFlip.Tests
{
    internal static class CapsLockGestureTests
    {
        public static void Add(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("Caps Lock: a press switches the layout", PressSwitches));
            scenarios.Add(new Scenario("Caps Lock: a long press is the regular Caps Lock", LongPressIsCapsLock));
            scenarios.Add(new Scenario("Caps Lock: modifiers, the setting and a lost release", LeftToTheSystem));
        }

        private static void PressSwitches()
        {
            CapsLockGesture gesture = new CapsLockGesture();
            Expect.Equal(gesture.Down(1000, true), CapsLockStep.Switch);
            Expect.Equal(gesture.Up(1080), CapsLockStep.Swallow);
            // Just under the limit is still a press, with auto-repeat or without.
            Expect.Equal(gesture.Down(2000, true), CapsLockStep.Switch);
            Expect.Equal(gesture.Down(2300, true), CapsLockStep.Swallow);
            Expect.Equal(gesture.Up(2499), CapsLockStep.Swallow);
            // A release with nothing taken belongs to the system.
            Expect.Equal(gesture.Up(3000), CapsLockStep.Pass);
        }

        private static void LongPressIsCapsLock()
        {
            // Auto-repeat shows it while the key is still down: once, and the release adds nothing.
            CapsLockGesture gesture = new CapsLockGesture();
            Expect.Equal(gesture.Down(1000, true), CapsLockStep.Switch);
            Expect.Equal(gesture.Down(1250, true), CapsLockStep.Swallow);
            Expect.Equal(gesture.Down(1500, true), CapsLockStep.Hold);
            Expect.Equal(gesture.Down(1533, true), CapsLockStep.Swallow);
            Expect.Equal(gesture.Up(1900), CapsLockStep.Swallow);
            // Without auto-repeat in time, the release shows it.
            Expect.Equal(gesture.Down(3000, true), CapsLockStep.Switch);
            Expect.Equal(gesture.Up(3700), CapsLockStep.Hold);
            // The next press starts from nothing.
            Expect.Equal(gesture.Down(5000, true), CapsLockStep.Switch);
            Expect.Equal(gesture.Up(5100), CapsLockStep.Swallow);
            // The clock may wrap around during a press.
            Expect.Equal(gesture.Down(int.MaxValue - 100, true), CapsLockStep.Switch);
            Expect.Equal(gesture.Up(unchecked(int.MaxValue + 500)), CapsLockStep.Hold);
        }

        private static void LeftToTheSystem()
        {
            CapsLockGesture gesture = new CapsLockGesture();
            // With a modifier held, or with the setting off, every event is the system's.
            Expect.Equal(gesture.Down(1000, false), CapsLockStep.Pass);
            Expect.Equal(gesture.Down(1500, false), CapsLockStep.Pass);
            Expect.Equal(gesture.Up(1600), CapsLockStep.Pass);
            // A modifier that is pressed during a taken press does not hand it over half-way.
            Expect.Equal(gesture.Down(2000, true), CapsLockStep.Switch);
            Expect.Equal(gesture.Down(2300, false), CapsLockStep.Swallow);
            Expect.Equal(gesture.Up(2350), CapsLockStep.Swallow);
            // A release that never arrived: after a silence that auto-repeat never leaves,
            // a key-down is a new press, not a repeat of the old one.
            Expect.Equal(gesture.Down(3000, true), CapsLockStep.Switch);
            Expect.Equal(gesture.Down(9000, true), CapsLockStep.Switch);
            Expect.Equal(gesture.Up(9050), CapsLockStep.Swallow);
            Expect.Equal(gesture.Down(20000, true), CapsLockStep.Switch);
            Expect.Equal(gesture.Down(30000, false), CapsLockStep.Pass);
            Expect.Equal(gesture.Up(30050), CapsLockStep.Pass);
        }
    }
}
