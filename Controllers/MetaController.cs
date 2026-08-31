using System.Reflection;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Controllers;

[ApiController]
[Route("api/meta")]
public sealed class MetaController : ControllerBase
{
    internal const string ApiContractVersion = "2026-08-phase1";
    internal const string DataPipelineVersion = "legacy-unversioned";
    internal const string EvaluationVersion = "legacy-unversioned";

    [HttpGet]
    public ActionResult<MetaResponse> Get() => Ok(new MetaResponse
    {
        AppVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
        ApiContractVersion = ApiContractVersion,
        DataPipelineVersion = DataPipelineVersion,
        EvaluationVersion = EvaluationVersion,
        Environment = "Research"
    });
}

public sealed class MetaResponse
{
    public string AppVersion { get; set; } = "unknown";
    public string ApiContractVersion { get; set; } = "";
    public string DataPipelineVersion { get; set; } = "";
    public string EvaluationVersion { get; set; } = "";
    public string Environment { get; set; } = "Research";
}
