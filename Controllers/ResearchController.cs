using Backend.Services.Models;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Controllers;

[ApiController]
[Route("api/research")]
public sealed class ResearchController : ControllerBase
{
    [HttpGet("specification")]
    public ActionResult<ResearchSpecificationDto> GetSpecification() => Ok(new ResearchSpecificationDto());
}
