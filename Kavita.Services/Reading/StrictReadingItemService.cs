using System;
using Kavita.API.Services;
using Kavita.Models.Entities.Enums;
using Kavita.Models.Metadata;
using Kavita.Models.Parser;
using Kavita.Services.Scanner;
using Kavita.Services.Scanner.StrictMode;
using Microsoft.Extensions.Logging;

namespace Kavita.Services.Reading;

/// <summary>
/// Fork-only sibling to <see cref="ReadingItemService"/> used by
/// <c>ParseScannedFiles</c> when a library is on strictness level &gt;= 1.
/// Owns the <see cref="StrictParser"/> instance and writes per-file outcomes
/// to <see cref="IParserDebugLog"/>. Reuses upstream's archive / book / image
/// services for ComicInfo extraction so the parser respects embedded
/// metadata the same way upstream does.
/// </summary>
public class StrictReadingItemService : IStrictReadingItemService
{
    private readonly IArchiveService _archiveService;
    private readonly IBookService _bookService;
    private readonly StrictParser _strictParser;
    private readonly IParserDebugLog _parserDebugLog;
    private readonly ILogger<StrictReadingItemService> _logger;

    public StrictReadingItemService(IArchiveService archiveService, IBookService bookService,
        IDirectoryService directoryService, IParserDebugLog parserDebugLog,
        ILogger<StrictReadingItemService> logger)
    {
        _archiveService = archiveService;
        _bookService = bookService;
        _parserDebugLog = parserDebugLog;
        _logger = logger;
        _strictParser = new StrictParser(directoryService, new ImageParser(directoryService));
    }

    public ParserInfo? ParseFile(string path, string rootPath, string libraryRoot,
        LibraryType type, bool enableMetadata, int parserStrictnessLevel, int libraryId)
    {
        try
        {
            var comicInfo = GetComicInfo(path, enableMetadata);
            var result = _strictParser.ParseDetailed(path, rootPath, libraryRoot, type,
                enableMetadata, comicInfo, parserStrictnessLevel);
            _parserDebugLog.Record(BuildEntry(libraryId, parserStrictnessLevel, path, libraryRoot, result));
            return result.Info;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "There was an exception when parsing file {FilePath}", path);
            return null;
        }
    }

    private ComicInfo? GetComicInfo(string filePath, bool enableMetadata)
    {
        if (!enableMetadata) return null;
        if (Parser.IsEpub(filePath) || Parser.IsPdf(filePath)) return _bookService.GetComicInfo(filePath);
        if (Parser.IsComicInfoExtension(filePath)) return _archiveService.GetComicInfo(filePath);
        return null;
    }

    private static ParserDebugEntry BuildEntry(int libraryId, int level, string filePath, string libraryRoot, StrictParseResult result)
    {
        var info = result.Info;
        return new ParserDebugEntry
        {
            TimestampUtc = DateTime.UtcNow,
            LibraryId = libraryId,
            LevelAtParse = level,
            FilePath = filePath,
            LibraryRoot = libraryRoot,
            Outcome = result.Outcome,
            Series = info?.Series ?? string.Empty,
            Volumes = info?.Volumes ?? string.Empty,
            Chapters = info?.Chapters ?? string.Empty,
            IsSpecial = info?.IsSpecial ?? false,
            SeriesReleaseYear = info?.SeriesReleaseYear ?? 0,
            Tokens = result.Tokens,
            Reason = result.Reason,
        };
    }
}
