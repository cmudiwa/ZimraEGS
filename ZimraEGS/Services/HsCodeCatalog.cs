using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace ZimraEGS.Services
{
    public record HsCode(string Code, string Description, string Heading, string Chapter);

    /// <summary>
    /// The 8-digit codes of ZIMRA's customs tariff (S.I. 203 of 2022) and ZIMRA's general codes for services,
    /// for finding and checking the HS code of an item. Built by tools/hs-codes.
    /// </summary>
    public class HsCodeCatalog
    {
        private static readonly HashSet<string> StopWords =
        [
            "a", "an", "and", "or", "of", "the", "for", "with", "without", "in", "on", "to", "by", "not", "other", "others",
            "kg", "g", "ml", "l", "pcs", "pc", "each", "x", "per", "new", "item", "items", "pack", "packet", "box",
        ];

        private readonly List<(HsCode code, string text, string headingText, string chapterText)> _entries = [];
        private readonly Dictionary<string, HsCode> _byCode = [];
        private readonly Dictionary<string, string> _headings = [];

        public string Source { get; }

        public HsCodeCatalog()
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ZimraEGS.hs-codes.json")
                ?? throw new InvalidOperationException("The HS code list is missing from the build.");
            using var reader = new StreamReader(stream);
            var data = JObject.Parse(reader.ReadToEnd());

            Source = (string?)data["source"] ?? string.Empty;
            foreach (var p in ((JObject)data["headings"]!).Properties()) _headings[p.Name] = (string?)p.Value ?? string.Empty;
            var chapters = ((JObject)data["chapters"]!).Properties().ToDictionary(p => p.Name, p => (string?)p.Value ?? string.Empty);

            foreach (var row in (JArray)data["codes"]!)
            {
                string code = (string)row[0]!, description = (string)row[1]!;
                var entry = new HsCode(code, description, _headings.GetValueOrDefault(code[..4], string.Empty), chapters.GetValueOrDefault(code[..2], string.Empty));
                _byCode[code] = entry;
                _entries.Add((entry, Normalise(description), Normalise(entry.Heading), Normalise(entry.Chapter)));
            }
        }

        public int Count => _byCode.Count;

        private static string Normalise(string text) => " " + Regex.Replace(text.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim() + " ";

        /// <summary>Plain search words: lower case, without stop words, and without a plural ending.</summary>
        private static List<string> Words(string query)
        {
            return Regex.Split(query.ToLowerInvariant(), @"[^a-z0-9]+")
                .Where(w => w.Length >= 2 && !StopWords.Contains(w) && !w.All(char.IsDigit))
                .Select(w => w.Length > 4 && w.EndsWith("es") ? w[..^2] : w.Length > 3 && w.EndsWith('s') ? w[..^1] : w)
                .Distinct()
                .ToList();
        }

        public HsCode? Find(string code) => _byCode.GetValueOrDefault(code);

        public string? HeadingTitle(string fourDigits) => _headings.GetValueOrDefault(fourDigits);

        /// <summary>
        /// Searches by code (all digits: codes starting with them) or by words (codes whose description or heading
        /// contains them; codes matching every word first, then the best partial matches).
        /// </summary>
        public List<HsCode> Search(string query, int limit = 30)
        {
            query = (query ?? string.Empty).Trim();
            limit = Math.Clamp(limit, 1, 100);
            if (query.Length == 0) return [];

            string digits = Regex.Replace(query, @"[\s.]", "");
            if (digits.All(char.IsDigit))
            {
                return _entries.Where(e => e.code.Code.StartsWith(digits, StringComparison.Ordinal)).Take(limit).Select(e => e.code).ToList();
            }

            var words = Words(query);
            if (words.Count == 0) return [];
            string phrase = Normalise(query).Trim();

            var scored = new List<(HsCode code, int matched, int score)>();
            foreach (var (code, text, headingText, chapterText) in _entries)
            {
                int matched = 0, score = 0;
                foreach (var w in words)
                {
                    // Match at the start of a word, so "tyre" finds "tyres" but "oil" does not find "foil".
                    bool inDescription = text.Contains(" " + w, StringComparison.Ordinal);
                    bool inHeading = headingText.Contains(" " + w, StringComparison.Ordinal);
                    bool inChapter = chapterText.Contains(" " + w, StringComparison.Ordinal);
                    if (inDescription || inHeading) matched++;
                    // The heading names the kind of goods; a word that only appears in a line is often incidental
                    // ("cranes on tyres"), so the heading weighs most.
                    if (inHeading) score += headingText.Contains(" " + w + " ", StringComparison.Ordinal) ? 6 : 5;
                    if (inDescription) score += text.Contains(" " + w + " ", StringComparison.Ordinal) ? 4 : 3;
                    if (inChapter) score += 2;
                }
                if (matched == 0) continue;
                if (phrase.Length > 3 && text.Contains(" " + phrase, StringComparison.Ordinal)) score += 5;
                scored.Add((code, matched, score));
            }

            return scored
                .OrderByDescending(s => s.matched)
                .ThenByDescending(s => s.score)
                .ThenBy(s => s.code.Description.Length)
                .ThenBy(s => s.code.Code, StringComparer.Ordinal)
                .Take(limit)
                .Select(s => s.code)
                .ToList();
        }
    }
}
