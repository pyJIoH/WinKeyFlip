using System.Collections.Generic;

namespace KeyFlip.Core
{
    /// The character a key position produces in one layout, or null for dead and control keys.
    public delegate string KeyCharacters(int scanCode, bool shifted);

    public static class KeyMap
    {
        /// Scan codes (set 1) of the printable main block, in the order tables are built:
        /// the four rows left to right, the ISO extra key, then Space. Keypad duplicates
        /// and control keys are left out.
        public static readonly int[] MainBlockScanCodes =
        {
            0x29, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D,
            0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x2B,
            0x1E, 0x1F, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28,
            0x2C, 0x2D, 0x2E, 0x2F, 0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x56,
            0x39
        };

        /// Builds a character table that moves text from one layout to the same key
        /// positions in another. The first key that produces a character wins.
        public static Dictionary<char, char> Build(KeyCharacters from, KeyCharacters to)
        {
            Dictionary<char, char> mapping = new Dictionary<char, char>();
            foreach (int scanCode in MainBlockScanCodes)
            {
                for (int shifted = 0; shifted < 2; shifted++)
                {
                    string source = from(scanCode, shifted == 1);
                    string target = to(scanCode, shifted == 1);
                    if (source == null || target == null || source.Length != 1 || target.Length != 1) continue;
                    if (!mapping.ContainsKey(source[0])) mapping[source[0]] = target[0];
                }
            }
            return mapping;
        }
    }
}
