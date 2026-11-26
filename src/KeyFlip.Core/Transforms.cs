using System.Collections.Generic;
using System.Text;

namespace KeyFlip.Core
{
    public static class Transforms
    {
        /// Uppercase forms that are longer than one character. .NET's simple case
        /// mapping leaves these unchanged.
        private static readonly Dictionary<string, string> ExpandingUppercase = new Dictionary<string, string>
        {
            { "ß", "SS" },
            { "\x0149", "\x02BCN" },    // N preceded by an apostrophe
            { "\xFB00", "FF" },         // The Latin ligatures ff, fi, fl, ffi, ffl, long st and st

            { "\xFB01", "FI" },
            { "\xFB02", "FL" },
            { "\xFB03", "FFI" },
            { "\xFB04", "FFL" },
            { "\xFB05", "ST" },
            { "\xFB06", "ST" }
        };

        private static string Uppercase(string character)
        {
            string expanded;
            return ExpandingUppercase.TryGetValue(character, out expanded) ? expanded : character.ToUpperInvariant();
        }

        /// Swaps the case of every cased code point. A character counts as uppercase
        /// when lowercasing changes it and uppercasing does not, and vice versa, so
        /// titlecase and uncased characters are left alone.
        public static string InvertCase(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            StringBuilder result = new StringBuilder(text.Length + 8);
            int index = 0;
            while (index < text.Length)
            {
                int length = char.IsHighSurrogate(text[index]) && index + 1 < text.Length
                    && char.IsLowSurrogate(text[index + 1]) ? 2 : 1;
                string character = text.Substring(index, length);
                string upper = Uppercase(character);
                string lower = character.ToLowerInvariant();
                if (lower != character && upper == character) result.Append(lower);
                else if (upper != character && lower == character) result.Append(upper);
                else result.Append(character);
                index += length;
            }
            return result.ToString();
        }

        public static Alphabet AlphabetOf(string text)
        {
            bool latin = false;
            bool cyrillic = false;
            foreach (char c in text ?? "")
            {
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')) latin = true;
                else if (c >= 'Ѐ' && c <= 'ӿ') cyrillic = true;
            }
            if (latin && cyrillic) return Alphabet.Mixed;
            if (latin) return Alphabet.English;
            if (cyrillic) return Alphabet.Russian;
            return Alphabet.Neither;
        }

        /// Maps each character through `mapping`; everything else is preserved.
        public static string Convert(string text, IDictionary<char, char> mapping)
        {
            if (string.IsNullOrEmpty(text)) return "";
            StringBuilder result = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                char mapped;
                result.Append(mapping.TryGetValue(c, out mapped) ? mapped : c);
            }
            return result.ToString();
        }
    }
}
