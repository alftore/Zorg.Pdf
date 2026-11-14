using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Zorg.Pdf.Hyphenation;

/// <summary>
/// Hyphenates with Liang's patterns (TeX's), from the hyph-utf8 pattern files shipped unchanged in this package:
/// English (US and UK), German (1996 spelling, also for Austria and Switzerland), French, Norwegian (bokmål and
/// nynorsk) and Danish. A language's patterns are read on first use and kept for the process. Words in a language it
/// doesn't know are left to soft hyphens.
/// </summary>
/// <example>
/// <code>
/// var options = new RenderOptions { Fonts = fonts, Hyphenator = PatternHyphenator.Default };
/// // and in the document's styles: new TextStyle { Hyphens = Hyphens.Auto }, with the document's or the text's language
/// </code>
/// </example>
public sealed partial class PatternHyphenator : IHyphenator
{
    private readonly ConcurrentDictionary<string, Lazy<PatternSet>> _sets = new(StringComparer.Ordinal);

    private PatternHyphenator()
    {
    }

    /// <summary>The hyphenator for every language this package has patterns for.</summary>
    public static PatternHyphenator Default { get; } = new();

    /// <summary>The languages, as BCP 47 tags: en-US, en-GB, de, fr, nb, nn, da. Variants (de-CH, no, en-AU) map to the nearest.</summary>
    public static IReadOnlyList<string> Languages { get; } = ["en-US", "en-GB", "de", "fr", "nb", "nn", "da"];

    /// <inheritdoc/>
    public bool Supports(string language) => File(language) is not null;

    /// <inheritdoc/>
    public bool TryHyphenate(string language, ReadOnlySpan<char> word, Span<bool> breakAfter)
    {
        if (File(language) is not { } file)
        {
            return false;
        }

        if (breakAfter.Length < word.Length)
        {
            throw new ArgumentException("breakAfter must be as long as the word.", nameof(breakAfter));
        }

        _sets.GetOrAdd(file, f => new Lazy<PatternSet>(() => PatternSet.Load(f))).Value.Hyphenate(word, breakAfter);
        return true;
    }

    /// <summary>The pattern file for a language tag, or null.</summary>
    private static string? File(string language)
    {
        string tag = (language ?? "").Trim().ToUpperInvariant().Replace('_', '-');
        string primary = tag.Split('-')[0];
        return primary switch
        {
            "EN" when tag is "EN-GB" or "EN-UK" or "EN-IE" => "hyph-en-gb.tex",
            "EN" => "hyph-en-us.tex",
            "DE" or "GSW" => "hyph-de-1996.tex",
            "FR" => "hyph-fr.tex",
            "NB" or "NO" => "hyph-nb.tex",
            "NN" => "hyph-nn.tex",
            "DA" => "hyph-da.tex",
            _ => null,
        };
    }

    /// <summary>One language's patterns and exceptions, and the fewest letters before and after a hyphen.</summary>
    private sealed partial class PatternSet
    {
        private readonly Dictionary<string, byte[]> _patterns = new(StringComparer.Ordinal);
        private readonly Dictionary<string, bool[]> _exceptions = new(StringComparer.Ordinal);
        private int _longest;
        private int _left = 2;
        private int _right = 3;

        /// <summary>Reads a pattern file from the package, following its \input of another.</summary>
        public static PatternSet Load(string file)
        {
            var set = new PatternSet();
            set.Read(file, first: true, depth: 0);
            return set;
        }

        /// <summary>Marks where a word may break: odd weights between letters, at least the minimums from each end.</summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "TeX patterns are lower case; words are matched in lower case.")]
        public void Hyphenate(ReadOnlySpan<char> word, Span<bool> breakAfter)
        {
            string lower = word.ToString().ToLowerInvariant();
            if (_exceptions.TryGetValue(lower, out bool[]? listed))
            {
                for (int k = 0; k < lower.Length && k < listed.Length; k++)
                {
                    breakAfter[k] = listed[k] && k + 1 >= _left && lower.Length - (k + 1) >= _right;
                }

                return;
            }

            string dotted = "." + lower + ".";
            byte[] weights = new byte[dotted.Length + 1];
            Dictionary<string, byte[]>.AlternateLookup<ReadOnlySpan<char>> lookup = _patterns.GetAlternateLookup<ReadOnlySpan<char>>();
            for (int start = 0; start < dotted.Length; start++)
            {
                for (int length = 1; length <= _longest && start + length <= dotted.Length; length++)
                {
                    if (lookup.TryGetValue(dotted.AsSpan(start, length), out byte[]? pattern))
                    {
                        for (int k = 0; k < pattern.Length; k++)
                        {
                            weights[start + k] = Math.Max(weights[start + k], pattern[k]);
                        }
                    }
                }
            }

            // weights[i] sits before dotted[i]; between word letters k and k + 1 is before dotted[k + 2].
            for (int k = 0; k + 1 < lower.Length; k++)
            {
                breakAfter[k] = (weights[k + 2] & 1) == 1 && k + 1 >= _left && lower.Length - (k + 1) >= _right;
            }
        }

        private void Read(string file, bool first, int depth)
        {
            if (depth > 2)
            {
                throw new InvalidOperationException($"The pattern file {file} includes others too deeply.");
            }

            using Stream stream = typeof(PatternHyphenator).Assembly.GetManifestResourceStream(file)
                ?? throw new InvalidOperationException($"The pattern file {file} is missing from the package.");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string text = reader.ReadToEnd();
            if (first && Minimums().Match(text) is { Success: true } minimums)
            {
                _left = int.Parse(minimums.Groups[1].Value, CultureInfo.InvariantCulture);
                _right = int.Parse(minimums.Groups[2].Value, CultureInfo.InvariantCulture);
            }

            string code = Comment().Replace(text, "");
            foreach (Match include in Input().Matches(code))
            {
                Read(include.Groups[1].Value, first: false, depth + 1);
            }

            foreach (Match group in Group().Matches(code))
            {
                bool patterns = group.Groups[1].Value == "patterns";
                foreach (string token in group.Groups[2].Value.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                {
                    if (patterns)
                    {
                        AddPattern(token);
                    }
                    else
                    {
                        AddException(token);
                    }
                }
            }
        }

        /// <summary>"a1b2c" is the letters "abc" with weights 0, 1, 2, 0 before and between them.</summary>
        private void AddPattern(string token)
        {
            var letters = new StringBuilder(token.Length);
            var weights = new List<byte> { 0 };
            foreach (char c in token)
            {
                if (c is >= '0' and <= '9')
                {
                    weights[^1] = (byte)(c - '0');
                }
                else
                {
                    letters.Append(char.ToLowerInvariant(c));
                    weights.Add(0);
                }
            }

            string key = letters.ToString();
            _patterns[key] = [.. weights];
            _longest = Math.Max(_longest, key.Length);
        }

        /// <summary>"as-so-ciate": the word, with breaks after the letters before each hyphen.</summary>
        private void AddException(string token)
        {
            var word = new StringBuilder(token.Length);
            var breaks = new List<bool>();
            foreach (char c in token)
            {
                if (c == '-')
                {
                    if (breaks.Count > 0)
                    {
                        breaks[^1] = true;
                    }
                }
                else
                {
                    word.Append(char.ToLowerInvariant(c));
                    breaks.Add(false);
                }
            }

            _exceptions[word.ToString()] = [.. breaks];
        }

        [GeneratedRegex(@"hyphenmins:\s*%\s*(?:typesetting|generation):\s*%\s*left:\s*(\d+)\s*%\s*right:\s*(\d+)")]
        private static partial Regex Minimums();

        [GeneratedRegex(@"(?<!\\)%[^\n]*")]
        private static partial Regex Comment();

        [GeneratedRegex(@"\\input\s+(hyph-[a-z0-9-]+\.tex)")]
        private static partial Regex Input();

        [GeneratedRegex(@"\\(patterns|hyphenation)\s*\{([^}]*)\}")]
        private static partial Regex Group();
    }
}
