using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace ModSync
{
    // Texts live in external lang\<Language>.json files named like Valheim's own languages
    // ("English", "Spanish"...) and are looked up by identifier. English is the fallback.
    // Messages sent over the network are encoded as key + arguments so each
    // client renders them in its own language.
    internal static partial class Loc
    {
        private const char Separator = '\u001F';
        private const string FallbackLanguage = "English";
        private static readonly Dictionary<string, Dictionary<string, string>> Tables =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        private static Func<string> _languageProvider = ReadValheimLanguage;
        private static Type _localizationType;
        private static bool _localizationSearched;

        internal static bool HasLanguage(string language)
        {
            return Tables.ContainsKey(language);
        }

        internal static void SetLanguageProvider(Func<string> provider)
        {
            _languageProvider = provider ?? ReadValheimLanguage;
        }

        internal static string T(string key, params object[] args)
        {
            return Render(key, args, CurrentLanguage());
        }

        internal static string Encode(string key, params object[] args)
        {
            if (args == null || args.Length == 0)
            {
                return key;
            }

            string[] parts = new string[args.Length + 1];
            parts[0] = key;
            for (int index = 0; index < args.Length; index++)
            {
                parts[index + 1] = Convert.ToString(args[index], CultureInfo.InvariantCulture)
                    ?.Replace(Separator, ' ');
            }

            return string.Join(Separator.ToString(), parts);
        }

        internal static string Decode(string encoded, string language = null)
        {
            if (encoded == null)
            {
                return null;
            }

            string[] parts = encoded.Split(Separator);
            object[] args = new object[parts.Length - 1];
            for (int index = 1; index < parts.Length; index++)
            {
                args[index - 1] = parts[index];
            }

            return Render(parts[0], args, language ?? CurrentLanguage());
        }

        internal static string Render(string key, object[] args, string language)
        {
            string english = Lookup(FallbackLanguage, key) ?? key;
            string template = Lookup(language, key) ?? english;
            if (args == null || args.Length == 0)
            {
                return template;
            }

            try
            {
                return string.Format(CultureInfo.InvariantCulture, template, args);
            }
            catch (FormatException)
            {
                try
                {
                    return string.Format(CultureInfo.InvariantCulture, english, args);
                }
                catch (FormatException)
                {
                    return key;
                }
            }
        }

        private static string Lookup(string language, string key)
        {
            return language != null &&
                   Tables.TryGetValue(language, out Dictionary<string, string> table) &&
                   table.TryGetValue(key, out string text)
                ? text
                : null;
        }

        private static string CurrentLanguage()
        {
            try
            {
                return _languageProvider();
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Valheim exposes the selected language as its English name ("Spanish").
        private static string ReadValheimLanguage()
        {
            if (!_localizationSearched)
            {
                _localizationSearched = true;
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    _localizationType = assembly.GetType("Localization", false);
                    if (_localizationType != null &&
                        _localizationType.GetMethod("GetSelectedLanguage") != null)
                    {
                        break;
                    }

                    _localizationType = null;
                }
            }

            if (_localizationType == null)
            {
                return null;
            }

            object instance = (_localizationType.GetField("instance",
                                   BindingFlags.Public | BindingFlags.Static)?.GetValue(null)) ??
                              _localizationType.GetProperty("instance",
                                  BindingFlags.Public | BindingFlags.Static)?.GetValue(null, null);
            return instance == null
                ? null
                : _localizationType.GetMethod("GetSelectedLanguage")
                    .Invoke(instance, null) as string;
        }
    }
}
