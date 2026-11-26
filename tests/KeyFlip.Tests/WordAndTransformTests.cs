using System.Collections.Generic;
using KeyFlip.Core;

namespace KeyFlip.Tests
{
    internal static class WordAtCaretTests
    {
        public static void Add(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("Word and punctuation at caret", TrailingWord));
            scenarios.Add(new Scenario("Russian word at caret", RussianWord));
            scenarios.Add(new Scenario("UTF-16 offsets after emoji", Utf16EmojiPrefix));
            scenarios.Add(new Scenario("Joined emoji in word", JoinedEmojiInWord));
            scenarios.Add(new Scenario("Empty fields", EmptyAndWhitespace));
            scenarios.Add(new Scenario("Newline and tab boundaries", NewlineAndTabDoNotReachPreviousWord));
            scenarios.Add(new Scenario("Bounded context offset", BoundedWindowOffset));
            scenarios.Add(new Scenario("Truncated word rejected", TruncatedWordRejected));
            scenarios.Add(new Scenario("Word length limit", OversizedWordRejected));
            scenarios.Add(new Scenario("Trailing space limit", TooManyTrailingSpacesRejected));
            scenarios.Add(new Scenario("Negative offset", NegativeOffsetRejected));
            scenarios.Add(new Scenario("Combining characters", CombiningCharacters));
        }

        private const string Technologist = "\xD83D\xDC69\x200D\xD83D\xDCBB"; // woman technologist, joined

        private static void TrailingWord()
        {
            Expect.Equal(WordAtCaret.Range("hello ghbdtn! "), new TextRange(6, 7));
        }

        private static void RussianWord()
        {
            Expect.Equal(WordAtCaret.Range("привет мир"), new TextRange(7, 3));
        }

        private static void Utf16EmojiPrefix()
        {
            Expect.Equal(WordAtCaret.Range(Technologist + " привет"), new TextRange(6, 6));
        }

        private static void JoinedEmojiInWord()
        {
            Expect.Equal(WordAtCaret.Range("hi" + Technologist + " "), new TextRange(0, 7));
        }

        private static void EmptyAndWhitespace()
        {
            Expect.Null(WordAtCaret.Range(""));
            Expect.Null(WordAtCaret.Range("   "));
            Expect.Null(WordAtCaret.Range(null));
        }

        private static void NewlineAndTabDoNotReachPreviousWord()
        {
            Expect.Null(WordAtCaret.Range("hello\n"));
            Expect.Null(WordAtCaret.Range("hello\t"));
            Expect.Equal(WordAtCaret.Range("hello\nworld"), new TextRange(6, 5));
        }

        private static void BoundedWindowOffset()
        {
            Expect.Equal(WordAtCaret.Range("earlier hello", 100), new TextRange(108, 5));
        }

        private static void TruncatedWordRejected()
        {
            Expect.Null(WordAtCaret.Range("partialword", 100));
        }

        private static void OversizedWordRejected()
        {
            Expect.Null(WordAtCaret.Range(new string('x', 257)));
            Expect.Equal(WordAtCaret.Range(new string('x', 256)), new TextRange(0, 256));
        }

        private static void TooManyTrailingSpacesRejected()
        {
            Expect.Null(WordAtCaret.Range("word         "));
            Expect.Equal(WordAtCaret.Range("word        "), new TextRange(0, 4));
        }

        private static void NegativeOffsetRejected()
        {
            Expect.Null(WordAtCaret.Range("hello", -1));
        }

        private static void CombiningCharacters()
        {
            Expect.Equal(WordAtCaret.Range("e\x0301 "), new TextRange(0, 2));
        }
    }

    internal static class TransformTests
    {
        public static void Add(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("English and Russian case inversion", CaseInversionInBothLanguages));
            scenarios.Add(new Scenario("Preserve symbols and emoji", SymbolsAndEmojiArePreserved));
            scenarios.Add(new Scenario("Unicode length changes", CaseCanChangeUnicodeLength));
            scenarios.Add(new Scenario("Alphabet detection", AlphabetDetection));
            scenarios.Add(new Scenario("Physical mapping and unmapped characters", MappingPreservesUnmappedCharactersAndWhitespace));
        }

        private static void CaseInversionInBothLanguages()
        {
            Expect.Equal(Transforms.InvertCase("Hello ПРИВЕТ"), "hELLO привет");
            Expect.Equal(Transforms.InvertCase("Ёё ABC xyz"), "ёЁ abc XYZ");
        }

        private static void SymbolsAndEmojiArePreserved()
        {
            string text = "123 !? \xD83D\xDC69\x200D\xD83D\xDCBB";
            Expect.Equal(Transforms.InvertCase(text), text);
            Expect.Equal(Transforms.InvertCase("\uD83D"), "\uD83D"); // An unpaired surrogate.
        }

        private static void CaseCanChangeUnicodeLength()
        {
            Expect.Equal(Transforms.InvertCase("straße"), "STRASSE");
            Expect.Equal(Transforms.InvertCase("\xFB01sh"), "FISH");
        }

        private static void AlphabetDetection()
        {
            Expect.Equal(Transforms.AlphabetOf("ghbdtn!"), Alphabet.English);
            Expect.Equal(Transforms.AlphabetOf("Привет, ёж!"), Alphabet.Russian);
            Expect.Equal(Transforms.AlphabetOf("Hello мир"), Alphabet.Mixed);
            Expect.Equal(Transforms.AlphabetOf("123!?"), Alphabet.Neither);
        }

        private static void MappingPreservesUnmappedCharactersAndWhitespace()
        {
            Dictionary<char, char> mapping = new Dictionary<char, char>
            {
                { 'g', 'п' }, { 'h', 'р' }, { 'b', 'и' }, { 'd', 'в' }, { 't', 'е' }, { 'n', 'т' }
            };
            Expect.Equal(Transforms.Convert("ghbdtn \xD83D\xDC4B\n123", mapping), "привет \xD83D\xDC4B\n123");
        }
    }

    internal static class BoundaryTests
    {
        public static void Add(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("Tap duration boundaries and backwards clock", TapDurationBoundaries));
            scenarios.Add(new Scenario("Wrong modifier release and recovery", WrongReleaseAndRecovery));
            scenarios.Add(new Scenario("Duplicate modifier press cancels tap", DuplicatePressCancelsTap));
            scenarios.Add(new Scenario("Reset after a cancelled gesture", ResetAfterCancelledGesture));
            scenarios.Add(new Scenario("Document range overflow", DocumentRangeOverflow));
            scenarios.Add(new Scenario("Word limit counts UTF-16 units", WordLimitUsesUtf16));
            scenarios.Add(new Scenario("Unicode whitespace boundaries", UnicodeWhitespaceBoundaries));
            scenarios.Add(new Scenario("Complete English and Russian case round trip", WholeAlphabetsCaseRoundTrip));
            scenarios.Add(new Scenario("Multiline and empty transformations", MultilineAndEmptyTransforms));
        }

        private static void TapDurationBoundaries()
        {
            double[] elapsed = { 1, 1.001, -0.1 };
            KeyAction?[] expected = { KeyAction.Layout, null, null };
            for (int i = 0; i < elapsed.Length; i++)
            {
                TapDetector detector = new TapDetector();
                detector.ModifiersChanged(VirtualKeys.LeftAlt, Modifiers.Alt, 10);
                Expect.Equal(detector.ModifiersChanged(VirtualKeys.LeftAlt, Modifiers.None, 10 + elapsed[i]), expected[i]);
            }
        }

        private static void WrongReleaseAndRecovery()
        {
            TapDetector detector = new TapDetector();
            detector.ModifiersChanged(VirtualKeys.LeftAlt, Modifiers.Alt, 1);
            Expect.Null(detector.ModifiersChanged(VirtualKeys.RightAlt, Modifiers.None, 1.1));
            detector.ModifiersChanged(VirtualKeys.LeftControl, Modifiers.Control, 2);
            Expect.Equal(detector.ModifiersChanged(VirtualKeys.LeftControl, Modifiers.None, 2.1), KeyAction.InvertCase);
        }

        private static void DuplicatePressCancelsTap()
        {
            TapDetector detector = new TapDetector();
            detector.ModifiersChanged(VirtualKeys.LeftAlt, Modifiers.Alt, 1);
            Expect.Null(detector.ModifiersChanged(VirtualKeys.LeftAlt, Modifiers.Alt, 1.1));
            Expect.Null(detector.ModifiersChanged(VirtualKeys.LeftAlt, Modifiers.None, 1.2));
            detector.ModifiersChanged(VirtualKeys.LeftAlt, Modifiers.Alt, 2);
            Expect.Equal(detector.ModifiersChanged(VirtualKeys.LeftAlt, Modifiers.None, 2.1), KeyAction.Layout);
        }

        private static void ResetAfterCancelledGesture()
        {
            TapDetector detector = new TapDetector();
            detector.ModifiersChanged(VirtualKeys.LeftAlt, Modifiers.Alt, 1);
            detector.Cancel();
            detector.Cancel();
            detector.Reset(); // Focus change or hook restart without a key-up.
            detector.ModifiersChanged(VirtualKeys.LeftControl, Modifiers.Control, 2);
            Expect.Equal(detector.ModifiersChanged(VirtualKeys.LeftControl, Modifiers.None, 2.1), KeyAction.InvertCase);
        }

        private static void DocumentRangeOverflow()
        {
            Expect.Null(WordAtCaret.Range(" x", int.MaxValue));
            Expect.Null(WordAtCaret.Range(" x", int.MaxValue - 1));
            Expect.Equal(WordAtCaret.Range(" x", int.MaxValue - 2), new TextRange(int.MaxValue - 1, 1));
        }

        private static string Repeat(string text, int count)
        {
            System.Text.StringBuilder result = new System.Text.StringBuilder();
            for (int i = 0; i < count; i++) result.Append(text);
            return result.ToString();
        }

        private static void WordLimitUsesUtf16()
        {
            Expect.Equal(WordAtCaret.Range(Repeat("\xD83D\xDE00", 128)), new TextRange(0, 256));
            Expect.Null(WordAtCaret.Range(Repeat("\xD83D\xDE00", 129)));
        }

        private static void UnicodeWhitespaceBoundaries()
        {
            Expect.Equal(WordAtCaret.Range("first\x00A0word\x00A0"), new TextRange(6, 4));
            Expect.Null(WordAtCaret.Range("word\r\n  "));
            Expect.Null(WordAtCaret.Range("word\x2028"));
            Expect.Equal(WordAtCaret.Range("first\r\nword"), new TextRange(7, 4));
        }

        private static void WholeAlphabetsCaseRoundTrip()
        {
            const string lower = "abcdefghijklmnopqrstuvwxyz абвгдеёжзийклмнопрстуфхцчшщъыьэюя";
            const string upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ АБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯ";
            Expect.Equal(Transforms.InvertCase(lower), upper);
            Expect.Equal(Transforms.InvertCase(upper), lower);
            Expect.Equal(Transforms.InvertCase(Transforms.InvertCase(lower + upper)), lower + upper);
        }

        private static void MultilineAndEmptyTransforms()
        {
            Expect.Equal(Transforms.InvertCase("Hello\nМИР\t123"), "hELLO\nмир\t123");
            Expect.Equal(Transforms.InvertCase(""), "");
            Expect.Equal(Transforms.InvertCase(null), "");
            Expect.Equal(Transforms.AlphabetOf(""), Alphabet.Neither);
            Expect.Equal(Transforms.Convert("", new Dictionary<char, char> { { 'a', 'ф' } }), "");
            string text = "a\nя \xD83D\xDC69\x200D\xD83D\xDCBB";
            Expect.Equal(Transforms.Convert(text, new Dictionary<char, char>()), text);
        }
    }
}
