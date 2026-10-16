using Backend.Services;
using Backend.Services.Models;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Controllers;

/// <summary>
/// Public read-only catalogue of known instruments and their capability flags, so
/// clients can discover scope (symbol, venue, currency, timeframes, crypto-only
/// features) instead of hardcoding BTC assumptions.
/// </summary>
[ApiController]
[Route("api/instruments")]
public sealed class InstrumentsController : ControllerBase
{
    private readonly InstrumentCatalog _catalog;
    private readonly TimeProvider _timeProvider;

    public InstrumentsController(InstrumentCatalog catalog, TimeProvider? timeProvider = null)
    {
        _catalog = catalog;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    [HttpGet]
    [ProducesResponseType(typeof(InstrumentsResponse), StatusCodes.Status200OK)]
    public ActionResult<InstrumentsResponse> Get() => Ok(new InstrumentsResponse(
        InstrumentCatalog.ContractVersion,
        _timeProvider.GetUtcNow().UtcDateTime,
        _catalog.Instruments));
}
