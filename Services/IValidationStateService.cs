using System.Collections.Generic;
using System.Threading.Tasks;

namespace Tx9501.Services;

/// <summary>
/// Thread-safe service for managing validation state across HTTP requests and background tasks.
/// Uses MemoryCache to persist state that can be accessed from both request and background threads.
/// </summary>
public interface IValidationStateService
{
    /// <summary>
    /// Get the current validation state for a session
    /// </summary>
    ValidationState GetState(string sessionId);

    /// <summary>
    /// Mark validation as running
    /// </summary>
    void StartValidation(string sessionId);

    /// <summary>
    /// Update progress percentage
    /// </summary>
    void UpdateProgress(string sessionId, int percent);

    /// <summary>
    /// Mark validation as completed with success result
    /// </summary>
    void CompleteValidation(string sessionId, string resultMessage);

    /// <summary>
    /// Mark validation as completed with error
    /// </summary>
    void FailValidation(string sessionId, string errorMessage);

    /// <summary>
    /// Clear validation state
    /// </summary>
    void ClearValidation(string sessionId);
}

/// <summary>
/// Immutable validation state snapshot
/// </summary>
public class ValidationState
{
    public bool IsRunning { get; set; }
    public int Progress { get; set; }
    public string? ResultMessage { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime LastUpdated { get; set; }

    public ValidationState()
    {
        IsRunning = false;
        Progress = 0;
        ResultMessage = null;
        ErrorMessage = null;
        LastUpdated = DateTime.UtcNow;
    }
}
