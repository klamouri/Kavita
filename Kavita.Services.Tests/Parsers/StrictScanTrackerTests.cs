using System.Linq;
using Kavita.Services.Scanner.StrictMode;

namespace Kavita.Services.Tests.Parsers;

/// <summary>
/// Unit tests for <see cref="StrictScanTracker"/>. The tracker is the per-scan
/// in-memory state that lets <see cref="StrictSeriesLookup"/> skip Series rows
/// already claimed by an earlier ParsedSeries bucket — prevents two on-disk
/// folders from silently merging into one Series row mid-scan.
/// </summary>
public class StrictScanTrackerTests
{
    [Fact]
    public void Reset_ClearsClaimedSetForLibrary()
    {
        StrictScanTracker.Reset(100);
        StrictScanTracker.MarkClaimed(100, 5);
        StrictScanTracker.MarkClaimed(100, 6);
        Assert.Equal(2, StrictScanTracker.GetClaimed(100).Count);

        StrictScanTracker.Reset(100);
        Assert.Empty(StrictScanTracker.GetClaimed(100));
    }

    [Fact]
    public void MarkClaimed_IsIdempotent()
    {
        StrictScanTracker.Reset(101);
        StrictScanTracker.MarkClaimed(101, 7);
        StrictScanTracker.MarkClaimed(101, 7);
        StrictScanTracker.MarkClaimed(101, 7);
        Assert.Single(StrictScanTracker.GetClaimed(101));
    }

    [Fact]
    public void GetClaimed_IsolatedPerLibrary()
    {
        StrictScanTracker.Reset(102);
        StrictScanTracker.Reset(103);
        StrictScanTracker.MarkClaimed(102, 1);
        StrictScanTracker.MarkClaimed(102, 2);
        StrictScanTracker.MarkClaimed(103, 99);

        var a = StrictScanTracker.GetClaimed(102).OrderBy(x => x).ToList();
        var b = StrictScanTracker.GetClaimed(103).OrderBy(x => x).ToList();

        Assert.Equal(new[] { 1, 2 }, a);
        Assert.Equal(new[] { 99 }, b);
    }

    [Fact]
    public void GetClaimed_UnknownLibrary_ReturnsEmpty()
    {
        Assert.Empty(StrictScanTracker.GetClaimed(99999));
    }

    [Fact]
    public void MarkClaimed_WithoutReset_StillWorks()
    {
        // First MarkClaimed on a never-Reset library should still register.
        StrictScanTracker.MarkClaimed(104, 42);
        Assert.Contains(42, StrictScanTracker.GetClaimed(104));
        StrictScanTracker.Reset(104); // cleanup
    }

    [Fact]
    public void RegisterScannedPaths_NormalizesAndLowercases()
    {
        StrictScanTracker.Reset(200);
        StrictScanTracker.RegisterScannedPaths(200, new[]
        {
            "/Lib/Foo\\Bar.cbz", // backslash + mixed case
            "/lib/baz.cbz",
        });

        // Forward-slash + lowercase form should match.
        Assert.True(StrictScanTracker.IsPathScanned(200, "/lib/foo/bar.cbz"));
        Assert.True(StrictScanTracker.IsPathScanned(200, "/LIB/BAZ.CBZ"));
        Assert.False(StrictScanTracker.IsPathScanned(200, "/lib/missing.cbz"));
    }

    [Fact]
    public void IsPathScanned_EmptyOrUnknownLibrary_ReturnsFalse()
    {
        Assert.False(StrictScanTracker.IsPathScanned(77777, "/anything"));
        Assert.False(StrictScanTracker.IsPathScanned(77777, ""));
    }

    [Fact]
    public void Reset_ClearsScannedPaths()
    {
        StrictScanTracker.Reset(201);
        StrictScanTracker.RegisterScannedPaths(201, new[] { "/lib/foo.cbz" });
        Assert.True(StrictScanTracker.IsPathScanned(201, "/lib/foo.cbz"));

        StrictScanTracker.Reset(201);
        Assert.False(StrictScanTracker.IsPathScanned(201, "/lib/foo.cbz"));
    }

    [Fact]
    public void RegisterScannedPaths_PerLibraryIsolation()
    {
        StrictScanTracker.Reset(202);
        StrictScanTracker.Reset(203);
        StrictScanTracker.RegisterScannedPaths(202, new[] { "/lib/x.cbz" });

        Assert.True(StrictScanTracker.IsPathScanned(202, "/lib/x.cbz"));
        Assert.False(StrictScanTracker.IsPathScanned(203, "/lib/x.cbz"));
    }

    [Fact]
    public void EndScan_RemovesClaimedSetAndPaths()
    {
        // Codex finding #1: without EndScan, a follow-up ScanSeries (which doesn't
        // itself call BeginScan early enough) would see ids from the previous
        // ScanLibrary as still claimed and skip the strict lookups.
        StrictScanTracker.BeginScan(300);
        StrictScanTracker.MarkClaimed(300, 7);
        StrictScanTracker.RegisterScannedPaths(300, new[] { "/lib/x.cbz" });
        Assert.NotEmpty(StrictScanTracker.GetClaimed(300));
        Assert.True(StrictScanTracker.IsPathScanned(300, "/lib/x.cbz"));

        StrictScanTracker.EndScan(300);

        Assert.Empty(StrictScanTracker.GetClaimed(300));
        Assert.False(StrictScanTracker.IsPathScanned(300, "/lib/x.cbz"));
    }

    [Fact]
    public void EndScan_UnknownLibrary_DoesNotThrow()
    {
        StrictScanTracker.EndScan(987654);
    }

    [Fact]
    public void StrictScanScope_Disposing_CallsEndScan()
    {
        const int libraryId = 301;
        using (var scope = StrictScanScope.Begin(libraryId))
        {
            StrictScanTracker.MarkClaimed(libraryId, 13);
            Assert.NotEmpty(StrictScanTracker.GetClaimed(libraryId));
        }
        // scope.Dispose() ran via using → EndScan must have wiped the set.
        Assert.Empty(StrictScanTracker.GetClaimed(libraryId));
    }
}
