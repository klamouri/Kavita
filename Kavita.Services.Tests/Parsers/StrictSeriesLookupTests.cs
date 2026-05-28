using System.Threading.Tasks;
using Kavita.Common.Extensions;
using Kavita.Database.Tests;
using Kavita.Models.Builders;
using Kavita.Models.Entities;
using Kavita.Models.Entities.Enums;
using Kavita.Models.Parser;
using Kavita.Services;
using Kavita.Services.Builders;
using Kavita.Services.Scanner.StrictMode;
using Xunit.Abstractions;

namespace Kavita.Services.Tests.Parsers;

/// <summary>
/// Direct tests for <see cref="StrictSeriesLookup"/>. This is the fork's data-
/// preservation fallback: when the scanner's by-name lookup misses (because a
/// strictness-level change produced a different name for the same folder), the
/// fallback finds the existing <see cref="Series"/> row by FolderPath so the
/// SeriesId stays stable and per-series user data (progress, On Deck, bookmarks,
/// collections, reading-list links, ratings) stays attached.
/// </summary>
public class StrictSeriesLookupTests : AbstractDbTest
{
    public StrictSeriesLookupTests(ITestOutputHelper output) : base(output)
    {
        // Reset the per-library claimed-set so tests don't pollute each other.
        // Test seed DB uses library id 1; side cases use 999.
        StrictScanTracker.Reset(1);
        StrictScanTracker.Reset(999);
    }

    [Fact]
    public async Task FindByFolderPath_FolderPathMatches_ReturnsExistingSeries()
    {
        var (unitOfWork, ctx, _) = await CreateDatabase();
        var library = await ctx.Library.FindAsync(1);
        library!.Folders = new System.Collections.Generic.List<FolderPath>
        {
            new() { Path = "/lib" },
        };
        var existing = new SeriesBuilder("Quill Lantern Vol 1")
            .WithFormat(MangaFormat.Archive)
            .Build();
        existing.FolderPath = "/lib/Quill Lantern";
        existing.LibraryId = library.Id;
        ctx.Series.Add(existing);
        await ctx.SaveChangesAsync();

        var info = MakeInfo(series: "Quill Lantern", fullFilePath: "/lib/Quill Lantern/V01.cbz", format: MangaFormat.Archive);
        var match = await StrictSeriesLookup.FindByFolderPath(unitOfWork, library, info);

        Assert.NotNull(match);
        Assert.Equal(existing.Id, match!.Id);
        Assert.Equal("Quill Lantern", match.Name); // name updated to new parser output
    }

    [Fact]
    public async Task FindByFolderPath_NoMatch_ReturnsNull()
    {
        var (unitOfWork, ctx, _) = await CreateDatabase();
        var library = await ctx.Library.FindAsync(1);
        library!.Folders = new System.Collections.Generic.List<FolderPath>
        {
            new() { Path = "/lib" },
        };

        var info = MakeInfo(series: "Quill Lantern", fullFilePath: "/lib/Quill Lantern/V01.cbz", format: MangaFormat.Archive);
        var match = await StrictSeriesLookup.FindByFolderPath(unitOfWork, library, info);

        Assert.Null(match);
    }

    [Fact]
    public async Task FindByFolderPath_FormatMismatch_ReturnsNull()
    {
        var (unitOfWork, ctx, _) = await CreateDatabase();
        var library = await ctx.Library.FindAsync(1);
        library!.Folders = new System.Collections.Generic.List<FolderPath>
        {
            new() { Path = "/lib" },
        };
        var existing = new SeriesBuilder("Quill Lantern")
            .WithFormat(MangaFormat.Image)
            .Build();
        existing.FolderPath = "/lib/Quill Lantern";
        existing.LibraryId = library.Id;
        ctx.Series.Add(existing);
        await ctx.SaveChangesAsync();

        var info = MakeInfo(series: "Quill Lantern", fullFilePath: "/lib/Quill Lantern/V01.cbz", format: MangaFormat.Archive);
        var match = await StrictSeriesLookup.FindByFolderPath(unitOfWork, library, info);

        Assert.Null(match);
    }

    [Fact]
    public async Task FindByFolderPath_DifferentLibrary_ReturnsNull()
    {
        var (unitOfWork, ctx, _) = await CreateDatabase();
        var library = await ctx.Library.FindAsync(1);
        library!.Folders = new System.Collections.Generic.List<FolderPath>
        {
            new() { Path = "/lib" },
        };
        var otherLib = new LibraryBuilder("Other")
            .WithFolderPath(new FolderPathBuilder("/lib").Build())
            .Build();
        ctx.Library.Add(otherLib);
        await ctx.SaveChangesAsync();
        var existing = new SeriesBuilder("Quill Lantern")
            .WithFormat(MangaFormat.Archive)
            .Build();
        existing.FolderPath = "/lib/Quill Lantern";
        existing.LibraryId = otherLib.Id;
        ctx.Series.Add(existing);
        await ctx.SaveChangesAsync();

        var info = MakeInfo(series: "Quill Lantern", fullFilePath: "/lib/Quill Lantern/V01.cbz", format: MangaFormat.Archive);
        var match = await StrictSeriesLookup.FindByFolderPath(unitOfWork, library, info);

        Assert.Null(match);
    }

    [Fact]
    public async Task FindByFolderPath_FileNotUnderLibraryFolder_ReturnsNull()
    {
        var (unitOfWork, ctx, _) = await CreateDatabase();
        var library = await ctx.Library.FindAsync(1);
        library!.Folders = new System.Collections.Generic.List<FolderPath>
        {
            new() { Path = "/lib" },
        };

        var info = MakeInfo(series: "Quill Lantern", fullFilePath: "/elsewhere/Quill Lantern/V01.cbz", format: MangaFormat.Archive);
        var match = await StrictSeriesLookup.FindByFolderPath(unitOfWork, library, info);

        Assert.Null(match);
    }

    [Fact]
    public async Task FindByFolderPath_NameMatchesAlready_DoesNotMarkAsModified()
    {
        var (unitOfWork, ctx, _) = await CreateDatabase();
        var library = await ctx.Library.FindAsync(1);
        library!.Folders = new System.Collections.Generic.List<FolderPath>
        {
            new() { Path = "/lib" },
        };
        var existing = new SeriesBuilder("Quill Lantern")
            .WithFormat(MangaFormat.Archive)
            .Build();
        existing.FolderPath = "/lib/Quill Lantern";
        existing.LibraryId = library.Id;
        ctx.Series.Add(existing);
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();
        // Re-fetch so the entry isn't already tracked-modified
        existing = await ctx.Series.FindAsync(existing.Id);

        var info = MakeInfo(series: "Quill Lantern", fullFilePath: "/lib/Quill Lantern/V01.cbz", format: MangaFormat.Archive);
        var match = await StrictSeriesLookup.FindByFolderPath(unitOfWork, library, info);

        Assert.NotNull(match);
        var entry = ctx.Entry(match!);
        Assert.NotEqual(Microsoft.EntityFrameworkCore.EntityState.Modified, entry.State);
    }

    [Fact]
    public async Task FindByFolderPath_NameLocked_KeepsNameButUpdatesOriginalName()
    {
        // Kavita v0.9.1+ lets users / Kavita+ rename a series and lock the name, keeping
        // OriginalName as the on-disk anchor. The fallback must not undo that rename.
        var (unitOfWork, ctx, _) = await CreateDatabase();
        var library = await ctx.Library.FindAsync(1);
        library!.Folders = new System.Collections.Generic.List<FolderPath>
        {
            new() { Path = "/lib" },
        };
        var existing = new SeriesBuilder("Quill Lantern Vol 1")
            .WithFormat(MangaFormat.Archive)
            .Build();
        existing.Name = "The Quill Lantern Chronicles";
        existing.NameLocked = true;
        existing.FolderPath = "/lib/Quill Lantern";
        existing.LibraryId = library.Id;
        ctx.Series.Add(existing);
        await ctx.SaveChangesAsync();

        var info = MakeInfo(series: "Quill Lantern", fullFilePath: "/lib/Quill Lantern/V01.cbz", format: MangaFormat.Archive);
        var match = await StrictSeriesLookup.FindByFolderPath(unitOfWork, library, info);

        Assert.NotNull(match);
        Assert.Equal(existing.Id, match!.Id);
        Assert.Equal("The Quill Lantern Chronicles", match.Name);
        Assert.True(match.NameLocked);
        Assert.Equal("Quill Lantern", match.OriginalName);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // End-to-end transition tests: simulate the level-change rebucketing risk
    // by directly calling the fallback after seeding a series with user data,
    // then asserting that the user-data FKs still point at the same row.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LevelChange_PreservesSeriesId_AndAttachedData()
    {
        var (unitOfWork, ctx, _) = await CreateDatabase();
        var library = await ctx.Library.FindAsync(1);
        library!.Folders = new System.Collections.Generic.List<FolderPath>
        {
            new() { Path = "/lib" },
        };

        // Pre-change state: a series with the upstream-style name + a chapter, and
        // simulated user data anchored to its IDs.
        var series = new SeriesBuilder("Quill Lantern Vol 1") // upstream regex artefact
            .WithFormat(MangaFormat.Archive)
            .Build();
        series.FolderPath = "/lib/Quill Lantern";
        series.LibraryId = library.Id;
        var volume = new VolumeBuilder("1").Build();
        var chapter = new ChapterBuilder("1").Build();
        volume.Chapters = new System.Collections.Generic.List<Chapter> { chapter };
        series.Volumes = new System.Collections.Generic.List<Volume> { volume };
        ctx.Series.Add(series);
        await ctx.SaveChangesAsync();

        var preChangeSeriesId = series.Id;

        // Post-change scan: strict parser produces a different series name for
        // the same folder. The by-name lookup would miss; the fallback recovers.
        var info = MakeInfo(series: "Quill Lantern", fullFilePath: "/lib/Quill Lantern/V02.cbz", format: MangaFormat.Archive);
        var match = await StrictSeriesLookup.FindByFolderPath(unitOfWork, library, info);

        Assert.NotNull(match);
        Assert.Equal(preChangeSeriesId, match!.Id);   // SAME SeriesId → all FK-anchored user data follows
        Assert.Equal("Quill Lantern", match.Name);            // visible name updated to new parser output
        Assert.Single(match.Volumes);                  // existing volumes/chapters still attached
    }

    [Fact]
    public async Task RoundTrip_NameChangesTwice_SameSeriesIdThroughout()
    {
        var (unitOfWork, ctx, _) = await CreateDatabase();
        var library = await ctx.Library.FindAsync(1);
        library!.Folders = new System.Collections.Generic.List<FolderPath>
        {
            new() { Path = "/lib" },
        };
        var series = new SeriesBuilder("Old Upstream Name")
            .WithFormat(MangaFormat.Archive)
            .Build();
        series.FolderPath = "/lib/Quill Lantern";
        series.LibraryId = library.Id;
        ctx.Series.Add(series);
        await ctx.SaveChangesAsync();
        var initialId = series.Id;

        // Round 1: level 0 → 1 — name shifts to "Quill Lantern".
        var info1 = MakeInfo(series: "Quill Lantern", fullFilePath: "/lib/Quill Lantern/V01.cbz", format: MangaFormat.Archive);
        var match1 = await StrictSeriesLookup.FindByFolderPath(unitOfWork, library, info1);
        await ctx.SaveChangesAsync();
        Assert.Equal(initialId, match1!.Id);
        Assert.Equal("Quill Lantern", match1.Name);

        // Round 2: level 1 → 0 — upstream's regex produces yet another name. Same row.
        var info2 = MakeInfo(series: "Quill Lantern Vol 1", fullFilePath: "/lib/Quill Lantern/V01.cbz", format: MangaFormat.Archive);
        var match2 = await StrictSeriesLookup.FindByFolderPath(unitOfWork, library, info2);
        await ctx.SaveChangesAsync();
        Assert.Equal(initialId, match2!.Id);
        Assert.Equal("Quill Lantern Vol 1", match2.Name);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Claim tracking: a row already used by an earlier ParsedSeries bucket in
    // this scan must NOT be returned by the fallback. Prevents two on-disk
    // folders from collapsing into the same Series row mid-scan.
    // ─────────────────────────────────────────────────────────────────────────

    // ─────────────────────────────────────────────────────────────────────────
    // FindByNameStrict — strict-mode by-name lookup (replaces GuardClaimedByName).
    // Filters claimed ids in-query, prefers NormalizedName matches over OriginalName,
    // and uses FirstOrDefaultAsync so two rows with the same OriginalName don't throw.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FindByNameStrict_NoMatch_ReturnsNull()
    {
        var (unitOfWork, ctx, _) = await CreateDatabase();
        var library = await ctx.Library.FindAsync(1);
        var info = MakeInfo(series: "Stellar Drift", fullFilePath: "/lib/Stellar Drift/V01.cbz", format: MangaFormat.Archive);
        Assert.Null(await StrictSeriesLookup.FindByNameStrict(unitOfWork, library!, info));
    }

    [Fact]
    public async Task FindByNameStrict_NormalizedNameMatch_Returns()
    {
        var (unitOfWork, ctx, _) = await CreateDatabase();
        var library = await ctx.Library.FindAsync(1);
        var existing = new SeriesBuilder("Stellar Drift").WithFormat(MangaFormat.Archive).Build();
        existing.LibraryId = library!.Id;
        ctx.Series.Add(existing);
        await ctx.SaveChangesAsync();

        var info = MakeInfo(series: "Stellar Drift", fullFilePath: "/lib/Stellar Drift/V01.cbz", format: MangaFormat.Archive);
        var match = await StrictSeriesLookup.FindByNameStrict(unitOfWork, library, info);
        Assert.NotNull(match);
        Assert.Equal(existing.Id, match!.Id);
    }

    [Fact]
    public async Task FindByNameStrict_ClaimedSeries_IsFilteredOut()
    {
        var (unitOfWork, ctx, _) = await CreateDatabase();
        var library = await ctx.Library.FindAsync(1);
        var existing = new SeriesBuilder("Stellar Drift").WithFormat(MangaFormat.Archive).Build();
        existing.LibraryId = library!.Id;
        ctx.Series.Add(existing);
        await ctx.SaveChangesAsync();

        StrictScanTracker.MarkClaimed(library.Id, existing.Id);

        var info = MakeInfo(series: "Stellar Drift", fullFilePath: "/lib/Stellar Drift/V01.cbz", format: MangaFormat.Archive);
        Assert.Null(await StrictSeriesLookup.FindByNameStrict(unitOfWork, library, info));
    }

    [Fact]
    public async Task FindByNameStrict_TwoRowsShareOriginalName_PrefersNormalizedNameMatch()
    {
        // Regression for the post-split OriginalName collision (Codex finding #3):
        //   row A — Name="Stellar Drift (Intégrale)", OriginalName="Stellar Drift"
        //   row B — Name="Stellar Drift",             OriginalName="Stellar Drift"
        // Both rows match WHERE-clause-wise. Upstream's GetFullSeriesByAnyName uses
        // SingleOrDefaultAsync and throws. FindByNameStrict must return row B (the
        // one whose live Name matches the parser output) deterministically.
        var (unitOfWork, ctx, _) = await CreateDatabase();
        var library = await ctx.Library.FindAsync(1);

        var renamed = new SeriesBuilder("Stellar Drift").WithFormat(MangaFormat.Archive).Build();
        renamed.LibraryId = library!.Id;
        // Simulate a prior strict-fallback rename → post-ProcessSeries state:
        //   Name + NormalizedName follow the new parser output;
        //   LocalizedName + NormalizedLocalizedName are cleared (strict parser doesn't set LocalizedSeries);
        //   OriginalName is left at the pre-rename "Stellar Drift" (the bug we're guarding against).
        renamed.Name = "Stellar Drift (Intégrale)";
        renamed.NormalizedName = renamed.Name.ToNormalized();
        renamed.LocalizedName = string.Empty;
        renamed.NormalizedLocalizedName = string.Empty;
        ctx.Series.Add(renamed);

        var fresh = new SeriesBuilder("Stellar Drift").WithFormat(MangaFormat.Archive).Build();
        fresh.LibraryId = library.Id;
        fresh.LocalizedName = string.Empty;
        fresh.NormalizedLocalizedName = string.Empty;
        ctx.Series.Add(fresh);
        await ctx.SaveChangesAsync();

        var info = MakeInfo(series: "Stellar Drift", fullFilePath: "/lib/Stellar Drift/V01.cbz", format: MangaFormat.Archive);
        var match = await StrictSeriesLookup.FindByNameStrict(unitOfWork, library, info);

        Assert.NotNull(match);
        Assert.Equal(fresh.Id, match!.Id); // NormalizedName match wins over OriginalName-only match
    }

    [Fact]
    public async Task FindByFolderPath_ClaimedSeries_IsSkipped()
    {
        var (unitOfWork, ctx, _) = await CreateDatabase();
        var library = await ctx.Library.FindAsync(1);
        library!.Folders = new System.Collections.Generic.List<FolderPath> { new() { Path = "/lib" } };
        var existing = new SeriesBuilder("Stellar Drift")
            .WithFormat(MangaFormat.Archive)
            .Build();
        existing.FolderPath = "/lib/Stellar Drift (Intégrale)";
        existing.LibraryId = library.Id;
        ctx.Series.Add(existing);
        await ctx.SaveChangesAsync();

        // Mark it claimed by an earlier bucket.
        StrictScanTracker.MarkClaimed(library.Id, existing.Id);

        var info = MakeInfo(series: "Stellar Drift (Intégrale)",
            fullFilePath: "/lib/Stellar Drift (Intégrale)/V01.cbz", format: MangaFormat.Archive);
        var match = await StrictSeriesLookup.FindByFolderPath(unitOfWork, library, info);

        Assert.Null(match);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Basename overlap — folder-rename detector.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FindByFileBasenameOverlap_AllBasenamesMatch_ReturnsExistingSeries()
    {
        var (unitOfWork, ctx, _) = await CreateDatabase();
        var library = await ctx.Library.FindAsync(1);
        library!.Folders = new System.Collections.Generic.List<FolderPath> { new() { Path = "/lib" } };

        var existing = new SeriesBuilder("Quill Lantern")
            .WithFormat(MangaFormat.Archive)
            .Build();
        existing.FolderPath = "/lib/Quill Lantern";
        existing.LibraryId = library.Id;
        var volume = new VolumeBuilder("1").Build();
        var chapter = new ChapterBuilder("1").Build();
        chapter.Files = new System.Collections.Generic.List<MangaFile>
        {
            new() { FilePath = "/lib/Quill Lantern/Quill Lantern T01.cbr", Format = MangaFormat.Archive, Pages = 1 },
            new() { FilePath = "/lib/Quill Lantern/Quill Lantern T02.cbr", Format = MangaFormat.Archive, Pages = 1 },
        };
        volume.Chapters = new System.Collections.Generic.List<Chapter> { chapter };
        existing.Volumes = new System.Collections.Generic.List<Volume> { volume };
        ctx.Series.Add(existing);
        await ctx.SaveChangesAsync();

        // User renamed the folder Quill Lantern → Quill Lantern (Kanzenban). New parsed paths use
        // the new folder name but the basenames are identical.
        var infos = new[]
        {
            MakeInfo("Quill Lantern (Kanzenban)", "/lib/Quill Lantern (Kanzenban)/Quill Lantern T01.cbr", MangaFormat.Archive),
            MakeInfo("Quill Lantern (Kanzenban)", "/lib/Quill Lantern (Kanzenban)/Quill Lantern T02.cbr", MangaFormat.Archive),
        };

        var match = await StrictSeriesLookup.FindByFileBasenameOverlap(unitOfWork, library, infos);

        Assert.NotNull(match);
        Assert.Equal(existing.Id, match!.Id);
        Assert.Equal("Quill Lantern (Kanzenban)", match.Name); // renamed in place
    }

    [Fact]
    public async Task FindByFileBasenameOverlap_BelowThreshold_ReturnsNull()
    {
        var (unitOfWork, ctx, _) = await CreateDatabase();
        var library = await ctx.Library.FindAsync(1);
        library!.Folders = new System.Collections.Generic.List<FolderPath> { new() { Path = "/lib" } };

        var existing = new SeriesBuilder("Quill Lantern")
            .WithFormat(MangaFormat.Archive)
            .Build();
        existing.FolderPath = "/lib/Quill Lantern";
        existing.LibraryId = library.Id;
        var volume = new VolumeBuilder("1").Build();
        var chapter = new ChapterBuilder("1").Build();
        chapter.Files = new System.Collections.Generic.List<MangaFile>
        {
            new() { FilePath = "/lib/Quill Lantern/Quill Lantern T01.cbr", Format = MangaFormat.Archive, Pages = 1 },
        };
        volume.Chapters = new System.Collections.Generic.List<Chapter> { chapter };
        existing.Volumes = new System.Collections.Generic.List<Volume> { volume };
        ctx.Series.Add(existing);
        await ctx.SaveChangesAsync();

        // 1 of 5 = 20% overlap, below the 50% threshold.
        var infos = new[]
        {
            MakeInfo("X", "/lib/X/Quill Lantern T01.cbr", MangaFormat.Archive),
            MakeInfo("X", "/lib/X/Other A.cbr", MangaFormat.Archive),
            MakeInfo("X", "/lib/X/Other B.cbr", MangaFormat.Archive),
            MakeInfo("X", "/lib/X/Other C.cbr", MangaFormat.Archive),
            MakeInfo("X", "/lib/X/Other D.cbr", MangaFormat.Archive),
        };

        Assert.Null(await StrictSeriesLookup.FindByFileBasenameOverlap(unitOfWork, library, infos));
    }

    [Fact]
    public async Task FindByFileBasenameOverlap_ClaimedSeries_IsSkipped()
    {
        var (unitOfWork, ctx, _) = await CreateDatabase();
        var library = await ctx.Library.FindAsync(1);
        library!.Folders = new System.Collections.Generic.List<FolderPath> { new() { Path = "/lib" } };

        var existing = new SeriesBuilder("Quill Lantern")
            .WithFormat(MangaFormat.Archive)
            .Build();
        existing.FolderPath = "/lib/Quill Lantern";
        existing.LibraryId = library.Id;
        var volume = new VolumeBuilder("1").Build();
        var chapter = new ChapterBuilder("1").Build();
        chapter.Files = new System.Collections.Generic.List<MangaFile>
        {
            new() { FilePath = "/lib/Quill Lantern/Quill Lantern T01.cbr", Format = MangaFormat.Archive, Pages = 1 },
        };
        volume.Chapters = new System.Collections.Generic.List<Chapter> { chapter };
        existing.Volumes = new System.Collections.Generic.List<Volume> { volume };
        ctx.Series.Add(existing);
        await ctx.SaveChangesAsync();

        StrictScanTracker.MarkClaimed(library.Id, existing.Id);

        var infos = new[] { MakeInfo("X", "/lib/X/Quill Lantern T01.cbr", MangaFormat.Archive) };
        Assert.Null(await StrictSeriesLookup.FindByFileBasenameOverlap(unitOfWork, library, infos));
    }

    [Fact]
    public async Task FindByFileBasenameOverlap_OldFolderStillScanned_TreatedAsSplit_ReturnsNull()
    {
        // Regression: split-with-shared-basenames case.
        //   - Initial state: a series owns 5 files at /lib/Quill Lantern/*.cbr.
        //   - User adds a *new* sibling folder /lib/Quill Lantern (Kanzenban)/ with
        //     the same chapter basenames (alt-edition collision) but does NOT
        //     delete the original folder.
        //   - Two ParsedSeries buckets exist: one per folder.
        //   - The bucket for /lib/Quill Lantern (Kanzenban)/ runs basename overlap
        //     and matches the existing row (5/5 = 100%). Without the split guard
        //     the row inherits the new folder's files while still keeping the old
        //     ones — and then the /lib/Quill Lantern/ bucket creates a NEW series
        //     for the same on-disk paths, producing duplicate MangaFile rows.
        //   - With the guard: the basename match is skipped because the row's
        //     existing files are themselves being scanned by the other bucket.
        var (unitOfWork, ctx, _) = await CreateDatabase();
        var library = await ctx.Library.FindAsync(1);
        library!.Folders = new System.Collections.Generic.List<FolderPath> { new() { Path = "/lib" } };

        var existing = new SeriesBuilder("Quill Lantern")
            .WithFormat(MangaFormat.Archive)
            .Build();
        existing.FolderPath = "/lib/Quill Lantern";
        existing.LibraryId = library.Id;
        var volume = new VolumeBuilder("1").Build();
        var chapter = new ChapterBuilder("1").Build();
        chapter.Files = new System.Collections.Generic.List<MangaFile>
        {
            new() { FilePath = "/lib/Quill Lantern/Quill Lantern T01.cbr", Format = MangaFormat.Archive, Pages = 1 },
            new() { FilePath = "/lib/Quill Lantern/Quill Lantern T02.cbr", Format = MangaFormat.Archive, Pages = 1 },
            new() { FilePath = "/lib/Quill Lantern/Quill Lantern T03.cbr", Format = MangaFormat.Archive, Pages = 1 },
            new() { FilePath = "/lib/Quill Lantern/Quill Lantern T04.cbr", Format = MangaFormat.Archive, Pages = 1 },
            new() { FilePath = "/lib/Quill Lantern/Quill Lantern T05.cbr", Format = MangaFormat.Archive, Pages = 1 },
        };
        volume.Chapters = new System.Collections.Generic.List<Chapter> { chapter };
        existing.Volumes = new System.Collections.Generic.List<Volume> { volume };
        ctx.Series.Add(existing);
        await ctx.SaveChangesAsync();

        // Simulate the other bucket: register the OLD folder's file paths as also
        // being scanned right now. Any ONE overlap is enough to flag this as a split.
        StrictScanTracker.RegisterScannedPaths(library.Id, new[]
        {
            "/lib/Quill Lantern/Quill Lantern T01.cbr",
            "/lib/Quill Lantern/Quill Lantern T02.cbr",
            "/lib/Quill Lantern/Quill Lantern T03.cbr",
            "/lib/Quill Lantern/Quill Lantern T04.cbr",
            "/lib/Quill Lantern/Quill Lantern T05.cbr",
        });

        // The new-folder bucket's parsedInfos — identical basenames at a different
        // parent path. 100% basename overlap with series 25.
        var infos = new[]
        {
            MakeInfo("Quill Lantern (Kanzenban)", "/lib/Quill Lantern (Kanzenban)/Quill Lantern T01.cbr", MangaFormat.Archive),
            MakeInfo("Quill Lantern (Kanzenban)", "/lib/Quill Lantern (Kanzenban)/Quill Lantern T02.cbr", MangaFormat.Archive),
            MakeInfo("Quill Lantern (Kanzenban)", "/lib/Quill Lantern (Kanzenban)/Quill Lantern T03.cbr", MangaFormat.Archive),
            MakeInfo("Quill Lantern (Kanzenban)", "/lib/Quill Lantern (Kanzenban)/Quill Lantern T04.cbr", MangaFormat.Archive),
            MakeInfo("Quill Lantern (Kanzenban)", "/lib/Quill Lantern (Kanzenban)/Quill Lantern T05.cbr", MangaFormat.Archive),
        };

        Assert.Null(await StrictSeriesLookup.FindByFileBasenameOverlap(unitOfWork, library, infos));
    }

    [Fact]
    public async Task FindByFileBasenameOverlap_OldFolderStillExistsOnDisk_TreatedAsSplit_ReturnsNull()
    {
        // Codex finding #2: the prior "is the file in the active scan?" check
        // misses unchanged-folder splits (the old folder was scan-skipped because
        // it didn't change, so its files aren't in the registered set, so the
        // basename-overlap heuristic falsely treats the new sibling as a rename).
        // Direct Directory.Exists on the candidate's owned-file parent dirs covers
        // the gap.
        var (unitOfWork, ctx, _) = await CreateDatabase();
        var library = await ctx.Library.FindAsync(1);
        library!.Folders = new System.Collections.Generic.List<FolderPath> { new() { Path = "/lib" } };

        var existing = new SeriesBuilder("Quill Lantern").WithFormat(MangaFormat.Archive).Build();
        existing.FolderPath = "/lib/Quill Lantern";
        existing.LibraryId = library.Id;
        var volume = new VolumeBuilder("1").Build();
        var chapter = new ChapterBuilder("1").Build();
        chapter.Files = new System.Collections.Generic.List<MangaFile>
        {
            new() { FilePath = "/lib/Quill Lantern/Quill Lantern T01.cbr", Format = MangaFormat.Archive, Pages = 1 },
            new() { FilePath = "/lib/Quill Lantern/Quill Lantern T02.cbr", Format = MangaFormat.Archive, Pages = 1 },
        };
        volume.Chapters = new System.Collections.Generic.List<Chapter> { chapter };
        existing.Volumes = new System.Collections.Generic.List<Volume> { volume };
        ctx.Series.Add(existing);
        await ctx.SaveChangesAsync();

        var fs = new System.IO.Abstractions.TestingHelpers.MockFileSystem();
        fs.AddDirectory("/lib/Quill Lantern"); // simulates "old folder unchanged but still on disk"
        var directoryService = new DirectoryService(
            NSubstitute.Substitute.For<Microsoft.Extensions.Logging.ILogger<global::Kavita.Services.DirectoryService>>(), fs);

        var infos = new[]
        {
            MakeInfo("Quill Lantern (Kanzenban)", "/lib/Quill Lantern (Kanzenban)/Quill Lantern T01.cbr", MangaFormat.Archive),
            MakeInfo("Quill Lantern (Kanzenban)", "/lib/Quill Lantern (Kanzenban)/Quill Lantern T02.cbr", MangaFormat.Archive),
        };

        // Note: nothing registered in StrictScanTracker on purpose — the disk-existence
        // signal alone must be enough to skip the candidate.
        Assert.Null(await StrictSeriesLookup.FindByFileBasenameOverlap(unitOfWork, library, infos, directoryService));
    }

    [Fact]
    public async Task FindByFileBasenameOverlap_OldFolderGoneFromDisk_RenameMatches()
    {
        // Inverse of above: when the old folder is gone on disk (a true rename),
        // basename-overlap should still match.
        var (unitOfWork, ctx, _) = await CreateDatabase();
        var library = await ctx.Library.FindAsync(1);
        library!.Folders = new System.Collections.Generic.List<FolderPath> { new() { Path = "/lib" } };

        var existing = new SeriesBuilder("Quill Lantern").WithFormat(MangaFormat.Archive).Build();
        existing.FolderPath = "/lib/Quill Lantern";
        existing.LibraryId = library.Id;
        var volume = new VolumeBuilder("1").Build();
        var chapter = new ChapterBuilder("1").Build();
        chapter.Files = new System.Collections.Generic.List<MangaFile>
        {
            new() { FilePath = "/lib/Quill Lantern/Quill Lantern T01.cbr", Format = MangaFormat.Archive, Pages = 1 },
            new() { FilePath = "/lib/Quill Lantern/Quill Lantern T02.cbr", Format = MangaFormat.Archive, Pages = 1 },
        };
        volume.Chapters = new System.Collections.Generic.List<Chapter> { chapter };
        existing.Volumes = new System.Collections.Generic.List<Volume> { volume };
        ctx.Series.Add(existing);
        await ctx.SaveChangesAsync();

        var fs = new System.IO.Abstractions.TestingHelpers.MockFileSystem();
        // Old folder NOT added — gone from disk.
        var directoryService = new DirectoryService(
            NSubstitute.Substitute.For<Microsoft.Extensions.Logging.ILogger<global::Kavita.Services.DirectoryService>>(), fs);

        var infos = new[]
        {
            MakeInfo("Quill Lantern (Kanzenban)", "/lib/Quill Lantern (Kanzenban)/Quill Lantern T01.cbr", MangaFormat.Archive),
            MakeInfo("Quill Lantern (Kanzenban)", "/lib/Quill Lantern (Kanzenban)/Quill Lantern T02.cbr", MangaFormat.Archive),
        };

        var match = await StrictSeriesLookup.FindByFileBasenameOverlap(unitOfWork, library, infos, directoryService);
        Assert.NotNull(match);
        Assert.Equal(existing.Id, match!.Id);
        Assert.Equal("Quill Lantern (Kanzenban)", match.Name);
        Assert.Equal("Quill Lantern (Kanzenban)", match.OriginalName); // RenameAndReturn also aligns OriginalName
    }

    [Fact]
    public async Task FindByFileBasenameOverlap_FormatMismatch_ReturnsNull()
    {
        var (unitOfWork, ctx, _) = await CreateDatabase();
        var library = await ctx.Library.FindAsync(1);
        library!.Folders = new System.Collections.Generic.List<FolderPath> { new() { Path = "/lib" } };

        var existing = new SeriesBuilder("Quill Lantern")
            .WithFormat(MangaFormat.Image)
            .Build();
        existing.FolderPath = "/lib/Quill Lantern";
        existing.LibraryId = library.Id;
        var volume = new VolumeBuilder("1").Build();
        var chapter = new ChapterBuilder("1").Build();
        chapter.Files = new System.Collections.Generic.List<MangaFile>
        {
            new() { FilePath = "/lib/Quill Lantern/Quill Lantern T01.cbr", Format = MangaFormat.Image, Pages = 1 },
        };
        volume.Chapters = new System.Collections.Generic.List<Chapter> { chapter };
        existing.Volumes = new System.Collections.Generic.List<Volume> { volume };
        ctx.Series.Add(existing);
        await ctx.SaveChangesAsync();

        var infos = new[] { MakeInfo("X", "/lib/X/Quill Lantern T01.cbr", MangaFormat.Archive) };
        Assert.Null(await StrictSeriesLookup.FindByFileBasenameOverlap(unitOfWork, library, infos));
    }

    private static ParserInfo MakeInfo(string series, string fullFilePath, MangaFormat format) => new()
    {
        Series = series,
        FullFilePath = fullFilePath,
        Format = format,
    };
}
