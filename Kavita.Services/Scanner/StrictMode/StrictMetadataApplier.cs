using System;
using System.Collections.Generic;
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
/// <para>External IDs (AniListId, MalId, …) come per-file from <see cref="ParserInfo"/>.
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

        var hardcover = FirstPositive(infos, i => i.HardcoverId);
        if (hardcover.HasValue) series.HardcoverId = hardcover.Value;

        var metron = FirstPositive(infos, i => i.MetronId);
        if (metron.HasValue) series.MetronId = metron.Value;

        var comicVine = infos
            .Select(i => string.IsNullOrEmpty(i.ComicVineSeriesId) ? i.ComicVineId : i.ComicVineSeriesId)
            .FirstOrDefault(v => !string.IsNullOrEmpty(v));
        if (!string.IsNullOrEmpty(comicVine)) series.ComicVineId = comicVine;

        var mangaBaka = FirstPositive(infos, i => i.MangaBakaId);
        if (mangaBaka.HasValue) series.MangaBakaId = mangaBaka.Value;
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

    private static int? FirstPositive(IEnumerable<ParserInfo> infos, Func<ParserInfo, int?> selector)
        => infos.Select(selector).FirstOrDefault(v => v.HasValue && v.Value > 0);

    private static long? FirstPositive(IEnumerable<ParserInfo> infos, Func<ParserInfo, long?> selector)
        => infos.Select(selector).FirstOrDefault(v => v.HasValue && v.Value > 0);
}
