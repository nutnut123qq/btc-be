using Backend.Services;
using Backend.Services.Models;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Controllers;

[ApiController]
[Route("api/research/capabilities")]
public sealed class ResearchCapabilitiesController : ControllerBase
{
    private readonly ITechnicalCapabilityRegistry _registry;

    public ResearchCapabilitiesController(ITechnicalCapabilityRegistry registry)
    {
        _registry = registry;
    }

    [HttpGet]
    [ProducesResponseType(typeof(TechnicalCapabilitiesResponse), StatusCodes.Status200OK)]
    public ActionResult<TechnicalCapabilitiesResponse> Get() => Ok(_registry.GetSnapshot());
}
