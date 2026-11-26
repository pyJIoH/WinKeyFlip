using System;

namespace KeyFlip.Core
{
    /// A range in UTF-16 units.
    public struct TextRange : IEquatable<TextRange>
    {
        public readonly int Location;
        public readonly int Length;

        public TextRange(int location, int length)
        {
            Location = location;
            Length = length;
        }

        public bool Equals(TextRange other) { return Location == other.Location && Length == other.Length; }
        public override bool Equals(object obj) { return obj is TextRange && Equals((TextRange)obj); }
        public override int GetHashCode() { return unchecked(Location * 397 ^ Length); }
        public override string ToString() { return "{" + Location + ", " + Length + "}"; }
    }

    public static class TextRules
    {
        public const int MaximumWordLength = 256;
        public const int MaximumTrailingSpaces = 8;
        public const int MaximumSelectionLength = 100000;

        public static bool IsNewline(char c)
        {
            return c == '\n' || c == '\r' || c == '\v' || c == '\f' || c == '\u0085' || c == '\x2028' || c == '\x2029';
        }
    }

    /// Finds a complete word in a bounded slice ending at the insertion point.
    public static class WordAtCaret
    {
        /// `documentOffset` is the position of the slice in the document. A non-zero
        /// offset means text precedes the slice, so a word touching its start may be clipped.
        public static TextRange? Range(string precedingText, int documentOffset = 0)
        {
            if (documentOffset < 0 || precedingText == null) return null;
            int end = precedingText.Length;
            int spaces = 0;
            while (end > 0)
            {
                char character = precedingText[end - 1];
                if (!char.IsWhiteSpace(character)) break;
                if (TextRules.IsNewline(character) || character == '\t') return null;
                spaces++;
                if (spaces > TextRules.MaximumTrailingSpaces) return null;
                end--;
            }
            if (end == 0) return null;
            int start = end;
            while (start > 0 && !char.IsWhiteSpace(precedingText[start - 1])) start--;
            // Do not return a clipped tail of a word that started before the slice.
            if (start == 0 && documentOffset != 0) return null;
            int length = end - start;
            if (length <= 0 || length > TextRules.MaximumWordLength
                || documentOffset > int.MaxValue - start - length)
            {
                return null;
            }
            return new TextRange(documentOffset + start, length);
        }
    }
}
