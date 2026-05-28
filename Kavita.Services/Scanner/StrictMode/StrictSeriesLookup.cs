using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Kavita.API.Database;
using Kavita.API.Repositories;
using Kavita.API.Services;
using Kavita.Common.Extensions;
using Kavita.Models.Entities;
using Kavita.Models.Entities.Enums;
using Kavita.Models.Parser;
using Microsoft.EntityFrameworkCore;

namespace Kavita.Services.Scanner.StrictMode;

/// <summary>
/// Fork-only fallback for the scanner's match-existing-series step in
/// <see cref="Kavita.Services.Scanner.ProcessSeries"/>.
///
/// <para>Two scenarios are covered:</para>
/// <list type="number">
/// <item><b>Parser-name drift</b> (level change). <see cref="FindByFolderPath"/>
/// matches an existing <see cref="Series"/> by <see cref="Series.FolderPath"/> when
/// the by-name lookup misses. Preserves SeriesId stability across strictness-level
/// changes so reading progress, On Deck, bookmarks, collections, reading-list
/// links, and ratings stay attached.</item>
/// <item><b>Folder rename on disk</b>. <see cref="FindByFileBasenameOverlap"/>
/// matches an existing series when ≥ <see cref="OverlapThreshold"/> of the new
/// folder's file basenames coincide with files the series already owns. Same
/// SeriesId-preservation goal, recovers from out-of-band folder renames the
/// user did directly on disk.</item>
/// </list>
///
/// <para>Both fallbacks consult <see cref="StrictScanTracker"/> to skip series
/// already claimed by an earlier ParsedSeries bucket in the same scan. Without
/// that, two on-disk folders that upstream's regex had collapsed into one Series
/// row could trip a race where the second bucket either re-claims the row or
/// triggers an end-of-scan empty-series cleanup that purges its sibling.</para>
/// </summary>
public static class StrictSeriesLookup
{
    /// <summary>≥ 50% basename overlap required to treat the new folder as a rename.</summary>
    public const double OverlapThreshold = 0.5;

    /// <summary>
    /// Strict-mode by-name lookup. Mirrors <c>SeriesRepository.GetFullSeriesByAnyName</c>'s WHERE
    /// clause but (a) filters out series already claimed in this scan in-query, (b) orders so
    /// NormalizedName / NormalizedLocalizedName matches outrank <c>OriginalName</c> matches, and
    /// (c) uses <c>FirstOrDefaultAsync</c> instead of <c>SingleOrDefaultAsync</c>.
    ///
    /// <para>Why (c): after a split-then-create cycle (Codex finding #3) two rows can end up
    /// sharing <c>OriginalName</c> — the renamed-but-OriginalName-untouched row plus the freshly
    /// created row whose <c>SeriesBuilder.Build()</c> seeds <c>OriginalName</c> from the parser
    /// output. <c>SingleOrDefaultAsync</c> would throw on the next scan's by-name lookup. The
    /// ordering in (b) picks the "right" row (the one matching the live Name) deterministically.</para>
    /// </summary>
    public static async Task<Series?> FindByNameStrict(IUnitOfWork unitOfWork, Library library, ParserInfo info)
    {
        if (info.Format == MangaFormat.Unknown || string.IsNullOrEmpty(info.Series)) return null;

        var normalizedSeries = info.Series.ToNormalized();
        var localized = info.LocalizedSeries ?? string.Empty;
        var normalizedLocalized = localized.ToNormalized();
        var claimedIds = StrictScanTracker.GetClaimed(library.Id);

        var hasLocalized = normalizedLocalized != string.Empty;

        return await BuildFullSeriesQuery(unitOfWork)
            .Where(s => s.LibraryId == library.Id)
            .Where(s => s.Format == info.Format)
            .Where(s => !claimedIds.Contains(s.Id))
            .Where(s =>
                s.NormalizedName == normalizedSeries
                || (hasLocalized && s.NormalizedName == normalizedLocalized)
                || s.NormalizedLocalizedName == normalizedSeries
                || (hasLocalized && s.NormalizedLocalizedName == normalizedLocalized)
                || (s.OriginalName != null && s.OriginalName == info.Series))
            // Prefer matches on the live name fields over stale OriginalName matches.
            .OrderBy(s =>
                s.NormalizedName == normalizedSeries
                || (hasLocalized && s.NormalizedName == normalizedLocalized)
                || s.NormalizedLocalizedName == normalizedSeries
                || (hasLocalized && s.NormalizedLocalizedName == normalizedLocalized)
                    ? 0 : 1)
            .ThenBy(s => s.Id)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// One-call dispatcher used by <c>ProcessSeries.ProcessSeriesAsync</c>. At level 0 delegates
    /// straight to <c>GetFullSeriesByAnyName</c> for upstream-identical behavior. At level >= 1
    /// runs the strict by-name lookup (claim-filtered, OriginalName-deprioritized,
    /// <c>FirstOrDefault</c>-safe) and then the folder-path + basename-overlap fallbacks, marking
    /// the resolved row as claimed for the remainder of this scan. Returns <c>null</c> when nothing
    /// matches so the caller can create a fresh row.
    /// </summary>
    public static async Task<Series?> ResolveAndClaim(IUnitOfWork unitOfWork, Library library,
        ParserInfo firstInfo, IList<ParserInfo> parsedInfos, IDirectoryService? directoryService = null)
    {
        Series? series;
        if (library.ParserStrictnessLevel >= 1)
        {
            series = await FindByNameStrict(unitOfWork, library, firstInfo)
                ?? await FindByFolderPath(unitOfWork, library, firstInfo)
                ?? await FindByFileBasenameOverlap(unitOfWork, library, parsedInfos, directoryService);
        }
        else
        {
            series = await unitOfWork.SeriesRepository.GetFullSeriesByAnyName(
                firstInfo.Series, firstInfo.LocalizedSeries, library.Id, firstInfo.Format);
        }

        if (series is { Id: > 0 }) StrictScanTracker.MarkClaimed(library.Id, series.Id);
        return series;
    }

    public static async Task<Series?> FindByFolderPath(IUnitOfWork unitOfWork, Library library, ParserInfo info)
    {
        var folderPath = ResolveSeriesFolderPath(library, info.FullFilePath);
        if (string.IsNullOrEmpty(folderPath)) return null;

        var claimedIds = StrictScanTracker.GetClaimed(library.Id);

        var match = await BuildFullSeriesQuery(unitOfWork)
            .Where(s => s.LibraryId == library.Id)
            .Where(s => s.Format == info.Format)
            .Where(s => !claimedIds.Contains(s.Id))
            .Where(s => s.FolderPath != null && s.FolderPath.Equals(folderPath))
            .FirstOrDefaultAsync();

        return match == null ? null : RenameAndReturn(match, info);
    }

    /// <summary>
    /// Folder-rename detector. Looks for an existing series (same library + format,
    /// not yet claimed in this scan) whose owned MangaFile basenames overlap the
    /// incoming parsed files by at least <see cref="OverlapThreshold"/>. When a
    /// match is found the series is reused and renamed in place — the next time
    /// <c>UpdateSeriesFolderPath</c> runs it rewrites <see cref="Series.FolderPath"/>
    /// to the new location automatically.
    ///
    /// <para>Split guard: a candidate is skipped when ANY parent directory of its
    /// existing MangaFile paths still exists on disk (Codex finding #2 — direct
    /// disk check is robust to unchanged-folder buckets that don't show up in
    /// the path-tracker). As a belt-and-suspenders, also skip candidates whose
    /// existing FilePaths are still being scanned by some other bucket in the
    /// current scan (<see cref="StrictScanTracker.IsPathScanned"/>). Either signal
    /// means the candidate's old folder is the home of an in-scope bucket and
    /// that bucket should claim the row via name or FolderPath identity.</para>
    ///
    /// <para>When <paramref name="directoryService"/> is null only the in-scan
    /// path check applies (covers tests that don't wire a directory service).</para>
    /// </summary>
    public static async Task<Series?> FindByFileBasenameOverlap(
        IUnitOfWork unitOfWork, Library library, IList<ParserInfo> parsedInfos,
        IDirectoryService? directoryService = null)
    {
        var parsedBasenames = parsedInfos
            .Select(i => Parser.NormalizePath(i.FullFilePath))
            .Select(p => Path.GetFileName(p))
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!.ToLowerInvariant())
            .ToHashSet();
        if (parsedBasenames.Count == 0) return null;

        var claimedIds = StrictScanTracker.GetClaimed(library.Id);
        var format = parsedInfos[0].Format;

        var candidates = await BuildFullSeriesQuery(unitOfWork)
            .Where(s => s.LibraryId == library.Id)
            .Where(s => s.Format == format)
            .Where(s => !claimedIds.Contains(s.Id))
            .ToListAsync();

        Series? best = null;
        double bestRatio = 0;
        foreach (var s in candidates)
        {
            var ownedFiles = s.Volumes
                .SelectMany(v => v.Chapters)
                .SelectMany(c => c.Files)
                .Select(f => f.FilePath ?? string.Empty)
                .Where(p => p.Length > 0)
                .ToList();
            if (ownedFiles.Count == 0) continue;

            if (CandidateOldFolderStillPresent(library.Id, ownedFiles, directoryService)) continue;

            var owned = ownedFiles
                .Select(p => Path.GetFileName(Parser.NormalizePath(p)) ?? string.Empty)
                .Where(n => n.Length > 0)
                .Select(n => n.ToLowerInvariant())
                .ToHashSet();
            if (owned.Count == 0) continue;

            var overlap = parsedBasenames.Intersect(owned).Count();
            var ratio = (double)overlap / parsedBasenames.Count;
            if (ratio >= OverlapThreshold && ratio > bestRatio)
            {
                best = s;
                bestRatio = ratio;
            }
        }

        return best == null ? null : RenameAndReturn(best, parsedInfos[0]);
    }

    /// <summary>
    /// Split-guard core. Returns true iff the candidate series's old home is still
    /// "there" by either signal: any of its <paramref name="ownedFiles"/> paths is
    /// in the current scan's registered set, or any unique parent directory of
    /// those paths still exists on disk per <paramref name="directoryService"/>.
    /// </summary>
    private static bool CandidateOldFolderStillPresent(int libraryId, IList<string> ownedFiles,
        IDirectoryService? directoryService)
    {
        foreach (var p in ownedFiles)
        {
            if (StrictScanTracker.IsPathScanned(libraryId, p)) return true;
        }
        if (directoryService == null) return false;
        var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in ownedFiles)
        {
            var dir = Path.GetDirectoryName(Parser.NormalizePath(p));
            if (string.IsNullOrEmpty(dir)) continue;
            if (!seenDirs.Add(dir)) continue;
            if (directoryService.Exists(dir)) return true;
        }
        return false;
    }

    private static IQueryable<Series> BuildFullSeriesQuery(IUnitOfWork unitOfWork)
        => unitOfWork.DataContext.Series
            .Include(s => s.Library)
            .Include(s => s.Metadata)
                .ThenInclude(m => m.People)
                    .ThenInclude(p => p.Person)
            .Include(s => s.Metadata).ThenInclude(m => m.Genres)
            .Include(s => s.Metadata).ThenInclude(m => m.Tags)
            .Include(s => s.Volumes).ThenInclude(v => v.Chapters)
                .ThenInclude(c => c.People).ThenInclude(p => p.Person)
            .Include(s => s.Volumes).ThenInclude(v => v.Chapters).ThenInclude(c => c.Tags)
            .Include(s => s.Volumes).ThenInclude(v => v.Chapters).ThenInclude(c => c.Genres)
            .Include(s => s.Volumes).ThenInclude(v => v.Chapters).ThenInclude(c => c.Files)
            .AsSplitQuery();

    private static Series RenameAndReturn(Series match, ParserInfo info)
    {
        // Sync the visible name to the new parser's output. Upstream never updates
        // Series.Name on subsequent scans (it's sticky after creation); when we
        // matched by folder-path or basename-overlap the parser is intentionally
        // producing a different name and the point of these fallbacks is to honor it.
        // NormalizedName is recomputed by the caller (ProcessSeries) immediately
        // after this returns, so we only need to set Name.
        //
        // Also keep OriginalName aligned with the renamed Name (Codex finding #3
        // mitigation). If we leave OriginalName at its pre-rename value, a sibling
        // bucket that later creates a new Series via SeriesBuilder will seed
        // OriginalName from its own parser output — and the two rows can end up
        // sharing OriginalName, which makes upstream's by-name SingleOrDefaultAsync
        // throw on the next scan.
        //
        // A locked Name (user or Kavita+ rename, v0.9.1+) is kept: only OriginalName, the
        // on-disk anchor upstream matches on, follows the parser.
        if (string.IsNullOrEmpty(info.Series)) return match;

        if (!match.NameLocked && match.Name != info.Series)
        {
            match.Name = info.Series;
        }
        if (match.OriginalName != info.Series)
        {
            match.OriginalName = info.Series;
        }
        return match;
    }

    /// <summary>
    /// Derive the immediate-child-of-library-root folder for a given file path,
    /// then return that folder's full normalized path (matches the shape of
    /// <see cref="Series.FolderPath"/> as set by <c>UpdateSeriesFolderPath</c>).
    /// Returns empty string if the file is not under any library folder.
    /// </summary>
    private static string ResolveSeriesFolderPath(Library library, string fullFilePath)
    {
        if (string.IsNullOrEmpty(fullFilePath)) return string.Empty;
        var normalizedFile = Parser.NormalizePath(fullFilePath);
        foreach (var folder in library.Folders)
        {
            var root = Parser.NormalizePath(folder.Path).TrimEnd('/');
            if (!normalizedFile.StartsWith($"{root}/", StringComparison.OrdinalIgnoreCase)) continue;
            var rel = normalizedFile[(root.Length + 1)..];
            var sep = rel.IndexOf('/');
            if (sep <= 0) return string.Empty;
            return $"{root}/{rel[..sep]}";
        }
        return string.Empty;
    }
}
