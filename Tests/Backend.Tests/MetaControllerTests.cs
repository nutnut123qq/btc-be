using Backend.Controllers;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Tests;

public class MetaControllerTests
{
    [Fact]
    public void Get_ReportsResearchContractVersions()
    {
        var response = Assert.IsType<OkObjectResult>(new MetaController().Get().Result);
        var body = Assert.IsType<MetaResponse>(response.Value);

        Assert.Equal("Research", body.Environment);
        Assert.Equal(MetaController.ApiContractVersion, body.ApiContractVersion);
        Assert.NotEmpty(body.AppVersion);
    }
}
