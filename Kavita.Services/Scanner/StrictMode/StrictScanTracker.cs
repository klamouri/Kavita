using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Kavita.Services.Scanner.StrictMode;

/// <summary>
/// Fork-only per-library-scan state. Tracks two things:
/// <list type="bullet">
/// <item><b>Claimed series ids</b> — rows already used as parent in this scan's
/// <c>ProcessSeries</c> iterations.</item>
/// <item><b>Scanned file paths</b> — every file (normalized, lowercase) that any
/// ParsedSeries bucket plans to ingest in this scan. Lets
/// <see cref="StrictSeriesLookup.FindByFileBasenameOverlap"/> distinguish a true
/// folder rename (candidate's old files are gone from disk → none in this scan's
/// path set) from a folder split (candidate's old files are still being scanned
/// by another bucket → leave that bucket the row).</item>
/// </list>
///
/// <para>Why claim-tracking: when two on-disk folders historically merged into one
/// Series row (upstream's regex normalized both filenames to the same series name),
/// the first ParsedSeries bucket claims the row by name; the second bucket then
/// misses by name and falls through to the folder-path fallback. Without
/// claim-tracking the fallback can return the same row again (overwriting the
/// first bucket's work), or it can return a sibling row that then gets purged by
/// the end-of-scan empty-series cleanup before it can be filled.</para>
///
/// <para>Why path-tracking: order-independence for the rename-vs-split decision.
/// Whichever bucket runs first must see the same "this candidate's old files are
/// also being scanned elsewhere" signal regardless of bucket ordering.</para>
///
/// <para>State is keyed by library id so concurrent scans of different libraries
/// don't cross-contaminate. <see cref="Reset"/> is called at the start of each
/// library scan from <c>ScannerService</c>; <see cref="RegisterScannedPaths"/>
/// is called once after parsing but before bucket iteration begins.</para>
/// </summary>
public static class StrictScanTracker
{
    private static readonly ConcurrentDictionary<int, ConcurrentDictionary<int, byte>> ClaimedByLibrary = new();
    private static readonly ConcurrentDictionary<int, HashSet<string>> PathsByLibrary = new();

    public static void Reset(int libraryId)
    {
        ClaimedByLibrary[libraryId] = new ConcurrentDictionary<int, byte>();
        PathsByLibrary[libraryId] = new HashSet<string>();
    }

    /// <summary>
    /// Fork-only convenience for <c>ScannerService</c>: clears per-scan state at the
    /// start of <c>ScanLibrary</c> / <c>ScanSeries</c>. One-call form so the upstream
    /// method body stays terse — see <see cref="Reset"/> for what's cleared and why.
    /// </summary>
    public static void BeginScan(int libraryId) => Reset(libraryId);

    /// <summary>
    /// Fork-only convenience for <c>ScannerService</c>: removes the per-scan state for
    /// <paramref name="libraryId"/> entirely. Must be called from a <c>finally</c> in
    /// <c>ScanLibrary</c> / <c>ScanSeries</c> so stale claimed ids don't poison a later
    /// single-series scan that doesn't itself call <see cref="BeginScan"/> early enough.
    /// Without this, a follow-up <c>ScanSeries</c> would see ids from the previous
    /// <c>ScanLibrary</c> as still claimed and skip the strict lookups, then drop
    /// through to create-new on rows that actually exist (Codex finding #1).
    /// </summary>
    public static void EndScan(int libraryId)
    {
        ClaimedByLibrary.TryRemove(libraryId, out _);
        PathsByLibrary.TryRemove(libraryId, out _);
    }

    /// <summary>
    /// Fork-only convenience for <c>ScannerService</c>: registers every file path
    /// across every <c>ParsedSeries</c> bucket so <see cref="StrictSeriesLookup.FindByFileBasenameOverlap"/>
    /// can tell a true folder rename apart from a split.
    /// </summary>
    public static void RegisterScannedFiles(int libraryId,
        IDictionary<Kavita.Models.Parser.ParsedSeries, IList<Kavita.Models.Parser.ParserInfo>> parsedSeries)
        => RegisterScannedPaths(libraryId, parsedSeries.Values.SelectMany(infos => infos).Select(i => i.FullFilePath));

    public static void MarkClaimed(int libraryId, int seriesId)
        => ClaimedByLibrary.GetOrAdd(libraryId, _ => new ConcurrentDictionary<int, byte>()).TryAdd(seriesId, 0);

    public static IReadOnlyCollection<int> GetClaimed(int libraryId)
        => ClaimedByLibrary.TryGetValue(libraryId, out var set) ? set.Keys.ToList() : (IReadOnlyCollection<int>)System.Array.Empty<int>();

    /// <summary>
    /// Register all file paths that will be ingested in this library's scan.
    /// Paths are normalized (forward slashes, lowercased) before storage so
    /// callers don't need to pre-normalize.
    /// </summary>
    public static void RegisterScannedPaths(int libraryId, IEnumerable<string> fullFilePaths)
    {
        var set = PathsByLibrary.GetOrAdd(libraryId, _ => new HashSet<string>());
        lock (set)
        {
            foreach (var p in fullFilePaths)
            {
                if (string.IsNullOrEmpty(p)) continue;
                set.Add(Parser.NormalizePath(p).ToLowerInvariant());
            }
        }
    }

    /// <summary>True if <paramref name="fullFilePath"/> is in the active scan's path set.</summary>
    public static bool IsPathScanned(int libraryId, string fullFilePath)
    {
        if (string.IsNullOrEmpty(fullFilePath)) return false;
        if (!PathsByLibrary.TryGetValue(libraryId, out var set)) return false;
        var key = Parser.NormalizePath(fullFilePath).ToLowerInvariant();
        lock (set)
        {
            return set.Contains(key);
        }
    }
}
