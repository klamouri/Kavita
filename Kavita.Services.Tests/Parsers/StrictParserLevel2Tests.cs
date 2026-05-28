using System.IO.Abstractions.TestingHelpers;
using Kavita.API.Services;
using Kavita.Database.Tests;
using Kavita.Models.Entities.Enums;
using Kavita.Services.Scanner;
using Kavita.Services.Scanner.StrictMode;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Kavita.Services.Tests.Parsers;

/// <summary>
/// Tests for level-2 ("Stricter") rules. Level 2 requires the kind token to be the
/// first word of some <c>" - "</c>-separated segment; files with no kind token at all
/// are rejected outright (no Specials/ inference, no loose-leaf fallback).
///
/// Each test pair shows the same input scored at level 1 (Lenient / accepted with caveat)
/// vs level 2 (Rejected) so the contract delta is visible at a glance.
/// </summary>
public class StrictParserLevel2Tests : AbstractFsTest
{
    private readonly StrictParser _parser;
    private readonly string _libraryRoot;

    public StrictParserLevel2Tests()
    {
        var fs = CreateFileSystem();
        _libraryRoot = Path.Join(DataDirectory, "Manga/");
        fs.AddDirectory(_libraryRoot);

        // Kind token present but not preceded by " - " — level 1 finds it by word-scan,
        // level 2 rejects it.
        fs.AddFile($"{_libraryRoot}Hollow Sparrow (Intégrale)/Hollow Sparrow T01 (Author) (2000).cbz", new MockFileData(""));

        // Kind token at the start of a " - " segment — accepted at both levels.
        fs.AddFile($"{_libraryRoot}Brass Lantern/Brass Lantern - V01 (1990).cbz", new MockFileData(""));

        // No kind token at all — level 1 falls back to loose-leaf (Lenient),
        // level 2 rejects.
        fs.AddFile($"{_libraryRoot}Mystery Series/some random file.cbz", new MockFileData(""));

        // Specials/ subfolder, no SP token — level 1 marks as Special (Lenient),
        // level 2 rejects (no kind token).
        fs.AddFile($"{_libraryRoot}Quill Lantern/Specials/Color Edition Extra.cbz", new MockFileData(""));

        // Specials/ subfolder WITH an SP token in a " - " segment — accepted at both.
        fs.AddFile($"{_libraryRoot}Quill Lantern/Specials/Quill Lantern - SP1 - Color Edition.cbz", new MockFileData(""));

        var ds = new DirectoryService(Substitute.For<ILogger<DirectoryService>>(), fs);
        _parser = new StrictParser(ds, new ImageParser(ds));
    }

    [Fact]
    public void KindTokenInsideName_NoDashSeparator_AcceptedAtLevel1_RejectedAtLevel2()
    {
        var l1 = _parser.ParseDetailed(
            $"{_libraryRoot}Hollow Sparrow (Intégrale)/Hollow Sparrow T01 (Author) (2000).cbz",
            $"{_libraryRoot}Hollow Sparrow (Intégrale)/", _libraryRoot, LibraryType.Manga, level: 1);
        var l2 = _parser.ParseDetailed(
            $"{_libraryRoot}Hollow Sparrow (Intégrale)/Hollow Sparrow T01 (Author) (2000).cbz",
            $"{_libraryRoot}Hollow Sparrow (Intégrale)/", _libraryRoot, LibraryType.Manga, level: 2);

        Assert.NotNull(l1.Info);
        Assert.Equal(ParserOutcome.Lenient, l1.Outcome);
        Assert.Equal("1", l1.Info!.Volumes);

        Assert.Null(l2.Info);
        Assert.Equal(ParserOutcome.Rejected, l2.Outcome);
        Assert.Contains("level 2", l2.Reason);
    }

    [Fact]
    public void KindTokenAtStartOfDashSegment_AcceptedAtBothLevels()
    {
        var l1 = _parser.ParseDetailed(
            $"{_libraryRoot}Brass Lantern/Brass Lantern - V01 (1990).cbz",
            $"{_libraryRoot}Brass Lantern/", _libraryRoot, LibraryType.Manga, level: 1);
        var l2 = _parser.ParseDetailed(
            $"{_libraryRoot}Brass Lantern/Brass Lantern - V01 (1990).cbz",
            $"{_libraryRoot}Brass Lantern/", _libraryRoot, LibraryType.Manga, level: 2);

        Assert.NotNull(l1.Info);
        Assert.Equal(ParserOutcome.Accepted, l1.Outcome);
        Assert.Equal("1", l1.Info!.Volumes);

        Assert.NotNull(l2.Info);
        Assert.Equal(ParserOutcome.Accepted, l2.Outcome);
        Assert.Equal("1", l2.Info!.Volumes);
    }

    [Fact]
    public void NoKindTokenAtAll_LooseLeafAtLevel1_RejectedAtLevel2()
    {
        var l1 = _parser.ParseDetailed(
            $"{_libraryRoot}Mystery Series/some random file.cbz",
            $"{_libraryRoot}Mystery Series/", _libraryRoot, LibraryType.Manga, level: 1);
        var l2 = _parser.ParseDetailed(
            $"{_libraryRoot}Mystery Series/some random file.cbz",
            $"{_libraryRoot}Mystery Series/", _libraryRoot, LibraryType.Manga, level: 2);

        Assert.NotNull(l1.Info);
        Assert.Equal(ParserOutcome.Lenient, l1.Outcome);
        Assert.Equal(Parser.LooseLeafVolume, l1.Info!.Volumes);

        Assert.Null(l2.Info);
        Assert.Equal(ParserOutcome.Rejected, l2.Outcome);
    }

    [Fact]
    public void SpecialsFolderNoKindToken_SpecialAtLevel1_RejectedAtLevel2()
    {
        var l1 = _parser.ParseDetailed(
            $"{_libraryRoot}Quill Lantern/Specials/Color Edition Extra.cbz",
            $"{_libraryRoot}Quill Lantern/Specials/", _libraryRoot, LibraryType.Manga, level: 1);
        var l2 = _parser.ParseDetailed(
            $"{_libraryRoot}Quill Lantern/Specials/Color Edition Extra.cbz",
            $"{_libraryRoot}Quill Lantern/Specials/", _libraryRoot, LibraryType.Manga, level: 2);

        Assert.NotNull(l1.Info);
        Assert.Equal(ParserOutcome.Lenient, l1.Outcome);
        Assert.True(l1.Info!.IsSpecial);

        Assert.Null(l2.Info);
        Assert.Equal(ParserOutcome.Rejected, l2.Outcome);
    }

    [Fact]
    public void SpecialsFolderWithSpInDashSegment_AcceptedAtBothLevels()
    {
        var l1 = _parser.ParseDetailed(
            $"{_libraryRoot}Quill Lantern/Specials/Quill Lantern - SP1 - Color Edition.cbz",
            $"{_libraryRoot}Quill Lantern/Specials/", _libraryRoot, LibraryType.Manga, level: 1);
        var l2 = _parser.ParseDetailed(
            $"{_libraryRoot}Quill Lantern/Specials/Quill Lantern - SP1 - Color Edition.cbz",
            $"{_libraryRoot}Quill Lantern/Specials/", _libraryRoot, LibraryType.Manga, level: 2);

        Assert.NotNull(l1.Info);
        Assert.Equal(ParserOutcome.Accepted, l1.Outcome);
        Assert.True(l1.Info!.IsSpecial);

        Assert.NotNull(l2.Info);
        Assert.Equal(ParserOutcome.Accepted, l2.Outcome);
        Assert.True(l2.Info!.IsSpecial);
    }

    [Fact]
    public void FileOutsideSeriesFolder_RejectedAtAnyLevel()
    {
        var l1 = _parser.ParseDetailed(
            $"{_libraryRoot}loose.cbz", _libraryRoot, _libraryRoot, LibraryType.Manga, level: 1);
        var l2 = _parser.ParseDetailed(
            $"{_libraryRoot}loose.cbz", _libraryRoot, _libraryRoot, LibraryType.Manga, level: 2);

        Assert.Null(l1.Info);
        Assert.Equal(ParserOutcome.Rejected, l1.Outcome);
        Assert.Null(l2.Info);
        Assert.Equal(ParserOutcome.Rejected, l2.Outcome);
    }
}
