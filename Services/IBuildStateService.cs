namespace Tx9501.Services;

/// <summary>
/// Thread-safe service for managing build state across HTTP requests and background tasks.
/// </summary>
public interface IBuildStateService
{
    /// <summary>
    /// True when build state exists for the given session.
    /// </summary>
    bool HasState(string sessionId);

    /// <summary>
    /// Get the current build state for a session.
    /// </summary>
    BuildState GetState(string sessionId);

    /// <summary>
    /// Mark build as running.
    /// </summary>
    void StartBuild(string sessionId, string? scopeDisplay = null);

    /// <summary>
    /// Update build progress percentage.
    /// </summary>
    void UpdateProgress(string sessionId, int percent);

    /// <summary>
    /// Mark build as completed with success result.
    /// </summary>
    void CompleteBuild(string sessionId, string resultMessage);

    /// <summary>
    /// Mark build as completed with error.
    /// </summary>
    void FailBuild(string sessionId, string errorMessage);

    /// <summary>
    /// Clear build state.
    /// </summary>
    void ClearBuild(string sessionId);
}

/// <summary>
/// Build state snapshot.
/// </summary>
public class BuildState
{
    public bool IsRunning { get; set; }
    public int Progress { get; set; }
    public string? ScopeDisplay { get; set; }
    public string? ResultMessage { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime LastUpdated { get; set; }

    public BuildState()
    {
        IsRunning = false;
        Progress = 0;
        ScopeDisplay = null;
        ResultMessage = null;
        ErrorMessage = null;
        LastUpdated = DateTime.UtcNow;
    }
}