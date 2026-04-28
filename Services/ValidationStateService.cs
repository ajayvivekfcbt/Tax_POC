using Microsoft.Extensions.Caching.Memory;

namespace Tx9501.Services;

/// <summary>
/// Implements thread-safe validation state management using MemoryCache.
/// Safe for use in background Task.Run() threads and across HTTP requests.
/// </summary>
public class ValidationStateService : IValidationStateService
{
    private readonly IMemoryCache _cache;
    private readonly ILogger<ValidationStateService> _logger;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);

    public ValidationStateService(IMemoryCache cache, ILogger<ValidationStateService> logger)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private string GetCacheKey(string sessionId) => $"validation_state:{sessionId}";

    public ValidationState GetState(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return new ValidationState();

        var key = GetCacheKey(sessionId);
        if (_cache.TryGetValue(key, out ValidationState? state))
        {
            return state ?? new ValidationState();
        }

        // Return empty state if not found
        return new ValidationState();
    }

    public void StartValidation(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        var state = new ValidationState
        {
            IsRunning = true,
            Progress = 0,
            ResultMessage = null,
            ErrorMessage = null,
            LastUpdated = DateTime.UtcNow
        };

        var key = GetCacheKey(sessionId);
        _cache.Set(key, state, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = CacheDuration,
            SlidingExpiration = TimeSpan.FromMinutes(5)
        });

        _logger.LogInformation("Validation started for session {SessionId}", sessionId);
    }

    public void UpdateProgress(string sessionId, int percent)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        var key = GetCacheKey(sessionId);
        var state = GetState(sessionId);
        
        state.Progress = Math.Clamp(percent, 0, 100);
        state.LastUpdated = DateTime.UtcNow;

        _cache.Set(key, state, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = CacheDuration,
            SlidingExpiration = TimeSpan.FromMinutes(5)
        });

        _logger.LogDebug("Validation progress updated: session {SessionId}, progress {Progress}%", sessionId, percent);
    }

    public void CompleteValidation(string sessionId, string resultMessage)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        var key = GetCacheKey(sessionId);
        var state = new ValidationState
        {
            IsRunning = false,
            Progress = 100,
            ResultMessage = resultMessage,
            ErrorMessage = null,
            LastUpdated = DateTime.UtcNow
        };

        _cache.Set(key, state, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = CacheDuration,
            SlidingExpiration = TimeSpan.FromMinutes(5)
        });

        _logger.LogInformation("Validation completed successfully for session {SessionId}", sessionId);
    }

    public void FailValidation(string sessionId, string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        var key = GetCacheKey(sessionId);
        var state = new ValidationState
        {
            IsRunning = false,
            Progress = 0,
            ResultMessage = null,
            ErrorMessage = errorMessage,
            LastUpdated = DateTime.UtcNow
        };

        _cache.Set(key, state, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = CacheDuration,
            SlidingExpiration = TimeSpan.FromMinutes(5)
        });

        _logger.LogError("Validation failed for session {SessionId}: {Error}", sessionId, errorMessage);
    }

    public void ClearValidation(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        var key = GetCacheKey(sessionId);
        _cache.Remove(key);

        _logger.LogInformation("Validation state cleared for session {SessionId}", sessionId);
    }
}
