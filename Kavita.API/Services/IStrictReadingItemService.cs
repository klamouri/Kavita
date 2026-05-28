using Kavita.Models.Entities.Enums;
using Kavita.Models.Parser;

namespace Kavita.API.Services;

/// <summary>
/// Fork-only sibling to <see cref="IReadingItemService"/>. Carries the extra
/// parameters the strict parser needs (<c>parserStrictnessLevel</c> +
/// <c>libraryId</c> for the Parser Logs page) and is invoked by
/// <c>ParseScannedFiles</c> only when the library is on strictness level 1 or 2.
///
/// <para>Keeping this on a separate interface means upstream's
/// <see cref="IReadingItemService"/> stays bit-for-bit unchanged — every test
/// mock and downstream caller compiles without modification.</para>
/// </summary>
public interface IStrictReadingItemService
{
    ParserInfo? ParseFile(string path, string rootPath, string libraryRoot,
        LibraryType type, bool enableMetadata, int parserStrictnessLevel, int libraryId);
}
