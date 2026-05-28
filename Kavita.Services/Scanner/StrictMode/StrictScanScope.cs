using System;
using System.Collections.Generic;
using Kavita.Models.Parser;

namespace Kavita.Services.Scanner.StrictMode;

/// <summary>
/// Disposable wrapper around <see cref="StrictScanTracker.BeginScan"/> /
/// <see cref="StrictScanTracker.EndScan"/>. Lets the upstream scanner methods
/// use a single <c>using var</c> declaration instead of a try/finally pair,
/// and guarantees cleanup even on exception paths so stale claimed ids can't
/// poison a follow-up <c>ScanSeries</c> (Codex finding #1).
///
/// <para>Usage in <c>ScannerService</c>:</para>
/// <code>
/// using var strictScan = StrictMode.StrictScanScope.Begin(libraryId); // fork
/// …
/// strictScan.RegisterScannedFiles(parsedSeries); // fork
/// </code>
/// </summary>
public sealed class StrictScanScope : IDisposable
{
    public int LibraryId { get; }

    private StrictScanScope(int libraryId) { LibraryId = libraryId; }

    public static StrictScanScope Begin(int libraryId)
    {
        StrictScanTracker.BeginScan(libraryId);
        return new StrictScanScope(libraryId);
    }

    public void RegisterScannedFiles(IDictionary<ParsedSeries, IList<ParserInfo>> parsedSeries)
        => StrictScanTracker.RegisterScannedFiles(LibraryId, parsedSeries);

    public void Dispose() => StrictScanTracker.EndScan(LibraryId);
}
