using System.Collections.Generic;
using Kavita.Models.Builders;
using Kavita.Models.Entities;
using Kavita.Models.Entities.Enums;
using Kavita.Models.Parser;
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
            InfoIn("Series Alpha",
                anilist: 90013, mal: 90014L, hardcover: 90015, metron: 90016L,
                comicVine: "cv-50", mangaBaka: 90018L),
        };

        StrictMetadataApplier.Apply(series, infos, NewLibrary());

        Assert.Equal(90013, series.AniListId);
        Assert.Equal(90014L, series.MalId);
        Assert.Equal(90015, series.HardcoverId);
        Assert.Equal(90016L, series.MetronId);
        Assert.Equal("cv-50", series.ComicVineId);
        Assert.Equal(90018L, series.MangaBakaId);
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
            InfoIn("Series Alpha", hardcover: 11),
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
        long? mangaBaka = null) => new()
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
