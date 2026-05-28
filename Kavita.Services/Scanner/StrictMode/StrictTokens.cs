using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Kavita.Models.Parser;

namespace Kavita.Services.Scanner.StrictMode;

/// <summary>
/// Pure helpers for the Strict-mode token grammar.
/// A token is <c>{key-value}</c> where key matches <c>[A-Za-z][A-Za-z0-9]*</c> and value is any
/// run of characters that does not contain '{' or '}'.
/// </summary>
internal static class StrictTokens
{
    private static readonly Regex TokenRegex = new(
        @"\{(?<key>[A-Za-z][A-Za-z0-9]*)-(?<value>[^{}]+)\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TrailingYearRegex = new(
        @"\s*\((?<year>\d{4})\)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Extracts every <c>{key-value}</c> token from <paramref name="input"/> and returns the
    /// remainder (input with tokens removed and trimmed) plus the parsed token list.
    /// </summary>
    public static (string Remainder, IReadOnlyList<(string Key, string Value)> Tokens) Extract(string input)
    {
        var tokens = new List<(string Key, string Value)>();
        var matches = TokenRegex.Matches(input);
        foreach (Match m in matches)
        {
            tokens.Add((m.Groups["key"].Value, m.Groups["value"].Value));
        }

        var remainder = tokens.Count == 0
            ? input
            : TokenRegex.Replace(input, string.Empty);

        return (remainder.Trim(), tokens);
    }

    /// <summary>
    /// Returns the last positive <c>{hardcoverId-…}</c> value in <paramref name="tokens"/>
    /// (later tokens win, matching <see cref="ApplyToParserInfo"/>).
    /// </summary>
    public static bool TryGetHardcoverId(IEnumerable<(string Key, string Value)> tokens, out int hardcoverId)
    {
        hardcoverId = 0;
        foreach (var (key, value) in tokens)
        {
            if (key.Equals("hardcoverId", System.StringComparison.OrdinalIgnoreCase)
                && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                && parsed > 0)
            {
                hardcoverId = parsed;
            }
        }
        return hardcoverId > 0;
    }

    /// <summary>
    /// Peels a trailing <c>(YYYY)</c> off a name. Returns (remainder, year). year is 0 if absent.
    /// </summary>
    public static (string Remainder, int Year) PeelTrailingYear(string input)
    {
        var match = TrailingYearRegex.Match(input);
        if (!match.Success)
        {
            return (input, 0);
        }

        var year = int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture);
        var remainder = input[..match.Index].TrimEnd();
        return (remainder, year);
    }

    /// <summary>
    /// Applies known external-id token keys onto <paramref name="info"/>. Unknown keys are
    /// silently dropped. Token keys are case-insensitive.
    /// </summary>
    public static void ApplyToParserInfo(ParserInfo info, IEnumerable<(string Key, string Value)> tokens)
    {
        foreach (var (key, value) in tokens)
        {
            switch (key.ToLowerInvariant())
            {
                case "anilistid":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var anilist))
                    {
                        info.AniListId = anilist;
                    }
                    break;
                case "malid":
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mal))
                    {
                        info.MalId = mal;
                    }
                    break;
                // `hardcoverId` is a Hardcover *series* id. Since Kavita v0.9.1, ParserInfo.HardcoverId
                // (and the Chapter.HardcoverId it feeds) is a Hardcover *book* id used for scrobbling,
                // so the token is never stored here — StrictMetadataApplier reads it via TryGetHardcoverId.
                case "metronid":
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var metron))
                    {
                        info.MetronId = metron;
                    }
                    break;
                case "comicvineid":
                    info.ComicVineId = value;
                    break;
                case "comicvineseriesid":
                    info.ComicVineSeriesId = value;
                    break;
                case "mangabakaid":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mb))
                    {
                        info.MangaBakaId = mb;
                    }
                    break;
                // `language` and `publicationStatus` tokens are folder-only and applied directly
                // in StrictMetadataApplier — they're intentionally not stored on ParserInfo to
                // keep the upstream-touched ParserInfo surface minimal.
            }
        }
    }
}
