using System.Collections.Generic;
using Kavita.Models.Builders;
using Kavita.Models.Entities;
using Kavita.Models.Entities.Enums;
using Kavita.Models.Parser;
using Kavita.Services.Builders;
using Kavita.Services.Scanner;
using Kavita.Services.Scanner.StrictMode;

namespace Kavita.Services.Tests.Parsers;

/// <summary>
/// Unit tests for <see cref="StrictChapterLookup.FindByFilePath"/> — the
/// chapter-level data-preservation fallback. When upstream and strict
/// produce different Chapter.Range values for the same file (the classic
/// case being upstream's regex picking up a resolution tag like
/// "[eBook officiel 1920]" as a chapter number), the fallback finds the
/// existing chapter that already owns the file path so ProcessSeries
/// reuses the row instead of creating a duplicate.
/// </summary>
public class StrictChapterLookupTests
{
    [Fact]
    public void FindByFilePath_FilePathInChapterFiles_ReturnsThatChapter()
    {
        var existing = new ChapterBuilder("1920").Build();
        existing.Files = new List<MangaFile>
        {
            new() { FilePath = "/lib/Quill Lantern/Quill Lantern T01.cbr", Format = MangaFormat.Archive, Pages = 191 },
        };
        var info = NewInfo("/lib/Quill Lantern/Quill Lantern T01.cbr");

        var match = StrictChapterLookup.FindByFilePath(new[] { existing }, info);

        Assert.Same(existing, match);
    }

    [Fact]
    public void FindByFilePath_NoFilePathMatch_ReturnsNull()
    {
        var existing = new ChapterBuilder("1").Build();
        existing.Files = new List<MangaFile>
        {
            new() { FilePath = "/lib/Quill Lantern/some-other-file.cbr", Format = MangaFormat.Archive, Pages = 1 },
        };
        var info = NewInfo("/lib/Quill Lantern/Quill Lantern T01.cbr");

        Assert.Null(StrictChapterLookup.FindByFilePath(new[] { existing }, info));
    }

    [Fact]
    public void FindByFilePath_EmptyChapterList_ReturnsNull()
    {
        Assert.Null(StrictChapterLookup.FindByFilePath(System.Array.Empty<Chapter>(), NewInfo("/lib/x.cbr")));
    }

    [Fact]
    public void FindByFilePath_EmptyFilePath_ReturnsNull()
    {
        var existing = new ChapterBuilder("1").Build();
        existing.Files = new List<MangaFile>
        {
            new() { FilePath = "/lib/x.cbr", Format = MangaFormat.Archive, Pages = 1 },
        };
        Assert.Null(StrictChapterLookup.FindByFilePath(new[] { existing }, NewInfo("")));
    }

    [Fact]
    public void FindByFilePath_PathComparisonIsCaseInsensitive_AndNormalized()
    {
        var existing = new ChapterBuilder("1").Build();
        existing.Files = new List<MangaFile>
        {
            new() { FilePath = "/lib/Quill Lantern/Quill Lantern T01.cbr", Format = MangaFormat.Archive, Pages = 1 },
        };
        var info = NewInfo("/lib/Quill Lantern\\Quill Lantern T01.cbr"); // mixed slashes, same path

        Assert.Same(existing, StrictChapterLookup.FindByFilePath(new[] { existing }, info));
    }

    [Fact]
    public void FindByFilePath_IsSpecial_ReturnsNull_BecauseUpstreamHandlesSpecialsAlready()
    {
        // Specials already get a file-path match in ChapterListExtensions.GetChapterByRange;
        // the fork-only fallback intentionally short-circuits on specials so we don't
        // duplicate that logic (and potentially conflict with it).
        var existing = new ChapterBuilder("1").Build();
        existing.Files = new List<MangaFile>
        {
            new() { FilePath = "/lib/Quill Lantern/Specials/SP1.cbz", Format = MangaFormat.Archive, Pages = 1 },
        };
        var info = NewInfo("/lib/Quill Lantern/Specials/SP1.cbz");
        info.IsSpecial = true;

        Assert.Null(StrictChapterLookup.FindByFilePath(new[] { existing }, info));
    }

    [Fact]
    public void FindByFilePath_PicksFirstMatchingChapter_IfMultipleClaim()
    {
        // Defensive: a Volume shouldn't normally have two chapters claiming the same
        // file, but if it does (e.g. mid-fix DB state), return the first one.
        var first = new ChapterBuilder("a").Build();
        first.Files = new List<MangaFile> { new() { FilePath = "/lib/x.cbr", Format = MangaFormat.Archive, Pages = 1 } };
        var second = new ChapterBuilder("b").Build();
        second.Files = new List<MangaFile> { new() { FilePath = "/lib/x.cbr", Format = MangaFormat.Archive, Pages = 1 } };

        var match = StrictChapterLookup.FindByFilePath(new[] { first, second }, NewInfo("/lib/x.cbr"));
        Assert.Same(first, match);
    }

    private static ParserInfo NewInfo(string fullFilePath) => new()
    {
        Series = "Test",
        FullFilePath = fullFilePath,
        Format = MangaFormat.Archive,
        Volumes = "1",
        Chapters = Parser.DefaultChapter,
    };
}
