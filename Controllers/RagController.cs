using Backend.Services;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Controllers;

[ApiController]
[Route("api/rag")]
public class RagController : ControllerBase
{
    private readonly IRagService _ragService;

    public RagController(IRagService ragService)
    {
        _ragService = ragService;
    }

    [HttpGet("news-context")]
    public async Task<IActionResult> GetNewsContext(
        [FromQuery] string query,
        [FromQuery] int topK = 8,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return BadRequest("Query is required.");

        if (topK is < 1 or > 50)
            return BadRequest("topK must be between 1 and 50.");

        var context = await _ragService.BuildNewsContextAsync(
            query,
            topK: topK,
            cancellationToken: cancellationToken);

        return Ok(new
        {
            query,
            topK,
            news_context = context
        });
    }
}

