using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions.TestingHelpers;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kavita.Database.Tests;
using Kavita.Models.Builders;
using Kavita.Models.Entities;
using Kavita.Models.Entities.Enums;
using Kavita.Models.Parser;
using Kavita.Services.Scanner;
using Kavita.Services.Scanner.StrictMode;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Kavita.Services.Tests.Parsers;

/// <summary>
/// Library-shape tests for the Strict-mode parser. The fixture is the synthetic
/// catalog at <c>Test Data/StrictParser/catalog.json</c> (22 series, ~90 file
/// entries), each entry materialized into an in-memory <see cref="MockFileSystem"/>.
///
/// <para>The same catalog drives the on-disk fixture renderer
/// (<c>scripts/generate-test-library.py</c>), so these unit tests exercise the
/// exact tree the smoke test scans through Docker. To add a new case, hand-edit
/// <c>catalog.json</c> and the test fixture picks it up on the next run.</para>
/// </summary>
public class StrictParserLibraryFixtureTests : AbstractFsTest
{
    private static string CatalogPath =>
        Path.Combine(AppContext.BaseDirectory, "Test Data", "StrictParser", "catalog.json");

    private readonly Catalog _catalog;
    private readonly StrictParser _parser;
    private readonly MockFileSystem _fs;
    private readonly string _libraryRoot;

    public StrictParserLibraryFixtureTests()
    {
        _catalog = LoadCatalog();
        _fs = CreateFileSystem();
        _libraryRoot = EnsureTrailingSlash(Path.Join(DataDirectory, "manga"));
        _fs.AddDirectory(_libraryRoot);

        // Materialize the catalog as an in-memory file tree. For cbz entries, add
        // the cbz itself. For loose-images-folder entries, add a representative
        // 01.png inside the folder so the parser sees a real path under it.
        foreach (var series in _catalog.Series)
        {
            foreach (var file in series.Files)
            {
                var path = file.Format == "cbz"
                    ? MockPath(file.RelativePath)
                    : MockPath(file.RelativePath, "01.png");
                _fs.AddFile(path, new MockFileData(""));
            }
        }

        var ds = new DirectoryService(Substitute.For<ILogger<DirectoryService>>(), _fs);
        _parser = new StrictParser(ds, new ImageParser(ds));
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Catalog wiring — make sure the bridge JSON ships with the test binary
    //  and has the shape the rest of the suite assumes.
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Catalog_IsCopiedToTestOutputDirectory()
    {
        Assert.True(File.Exists(CatalogPath),
            $"catalog.json not found at {CatalogPath}. Did the csproj <None Update=…CopyToOutputDirectory> entry get removed?");
    }

    [Fact]
    public void Catalog_HasExpectedShape()
    {
        Assert.Equal(1, _catalog.SchemaVersion);
        Assert.Equal(23, _catalog.Series.Count);

        var allFiles = _catalog.Series.SelectMany(s => s.Files).ToList();
        Assert.True(allFiles.Count >= 80,
            $"Expected at least 80 file entries; found {allFiles.Count}.");

        foreach (var file in allFiles)
        {
            Assert.Contains(file.Format, new[] { "cbz", "loose-images-folder" });
        }

        // Iron Garden editions share their cover_color but have distinct circle_colors.
        var ironGarden = _catalog.Series.Where(s => s.Slug.StartsWith("iron-garden-")).ToList();
        Assert.Equal(3, ironGarden.Count);
        Assert.Single(ironGarden.Select(s => string.Join(",", s.CoverColor)).Distinct());
        Assert.Equal(3, ironGarden.Select(s => string.Join(",", s.CircleColor!)).Distinct().Count());
    }

    [Fact]
    public void StrictParser_HandlesEveryCatalogEntry_WithoutThrowing()
    {
        // Smoke test: every declared path is parsed exactly once and the parser
        // either returns a ParserInfo or null. Either is acceptable — the point is
        // that adding a new catalog entry can never silently break parsing.
        var parsed = 0;
        foreach (var f in _fs.AllFiles.Select(NormalizeMockPath))
        {
            var dir = DirectoryOf(f);
            _ = _parser.Parse(f, dir, _libraryRoot, LibraryType.Manga);
            parsed++;
        }
        Assert.Equal(_fs.AllFiles.Count(), parsed);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Library-shape regressions — each test names the catalog slugs it covers,
    //  parses every file inside (recursive, so Specials/ subfolders are included),
    //  and asserts on the result.
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Iron Garden ships as three sibling folders (<c>Iron Garden (Tankobon)</c>,
    /// <c>Iron Garden (Kanzenban)</c>, <c>Iron Garden (Color)</c>) whose cbz
    /// files share the literal prefix <c>"Iron Garden - V0X (year).cbz"</c>.
    /// The parser must use folder name as series identity — a prefix shortcut
    /// would collapse all 9 cbz into one series and break edition separation.
    /// </summary>
    [Fact]
    public void IronGarden_ThreeEditions_ProduceThreeDistinctSeries()
    {
        var infos = ParseUnder("iron-garden-tankobon", "iron-garden-kanzenban", "iron-garden-color");
        var series = infos.Select(i => i.Series).Distinct().ToList();

        Assert.Equal(3, series.Count);
        Assert.Contains("Iron Garden (Tankobon)", series);
        Assert.Contains("Iron Garden (Kanzenban)", series);
        Assert.Contains("Iron Garden (Color)", series);

        foreach (var name in series)
        {
            Assert.Equal(3, infos.Count(i => i.Series == name));
        }
    }

    [Fact]
    public void StellarDrift_OneSeriesWithSpecialsSubfolder()
    {
        var infos = ParseUnder("stellar-drift");
        var series = infos.Select(i => i.Series).Distinct().ToList();

        Assert.Single(series);
        Assert.Equal("Stellar Drift", series[0]);
        Assert.Equal(2, infos.Count(i => i.IsSpecial));
        Assert.Equal(3, infos.Count(i => !i.IsSpecial));
    }

    [Fact]
    public void WanderingLantern_MixesVolumesAndLooseChaptersInOneSeries()
    {
        var infos = ParseUnder("wandering-lantern");
        var series = infos.Select(i => i.Series).Distinct().ToList();

        Assert.Single(series);
        Assert.Equal("Wandering Lantern", series[0]);

        var volumes = infos.Where(i => i.Volumes != Parser.LooseLeafVolume).Select(i => i.Volumes).Distinct().ToList();
        var chapters = infos.Where(i => i.Chapters != Parser.DefaultChapter).Select(i => i.Chapters).Distinct().ToList();
        Assert.Equal(3, volumes.Count);
        Assert.Equal(3, chapters.Count);
    }

    [Fact]
    public void CrimsonVeil_NamedV00Prequel_LandsInSameSeries()
    {
        var infos = ParseUnder("crimson-veil");
        var series = infos.Select(i => i.Series).Distinct().ToList();

        Assert.Single(series);
        Assert.Equal("Crimson Veil", series[0]);
        Assert.Equal(6, infos.Count);
        // V00 is the named prequel; normalized via RemoveLeadingZeroes ("00" → "0").
        Assert.Contains(infos, i => i.Volumes == "0");
    }

    /// <summary>
    /// Quiet Engine is the "fully tokenized" catalog entry — folder carries one
    /// token of every recognized external-ID kind, every cbz carries an
    /// <c>{extraOriginalFilename-…}</c>. The parser must apply the recognized
    /// tokens to <see cref="ParserInfo"/> and silently drop the extra ones.
    /// </summary>
    [Fact]
    public void QuietEngine_AppliesEveryRecognizedTokenKind_AndDropsExtraOriginalFilename()
    {
        var infos = ParseUnder("quiet-engine");

        Assert.NotEmpty(infos);
        foreach (var info in infos)
        {
            Assert.Equal("Quiet Engine", info.Series);
            Assert.Equal(90013, info.AniListId);
            Assert.Equal(90013L, info.MalId);
            Assert.False(info.HardcoverId > 0); // series id: applied to Series only, never to ParserInfo/Chapter
            Assert.Equal(90213L, info.MetronId);
            Assert.Equal("cv-quiet-engine", info.ComicVineSeriesId);
            Assert.Equal(90313, info.MangaBakaId);
        }

        var series = new SeriesBuilder("Test").Build();
        StrictMetadataApplier.Apply(series, infos, new Library
        {
            Name = "Test",
            Folders = new List<FolderPath> { new() { Path = _libraryRoot } },
        });
        Assert.Equal(90113, series.HardcoverId);
    }

    /// <summary>
    /// Crimson Veil's folder carries <c>{language-en} {publicationStatus-ongoing}</c>.
    /// These are folder-only tokens — re-extracted by <see cref="StrictMetadataApplier"/>
    /// from the series folder name at apply time, not stored on <see cref="ParserInfo"/>.
    /// </summary>
    [Fact]
    public void CrimsonVeil_FolderLanguageAndStatus_LandOnSeriesMetadata()
    {
        AssertFolderTokensApplied("crimson-veil", language: "en", status: PublicationStatus.OnGoing);
    }

    [Fact]
    public void IronGardenKanzenban_FolderTokens_LandOnSeriesMetadata()
    {
        AssertFolderTokensApplied("iron-garden-kanzenban", language: "ja", status: PublicationStatus.Hiatus);
    }

    [Fact]
    public void HollowSparrow_FolderStatusOnly_LandsOnSeriesMetadata()
    {
        AssertFolderTokensApplied("hollow-sparrow", language: null, status: PublicationStatus.Completed);
    }

    private void AssertFolderTokensApplied(string slug, string? language, PublicationStatus? status)
    {
        var infos = ParseUnder(slug);
        Assert.NotEmpty(infos);

        var series = new SeriesBuilder("Test").Build();
        var library = new Library
        {
            Name = "Test",
            Folders = new List<FolderPath> { new() { Path = _libraryRoot } },
        };
        StrictMetadataApplier.Apply(series, infos, library);

        if (language is not null)
        {
            Assert.Equal(language, series.Metadata.Language);
            Assert.True(series.Metadata.LanguageLocked);
        }
        else
        {
            Assert.False(series.Metadata.LanguageLocked);
        }

        if (status is not null)
        {
            Assert.Equal(status.Value, series.Metadata.PublicationStatus);
            Assert.True(series.Metadata.PublicationStatusLocked);
        }
        else
        {
            Assert.False(series.Metadata.PublicationStatusLocked);
        }
    }

    /// <summary>
    /// Naked Tokens has a folder name that is *only* a year + tokens
    /// (<c>(2020) {aniListId-90099}/V01.cbz</c>). After peeling tokens and the
    /// trailing year the series name strips to empty — the parser must reject
    /// with a "no name after peeling tokens/year" reason rather than ship a
    /// blank-name series.
    /// </summary>
    [Fact]
    public void NakedTokens_FolderPeelsToEmpty_IsRejectedWithReason()
    {
        var infos = ParseUnder("naked-tokens");
        Assert.Empty(infos);

        var nakedFile = _fs.AllFiles.Single(f => f.Contains("{aniListId-90099}"));
        nakedFile = NormalizeMockPath(nakedFile);
        var dir = DirectoryOf(nakedFile);
        var detailed = _parser.ParseDetailed(nakedFile, dir, _libraryRoot, LibraryType.Manga);

        Assert.Null(detailed.Info);
        Assert.Contains("no name after peeling tokens/year", detailed.Reason);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses every file under any of the catalog series identified by slug.
    /// Returns only non-null <see cref="ParserInfo"/>s — rejected files are dropped.
    /// </summary>
    private List<ParserInfo> ParseUnder(params string[] slugs)
    {
        var folders = _catalog.Series
            .Where(s => slugs.Contains(s.Slug))
            .SelectMany(s => s.Files.Select(f => f.RelativePath.Split('/')[0]))
            .Distinct()
            .ToList();

        var prefixes = folders.Select(f => MockPath(f, "")).ToList();
        return _fs.AllFiles
            .Select(NormalizeMockPath)
            .Where(f => prefixes.Any(p => f.StartsWith(p, StringComparison.Ordinal)))
            .Select(f =>
            {
                var dir = DirectoryOf(f);
                return _parser.Parse(f, dir, _libraryRoot, LibraryType.Manga);
            })
            .Where(i => i != null)
            .Cast<ParserInfo>()
            .ToList();
    }

    private string MockPath(params string[] parts)
    {
        var suffix = string.Join("/", parts).TrimStart('/');
        return NormalizeMockPath($"{_libraryRoot}{suffix}");
    }

    private static string DirectoryOf(string filePath)
    {
        var normalized = NormalizeMockPath(filePath);
        var lastSlash = normalized.LastIndexOf('/');
        return lastSlash < 0 ? "" : normalized[..(lastSlash + 1)];
    }

    private static string EnsureTrailingSlash(string path)
    {
        var normalized = NormalizeMockPath(path);
        return normalized.EndsWith('/') ? normalized : normalized + "/";
    }

    private static string NormalizeMockPath(string path)
    {
        return path.Replace('\\', '/');
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Catalog DTOs — kept inline so adding a field is a one-place change.
    // ─────────────────────────────────────────────────────────────────────────────

    private static Catalog LoadCatalog()
    {
        var json = File.ReadAllText(CatalogPath);
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        return JsonSerializer.Deserialize<Catalog>(json, options)
            ?? throw new InvalidOperationException("catalog.json deserialized to null");
    }

    private sealed class Catalog
    {
        public int SchemaVersion { get; set; }
        public List<SeriesEntry> Series { get; set; } = new();
    }

    private sealed class SeriesEntry
    {
        public string Slug { get; set; } = "";
        public string Name { get; set; } = "";
        public List<int> CoverColor { get; set; } = new();
        public List<int>? CircleColor { get; set; }
        [JsonPropertyName("anilist_id")] public int AnilistId { get; set; }
        [JsonPropertyName("mal_id")] public long MalId { get; set; }
        public int Year { get; set; }
        public List<FileEntry> Files { get; set; } = new();
    }

    private sealed class FileEntry
    {
        public string RelativePath { get; set; } = "";
        public string Format { get; set; } = "";
    }
}
