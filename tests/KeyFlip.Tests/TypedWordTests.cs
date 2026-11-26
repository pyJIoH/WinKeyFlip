using System.Collections.Generic;
using KeyFlip.Core;

namespace KeyFlip.Tests
{
    internal static class TypedWordTests
    {
        public static void Add(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("Typed word and trailing spaces", WordAndTrailingSpaces));
            scenarios.Add(new Scenario("Typed word Backspace handling", BackspaceRemovesSpacesThenLetters));
            scenarios.Add(new Scenario("Typed word whitespace boundaries", LeadingSpacesAndOtherWhitespace));
            scenarios.Add(new Scenario("Typed word trailing space limit", TrailingSpaceLimit));
            scenarios.Add(new Scenario("Typed word length limit", OverflowWaitsForNextWord));
            scenarios.Add(new Scenario("Retyped word keeps its spaces", ReplacementKeepsSpaces));
            scenarios.Add(new Scenario("Typed word matches editor text at a boundary", EditorTextMatching));
            scenarios.Add(new Scenario("Typed word snapshot is independent", SnapshotIsIndependent));
            scenarios.Add(new Scenario("Whole-line copy without a selection", WholeLineCopyDetection));
        }

        private static TypedWordBuffer Typed(string text)
        {
            TypedWordBuffer buffer = new TypedWordBuffer();
            foreach (char character in text) buffer.Insert(character.ToString());
            return buffer;
        }

        private static void WordAndTrailingSpaces()
        {
            TypedWordBuffer buffer = Typed("ghbdtn");
            Expect.Equal(buffer.Text, "ghbdtn");
            Expect.Equal(buffer.CharacterCount, 6);
            Expect.Equal(buffer.IsConvertible, true);
            buffer.Insert(" ");
            buffer.Insert(" ");
            Expect.Equal(buffer.Text, "ghbdtn");
            Expect.Equal(buffer.TrailingText, "  ");
            Expect.Equal(buffer.CharacterCount, 8);
            buffer.Insert("x");
            Expect.Equal(buffer.Text, "x");
            Expect.Equal(buffer.TrailingSpaces, 0);
        }

        private static void BackspaceRemovesSpacesThenLetters()
        {
            TypedWordBuffer buffer = Typed("abc  ");
            buffer.DeleteBackward();
            Expect.Equal(buffer.CharacterCount, 4);
            buffer.DeleteBackward();
            buffer.DeleteBackward();
            Expect.Equal(buffer.Text, "ab");
            Expect.Equal(buffer.TrailingSpaces, 0);
            buffer.DeleteBackward();
            buffer.DeleteBackward();
            Expect.Equal(buffer.IsConvertible, false);
            buffer.DeleteBackward(); // Deleting text from before the tracked word.
            Expect.Equal(buffer, new TypedWordBuffer());
            buffer.Insert("q");
            Expect.Equal(buffer.Text, "q");
        }

        private static void LeadingSpacesAndOtherWhitespace()
        {
            Expect.Equal(Typed("   "), new TypedWordBuffer());
            Expect.Equal(Typed(" a").Text, "a");
            foreach (string separator in new[] { "\t", "\n", "\x00A0" })
            {
                TypedWordBuffer word = Typed("word");
                word.Insert(separator);
                Expect.Equal(word, new TypedWordBuffer());
            }
            foreach (string invalid in new[] { "ab", "", null, "\uD83D" })
            {
                TypedWordBuffer word = Typed("word");
                word.Insert(invalid);
                Expect.Equal(word, new TypedWordBuffer());
            }
        }

        private static void TrailingSpaceLimit()
        {
            Expect.Equal(Typed("word" + new string(' ', 8)).TrailingSpaces, 8);
            Expect.Equal(Typed("word" + new string(' ', 9)), new TypedWordBuffer());
        }

        private static void OverflowWaitsForNextWord()
        {
            TypedWordBuffer buffer = Typed(new string('a', 256));
            Expect.Equal(buffer.CharacterCount, 256);
            buffer.Insert("a");
            Expect.Equal(buffer.IsConvertible, false);
            buffer.Insert("b"); // Still the clipped tail of the long word.
            buffer.DeleteBackward();
            Expect.Equal(buffer.IsConvertible, false);
            buffer.Insert(" ");
            buffer.Insert("c");
            Expect.Equal(buffer.Text, "c");
        }

        private static void ReplacementKeepsSpaces()
        {
            TypedWordBuffer buffer = Typed("gh ");
            buffer.ReplaceWord("пр");
            Expect.Equal(buffer.Text, "пр");
            Expect.Equal(buffer.TrailingText, " ");
            Expect.Equal(buffer.CharacterCount, 3);
            buffer.ReplaceWord("SSx"); // A replacement may change the character count.
            Expect.Equal(buffer.CharacterCount, 4);
            buffer.ReplaceWord("");
            Expect.Equal(buffer, new TypedWordBuffer());
        }

        private static void EditorTextMatching()
        {
            TypedWordBuffer buffer = Typed("ghbdtn ");
            Expect.Equal(buffer.Matches("first ghbdtn ", false), true);
            Expect.Equal(buffer.Matches("\nghbdtn ", false), true);
            Expect.Equal(buffer.Matches("ghbdtn ", true), true);
            Expect.Equal(buffer.Matches("ghbdtn ", false), false);
            Expect.Equal(buffer.Matches("first ghbdtn\x00A0", false), true);
            // Typed into the end of an existing word: the editor shows the whole word.
            Expect.Equal(buffer.Matches("xghbdtn ", false), false);
            Expect.Equal(buffer.Matches("\xD83D\xDE00ghbdtn ", false), false);
            Expect.Equal(buffer.Matches("first ghbdtn", false), false);
            Expect.Equal(buffer.Matches("first ghbdtx ", false), false);
            Expect.Equal(buffer.Matches("", true), false);
            Expect.Equal(buffer.Matches(null, true), false);
            Expect.Equal(new TypedWordBuffer().Matches("", true), false);
        }

        private static void SnapshotIsIndependent()
        {
            TypedWordBuffer buffer = Typed("ghbdtn ");
            TypedWordBuffer snapshot = buffer.Clone();
            buffer.Reset();
            Expect.Equal(snapshot.Text, "ghbdtn");
            Expect.Equal(snapshot.TrailingSpaces, 1);
            Expect.Equal(buffer, new TypedWordBuffer());
        }

        private static void WholeLineCopyDetection()
        {
            Expect.Equal(CopiedSelection.IsLikelyWholeLineCopy("let x = 1\n"), true);
            Expect.Equal(CopiedSelection.IsLikelyWholeLineCopy("let x = 1\r\n"), true);
            Expect.Equal(CopiedSelection.IsLikelyWholeLineCopy("\n"), true);
            Expect.Equal(CopiedSelection.IsLikelyWholeLineCopy("\r\n"), true);
            Expect.Equal(CopiedSelection.IsLikelyWholeLineCopy("ghbdtn"), false);
            Expect.Equal(CopiedSelection.IsLikelyWholeLineCopy("one\ntwo\n"), false);
            Expect.Equal(CopiedSelection.IsLikelyWholeLineCopy("one\r\ntwo\r\n"), false);
            Expect.Equal(CopiedSelection.IsLikelyWholeLineCopy("one\ntwo"), false);
            Expect.Equal(CopiedSelection.IsLikelyWholeLineCopy(""), false);
        }
    }
}
