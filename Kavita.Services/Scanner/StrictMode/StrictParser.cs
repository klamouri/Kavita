using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Kavita.API.Services;
using Kavita.Models.Entities.Enums;
using Kavita.Models.Metadata;
using Kavita.Models.Parser;

namespace Kavita.Services.Scanner.StrictMode;

/// <summary>
/// Fork-only parser invoked when a library has <c>ParserStrictnessLevel &gt;= 1</c>.
/// Level 0 libraries never reach this code path — the upstream parser cascade in
/// <see cref="Reading.ReadingItemService"/> handles them unchanged.
///
/// <para><b>Phase 0 encapsulation audit (do not rely on this for runtime, only for future
/// rebases):</b></para>
/// <list type="bullet">
///   <item><see cref="Reading.ReadingItemService"/> news up every parser inline; dispatch lives
///   in its private Parse method. The fork adds one early branch there, gated on
///   <c>parserStrictnessLevel &gt;= 1</c> plumbed through
///   <see cref="IReadingItemService.ParseFile"/>.</item>
///   <item>Series identity flows through <see cref="ParserInfo.Series"/>; nothing downstream
///   re-derives it, so writing the series-folder name into <see cref="ParserInfo.Series"/> is
///   sufficient to fix the "two editions get merged" / "one series gets split" failure modes.</item>
///   <item>Per-library settings already flow via <c>ParseScannedFiles -&gt; ParseFile</c>
///   (template: <c>library.EnableMetadata</c>); the new int follows the same path.</item>
/// </list>
///
/// <para><b>Two strictness levels:</b></para>
/// <list type="number">
///   <item><b>Level 1 — Strict.</b> The kind token (<c>V/T &lt;n&gt;</c>, <c>C/CH &lt;n&gt;</c>,
///   <c>SP&lt;n&gt;</c>) may appear anywhere in the filename as a whitespace-separated word
///   ("word-scan"). Files with no kind token at all are kept as loose-leaf (or special if
///   under <c>Specials/</c>) so they remain visible.</item>
///   <item><b>Level 2 — Stricter.</b> The kind token must be the first word of some
///   <c>" - "</c>-separated segment of the filename. Files that satisfy this are
///   <see cref="ParserOutcome.Accepted"/>; everything else is rejected — no loose-leaf
///   fallback, no Specials/ inference.</item>
/// </list>
///
/// <para>At level 1, the parser <b>also</b> computes the level-2 outcome so it can label each
/// accepted file as either <see cref="ParserOutcome.Accepted"/> (would also pass at level 2) or
/// <see cref="ParserOutcome.Lenient"/> (accepted here, would fail level 2). Hard rejections
/// (file outside any series folder, empty series name after token peel, etc.) are
/// <see cref="ParserOutcome.Rejected"/> at any level.</para>
///
/// <para>See fork-docs/parser-strictness.md for the user-facing contract.</para>
/// </summary>
public class StrictParser(IDirectoryService directoryService, IDefaultParser imageParser)
    : DefaultParser(directoryService)
{
    // Matches a bare kind+number word (no surrounding noise). Groups: vol/volNum, ch/chNum, spNum.
    // Kind prefixes (case-insensitive):
    //   - Volume: V, T, Volume, Tome
    //   - Chapter: C, CH, Chapter
    //   - Special: SP
    // The long forms (Volume/Tome/Chapter) match either spaceless ("Volume1") or — via
    // SpacedKindRegex below — spaced ("Volume 1", collapsed to "Volume1" before this regex runs).
    private static readonly Regex KindTokenRegex = new(
        @"^(?:(?<vol>V(?:olume)?|T(?:ome)?)(?<volNum>\d+(?:\.\d+)?)|(?<ch>CH(?:apter)?|C)(?<chNum>\d+(?:\.\d+)?)|SP(?<spNum>\d+(?:\.\d+)?))$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // Collapses "Volume 1" → "Volume1", "Tome 03" → "Tome03", "Chapter 12.5" → "Chapter12.5".
    // Run before tokenization so the spaced long forms are picked up by KindTokenRegex.
    private static readonly Regex SpacedKindRegex = new(
        @"\b(Volume|Tome|Chapter)\s+(\d+(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private const string SpecialsFolderName = "Specials";

    /// <summary>
    /// Strict mode applies to whichever library has the level set &gt;= 1. The dispatcher in
    /// <see cref="Reading.ReadingItemService"/> uses the library setting rather than file content
    /// to decide, so <see cref="IsApplicable"/> just returns true.
    /// </summary>
    public override bool IsApplicable(string filePath, LibraryType type) => true;

    /// <summary>
    /// Convenience entry point matching <see cref="IDefaultParser"/>. Defaults to level 1 so
    /// existing tests that call <c>Parse(...)</c> without a level keep their original semantics.
    /// </summary>
    public override ParserInfo? Parse(string filePath, string rootPath, string libraryRoot,
        LibraryType type, bool enableMetadata = true, ComicInfo? comicInfo = null)
        => ParseDetailed(filePath, rootPath, libraryRoot, type, enableMetadata, comicInfo, level: 1).Info;

    /// <summary>
    /// Detailed entry point used by the dispatcher. Returns both the parsed info (or null on
    /// reject) and the per-entry <see cref="ParserOutcome"/> + human-readable reason that goes
    /// into the Parser Logs page.
    /// </summary>
    /// <param name="level">1 = Strict, 2 = Stricter. Other values are treated as 1.</param>
    public StrictParseResult ParseDetailed(string filePath, string rootPath, string libraryRoot,
        LibraryType type, bool enableMetadata = true, ComicInfo? comicInfo = null, int level = 1)
    {
        var fileName = directoryService.FileSystem.Path.GetFileNameWithoutExtension(filePath);
        if (type != LibraryType.Image && Parser.IsCoverImage(directoryService.FileSystem.Path.GetFileName(filePath)))
        {
            return StrictParseResult.Reject("cover image — skipped");
        }

        if (Parser.IsImage(filePath))
        {
            var imageInfo = imageParser.Parse(filePath, rootPath, libraryRoot, LibraryType.Image, enableMetadata, comicInfo);
            return imageInfo == null
                ? StrictParseResult.Reject("image parser returned null")
                : StrictParseResult.Accept(imageInfo, ParserOutcome.Accepted, "delegated to image parser");
        }

        var normalizedLibraryRoot = Parser.NormalizePath(libraryRoot);
        var normalizedFile = Parser.NormalizePath(filePath);

        var seriesFolderName = ResolveSeriesFolderName(normalizedLibraryRoot, normalizedFile);
        if (string.IsNullOrEmpty(seriesFolderName))
        {
            return StrictParseResult.Reject("file is not inside an immediate child folder of the library root");
        }

        var (folderRemainderAfterTokens, folderTokens) = StrictTokens.Extract(seriesFolderName);
        var (seriesName, seriesYear) = StrictTokens.PeelTrailingYear(folderRemainderAfterTokens);
        if (string.IsNullOrEmpty(seriesName))
        {
            return StrictParseResult.Reject($"series folder '{seriesFolderName}' has no name after peeling tokens/year");
        }

        var underSpecials = IsUnderSpecialsFolder(normalizedLibraryRoot, seriesFolderName, normalizedFile);
        var classification = ClassifyFileWithLevel(fileName, underSpecials, level);

        if (classification.Outcome == ParserOutcome.Rejected)
        {
            return StrictParseResult.Reject(classification.Reason);
        }

        var (_, fileTokens) = StrictTokens.Extract(fileName);

        var ret = new ParserInfo
        {
            Filename = Path.GetFileName(filePath),
            Format = Parser.ParseFormat(filePath),
            Title = Parser.RemoveExtensionIfSupported(fileName)!,
            FullFilePath = Parser.NormalizePath(filePath),
            Series = seriesName,
            ComicInfo = comicInfo,
            Volumes = classification.Volumes,
            Chapters = classification.Chapters,
            IsSpecial = classification.IsSpecial,
            SeriesReleaseYear = seriesYear,
        };

        // Order matters: ComicInfo IDs first (parity with BasicParser), then folder/file tokens
        // overwrite. In Strict mode the filename / folder name is the source of truth.
        ParseExternalIdsFromNotesAndWeblinks(ret);
        StrictTokens.ApplyToParserInfo(ret, folderTokens);
        StrictTokens.ApplyToParserInfo(ret, fileTokens);

        if (ret.IsSpecial)
        {
            ret.Volumes = Parser.SpecialVolume;
            if (string.IsNullOrEmpty(ret.Chapters) || Parser.IsDefaultChapter(ret.Chapters))
            {
                ret.Chapters = Parser.DefaultChapter;
            }
        }

        if (enableMetadata)
        {
            UpdateFromComicInfo(ret);
            // Folder + filename are the source of truth in Strict mode. UpdateFromComicInfo
            // may have just overwritten Series / Volumes / Chapters / IsSpecial from the
            // embedded ComicInfo.xml — restore them to what our classification produced.
            // Inherited ComicInfo fields like SeriesSort, LocalizedSeries, AgeRating, Summary,
            // Genres etc. are still useful metadata enrichment and remain whatever
            // UpdateFromComicInfo set them to.
            ret.Series = seriesName;
            ret.IsSpecial = classification.IsSpecial;
            ret.Volumes = classification.IsSpecial ? Parser.SpecialVolume : classification.Volumes;
            ret.Chapters = classification.IsSpecial
                ? Parser.DefaultChapter
                : classification.Chapters;
        }

        FinalizeNumbers(ret);

        var allTokens = folderTokens.Concat(fileTokens);
        var tokenSummary = string.Join(",", allTokens.Select(t => $"{t.Key}={t.Value}"));
        return StrictParseResult.Accept(ret, classification.Outcome, classification.Reason, tokenSummary);
    }

    /// <summary>
    /// Returns the name of the immediate child folder of <paramref name="libraryRoot"/> that
    /// contains <paramref name="filePath"/>, or empty string if the file is loose at the
    /// library root.
    /// </summary>
    private static string ResolveSeriesFolderName(string libraryRoot, string filePath)
    {
        if (!filePath.StartsWith(libraryRoot, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var relative = filePath[libraryRoot.Length..].TrimStart('/');
        var sepIdx = relative.IndexOf('/');
        return sepIdx <= 0 ? string.Empty : relative[..sepIdx];
    }

    private static bool IsUnderSpecialsFolder(string libraryRoot, string seriesFolderName, string filePath)
    {
        // libraryRoot may already end with '/' (test fixtures use "Manga/"). TrimEnd to avoid
        // the resulting prefix containing "//" — StartsWith against the (single-slash)
        // file path would otherwise miss.
        var root = libraryRoot.TrimEnd('/');
        var prefix = $"{root}/{seriesFolderName}/{SpecialsFolderName}/";
        return filePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Tries to classify a filename against both level-2 (kind token at start of a "<c> - </c>"
    /// segment) and, if that fails AND level=1, level-1 fallbacks (word-scan / Specials/ /
    /// loose-leaf). Returns the resulting volumes / chapters / IsSpecial along with the
    /// <see cref="ParserOutcome"/> that should be recorded for this file.
    /// </summary>
    private static FileClassification ClassifyFileWithLevel(string fileNameNoExt, bool isUnderSpecials, int level)
    {
        // Strip {key-value} tokens first so their internal text can't masquerade as a kind token.
        var (cleaned, _) = StrictTokens.Extract(fileNameNoExt);
        // Collapse "Volume 1" / "Tome 03" / "Chapter 12.5" into single tokens. Done once
        // here so both the level-2 segment check and the level-1 word-scan see the same
        // input. Spaceless forms ("Volume1") aren't affected and match KindTokenRegex
        // on their own.
        cleaned = SpacedKindRegex.Replace(cleaned, "$1$2");

        // === Level-2 attempt: first word of some " - "-separated segment must be a kind token. ===
        foreach (var segment in cleaned.Split(" - "))
        {
            var firstWord = segment.TrimStart().Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.IsNullOrEmpty(firstWord)) continue;

            var match = KindTokenRegex.Match(firstWord);
            if (!match.Success) continue;

            // Accepted at level 2 (and therefore at level 1 too).
            return BuildFromMatch(match, ParserOutcome.Accepted, ReasonFor(match));
        }

        // === Level-2 has nothing more to try — at level 2, this is a reject. ===
        if (level >= 2)
        {
            return new FileClassification(Parser.LooseLeafVolume, Parser.DefaultChapter, false,
                ParserOutcome.Rejected,
                "level 2: no \" - \"-separated segment whose first word is a kind token");
        }

        // === Level-1 fallback A: kind token anywhere in the filename (word-scan). ===
        // Treat '.' as a separator too, so scene-style dotted names like
        // "Driftgrade.T01.FRENCH.CBZ.eBook-Paprika+" still tokenize to a list containing
        // "T01". Level 2 deliberately doesn't get this treatment — those filenames have
        // no " - " segment to anchor against, which is exactly what level 2 rejects.
        foreach (var word in cleaned.Split(new[] { ' ', '\t', '.' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var match = KindTokenRegex.Match(word);
            if (!match.Success) continue;

            return BuildFromMatch(match, ParserOutcome.Lenient,
                $"{ReasonFor(match)} — kind token outside a \" - \" segment; level 2 would reject");
        }

        // === Level-1 fallback B: under Specials/ subfolder ⇒ Special. ===
        if (isUnderSpecials)
        {
            return new FileClassification(Parser.SpecialVolume, Parser.DefaultChapter, true,
                ParserOutcome.Lenient,
                "under Specials/ subfolder (no kind token in filename); level 2 would reject");
        }

        // === Level-1 fallback C: loose-leaf placement so the file stays visible. ===
        return new FileClassification(Parser.LooseLeafVolume, Parser.DefaultChapter, false,
            ParserOutcome.Lenient,
            "no kind token found — loose-leaf placement; level 2 would reject");
    }

    private static FileClassification BuildFromMatch(Match match, ParserOutcome outcome, string reason)
    {
        // Volume / chapter numbers go through RemoveLeadingZeroes to match upstream's
        // canonical form ("V01" → "1", not "01"). Without this, Volume.LookupName /
        // Chapter.Number diverge from upstream and ProcessSeries.UpdateVolumes /
        // UpdateChapters can't match existing rows after a level change — old rows
        // get deleted, cascading user progress + bookmarks out.
        if (match.Groups["vol"].Success)
        {
            return new FileClassification(Parser.RemoveLeadingZeroes(match.Groups["volNum"].Value), Parser.DefaultChapter, false, outcome, reason);
        }
        if (match.Groups["ch"].Success)
        {
            return new FileClassification(Parser.LooseLeafVolume, Parser.RemoveLeadingZeroes(match.Groups["chNum"].Value), false, outcome, reason);
        }
        // SP
        return new FileClassification(Parser.SpecialVolume, Parser.DefaultChapter, true, outcome, reason);
    }

    private static string ReasonFor(Match match)
    {
        if (match.Groups["vol"].Success) return $"volume token V/T{match.Groups["volNum"].Value}";
        if (match.Groups["ch"].Success) return $"chapter token C/CH{match.Groups["chNum"].Value}";
        return $"special token SP{match.Groups["spNum"].Value}";
    }

    private sealed record FileClassification(string Volumes, string Chapters, bool IsSpecial,
        ParserOutcome Outcome, string Reason);
}

/// <summary>
/// Carrier returned by <see cref="StrictParser.ParseDetailed"/>. <c>Info == null</c> means
/// the file was rejected; <see cref="Reason"/> always carries a short human-readable note.
/// <see cref="Outcome"/> distinguishes Accepted / Lenient / Rejected.
/// </summary>
public sealed record StrictParseResult(ParserInfo? Info, ParserOutcome Outcome, string Reason, string Tokens = "")
{
    public static StrictParseResult Accept(ParserInfo info, ParserOutcome outcome, string reason, string tokens = "")
        => new(info, outcome, reason, tokens);

    public static StrictParseResult Reject(string reason) => new(null, ParserOutcome.Rejected, reason);
}
