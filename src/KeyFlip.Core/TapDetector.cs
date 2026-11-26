namespace KeyFlip.Core
{
    /// A shortcut is a complete, brief press/release of one trigger key with no
    /// intervening activity. The caller reports every modifier transition (without
    /// auto-repeat) together with the modifier state after it.
    public sealed class TapDetector
    {
        private enum State { Idle, Armed, Cancelled }

        private State state = State.Idle;
        private int armedKey;
        private KeyAction armedAction;
        private double started;

        /// Seconds.
        public double MaximumDuration = 1.0;

        /// The key that converts the layout. The other one of the two never triggers.
        public LayoutTapKey LayoutKey = LayoutTapKey.Alt;

        /// Any non-modifier activity: a key press, a click, scrolling.
        public void Cancel()
        {
            if (state == State.Armed) state = State.Cancelled;
        }

        public void Reset()
        {
            state = State.Idle;
        }

        public KeyAction? ModifiersChanged(int key, Modifiers modifiers, double time)
        {
            switch (state)
            {
                case State.Cancelled:
                    if (modifiers == Modifiers.None) state = State.Idle;
                    break;
                case State.Armed:
                    state = modifiers == Modifiers.None ? State.Idle : State.Cancelled;
                    if (key == armedKey && modifiers == Modifiers.None
                        && time >= started && time - started <= MaximumDuration)
                    {
                        return armedAction;
                    }
                    break;
                default:
                    if (IsLayoutTap(key, modifiers))
                    {
                        Arm(key, KeyAction.Layout, time);
                    }
                    else if (key == VirtualKeys.LeftControl && modifiers == Modifiers.Control)
                    {
                        // Left only; right Ctrl never triggers.
                        Arm(key, KeyAction.InvertCase, time);
                    }
                    else if (modifiers != Modifiers.None)
                    {
                        state = State.Cancelled;
                    }
                    break;
            }
            return null;
        }

        private bool IsLayoutTap(int key, Modifiers modifiers)
        {
            if (LayoutKey == LayoutTapKey.Win)
            {
                return (key == VirtualKeys.LeftWin || key == VirtualKeys.RightWin) && modifiers == Modifiers.Win;
            }
            return (key == VirtualKeys.LeftAlt || key == VirtualKeys.RightAlt) && modifiers == Modifiers.Alt;
        }

        private void Arm(int key, KeyAction action, double time)
        {
            state = State.Armed;
            armedKey = key;
            armedAction = action;
            started = time;
        }
    }
}
