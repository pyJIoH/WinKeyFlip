namespace KeyFlip.Core
{
    /// What to do with one Caps Lock key event.
    public enum CapsLockStep
    {
        /// Not taken: the event goes to the system as it is.
        Pass,
        /// Kept from the system; nothing else happens.
        Swallow,
        /// Kept from the system; switch the keyboard layout.
        Switch,
        /// Kept from the system; this press has lasted long enough to be the regular
        /// Caps Lock, so toggle it and put the layout back.
        Hold
    }

    /// Caps Lock as the layout switch. A press switches the layout at once, so that a
    /// key typed right after it is already in the new layout. A press that lasts is
    /// the regular Caps Lock instead, as on macOS. While a press is taken, the key
    /// never reaches the system, so the Caps Lock state changes only when asked for.
    ///
    /// Times are milliseconds on a clock that may wrap around.
    public sealed class CapsLockGesture
    {
        /// A press of at least this long is the regular Caps Lock.
        public int HoldMilliseconds = 500;

        /// Auto-repeat events are never this far apart. After a longer silence a
        /// key-down is a new press: the release went somewhere KeyFlip cannot see
        /// (the lock screen, an elevated window).
        public int LostReleaseMilliseconds = 1500;

        private bool taken;
        private bool held;
        private int pressed;
        private int last;

        /// A key-down, auto-repeat included. `eligible`: the feature is on and no
        /// modifier is held; otherwise a new press is left to the system.
        public CapsLockStep Down(int time, bool eligible)
        {
            if (taken && unchecked(time - last) > LostReleaseMilliseconds) taken = false;
            if (taken)
            {
                last = time;
                return Lasted(time) ? CapsLockStep.Hold : CapsLockStep.Swallow;
            }
            if (!eligible) return CapsLockStep.Pass;
            taken = true;
            held = false;
            pressed = time;
            last = time;
            return CapsLockStep.Switch;
        }

        /// A key-up. Without auto-repeat in time, this is where a long press shows.
        public CapsLockStep Up(int time)
        {
            if (!taken) return CapsLockStep.Pass;
            taken = false;
            return Lasted(time) ? CapsLockStep.Hold : CapsLockStep.Swallow;
        }

        /// True once per press, as soon as it has lasted long enough.
        private bool Lasted(int time)
        {
            if (held || unchecked(time - pressed) < HoldMilliseconds) return false;
            held = true;
            return true;
        }
    }
}
