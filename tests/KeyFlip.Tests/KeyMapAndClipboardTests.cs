using System;
using System.Collections.Generic;
using KeyFlip.Core;

namespace KeyFlip.Tests
{
    /// The standard U.S. and Russian layouts, by scan code, for tests that must not
    /// depend on what is installed.
    internal static class ReferenceLayouts
    {
        private static readonly int[] Rows =
        {
            0x29, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D,
            0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x2B,
            0x1E, 0x1F, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28,
            0x2C, 0x2D, 0x2E, 0x2F, 0x30, 0x31, 0x32, 0x33, 0x34, 0x35,
            0x56, 0x39
        };

        private const string UsPlain = "`1234567890-=" + "qwertyuiop[]\\" + "asdfghjkl;'" + "zxcvbnm,./" + "\\ ";
        private const string UsShift = "~!@#$%^&*()_+" + "QWERTYUIOP{}|" + "ASDFGHJKL:\"" + "ZXCVBNM<>?" + "| ";
        private const string RuPlain = "ё1234567890-=" + "йцукенгшщзхъ\\" + "фывапролджэ" + "ячсмитьбю." + "\\ ";
        private const string RuShift = "Ё!\"№;%:?*()_+" + "ЙЦУКЕНГШЩЗХЪ/" + "ФЫВАПРОЛДЖЭ" + "ЯЧСМИТЬБЮ," + "/ ";

        private static string Lookup(string plain, string shift, int scanCode, bool shifted)
        {
            int index = Array.IndexOf(Rows, scanCode);
            return index < 0 ? null : (shifted ? shift : plain)[index].ToString();
        }

        public static string Us(int scanCode, bool shifted) { return Lookup(UsPlain, UsShift, scanCode, shifted); }
        public static string Ru(int scanCode, bool shifted) { return Lookup(RuPlain, RuShift, scanCode, shifted); }
    }

    internal static class KeyMapTests
    {
        public static void Add(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("Layout table converts both directions", BothDirections));
            scenarios.Add(new Scenario("Layout table punctuation follows key positions", PunctuationFollowsKeys));
            scenarios.Add(new Scenario("Layout table skips dead and missing keys", DeadAndMissingKeys));
            scenarios.Add(new Scenario("Layout table covers every main-block key once", MainBlockIsComplete));
        }

        private static void BothDirections()
        {
            Dictionary<char, char> toRussian = KeyMap.Build(ReferenceLayouts.Us, ReferenceLayouts.Ru);
            Dictionary<char, char> toEnglish = KeyMap.Build(ReferenceLayouts.Ru, ReferenceLayouts.Us);
            Expect.Equal(Transforms.Convert("ghbdtn", toRussian), "привет");
            Expect.Equal(Transforms.Convert("привет", toEnglish), "ghbdtn");
            Expect.Equal(Transforms.Convert("GhBdTn", toRussian), "ПрИвЕт");
            Expect.Equal(Transforms.Convert("ПрИвЕт", toEnglish), "GhBdTn");
            Expect.Equal(Transforms.Convert("ghbdtn\nGhbdtn \xD83D\xDC4B", toRussian), "привет\nПривет \xD83D\xDC4B");
            const string alphabet = "абвгдеёжзийклмнопрстуфхцчшщъыьэюя";
            Expect.Equal(Transforms.Convert(Transforms.Convert(alphabet, toEnglish), toRussian), alphabet);
            Expect.Equal(Transforms.Convert(alphabet, toEnglish), "f,dult`;pbqrkvyjghcnea[wxio]sm'.z");
        }

        private static void PunctuationFollowsKeys()
        {
            Dictionary<char, char> toRussian = KeyMap.Build(ReferenceLayouts.Us, ReferenceLayouts.Ru);
            Dictionary<char, char> toEnglish = KeyMap.Build(ReferenceLayouts.Ru, ReferenceLayouts.Us);
            Expect.Equal(Transforms.Convert("ghbdtn? rfr ltkf&", toRussian), "привет, как дела?");
            Expect.Equal(Transforms.Convert("привет, как дела?", toEnglish), "ghbdtn? rfr ltkf&");
            Expect.Equal(Transforms.Convert("@#$^|/", toRussian), "\"№;:/.");
            Expect.Equal(Transforms.Convert("1 2-3=4", toRussian), "1 2-3=4");
            // Both backslash keys agree, so neither direction is ambiguous.
            Expect.Equal(Transforms.Convert("\\", toRussian), "\\");
            Expect.Equal(Transforms.Convert("/", toEnglish), "|");
        }

        private static void DeadAndMissingKeys()
        {
            KeyCharacters sparse = delegate(int scanCode, bool shifted)
            {
                if (scanCode == 0x10) return shifted ? null : "q"; // Shifted: a dead key.
                if (scanCode == 0x11) return "ab";                 // A ligature: not one character.
                if (scanCode == 0x12) return "q";                  // A duplicate; the first key wins.
                return null;
            };
            KeyCharacters target = delegate(int scanCode, bool shifted) { return scanCode == 0x12 ? "x" : "й"; };
            Dictionary<char, char> mapping = KeyMap.Build(sparse, target);
            Expect.Equal(mapping.Count, 1);
            Expect.Equal(mapping['q'], 'й');
        }

        private static void MainBlockIsComplete()
        {
            HashSet<int> seen = new HashSet<int>();
            foreach (int scanCode in KeyMap.MainBlockScanCodes) Expect.True(seen.Add(scanCode), "duplicate scan code " + scanCode);
            Expect.Equal(seen.Count, 49);
            foreach (int scanCode in KeyMap.MainBlockScanCodes)
            {
                Expect.True(ReferenceLayouts.Us(scanCode, false) != null, "no reference character for scan code " + scanCode);
            }
        }
    }

    /// An in-memory clipboard that numbers its changes the way Windows does: more
    /// than one step per write.
    internal sealed class FakeClipboard : IClipboard
    {
        private sealed class Snapshot : ClipboardSnapshot
        {
            public Dictionary<string, byte[]> Formats;
        }

        public Dictionary<string, byte[]> Formats = new Dictionary<string, byte[]>();
        public uint Sequence = 100;
        public bool CaptureRendersData;
        public bool FailNextWrite;
        public bool FailRestore;
        public bool FailRestoreHalfway;
        public bool Unreadable;
        public int Restores;

        public uint SequenceNumber { get { return Sequence; } }

        public void UserCopies(string text)
        {
            Formats = new Dictionary<string, byte[]> { { "text", System.Text.Encoding.UTF8.GetBytes(text) } };
            Sequence += 5;
        }

        public ClipboardSnapshot Capture()
        {
            if (Unreadable) throw new AppFailure("The clipboard cannot be preserved right now. Try copying ordinary text first.");
            if (CaptureRendersData) Sequence += 2;
            Snapshot snapshot = new Snapshot();
            snapshot.Formats = new Dictionary<string, byte[]>(Formats);
            snapshot.SequenceNumber = Sequence;
            return snapshot;
        }

        public uint SetTemporaryText(string text)
        {
            if (FailNextWrite)
            {
                FailNextWrite = false;
                Formats = new Dictionary<string, byte[]>(); // Emptied, then the write failed.
                Sequence += 1;
                throw new InvalidOperationException("write failed");
            }
            UserCopies(text);
            return Sequence;
        }

        public uint Restore(ClipboardSnapshot snapshot)
        {
            if (FailRestore) throw new InvalidOperationException("busy");
            if (FailRestoreHalfway)
            {
                FailRestoreHalfway = false;
                Formats = new Dictionary<string, byte[]>(); // Emptied, then a write failed.
                Sequence += 1;
                throw new PartialClipboardWrite(Sequence);
            }
            Formats = new Dictionary<string, byte[]>(((Snapshot)snapshot).Formats);
            Sequence += 3;
            Restores++;
            return Sequence;
        }

        public string GetText()
        {
            byte[] bytes;
            return Formats.TryGetValue("text", out bytes) ? System.Text.Encoding.UTF8.GetString(bytes) : null;
        }
    }

    internal static class ClipboardLeaseTests
    {
        public static void Add(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("Clipboard staged and restored with every format", StageAndRestore));
            scenarios.Add(new Scenario("Clipboard restore is idempotent", RestoreTwice));
            scenarios.Add(new Scenario("Newer clipboard copy is not overwritten", NewerCopyProtected));
            scenarios.Add(new Scenario("Empty clipboard restored as empty", EmptyClipboard));
            scenarios.Add(new Scenario("Clipboard saved before a copy, staged after it", CopyThenStage));
            scenarios.Add(new Scenario("Copy raced by a newer copy", RacedCopy));
            scenarios.Add(new Scenario("Saving that renders data does not look like a change", CaptureAdvancesSequence));
            scenarios.Add(new Scenario("Failed staging restores an emptied clipboard", FailedStage));
            scenarios.Add(new Scenario("Failed restore can be retried", FailedRestore));
            scenarios.Add(new Scenario("A restore that failed halfway can be finished", HalfFailedRestore));
            scenarios.Add(new Scenario("Unreadable clipboard stops before any change", UnreadableClipboard));
            scenarios.Add(new Scenario("Disposing a lease restores the clipboard", DisposeRestores));
        }

        private static FakeClipboard WithOriginal()
        {
            FakeClipboard clipboard = new FakeClipboard();
            clipboard.UserCopies("original привет");
            clipboard.Formats["binary"] = new byte[] { 0, 1, 2, 255 };
            return clipboard;
        }

        private static void HalfFailedRestore()
        {
            FakeClipboard clipboard = WithOriginal();
            ClipboardLease lease = new ClipboardLease(clipboard, "temporary");
            clipboard.FailRestoreHalfway = true;
            Expect.Equal(lease.Restore(), false);
            Expect.True(clipboard.GetText() == null, "the failed restore left text behind");
            Expect.Equal(lease.HoldsTemporaryContents, true);
            Expect.Equal(lease.Restore(), true);
            Expect.Equal(clipboard.GetText(), "original привет");
            Expect.Equal(clipboard.Formats.Count, 2);
            Expect.Equal(lease.HoldsTemporaryContents, false);
        }

        private static void StageAndRestore()
        {
            FakeClipboard clipboard = WithOriginal();
            ClipboardLease lease = new ClipboardLease(clipboard, "temporary");
            Expect.Equal(clipboard.GetText(), "temporary");
            Expect.Equal(lease.HoldsTemporaryContents, true);
            Expect.Equal(lease.Restore(), true);
            Expect.Equal(clipboard.GetText(), "original привет");
            Expect.Equal(clipboard.Formats.Count, 2);
            Expect.Equal(BitConverter.ToString(clipboard.Formats["binary"]), "00-01-02-FF");
            Expect.Equal(lease.HoldsTemporaryContents, false);
        }

        private static void RestoreTwice()
        {
            FakeClipboard clipboard = WithOriginal();
            ClipboardLease lease = new ClipboardLease(clipboard, "temporary");
            lease.Restore();
            uint restored = clipboard.Sequence;
            lease.Restore();
            Expect.Equal(clipboard.Sequence, restored);
            Expect.Equal(clipboard.Restores, 1);
        }

        private static void NewerCopyProtected()
        {
            FakeClipboard clipboard = WithOriginal();
            ClipboardLease lease = new ClipboardLease(clipboard, "temporary again");
            clipboard.UserCopies("new user copy");
            lease.Restore();
            Expect.Equal(clipboard.GetText(), "new user copy");
            Expect.Equal(clipboard.Restores, 0);
        }

        private static void EmptyClipboard()
        {
            FakeClipboard clipboard = new FakeClipboard();
            ClipboardLease lease = new ClipboardLease(clipboard, "temporary");
            lease.Restore();
            Expect.Equal(clipboard.Formats.Count, 0);
            Expect.Equal(clipboard.GetText(), null);
        }

        private static void CopyThenStage()
        {
            FakeClipboard clipboard = new FakeClipboard();
            clipboard.UserCopies("before copy");
            ClipboardLease lease = new ClipboardLease(clipboard);
            Expect.Equal(clipboard.GetText(), "before copy");
            Expect.Equal(lease.ChangedSinceSaved, false);
            Expect.Equal(lease.HoldsTemporaryContents, false);
            lease.Restore(); // Nothing is owned yet: must not touch the clipboard.
            Expect.Equal(clipboard.Restores, 0);
            clipboard.UserCopies("selected text"); // The application answers the synthetic copy.
            Expect.Equal(lease.ChangedSinceSaved, true);
            lease.Claim();
            lease.Stage("converted text");
            Expect.Equal(clipboard.GetText(), "converted text");
            lease.Restore();
            Expect.Equal(clipboard.GetText(), "before copy");
        }

        private static void RacedCopy()
        {
            FakeClipboard clipboard = new FakeClipboard();
            clipboard.UserCopies("before copy");
            ClipboardLease lease = new ClipboardLease(clipboard);
            clipboard.UserCopies("selected text");
            lease.Claim();
            clipboard.UserCopies("new user copy");
            Expect.Throws(ClipboardLease.ChangedMessage, delegate { lease.Stage("converted text"); });
            lease.Restore();
            Expect.Equal(clipboard.GetText(), "new user copy");
            // Staging without a claim is refused as well once the clipboard has moved on.
            ClipboardLease unclaimed = new ClipboardLease(clipboard);
            clipboard.UserCopies("another copy");
            Expect.Throws(ClipboardLease.ChangedMessage, delegate { unclaimed.Stage("converted text"); });
            Expect.Equal(clipboard.GetText(), "another copy");
        }

        private static void CaptureAdvancesSequence()
        {
            FakeClipboard clipboard = WithOriginal();
            clipboard.CaptureRendersData = true;
            ClipboardLease lease = new ClipboardLease(clipboard, "temporary");
            Expect.Equal(clipboard.GetText(), "temporary");
            lease.Restore();
            Expect.Equal(clipboard.GetText(), "original привет");
        }

        private static void FailedStage()
        {
            FakeClipboard clipboard = WithOriginal();
            ClipboardLease lease = new ClipboardLease(clipboard);
            clipboard.FailNextWrite = true;
            Expect.Throws("Could not prepare the text for insertion.", delegate { lease.Stage("temporary"); });
            Expect.Equal(clipboard.GetText(), "original привет");
            Expect.Equal(lease.HoldsTemporaryContents, false);
        }

        private static void FailedRestore()
        {
            FakeClipboard clipboard = WithOriginal();
            ClipboardLease lease = new ClipboardLease(clipboard, "temporary");
            clipboard.FailRestore = true;
            Expect.Equal(lease.Restore(), false);
            Expect.Equal(lease.HoldsTemporaryContents, true);
            clipboard.FailRestore = false;
            Expect.Equal(lease.Restore(), true);
            Expect.Equal(clipboard.GetText(), "original привет");
        }

        private static void UnreadableClipboard()
        {
            FakeClipboard clipboard = WithOriginal();
            clipboard.Unreadable = true;
            uint before = clipboard.Sequence;
            Expect.Throws("The clipboard cannot be preserved right now. Try copying ordinary text first.",
                          delegate { new ClipboardLease(clipboard, "temporary"); });
            Expect.Equal(clipboard.Sequence, before);
            Expect.Equal(clipboard.GetText(), "original привет");
        }

        private static void DisposeRestores()
        {
            FakeClipboard clipboard = new FakeClipboard();
            clipboard.UserCopies("restore on release");
            using (new ClipboardLease(clipboard, "temporary"))
            {
                Expect.Equal(clipboard.GetText(), "temporary");
            }
            Expect.Equal(clipboard.GetText(), "restore on release");
        }
    }
}
