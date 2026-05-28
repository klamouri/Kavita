using System;
using System.Linq;
using System.Threading.Tasks;
using Kavita.API.Services;
using Kavita.Services.Scanner.StrictMode;

namespace Kavita.Services.Tests.Parsers;

/// <summary>
/// Direct unit tests for the in-process ring buffer behind the Parser Logs UI.
/// In production this runs as a registered singleton populated by
/// <c>ReadingItemService</c> during scans — these tests drive it through
/// <see cref="IParserDebugLog"/> directly to cover the ring-eviction,
/// filtering, and concurrency guarantees in isolation.
/// </summary>
public class ParserDebugLogTests
{
    private const int Capacity = 2000;

    [Fact]
    public void Record_AppendsAndGetRecentReturnsNewestFirst()
    {
        var log = new ParserDebugLog();

        log.Record(MakeEntry(libraryId: 1, filePath: "first.cbz"));
        log.Record(MakeEntry(libraryId: 1, filePath: "second.cbz"));
        log.Record(MakeEntry(libraryId: 1, filePath: "third.cbz"));

        var recent = log.GetRecent();
        Assert.Equal(3, recent.Count);
        Assert.Equal("third.cbz", recent[0].FilePath);
        Assert.Equal("second.cbz", recent[1].FilePath);
        Assert.Equal("first.cbz", recent[2].FilePath);
    }

    [Fact]
    public void GetRecent_FilterByLibraryId_OnlyReturnsMatchingEntries()
    {
        var log = new ParserDebugLog();

        log.Record(MakeEntry(libraryId: 1, filePath: "lib1-a.cbz"));
        log.Record(MakeEntry(libraryId: 2, filePath: "lib2-a.cbz"));
        log.Record(MakeEntry(libraryId: 1, filePath: "lib1-b.cbz"));
        log.Record(MakeEntry(libraryId: 3, filePath: "lib3-a.cbz"));

        var lib1 = log.GetRecent(libraryId: 1);
        var lib2 = log.GetRecent(libraryId: 2);
        var lib99 = log.GetRecent(libraryId: 99);

        Assert.Equal(2, lib1.Count);
        Assert.All(lib1, e => Assert.Equal(1, e.LibraryId));
        Assert.Single(lib2);
        Assert.Empty(lib99);
    }

    [Fact]
    public void GetRecent_LimitCapsReturnedRowsButLeavesBufferIntact()
    {
        var log = new ParserDebugLog();
        for (var i = 0; i < 10; i++)
        {
            log.Record(MakeEntry(libraryId: 1, filePath: $"file{i:D2}.cbz"));
        }

        var capped = log.GetRecent(limit: 3);
        Assert.Equal(3, capped.Count);
        Assert.Equal("file09.cbz", capped[0].FilePath);
        Assert.Equal("file07.cbz", capped[2].FilePath);

        // Buffer was not emptied by the limited query.
        Assert.Equal(10, log.GetRecent(limit: 1000).Count);
    }

    [Fact]
    public void Record_PastCapacity_EvictsOldestEntriesFirst()
    {
        var log = new ParserDebugLog();
        for (var i = 0; i < Capacity + 50; i++)
        {
            log.Record(MakeEntry(libraryId: 1, filePath: $"f{i}.cbz"));
        }

        var all = log.GetRecent(limit: Capacity + 100);
        Assert.Equal(Capacity, all.Count);

        // Newest entry is the last inserted; oldest survivor is f50, not f0.
        Assert.Equal($"f{Capacity + 49}.cbz", all[0].FilePath);
        Assert.Equal("f50.cbz", all[^1].FilePath);
        Assert.DoesNotContain(all, e => e.FilePath == "f0.cbz");
    }

    [Fact]
    public void Clear_EmptiesTheBuffer()
    {
        var log = new ParserDebugLog();
        log.Record(MakeEntry(libraryId: 1, filePath: "a.cbz"));
        log.Record(MakeEntry(libraryId: 2, filePath: "b.cbz"));

        log.Clear();

        Assert.Empty(log.GetRecent());
    }

    [Fact]
    public async Task Record_IsThreadSafe_UnderConcurrentWrites()
    {
        var log = new ParserDebugLog();
        const int writers = 8;
        const int perWriter = 250;   // 2000 entries total — fits exactly at Capacity

        await Task.WhenAll(Enumerable.Range(0, writers).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < perWriter; i++)
            {
                log.Record(MakeEntry(libraryId: w, filePath: $"w{w}-f{i}.cbz"));
            }
        })));

        var all = log.GetRecent(limit: Capacity + 100);
        Assert.Equal(writers * perWriter, all.Count);
        // No torn writes: every entry must surface with a recognizable file path.
        Assert.All(all, e => Assert.StartsWith("w", e.FilePath));
    }

    private static ParserDebugEntry MakeEntry(int libraryId, string filePath) => new()
    {
        TimestampUtc = DateTime.UtcNow,
        LibraryId = libraryId,
        LevelAtParse = 1,
        FilePath = filePath,
        LibraryRoot = "/root",
        Outcome = ParserOutcome.Accepted,
    };
}
