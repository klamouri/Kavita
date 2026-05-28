using System;
using System.Collections.Generic;

namespace Kavita.API.Services;

/// <summary>
/// One row in the parser debug log. Recorded for every file the fork's strict / stricter
/// parser sees, so users can inspect why a file ended up where it did (or didn't).
///
/// **Only populated for libraries at <c>ParserStrictnessLevel &gt;= 1</c>.** Level-0 libraries
/// use the unmodified upstream parser and are intentionally not instrumented — keeping the
/// fork's edit footprint to a minimum.
/// </summary>
public sealed record ParserDebugEntry
{
    public required DateTime TimestampUtc { get; init; }
    public required int LibraryId { get; init; }
    /// <summary>Library strictness level (1 or 2) at the time of parsing.</summary>
    public required int LevelAtParse { get; init; }
    public required string FilePath { get; init; }
    public required string LibraryRoot { get; init; }
    public required ParserOutcome Outcome { get; init; }
    /// <summary>Series name the parser settled on; empty when Outcome is Rejected.</summary>
    public string Series { get; init; } = string.Empty;
    public string Volumes { get; init; } = string.Empty;
    public string Chapters { get; init; } = string.Empty;
    public bool IsSpecial { get; init; }
    public int SeriesReleaseYear { get; init; }
    /// <summary>Token keys+values found in folder + file, joined as "key=value,key=value".</summary>
    public string Tokens { get; init; } = string.Empty;
    /// <summary>Reason string explaining the outcome (e.g. "no kind token — loose-leaf placement").</summary>
    public string Reason { get; init; } = string.Empty;
}

/// <summary>
/// Per-file parser outcome.
/// <list type="bullet">
///   <item><see cref="Accepted"/> — the file parsed AND would also have parsed at the
///   highest strictness level (Level 2). "Approved by Level 2".</item>
///   <item><see cref="Lenient"/> — the file parsed at the library's current level, but Level 2
///   would have rejected it. Only ever emitted at Level 1.</item>
///   <item><see cref="Rejected"/> — the file did not parse at the library's current level.
///   Level 2 would also reject (by construction).</item>
/// </list>
/// </summary>
public enum ParserOutcome
{
    Accepted = 0,
    Lenient = 1,
    Rejected = 2,
}

public interface IParserDebugLog
{
    /// <summary>Append an entry. Oldest entries are evicted once the buffer is full.</summary>
    void Record(ParserDebugEntry entry);
    /// <summary>Returns recent entries, newest first. Filter by library if non-null.</summary>
    IReadOnlyList<ParserDebugEntry> GetRecent(int? libraryId = null, int limit = 500);
    /// <summary>Clears the buffer.</summary>
    void Clear();
}
