using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Kavita.Models.Builders;
using Kavita.Models.Entities;
using Kavita.Models.Entities.Enums;
using Kavita.Models.Parser;

namespace Kavita.Services.Scanner.StrictMode;

/// <summary>
/// Bridges Strict-mode parsed metadata onto a <see cref="Series"/> entity. Called from
/// <c>ProcessSeries</c> when the library has <c>ParserStrictnessLevel &gt;= 1</c>.
///
/// <para>External IDs (AniListId, MalId, …) come per-file from <see cref="ParserInfo"/>,
/// except <c>{hardcoverId-…}</c>, which is re-extracted from folder / file names.
/// Folder-only tokens (<c>{language-…}</c>, <c>{publicationStatus-…}</c>) and the
/// trailing <c>(YYYY)</c> are re-extracted from the series folder name here, so the
/// upstream <c>ParserInfo</c> doesn't have to carry fork-specific properties.</para>
///
/// <para>Auto-locks every <c>SeriesMetadata</c> field it writes (ReleaseYear, Language,
/// PublicationStatus) so ComicInfo / Kavita+ scrobbler can't silently overwrite it on
/// a later scan. External IDs on <c>Series</c> have no lock flags upstream — they're
/// best-effort and Kavita+ can overwrite them; see fork-docs/parser-strictness.md.</para>
/// </summary>
public static class StrictMetadataApplier
{
    public static void Apply(Series series, IEnumerable<ParserInfo> parsedInfos, Library library)
    {
        series.Metadata ??= new SeriesMetadataBuilder().Build();

        var infos = parsedInfos as IList<ParserInfo> ?? parsedInfos.ToList();
        if (infos.Count == 0) return;

        ApplyFolderMetadata(series, infos, library);
        ApplyExternalIds(series, infos);
        ApplyHardcoverSeriesId(series, infos, library);
    }

    /// <summary>
    /// Called from <c>ProcessSeries</c> right before upstream's <c>UpdateSeriesMetadata</c>.
    /// Since Kavita v0.9.1, a locked <c>PublicationStatus</c> skips
    /// <c>DeterminePublicationStatus</c> entirely, so MaxCount / TotalCount would freeze
    /// for every folder carrying <c>{publicationStatus-…}</c>. When the folder has a valid
    /// token, the lock is released so upstream recomputes the counts; <see cref="Apply"/>
    /// then re-asserts the token's status and re-locks it. A user lock without a token
    /// is left alone.
    /// </summary>
    public static void ReleaseTokenOwnedLocks(Series series, IEnumerable<ParserInfo> parsedInfos, Library library)
    {
        if (series.Metadata == null) return;
        var first = parsedInfos.FirstOrDefault();
        if (first == null) return;

        var (_, folderTokens) = StrictTokens.Extract(ResolveSeriesFolderName(library, first.FullFilePath));
        if (TryGetPublicationStatus(folderTokens, out _))
        {
            series.Metadata.PublicationStatusLocked = false;
        }
    }

    private static void ApplyFolderMetadata(Series series, IList<ParserInfo> infos, Library library)
    {
        var folderName = ResolveSeriesFolderName(library, infos[0].FullFilePath);
        if (string.IsNullOrEmpty(folderName)) return;

        var (remainderAfterTokens, folderTokens) = StrictTokens.Extract(folderName);
        var (_, year) = StrictTokens.PeelTrailingYear(remainderAfterTokens);
        if (year > 0)
        {
            series.Metadata.ReleaseYear = year;
            series.Metadata.ReleaseYearLocked = true;
        }

        foreach (var (key, value) in folderTokens)
        {
            switch (key.ToLowerInvariant())
            {
                case "language":
                    if (!string.IsNullOrEmpty(value))
                    {
                        series.Metadata.Language = value;
                        series.Metadata.LanguageLocked = true;
                    }
                    break;
                case "publicationstatus":
                    if (Enum.TryParse<PublicationStatus>(value, ignoreCase: true, out var status))
                    {
                        series.Metadata.PublicationStatus = status;
                        series.Metadata.PublicationStatusLocked = true;
                    }
                    break;
            }
        }
    }

    private static void ApplyExternalIds(Series series, IList<ParserInfo> infos)
    {
        var anilist = FirstPositive(infos, i => i.AniListId);
        if (anilist.HasValue) series.AniListId = anilist.Value;

        var mal = FirstPositive(infos, i => i.MalId);
        if (mal.HasValue) series.MalId = mal.Value;

        var metron = FirstPositive(infos, i => i.MetronId);
        if (metron.HasValue) series.MetronId = metron.Value;

        var comicVine = infos
            .Select(i => string.IsNullOrEmpty(i.ComicVineSeriesId) ? i.ComicVineId : i.ComicVineSeriesId)
            .FirstOrDefault(v => !string.IsNullOrEmpty(v));
        if (!string.IsNullOrEmpty(comicVine)) series.ComicVineId = comicVine;

        var mangaBaka = FirstPositive(infos, i => i.MangaBakaId);
        if (mangaBaka.HasValue) series.MangaBakaId = mangaBaka.Value;
    }

    /// <summary>
    /// <c>{hardcoverId-…}</c> is a series id, but <see cref="ParserInfo.HardcoverId"/> is a
    /// Hardcover book id since Kavita v0.9.1 (ComicInfo Web links, scrobbling), so
    /// <see cref="ParserInfo.HardcoverId"/> is never copied onto the series. The token is
    /// re-extracted per parsed file (its file token, else the folder token); the first file,
    /// in parse order, that resolves to a positive id sets <see cref="Series.HardcoverId"/>.
    /// </summary>
    private static void ApplyHardcoverSeriesId(Series series, IList<ParserInfo> infos, Library library)
    {
        var (_, folderTokens) = StrictTokens.Extract(ResolveSeriesFolderName(library, infos[0].FullFilePath));
        StrictTokens.TryGetHardcoverId(folderTokens, out var folderId);

        var tokenIds = new HashSet<int>();
        var seriesId = 0;
        foreach (var info in infos)
        {
            var fileName = Path.GetFileNameWithoutExtension(info.FullFilePath ?? string.Empty);
            var id = StrictTokens.TryGetHardcoverId(StrictTokens.Extract(fileName).Tokens, out var fileId) ? fileId : folderId;
            if (id <= 0) continue;

            tokenIds.Add(id);
            if (seriesId == 0) seriesId = id;
        }

        if (seriesId > 0)
        {
            series.HardcoverId = seriesId;
            // A token id is a series id; Kavita+ matching may have flagged the row as a stand-alone book.
            series.IsStandAlone = false;
        }

        ClearLegacyChapterHardcoverIds(series, infos, tokenIds);
    }

    /// <summary>
    /// Fork builds before v0.9.1.4-patched-0.1.0 copied the <c>{hardcoverId-…}</c> series id onto
    /// <see cref="Chapter.HardcoverId"/>, and upstream only overwrites that column with a positive
    /// ComicInfo book id — so the stale series id would be scrobbled as a book. Clears chapter ids
    /// that equal a token id, unless this scan parsed that same id from the chapter's ComicInfo.
    /// </summary>
    private static void ClearLegacyChapterHardcoverIds(Series series, IList<ParserInfo> infos, HashSet<int> tokenIds)
    {
        if (tokenIds.Count == 0 || series.Volumes == null) return;

        var bookIdByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var info in infos)
        {
            if (info.HardcoverId is > 0 && !string.IsNullOrEmpty(info.FullFilePath))
            {
                bookIdByPath[Parser.NormalizePath(info.FullFilePath)] = info.HardcoverId.Value;
            }
        }

        foreach (var chapter in series.Volumes.SelectMany(v => v.Chapters ?? Enumerable.Empty<Chapter>()))
        {
            if (!tokenIds.Contains(chapter.HardcoverId)) continue;

            var parsedAsBookId = chapter.Files?.Any(f =>
                bookIdByPath.TryGetValue(Parser.NormalizePath(f.FilePath), out var bookId) && bookId == chapter.HardcoverId) ?? false;
            if (!parsedAsBookId) chapter.HardcoverId = 0;
        }
    }

    private static string ResolveSeriesFolderName(Library library, string fullFilePath)
    {
        if (string.IsNullOrEmpty(fullFilePath)) return string.Empty;
        var normalizedFile = Parser.NormalizePath(fullFilePath);
        foreach (var folder in library.Folders)
        {
            var root = Parser.NormalizePath(folder.Path).TrimEnd('/');
            if (!normalizedFile.StartsWith($"{root}/", StringComparison.OrdinalIgnoreCase)) continue;
            var rel = normalizedFile[(root.Length + 1)..];
            var sep = rel.IndexOf('/');
            return sep <= 0 ? string.Empty : rel[..sep];
        }
        return string.Empty;
    }

    private static bool TryGetPublicationStatus(IEnumerable<(string Key, string Value)> folderTokens, out PublicationStatus status)
    {
        status = default;
        foreach (var (key, value) in folderTokens)
        {
            if (key.Equals("publicationStatus", StringComparison.OrdinalIgnoreCase)
                && Enum.TryParse(value, ignoreCase: true, out status))
            {
                return true;
            }
        }
        return false;
    }

    private static int? FirstPositive(IEnumerable<ParserInfo> infos, Func<ParserInfo, int?> selector)
        => infos.Select(selector).FirstOrDefault(v => v.HasValue && v.Value > 0);

    private static long? FirstPositive(IEnumerable<ParserInfo> infos, Func<ParserInfo, long?> selector)
        => infos.Select(selector).FirstOrDefault(v => v.HasValue && v.Value > 0);
}
