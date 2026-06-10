namespace Backend.Services.Models;

public class ApiErrorEnvelope
{
    public string Code { get; set; } = "UNKNOWN_ERROR";
    public string Message { get; set; } = string.Empty;
    public bool Retryable { get; set; }
    public string RequestId { get; set; } = string.Empty;
}
