using System.Collections.Generic;

namespace MediaInfoKeeper.Patch.MediaInfo.Bluray
{
    internal static class BlurayLanguageCodes
    {
        private static readonly Dictionary<string, string> CodeToName =
            new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
            {
                ["chi"] = "Chinese",
                ["zho"] = "Chinese",
                ["eng"] = "English",
                ["jpn"] = "Japanese",
                ["kor"] = "Korean",
                ["fra"] = "French",
                ["fre"] = "French",
                ["deu"] = "German",
                ["ger"] = "German",
                ["spa"] = "Spanish",
                ["ita"] = "Italian",
                ["rus"] = "Russian",
                ["por"] = "Portuguese",
                ["ara"] = "Arabic",
                ["tha"] = "Thai"
            };

        public static string GetName(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return null;
            }

            return CodeToName.TryGetValue(code, out var name) ? name : code;
        }
    }
}
