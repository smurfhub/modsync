using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ModSync
{
    // Translations: <directory>\<Language>.json, a flat JSON object that maps each key to its
    // text. Directories loaded later extend or override earlier ones; English is read first in
    // each directory and defines the keys and {n} placeholders every other language must keep.
    internal static partial class Loc
    {
        private static readonly Regex Placeholder = new Regex(@"\{(\d+)", RegexOptions.Compiled);

        // Returns the number of entries loaded; problems are reported through onWarning.
        internal static int LoadDirectory(string directory, Action<string> onWarning = null)
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return 0;
            }

            int loaded = 0;
            string[] files = Directory.GetFiles(directory, "*.json");
            Array.Sort(files, (left, right) =>
            {
                bool leftEnglish = string.Equals(Path.GetFileNameWithoutExtension(left),
                    FallbackLanguage, StringComparison.OrdinalIgnoreCase);
                bool rightEnglish = string.Equals(Path.GetFileNameWithoutExtension(right),
                    FallbackLanguage, StringComparison.OrdinalIgnoreCase);
                return leftEnglish != rightEnglish
                    ? (leftEnglish ? -1 : 1)
                    : string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
            });

            foreach (string file in files)
            {
                string language = Path.GetFileNameWithoutExtension(file);
                bool isEnglish = string.Equals(language, FallbackLanguage, StringComparison.OrdinalIgnoreCase);
                if (isEnglish)
                {
                    language = FallbackLanguage;
                }

                try
                {
                    Dictionary<string, string> entries = ParseFlatJson(File.ReadAllText(file, Encoding.UTF8));
                    if (!Tables.TryGetValue(language, out Dictionary<string, string> table))
                    {
                        table = new Dictionary<string, string>(StringComparer.Ordinal);
                        Tables[language] = table;
                    }

                    foreach (KeyValuePair<string, string> entry in entries)
                    {
                        if (string.IsNullOrEmpty(entry.Value))
                        {
                            continue;
                        }

                        string english = isEnglish ? null : Lookup(FallbackLanguage, entry.Key);
                        if (!isEnglish && english == null)
                        {
                            onWarning?.Invoke($"{language}.json: unknown key '{entry.Key}'; entry ignored.");
                            continue;
                        }

                        if (!isEnglish && !SamePlaceholders(english, entry.Value))
                        {
                            onWarning?.Invoke($"{language}.json: the placeholders of '{entry.Key}' do not match; entry ignored.");
                            continue;
                        }

                        table[entry.Key] = entry.Value;
                        loaded++;
                    }
                }
                catch (Exception exception) when (exception is IOException ||
                                                  exception is UnauthorizedAccessException ||
                                                  exception is FormatException)
                {
                    onWarning?.Invoke($"Could not read {Path.GetFileName(file)}: {exception.Message}");
                }
            }

            return loaded;
        }

        private static bool SamePlaceholders(string english, string translated)
        {
            return Indexes(english).SetEquals(Indexes(translated));
        }

        private static HashSet<string> Indexes(string text)
        {
            return new HashSet<string>(Placeholder.Matches(text).Cast<Match>().Select(m => m.Groups[1].Value));
        }

        // Minimal parser for {"key": "value", ...}; throws FormatException when malformed.
        internal static Dictionary<string, string> ParseFlatJson(string json)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.Ordinal);
            int position = 0;
            SkipWhitespace(json, ref position);
            Expect(json, ref position, '{');
            SkipWhitespace(json, ref position);
            if (Peek(json, position) == '}')
            {
                position++;
            }
            else
            {
                while (true)
                {
                    SkipWhitespace(json, ref position);
                    string key = ReadString(json, ref position);
                    SkipWhitespace(json, ref position);
                    Expect(json, ref position, ':');
                    SkipWhitespace(json, ref position);
                    result[key] = ReadString(json, ref position);
                    SkipWhitespace(json, ref position);
                    char next = Peek(json, position++);
                    if (next == '}')
                    {
                        break;
                    }

                    if (next != ',')
                    {
                        throw new FormatException("Expected ',' or '}'.");
                    }
                }
            }

            SkipWhitespace(json, ref position);
            if (position != json.Length)
            {
                throw new FormatException("Unexpected content after the JSON object.");
            }

            return result;
        }

        private static char Peek(string json, int position)
        {
            return position < json.Length ? json[position] : '\0';
        }

        private static void SkipWhitespace(string json, ref int position)
        {
            if (position == 0 && json.Length > 0 && json[0] == '\uFEFF')
            {
                position = 1;
            }

            while (position < json.Length && char.IsWhiteSpace(json[position]))
            {
                position++;
            }
        }

        private static void Expect(string json, ref int position, char expected)
        {
            if (Peek(json, position) != expected)
            {
                throw new FormatException($"Expected '{expected}' at position {position}.");
            }

            position++;
        }

        private static string ReadString(string json, ref int position)
        {
            Expect(json, ref position, '"');
            StringBuilder builder = new StringBuilder();
            while (true)
            {
                if (position >= json.Length)
                {
                    throw new FormatException("Unterminated string.");
                }

                char c = json[position++];
                if (c == '"')
                {
                    return builder.ToString();
                }

                if (c != '\\')
                {
                    builder.Append(c);
                    continue;
                }

                char escape = Peek(json, position++);
                switch (escape)
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'u':
                        if (position + 4 > json.Length ||
                            !int.TryParse(json.Substring(position, 4),
                                System.Globalization.NumberStyles.HexNumber,
                                System.Globalization.CultureInfo.InvariantCulture, out int code))
                        {
                            throw new FormatException("Invalid \\u escape.");
                        }

                        builder.Append((char)code);
                        position += 4;
                        break;
                    default:
                        throw new FormatException("Invalid escape sequence.");
                }
            }
        }
    }
}
