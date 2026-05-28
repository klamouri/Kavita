using System.Collections.Generic;
using Kavita.Models.Builders;
using Kavita.Models.Entities;
using Kavita.Models.Entities.Enums;
using Kavita.Models.Parser;
using Kavita.Services.Builders;
using Kavita.Services.Scanner.StrictMode;

namespace Kavita.Services.Tests.Parsers;

/// <summary>
/// Direct unit tests for <see cref="StrictMetadataApplier"/>. The applier is
/// also exercised end-to-end by the docker smoke test, but the live path is
/// hard to assert against — these tests cover the per-field copy and the
/// auto-lock contract in isolation.
///
/// <para>Folder-level fields (year, language, publicationStatus) are
/// re-extracted by the applier from the series folder name. Tests vary the
/// folder name to exercise different token combinations.</para>
/// </summary>
public class StrictMetadataApplierTests
{
    private const string LibRoot = "/lib";

    [Fact]
    public void Apply_NoInfos_DoesNothing_ButInitializesMetadata()
    {
        var series = NewSeries();
        StrictMetadataApplier.Apply(series, new List<ParserInfo>(), NewLibrary());

        Assert.NotNull(series.Metadata);
        Assert.False(series.Metadata.ReleaseYearLocked);
        Assert.Equal(0, series.AniListId);
    }

    [Fact]
    public void Apply_PreservesExistingMetadataInstance()
    {
        var series = NewSeries();
        series.Metadata.ReleaseYear = 1999;
        var metadata = series.Metadata;

        StrictMetadataApplier.Apply(series, new List<ParserInfo>(), NewLibrary());

        Assert.Same(metadata, series.Metadata);
        Assert.Equal(1999, series.Metadata.ReleaseYear);
    }

    [Fact]
    public void Apply_FolderTrailingYear_SetsReleaseYearAndLocksIt()
    {
        var series = NewSeries();
        var infos = new[] { InfoIn("Series Alpha (2020)") };

        StrictMetadataApplier.Apply(series, infos, NewLibrary());

        Assert.Equal(2020, series.Metadata.ReleaseYear);
        Assert.True(series.Metadata.ReleaseYearLocked);
    }

    [Fact]
    public void Apply_NoYearInFolder_LeavesReleaseYearUntouchedAndUnlocked()
    {
        var series = NewSeries();
        var infos = new[] { InfoIn("Series Alpha") };

        StrictMetadataApplier.Apply(series, infos, NewLibrary());

        Assert.Equal(0, series.Metadata.ReleaseYear);
        Assert.False(series.Metadata.ReleaseYearLocked);
    }

    [Fact]
    public void Apply_CopiesEveryRecognizedExternalId()
    {
        var series = NewSeries();
        var infos = new[]
        {
            InfoIn("Series Alpha {hardcoverId-90015}",
                anilist: 90013, mal: 90014L, metron: 90016L,
                comicVine: "cv-50", mangaBaka: 90018),
        };

        StrictMetadataApplier.Apply(series, infos, NewLibrary());

        Assert.Equal(90013, series.AniListId);
        Assert.Equal(90014L, series.MalId);
        Assert.Equal(90015, series.HardcoverId);
        Assert.Equal(90016L, series.MetronId);
        Assert.Equal("cv-50", series.ComicVineId);
        Assert.Equal(90018, series.MangaBakaId);
    }

    [Fact]
    public void Apply_ComicVineSeriesId_TakesPriorityOverComicVineId()
    {
        var series = NewSeries();
        var infos = new[] { InfoIn("Series Alpha", comicVine: "cv-issue-50", comicVineSeries: "cv-series-7") };

        StrictMetadataApplier.Apply(series, infos, NewLibrary());

        Assert.Equal("cv-series-7", series.ComicVineId);
    }

    [Fact]
    public void Apply_FallsBackToComicVineIdWhenSeriesIdMissing()
    {
        var series = NewSeries();
        var infos = new[] { InfoIn("Series Alpha", comicVine: "cv-issue-50") };

        StrictMetadataApplier.Apply(series, infos, NewLibrary());

        Assert.Equal("cv-issue-50", series.ComicVineId);
    }

    [Fact]
    public void Apply_SkipsZeroAndNullExternalIds()
    {
        var series = NewSeries();
        var infos = new[] { InfoIn("Series Alpha", mal: 0L, comicVine: string.Empty) };

        StrictMetadataApplier.Apply(series, infos, NewLibrary());

        Assert.Equal(0, series.AniListId);
        Assert.Equal(0L, series.MalId);
        Assert.Equal(0, series.HardcoverId);
        Assert.Null(series.ComicVineId);
    }

    [Fact]
    public void Apply_PicksFirstPositiveAcrossInfos_PerField()
    {
        // FirstPositive must skip nulls/zeros and surface the first non-zero per field.
        var series = NewSeries();
        var infos = new[]
        {
            InfoIn("Series Alpha", fileName: "V01 {hardcoverId-11}.cbz"),
            InfoIn("Series Alpha", anilist: 22),
            InfoIn("Series Alpha", mal: 33L),
            InfoIn("Series Alpha", anilist: 44), // ignored: 22 already wins
        };

        StrictMetadataApplier.Apply(series, infos, NewLibrary());

        Assert.Equal(22, series.AniListId);
        Assert.Equal(33L, series.MalId);
        Assert.Equal(11, series.HardcoverId);
    }

    [Fact]
    public void Apply_ParserInfoHardcoverId_IsBookId_NotCopiedToSeries()
    {
        // Since Kavita v0.9.1, ParserInfo.HardcoverId carries a Hardcover *book* id from
        // ComicInfo Web links. Only the {hardcoverId-…} token (a series id) may land on Series.
        var series = NewSeries();
        series.HardcoverId = 777; // set by upstream ProcessSeries from a Hardcover series link
        var infos = new[] { InfoIn("Series Alpha", hardcover: 12345) };

        StrictMetadataApplier.Apply(series, infos, NewLibrary());

        Assert.Equal(777, series.HardcoverId);
    }

    [Fact]
    public void Apply_FileHardcoverToken_WinsOverFolderToken()
    {
        var series = NewSeries();
        var infos = new[]
        {
            InfoIn("Series Alpha {hardcoverId-100}", fileName: "V01 {hardcoverId-200}.cbz", hardcover: 999),
        };

        StrictMetadataApplier.Apply(series, infos, NewLibrary());

        Assert.Equal(200, series.HardcoverId);
    }

    [Fact]
    public void Apply_FirstParsedFileDecidesHardcoverId_FolderTokenCanBeatLaterFileToken()
    {
        // Precedence is per parsed file (file token, else folder token); the first file that
        // resolves to a positive id wins for the series.
        var series = NewSeries();
        var infos = new[]
        {
            InfoIn("Series Alpha {hardcoverId-100}", fileName: "V01.cbz"),
            InfoIn("Series Alpha {hardcoverId-100}", fileName: "V02 {hardcoverId-200}.cbz"),
        };

        StrictMetadataApplier.Apply(series, infos, NewLibrary());

        Assert.Equal(100, series.HardcoverId);
    }

    [Fact]
    public void Apply_HardcoverToken_ClearsStandAloneFlag()
    {
        // Kavita+ matching can flag a series as a stand-alone book (HardcoverId = book id).
        // The token is a series id, so the flag must not survive it.
        var series = NewSeries();
        series.IsStandAlone = true;
        series.HardcoverId = 4242;
        var infos = new[] { InfoIn("Series Alpha {hardcoverId-100}") };

        StrictMetadataApplier.Apply(series, infos, NewLibrary());

        Assert.Equal(100, series.HardcoverId);
        Assert.False(series.IsStandAlone);
    }

    [Fact]
    public void Apply_NoHardcoverToken_LeavesStandAloneAndChapterIdsAlone()
    {
        var series = NewSeries();
        series.IsStandAlone = true;
        var chapter = ChapterWithFile("1", $"{LibRoot}/Series Alpha/V01.cbz", hardcoverId: 100);
        series.Volumes = new List<Volume> { new VolumeBuilder("1").WithChapter(chapter).Build() };

        StrictMetadataApplier.Apply(series, new[] { InfoIn("Series Alpha") }, NewLibrary());

        Assert.True(series.IsStandAlone);
        Assert.Equal(100, chapter.HardcoverId);
    }

    [Fact]
    public void Apply_ClearsLegacyChapterHardcoverIds_CopiedFromSeriesToken()
    {
        // Pre-v0.9.1.4 fork builds wrote the series token onto Chapter.HardcoverId, which v0.9.1+
        // scrobbles as a book id. Upstream never clears it, so the applier does.
        const string folder = "Series Alpha {hardcoverId-100}";
        var legacy = ChapterWithFile("1", $"{LibRoot}/{folder}/V01.cbz", hardcoverId: 100);
        var parsedBook = ChapterWithFile("2", $"{LibRoot}/{folder}/V02.cbz", hardcoverId: 100);
        var realBook = ChapterWithFile("3", $"{LibRoot}/{folder}/V03.cbz", hardcoverId: 555);
        var series = NewSeries();
        series.Volumes = new List<Volume>
        {
            new VolumeBuilder("1").WithChapter(legacy).WithChapter(parsedBook).WithChapter(realBook).Build(),
        };
        var infos = new[]
        {
            InfoIn(folder, fileName: "V01.cbz"),
            InfoIn(folder, fileName: "V02.cbz", hardcover: 100), // ComicInfo book id that happens to match
            InfoIn(folder, fileName: "V03.cbz", hardcover: 555),
        };

        StrictMetadataApplier.Apply(series, infos, NewLibrary());

        Assert.Equal(0, legacy.HardcoverId);
        Assert.Equal(100, parsedBook.HardcoverId);
        Assert.Equal(555, realBook.HardcoverId);
    }

    [Fact]
    public void ReleaseTokenOwnedLocks_StatusToken_UnlocksSoUpstreamRecomputesCounts_ApplyRelocks()
    {
        // Kavita v0.9.1+ skips DeterminePublicationStatus (MaxCount/TotalCount) while the
        // status is locked. A status token must not freeze the counts across scans.
        var series = NewSeries();
        series.Metadata.PublicationStatus = PublicationStatus.OnGoing;
        series.Metadata.PublicationStatusLocked = true; // locked by the previous scan
        var infos = new[] { InfoIn("Series Alpha {publicationStatus-ongoing}") };

        StrictMetadataApplier.ReleaseTokenOwnedLocks(series, infos, NewLibrary());
        Assert.False(series.Metadata.PublicationStatusLocked);

        // Upstream's DeterminePublicationStatus runs here and may pick a different status.
        series.Metadata.PublicationStatus = PublicationStatus.Completed;

        StrictMetadataApplier.Apply(series, infos, NewLibrary());
        Assert.Equal(PublicationStatus.OnGoing, series.Metadata.PublicationStatus);
        Assert.True(series.Metadata.PublicationStatusLocked);
    }

    [Theory]
    [InlineData("Series Alpha")]
    [InlineData("Series Alpha {publicationStatus-bogus}")]
    public void ReleaseTokenOwnedLocks_NoValidStatusToken_KeepsUserLock(string folderName)
    {
        var series = NewSeries();
        series.Metadata.PublicationStatusLocked = true; // user lock
        var infos = new[] { InfoIn(folderName) };

        StrictMetadataApplier.ReleaseTokenOwnedLocks(series, infos, NewLibrary());

        Assert.True(series.Metadata.PublicationStatusLocked);
    }

    [Fact]
    public void Apply_FolderLanguageToken_SetsMetadataAndLocks()
    {
        var series = NewSeries();
        var infos = new[] { InfoIn("Series Alpha {language-fr}") };

        StrictMetadataApplier.Apply(series, infos, NewLibrary());

        Assert.Equal("fr", series.Metadata.Language);
        Assert.True(series.Metadata.LanguageLocked);
    }

    [Fact]
    public void Apply_NoLanguageToken_DoesNotTouchMetadata()
    {
        var series = NewSeries();
        series.Metadata.Language = "de";
        var infos = new[] { InfoIn("Series Alpha") };

        StrictMetadataApplier.Apply(series, infos, NewLibrary());

        Assert.Equal("de", series.Metadata.Language);
        Assert.False(series.Metadata.LanguageLocked);
    }

    [Fact]
    public void Apply_FolderPublicationStatusToken_SetsMetadataAndLocks()
    {
        var series = NewSeries();
        var infos = new[] { InfoIn("Series Alpha {publicationStatus-completed}") };

        StrictMetadataApplier.Apply(series, infos, NewLibrary());

        Assert.Equal(PublicationStatus.Completed, series.Metadata.PublicationStatus);
        Assert.True(series.Metadata.PublicationStatusLocked);
    }

    [Fact]
    public void Apply_NoPublicationStatusToken_DoesNotTouchMetadata()
    {
        var series = NewSeries();
        series.Metadata.PublicationStatus = PublicationStatus.Ended;
        var infos = new[] { InfoIn("Series Alpha") };

        StrictMetadataApplier.Apply(series, infos, NewLibrary());

        Assert.Equal(PublicationStatus.Ended, series.Metadata.PublicationStatus);
        Assert.False(series.Metadata.PublicationStatusLocked);
    }

    [Fact]
    public void Apply_InvalidPublicationStatus_SilentlyDropped()
    {
        var series = NewSeries();
        var infos = new[] { InfoIn("Series Alpha {publicationStatus-bogus}") };

        StrictMetadataApplier.Apply(series, infos, NewLibrary());

        Assert.False(series.Metadata.PublicationStatusLocked);
    }

    [Fact]
    public void Apply_FileLevelLanguageToken_IsIgnored_FolderOnlyContract()
    {
        // File-level {language-...} on the filename is folder-only by the applier's
        // contract; per-chapter language has to come from ComicInfo.xml.
        var series = NewSeries();
        var infos = new[] { InfoIn("Series Alpha", fileName: "Series Alpha - V01 {language-fr}.cbz") };

        StrictMetadataApplier.Apply(series, infos, NewLibrary());

        Assert.False(series.Metadata.LanguageLocked);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────────────────────────

    private static Series NewSeries() => new SeriesBuilder("Test Series").Build();

    private static Chapter ChapterWithFile(string number, string filePath, int hardcoverId)
    {
        var chapter = new ChapterBuilder(number)
            .WithFile(new MangaFileBuilder(filePath, MangaFormat.Archive).Build())
            .Build();
        chapter.HardcoverId = hardcoverId;
        return chapter;
    }

    private static Library NewLibrary() => new()
    {
        Name = "Test Library",
        Folders = new List<FolderPath> { new() { Path = LibRoot } },
    };

    private static ParserInfo InfoIn(
        string seriesFolderName,
        string fileName = "V01.cbz",
        int? anilist = null,
        long? mal = null,
        int? hardcover = null,
        long? metron = null,
        string? comicVine = null,
        string? comicVineSeries = null,
        int? mangaBaka = null) => new()
        {
            Series = "Test Series",
            FullFilePath = $"{LibRoot}/{seriesFolderName}/{fileName}",
            AniListId = anilist,
            MalId = mal,
            HardcoverId = hardcover,
            MetronId = metron,
            ComicVineId = comicVine,
            ComicVineSeriesId = comicVineSeries,
            MangaBakaId = mangaBaka,
        };
}
