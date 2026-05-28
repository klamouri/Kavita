using System.IO.Abstractions.TestingHelpers;
using Kavita.API.Services;
using Kavita.Database.Tests;
using Kavita.Models.Entities.Enums;
using Kavita.Services.Scanner;
using Kavita.Services.Scanner.StrictMode;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Kavita.Services.Tests.Parsers;

public class StrictParserTests : AbstractFsTest
{
    private readonly StrictParser _parser;
    private readonly string _libraryRoot;

    public StrictParserTests()
    {
        var fs = CreateFileSystem();
        _libraryRoot = Path.Join(DataDirectory, "Manga/");
        fs.AddDirectory(_libraryRoot);

        // Folder-as-series — two editions of the same source franchise must NOT merge.
        fs.AddFile($"{_libraryRoot}Iron Garden (Kanzenban) (2020)/V01.cbz", new MockFileData(""));
        fs.AddFile($"{_libraryRoot}Iron Garden (Kanzenban) (2020)/V02.cbz", new MockFileData(""));
        fs.AddFile($"{_libraryRoot}Iron Garden (Tankobon)/V01.cbz", new MockFileData(""));

        // Real-world failure mode from the spec doc: T08 must group with the rest of the volumes.
        fs.AddFile($"{_libraryRoot}Frostline Saga/T01.cbz", new MockFileData(""));
        fs.AddFile($"{_libraryRoot}Frostline Saga/T08.cbz", new MockFileData(""));
        fs.AddFile($"{_libraryRoot}Frostline Saga/T11.cbz", new MockFileData(""));

        // Specials folder
        fs.AddFile($"{_libraryRoot}Quill Lantern/V01.cbz", new MockFileData(""));
        fs.AddFile($"{_libraryRoot}Quill Lantern/Specials/SP1 - Color Edition.cbz", new MockFileData(""));

        // Token-bearing series + file
        fs.AddFile($"{_libraryRoot}Brass Lantern {{aniListId-30002}} {{malId-2}}/V01.cbz", new MockFileData(""));
        fs.AddFile($"{_libraryRoot}Brass Lantern {{aniListId-30002}} {{malId-2}}/C50 {{comicVineId-cv-50}}.cbz", new MockFileData(""));

        // File loose at library root — should be rejected.
        fs.AddFile($"{_libraryRoot}LooseFile.cbz", new MockFileData(""));

        // Long-form kind tokens — spaced + spaceless variants.
        fs.AddFile($"{_libraryRoot}Moonlit Tavern/Volume 1.cbz", new MockFileData(""));
        fs.AddFile($"{_libraryRoot}Moonlit Tavern/Volume 2.cbz", new MockFileData(""));
        fs.AddFile($"{_libraryRoot}Long Form Spaceless/Volume3.cbz", new MockFileData(""));
        fs.AddFile($"{_libraryRoot}Long Form Tome/Tome 5.cbz", new MockFileData(""));
        fs.AddFile($"{_libraryRoot}Long Form Chapter/Chapter 7.cbz", new MockFileData(""));

        // Dotted scene-style filename — kind token surrounded by dots, level-1 only.
        fs.AddFile($"{_libraryRoot}Driftgrade/Driftgrade.T01.FRENCH.CBZ.eBook-Paprika+.cbz", new MockFileData(""));

        var ds = new DirectoryService(Substitute.For<ILogger<DirectoryService>>(), fs);
        _parser = new StrictParser(ds, new ImageParser(ds));
    }

    [Fact]
    public void DragonBallEditions_AreSeparateSeries()
    {
        var a = _parser.Parse($"{_libraryRoot}Iron Garden (Kanzenban) (2020)/V01.cbz",
            $"{_libraryRoot}Iron Garden (Kanzenban) (2020)/", _libraryRoot, LibraryType.Manga);
        var b = _parser.Parse($"{_libraryRoot}Iron Garden (Tankobon)/V01.cbz",
            $"{_libraryRoot}Iron Garden (Tankobon)/", _libraryRoot, LibraryType.Manga);

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal("Iron Garden (Kanzenban)", a!.Series);
        Assert.Equal(2020, a.SeriesReleaseYear);
        Assert.Equal("Iron Garden (Tankobon)", b!.Series);
        Assert.Equal(0, b.SeriesReleaseYear);
        Assert.NotEqual(a.Series, b.Series);
    }

    [Fact]
    public void DashSeparatedSeries_T08_ParsesAsVolume8OfSameSeries()
    {
        var t01 = _parser.Parse($"{_libraryRoot}Frostline Saga/T01.cbz",
            $"{_libraryRoot}Frostline Saga/", _libraryRoot, LibraryType.Manga);
        var t08 = _parser.Parse($"{_libraryRoot}Frostline Saga/T08.cbz",
            $"{_libraryRoot}Frostline Saga/", _libraryRoot, LibraryType.Manga);
        var t11 = _parser.Parse($"{_libraryRoot}Frostline Saga/T11.cbz",
            $"{_libraryRoot}Frostline Saga/", _libraryRoot, LibraryType.Manga);

        Assert.NotNull(t01);
        Assert.NotNull(t08);
        Assert.NotNull(t11);
        Assert.Equal("Frostline Saga", t01!.Series);
        Assert.Equal("Frostline Saga", t08!.Series);
        Assert.Equal("Frostline Saga", t11!.Series);
        // Volume numbers are normalized via Parser.RemoveLeadingZeroes to match
        // upstream's canonical form (no leading zeros) so Volume.LookupName diffs
        // don't trigger Volume row replacement on cross-parser scans.
        Assert.Equal("1", t01.Volumes);
        Assert.Equal("8", t08.Volumes);
        Assert.Equal("11", t11.Volumes);
    }

    [Fact]
    public void SpecialsFolder_MarksFileAsSpecial()
    {
        var sp = _parser.Parse($"{_libraryRoot}Quill Lantern/Specials/SP1 - Color Edition.cbz",
            $"{_libraryRoot}Quill Lantern/Specials/", _libraryRoot, LibraryType.Manga);

        Assert.NotNull(sp);
        Assert.True(sp!.IsSpecial);
        Assert.Equal("Quill Lantern", sp.Series);
    }

    [Fact]
    public void FolderTokens_ApplyToParserInfo()
    {
        var info = _parser.Parse($"{_libraryRoot}Brass Lantern {{aniListId-30002}} {{malId-2}}/V01.cbz",
            $"{_libraryRoot}Brass Lantern {{aniListId-30002}} {{malId-2}}/", _libraryRoot, LibraryType.Manga);

        Assert.NotNull(info);
        Assert.Equal("Brass Lantern", info!.Series);
        Assert.Equal(30002, info.AniListId);
        Assert.Equal(2L, info.MalId);
    }

    [Fact]
    public void FileTokens_OverrideFolderTokens()
    {
        var info = _parser.Parse($"{_libraryRoot}Brass Lantern {{aniListId-30002}} {{malId-2}}/C50 {{comicVineId-cv-50}}.cbz",
            $"{_libraryRoot}Brass Lantern {{aniListId-30002}} {{malId-2}}/", _libraryRoot, LibraryType.Manga);

        Assert.NotNull(info);
        Assert.Equal("Brass Lantern", info!.Series);
        Assert.Equal("cv-50", info.ComicVineId);
        // Folder tokens still applied since the file does not override them
        Assert.Equal(30002, info.AniListId);
        Assert.Equal("50", info.Chapters);
    }

    /// <summary>
    /// Regression for the level-change data-loss bug: the strict parser must
    /// return volume / chapter numbers in upstream's canonical form (no leading
    /// zeros). Without this, Volume.LookupName / Chapter.Number drift between
    /// "01" (strict) and "1" (upstream), the existing rows fail to match on the
    /// first scan after a level change, and ProcessSeries deletes the old
    /// Volume / Chapter rows — taking AppUserProgresses and AppUserBookmark
    /// with them via the cascade. See StrictParser.BuildFromMatch.
    /// </summary>
    [Fact]
    public void VolumeNumber_IsZeroStripped_ToMatchUpstreamCanonicalForm()
    {
        var t01 = _parser.Parse($"{_libraryRoot}Frostline Saga/T01.cbz",
            $"{_libraryRoot}Frostline Saga/", _libraryRoot, LibraryType.Manga);
        var t08 = _parser.Parse($"{_libraryRoot}Frostline Saga/T08.cbz",
            $"{_libraryRoot}Frostline Saga/", _libraryRoot, LibraryType.Manga);
        var t11 = _parser.Parse($"{_libraryRoot}Frostline Saga/T11.cbz",
            $"{_libraryRoot}Frostline Saga/", _libraryRoot, LibraryType.Manga);

        Assert.Equal("1", t01!.Volumes);    // NOT "01"
        Assert.Equal("8", t08!.Volumes);    // NOT "08"
        Assert.Equal("11", t11!.Volumes);   // double-digit unchanged
    }

    [Fact]
    public void LongKindToken_VolumeSpaced_AcceptedAtBothLevels()
    {
        var l1 = _parser.ParseDetailed($"{_libraryRoot}Moonlit Tavern/Volume 1.cbz",
            $"{_libraryRoot}Moonlit Tavern/", _libraryRoot, LibraryType.Manga, level: 1);
        var l2 = _parser.ParseDetailed($"{_libraryRoot}Moonlit Tavern/Volume 2.cbz",
            $"{_libraryRoot}Moonlit Tavern/", _libraryRoot, LibraryType.Manga, level: 2);

        Assert.NotNull(l1.Info);
        Assert.Equal("1", l1.Info!.Volumes);
        Assert.Equal(ParserOutcome.Accepted, l1.Outcome);

        Assert.NotNull(l2.Info);
        Assert.Equal("2", l2.Info!.Volumes);
        Assert.Equal(ParserOutcome.Accepted, l2.Outcome);
    }

    [Fact]
    public void LongKindToken_VolumeSpaceless_AcceptedAtBothLevels()
    {
        var info = _parser.Parse($"{_libraryRoot}Long Form Spaceless/Volume3.cbz",
            $"{_libraryRoot}Long Form Spaceless/", _libraryRoot, LibraryType.Manga);
        Assert.NotNull(info);
        Assert.Equal("3", info!.Volumes);
    }

    [Fact]
    public void LongKindToken_TomeAndChapter_BothRecognized()
    {
        var tome = _parser.Parse($"{_libraryRoot}Long Form Tome/Tome 5.cbz",
            $"{_libraryRoot}Long Form Tome/", _libraryRoot, LibraryType.Manga);
        var chap = _parser.Parse($"{_libraryRoot}Long Form Chapter/Chapter 7.cbz",
            $"{_libraryRoot}Long Form Chapter/", _libraryRoot, LibraryType.Manga);

        Assert.Equal("5", tome!.Volumes);
        Assert.Equal(Parser.LooseLeafVolume, chap!.Volumes);
        Assert.Equal("7", chap.Chapters);
    }

    [Fact]
    public void DottedFilename_KindTokenSurroundedByDots_AcceptedAtLevel1_RejectedAtLevel2()
    {
        // Driftgrade-style: "Driftgrade.T01.FRENCH.CBZ.eBook-Paprika+.cbz". No spaces,
        // no " - " separator; level-1 word-scan treats dots as separators so T01
        // is found; level-2 still rejects (no " - "-anchored kind token).
        var l1 = _parser.ParseDetailed(
            $"{_libraryRoot}Driftgrade/Driftgrade.T01.FRENCH.CBZ.eBook-Paprika+.cbz",
            $"{_libraryRoot}Driftgrade/", _libraryRoot, LibraryType.Manga, level: 1);
        var l2 = _parser.ParseDetailed(
            $"{_libraryRoot}Driftgrade/Driftgrade.T01.FRENCH.CBZ.eBook-Paprika+.cbz",
            $"{_libraryRoot}Driftgrade/", _libraryRoot, LibraryType.Manga, level: 2);

        Assert.NotNull(l1.Info);
        Assert.Equal("1", l1.Info!.Volumes);
        Assert.Equal(ParserOutcome.Lenient, l1.Outcome);

        Assert.Null(l2.Info);
        Assert.Equal(ParserOutcome.Rejected, l2.Outcome);
    }

    [Fact]
    public void LooseFileAtLibraryRoot_IsRejected()
    {
        var info = _parser.Parse($"{_libraryRoot}LooseFile.cbz", _libraryRoot, _libraryRoot, LibraryType.Manga);
        Assert.Null(info);
    }

    [Fact]
    public void ParseDetailed_SurfacesReasonOnReject()
    {
        var result = _parser.ParseDetailed($"{_libraryRoot}LooseFile.cbz", _libraryRoot, _libraryRoot, LibraryType.Manga);
        Assert.Null(result.Info);
        Assert.Contains("immediate child folder", result.Reason);
    }

    /// <summary>
    /// Regression test: <c>UpdateFromComicInfo</c> (inherited from <see cref="DefaultParser"/>)
    /// overwrites <c>info.Chapters</c> from <c>ComicInfo.Number</c> and <c>info.Volumes</c>
    /// from <c>ComicInfo.Volume</c>. In Strict mode the folder + filename are the source of
    /// truth — ComicInfo must not flip a Volume into a Chapter (or vice versa).
    /// </summary>
    [Fact]
    public void ComicInfoNumber_DoesNotOverrideClassificationChapter()
    {
        var info = _parser.Parse(
            $"{_libraryRoot}Frostline Saga/T01.cbz",
            $"{_libraryRoot}Frostline Saga/", _libraryRoot, LibraryType.Manga,
            enableMetadata: true,
            comicInfo: new Kavita.Models.Metadata.ComicInfo { Number = "1", Volume = "99" });

        Assert.NotNull(info);
        Assert.Equal("1", info!.Volumes); // classification (zero-stripped), NOT ComicInfo.Volume
        Assert.Equal(Parser.DefaultChapter, info.Chapters); // classification, NOT ComicInfo.Number
    }
}
