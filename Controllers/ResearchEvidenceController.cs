using Backend.Services;
using Backend.Services.Models;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Controllers;

[ApiController]
[Route("api/research/evidence")]
public sealed class ResearchEvidenceController : ControllerBase
{
    private readonly IResearchEvidenceCatalog _catalog;

    public ResearchEvidenceController(IResearchEvidenceCatalog catalog) => _catalog = catalog;

    [HttpGet]
    [ProducesResponseType(typeof(ResearchEvidenceCatalogResponse), StatusCodes.Status200OK)]
    public ActionResult<ResearchEvidenceCatalogResponse> GetCatalog() => Ok(_catalog.GetCatalog());

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(ResearchEvidenceDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<ResearchEvidenceDetailDto> GetDetail(string id)
    {
        var detail = _catalog.GetDetail(id);
        return detail is null ? NotFound() : Ok(detail);
    }
}
