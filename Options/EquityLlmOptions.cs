namespace Backend.Options;

/// <summary>
/// Backend side of the equity LLM run (spec §7). Bound to "EquityLlm". The request timeout must
/// exceed the AI's whole-run deadline EQUITY_LLM_TOTAL_TIMEOUT_S (default 240 s) and stay below the frontend proxy timeout (290 s) so the backend receives
/// the AI's run record (succeeded/failed/timeout) instead of cutting the call itself.
/// </summary>
public sealed class EquityLlmOptions
{
    public const string SectionName = "EquityLlm";
    public const string HttpClientName = "EquityLlm";

    public int RequestTimeoutSeconds { get; set; } = 270;

    /// <summary>
    /// In-app LLM runs are retired (EQX v2 B): research happens in an interactive session using the
    /// investment-research skill. false ⇒ POST /llm-runs answers 503 LLM_DISABLED before any lock,
    /// dossier build or upstream call. History stays readable.
    /// </summary>
    public bool Enabled { get; set; } = false;
}
