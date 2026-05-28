using System.Collections.Generic;
using System.Linq;
using Kavita.Models.Entities;
using Kavita.Models.Parser;
using Kavita.Services.Extensions;

namespace Kavita.Services.Scanner.StrictMode;

/// <summary>
/// Fork-only fallback for <see cref="Kavita.Services.Extensions.ChapterListExtensions.GetChapterByRange"/>.
///
/// <para>Background: upstream's regex parser can extract arbitrary numbers from
/// scene-style filenames (e.g. <c>Quill Lantern T01 (Author) [eBook officiel 1920].cbr</c>
/// gets a chapter Range of "1920" because the resolution tag was treated as a
/// chapter number). The strict parser, looking at the same file, correctly
/// classifies it as "volume-only, no chapter" (Range = DefaultChapter).
/// When ProcessSeries.UpdateChapters then asks the existing Chapter rows for one
/// whose Range matches the strict Range, no match — upstream's row has
/// Range="1920", strict produces Range=DefaultChapter. UpdateChapters creates a
/// new Chapter, AddOrUpdateFileForChapter creates a new MangaFile, and
/// RemoveChapters keeps the old row because its file is still in
/// parsedInfos (matched by directory, not range). Result: 2 Chapter rows + 2
/// MangaFile rows per file, indefinitely.</para>
///
/// <para>This helper short-circuits that by saying: if the by-range lookup
/// misses, but there's already a Chapter in this Volume whose Files include
/// the file we're trying to place, reuse that Chapter row. UpdateChapters'
/// later code (line 706 <c>chapter.UpdateFrom(info)</c> + lines 713-716
/// rewriting Number/MinNumber/MaxNumber/Range) updates the existing row in
/// place. Chapter identity preserved; AppUserProgresses / AppUserBookmark /
/// ReadingListItem FK chains intact.</para>
/// </summary>
public static class StrictChapterLookup
{
    public static Chapter? FindByFilePath(IEnumerable<Chapter> chapters, ParserInfo info)
    {
        if (string.IsNullOrEmpty(info.FullFilePath)) return null;
        var normalized = Parser.NormalizePath(info.FullFilePath);
        // Specials already get a file-path match in GetChapterByRange, so this
        // fork-only fallback only needs to handle the non-special case.
        if (info.IsSpecialInfo()) return null;
        return chapters.FirstOrDefault(c =>
            c.Files.Any(f => Parser.NormalizePath(f.FilePath).Equals(normalized, System.StringComparison.OrdinalIgnoreCase)));
    }
}
