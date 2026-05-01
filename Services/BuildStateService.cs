using Microsoft.Extensions.Caching.Memory;

namespace Tx9501.Services;

/// <summary>
/// Implements thread-safe build state management using MemoryCache.
/// </summary>
public class BuildStateService : IBuildStateService
{
    private readonly IMemoryCache _cache;
    private readonly ILogger<BuildStateService> _logger;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);

    public BuildStateService(IMemoryCache cache, ILogger<BuildStateService> logger)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private string GetCacheKey(string sessionId) => $"build_state:{sessionId}";

    public bool HasState(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return false;

        return _cache.TryGetValue(GetCacheKey(sessionId), out BuildState? _);
    }

    public BuildState GetState(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return new BuildState();

        var key = GetCacheKey(sessionId);
        if (_cache.TryGetValue(key, out BuildState? state))
        {
            return state ?? new BuildState();
        }

        return new BuildState();
    }

    public void StartBuild(string sessionId, string? scopeDisplay = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        var state = new BuildState
        {
            IsRunning = true,
            Progress = 0,
            ScopeDisplay = string.IsNullOrWhiteSpace(scopeDisplay) ? "All associations" : scopeDisplay,
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

        _logger.LogInformation("Build started for session {SessionId}", sessionId);
    }

    public void UpdateProgress(string sessionId, int percent)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        var key = GetCacheKey(sessionId);
        var state = GetState(sessionId);

        // Ignore late progress callbacks once build is in a terminal state.
        if (!state.IsRunning &&
            (!string.IsNullOrWhiteSpace(state.ResultMessage) || !string.IsNullOrWhiteSpace(state.ErrorMessage)))
        {
            return;
        }

        var clamped = Math.Clamp(percent, 0, 100);
        state.Progress = state.IsRunning ? Math.Max(state.Progress, clamped) : clamped;
        state.IsRunning = true;
        state.LastUpdated = DateTime.UtcNow;

        _cache.Set(key, state, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = CacheDuration,
            SlidingExpiration = TimeSpan.FromMinutes(5)
        });
    }

    public void CompleteBuild(string sessionId, string resultMessage)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        var previous = GetState(sessionId);
        var key = GetCacheKey(sessionId);
        var state = new BuildState
        {
            IsRunning = false,
            Progress = 100,
            ScopeDisplay = previous.ScopeDisplay,
            ResultMessage = resultMessage,
            ErrorMessage = null,
            LastUpdated = DateTime.UtcNow
        };

        _cache.Set(key, state, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = CacheDuration,
            SlidingExpiration = TimeSpan.FromMinutes(5)
        });

        _logger.LogInformation("Build completed successfully for session {SessionId}", sessionId);
    }

    public void FailBuild(string sessionId, string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        var previous = GetState(sessionId);
        var key = GetCacheKey(sessionId);
        var state = new BuildState
        {
            IsRunning = false,
            Progress = 0,
            ScopeDisplay = previous.ScopeDisplay,
            ResultMessage = null,
            ErrorMessage = errorMessage,
            LastUpdated = DateTime.UtcNow
        };

        _cache.Set(key, state, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = CacheDuration,
            SlidingExpiration = TimeSpan.FromMinutes(5)
        });

        _logger.LogError("Build failed for session {SessionId}: {Error}", sessionId, errorMessage);
    }

    public void ClearBuild(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        var key = GetCacheKey(sessionId);
        _cache.Remove(key);

        _logger.LogInformation("Build state cleared for session {SessionId}", sessionId);
    }
}