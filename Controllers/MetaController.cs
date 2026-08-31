using System.Reflection;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Controllers;

[ApiController]
[Route("api/meta")]
public sealed class MetaController : ControllerBase
{
    internal const string ApiContractVersion = Data.ResearchVersions.ApiContract;
    internal const string DataPipelineVersion = Data.ResearchVersions.DataPipeline;
    internal const string EvaluationVersion = Data.ResearchVersions.Evaluation;

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
