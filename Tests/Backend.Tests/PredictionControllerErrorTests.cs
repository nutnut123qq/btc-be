using Backend.Controllers;

namespace Backend.Tests;

public class PredictionControllerErrorTests
{
    [Fact]
    public void TryParsePredictError_PreservesNonRetryableArtifactError()
    {
        var result = PredictionController.TryParsePredictError("{\"code\":\"MODEL_ARTIFACT_INCOMPATIBLE\",\"message\":\"Không có model artifact đủ bằng chứng tương thích để suy luận.\",\"retryable\":false}");

        Assert.Equal("MODEL_ARTIFACT_INCOMPATIBLE", result.Code);
        Assert.Equal("Không có model artifact đủ bằng chứng tương thích để suy luận.", result.Message);
        Assert.False(result.Retryable);
    }

    [Fact]
    public void TryParsePredictError_PropagatesRetryableFalseForOtherStructuredErrors()
    {
        var result = PredictionController.TryParsePredictError("{\"code\":\"SOME_UPSTREAM_CODE\",\"message\":\"details\",\"retryable\":false}");

        Assert.Equal("AI_PREDICT_ERROR", result.Code);
        Assert.False(result.Retryable);
        Assert.DoesNotContain("details", result.Message);
    }

    [Fact]
    public void TryParsePredictError_PropagatesRetryableTrue()
    {
        var result = PredictionController.TryParsePredictError("{\"code\":\"UPSTREAM_TIMEOUT\",\"retryable\":true}");

        Assert.Equal("AI_PREDICT_ERROR", result.Code);
        Assert.True(result.Retryable);
    }

    [Fact]
    public void TryParsePredictError_MalformedBodyDefaultsToRetryable()
    {
        var result = PredictionController.TryParsePredictError("<html>proxy error</html>");

        Assert.Equal("AI_PREDICT_ERROR", result.Code);
        Assert.True(result.Retryable);
    }

    [Fact]
    public void TryParsePredictError_FastApiDetailBodyDefaultsToRetryable()
    {
        var result = PredictionController.TryParsePredictError("{\"detail\":\"feature vector invalid\"}");

        Assert.Equal("AI_PREDICT_ERROR", result.Code);
        Assert.True(result.Retryable);
        Assert.DoesNotContain("feature vector invalid", result.Message);
    }
}
