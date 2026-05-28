using System.Collections.Generic;
using Kavita.API.Services;
using Kavita.Models.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Kavita.Server.Controllers;

/// <summary>
/// Fork-only endpoints backing the Parser Logs UI page. The buffer is in-memory
/// and bounded; only libraries at <c>ParserStrictnessLevel &gt;= 1</c> populate it.
/// Kept in a separate controller so upstream's <see cref="LibraryController"/>
/// doesn't carry fork-specific dependencies.
/// </summary>
[Authorize]
[Route("api/parser-log")]
public class ParserLogController(IParserDebugLog parserDebugLog) : BaseApiController
{
    /// <summary>
    /// Returns recent entries from the parser debug log.
    /// </summary>
    /// <param name="libraryId">Optional library filter; omit (or pass 0) to see all libraries.</param>
    /// <param name="limit">Max entries to return (default 500).</param>
    [Authorize(Policy = PolicyGroups.AdminPolicy)]
    [HttpGet]
    public ActionResult<IReadOnlyList<ParserDebugEntry>> Get(int libraryId = 0, int limit = 500)
    {
        return Ok(parserDebugLog.GetRecent(libraryId == 0 ? null : libraryId, limit));
    }

    /// <summary>Clears the in-memory parser debug log.</summary>
    [Authorize(Policy = PolicyGroups.AdminPolicy)]
    [HttpPost("clear")]
    public ActionResult Clear()
    {
        parserDebugLog.Clear();
        return Ok();
    }
}
