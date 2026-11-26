using System.Text;

namespace KeyFlip.Core
{
    /// The word typed since the last caret-moving input, plus the spaces typed after it.
    /// It is kept only in memory so the word can be retyped in editors that do not expose
    /// their text. Each character was inserted by one key press and is removed by one Backspace.
    public sealed class TypedWordBuffer
    {
        private readonly StringBuilder word = new StringBuilder();
        private int trailingSpaces;
        /// The current word grew past the limit; ignore it until the next space.
        private bool overflowed;

        public string Text { get { return word.ToString(); } }
        public int TrailingSpaces { get { return trailingSpaces; } }
        public string TrailingText { get { return new string(' ', trailingSpaces); } }
        /// Characters after the start of the word: the number of Backspaces that remove it.
        public int CharacterCount { get { return word.Length + trailingSpaces; } }
        public bool IsConvertible { get { return word.Length > 0; } }

        public void Reset()
        {
            word.Length = 0;
            trailingSpaces = 0;
            overflowed = false;
        }

        /// Records a key press that inserted `text`. Anything other than one character
        /// means the editor's contents can no longer be predicted.
        public void Insert(string text)
        {
            if (text == null || text.Length != 1 || char.IsSurrogate(text[0]))
            {
                Reset();
                return;
            }
            char character = text[0];
            if (character == ' ')
            {
                if (overflowed) { Reset(); return; }
                // A space with no tracked word before it says nothing about the editor.
                if (word.Length == 0) return;
                trailingSpaces++;
                if (trailingSpaces > TextRules.MaximumTrailingSpaces) Reset();
                return;
            }
            if (char.IsWhiteSpace(character))
            {
                Reset();
                return;
            }
            if (trailingSpaces > 0)
            {
                word.Length = 0;
                trailingSpaces = 0;
            }
            if (overflowed) return;
            word.Append(character);
            if (word.Length > TextRules.MaximumWordLength)
            {
                word.Length = 0;
                overflowed = true;
            }
        }

        public void DeleteBackward()
        {
            if (trailingSpaces > 0) trailingSpaces--;
            else if (word.Length > 0) word.Length--;
        }

        /// Records that the word has been retyped as `replacement`; trailing spaces are kept.
        public void ReplaceWord(string replacement)
        {
            word.Length = 0;
            word.Append(replacement ?? "");
            overflowed = false;
            if (word.Length == 0) Reset();
        }

        /// Whether an editor's text before the caret ends with exactly this word and its
        /// spaces, starting at a word boundary. `precedingText` should include at least
        /// one character before the word unless it starts at the beginning of the document.
        public bool Matches(string precedingText, bool startsDocument)
        {
            if (!IsConvertible || precedingText == null) return false;
            string typed = Text + TrailingText;
            if (precedingText.Length < typed.Length) return false;
            int offset = precedingText.Length - typed.Length;
            for (int i = 0; i < typed.Length; i++)
            {
                char editor = precedingText[offset + i];
                // HTML editors store a trailing typed space as a no-break space.
                if (editor != typed[i] && !(typed[i] == ' ' && editor == '\x00A0')) return false;
            }
            if (offset == 0) return startsDocument;
            char boundary = precedingText[offset - 1];
            // A surrogate half is part of a non-whitespace character.
            return !char.IsSurrogate(boundary) && char.IsWhiteSpace(boundary);
        }

        public TypedWordBuffer Clone()
        {
            TypedWordBuffer copy = new TypedWordBuffer();
            copy.word.Append(word.ToString());
            copy.trailingSpaces = trailingSpaces;
            copy.overflowed = overflowed;
            return copy;
        }

        public override bool Equals(object obj)
        {
            TypedWordBuffer other = obj as TypedWordBuffer;
            return other != null && other.Text == Text && other.trailingSpaces == trailingSpaces
                && other.overflowed == overflowed;
        }

        public override int GetHashCode()
        {
            return Text.GetHashCode() ^ trailingSpaces;
        }

        public override string ToString()
        {
            return "[" + Text + "]+" + trailingSpaces + (overflowed ? " overflowed" : "");
        }
    }

    public static class CopiedSelection
    {
        /// Code editors such as VS Code, JetBrains IDEs, and Sublime Text copy the whole
        /// current line when nothing is selected. That must not be pasted back as a
        /// "converted selection", so a single line ending in a line break is rejected.
        public static bool IsLikelyWholeLineCopy(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            int end = text.Length - 1;
            if (!TextRules.IsNewline(text[end])) return false;
            if (text[end] == '\n' && end > 0 && text[end - 1] == '\r') end--;
            for (int i = 0; i < end; i++)
            {
                if (TextRules.IsNewline(text[i])) return false;
            }
            return true;
        }
    }
}
