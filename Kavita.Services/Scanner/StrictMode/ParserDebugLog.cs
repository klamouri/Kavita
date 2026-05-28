using System.Collections.Generic;
using System.Linq;
using Kavita.API.Services;

namespace Kavita.Services.Scanner.StrictMode;

/// <summary>
/// In-memory ring buffer holding the most recent parser events for libraries running at
/// strictness level &gt;= 1. Registered as a singleton so events from every scan are visible
/// to the Parser Logs UI page until the process restarts or the user clears the buffer.
///
/// Bounded at <see cref="Capacity"/> newest entries to keep memory pressure flat under
/// large scans.
/// </summary>
public sealed class ParserDebugLog : IParserDebugLog
{
    private const int Capacity = 2000;

    private readonly LinkedList<ParserDebugEntry> _entries = new();
    private readonly object _lock = new();

    public void Record(ParserDebugEntry entry)
    {
        lock (_lock)
        {
            _entries.AddFirst(entry);
            while (_entries.Count > Capacity)
            {
                _entries.RemoveLast();
            }
        }
    }

    public IReadOnlyList<ParserDebugEntry> GetRecent(int? libraryId = null, int limit = 500)
    {
        lock (_lock)
        {
            var query = _entries.AsEnumerable();
            if (libraryId is { } id)
            {
                query = query.Where(e => e.LibraryId == id);
            }
            return query.Take(limit).ToList();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
    }
}
