using System.Collections.Concurrent;

namespace RoomControl.Api.Services;

/// <summary>
/// 按账号限制连续登录失败。登录页会公开列出用户名，仅靠按 IP 限流无法阻止针对已知账号的爆破。
/// 不存在的用户名同样计数，避免通过锁定行为判断账号是否存在。
/// </summary>
public sealed class LoginThrottle
{
    public const int MaxFailures = 5;
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(5);
    private const int PruneThreshold = 10_000;

    private readonly ConcurrentDictionary<string, Attempts> _attempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _timeProvider;

    public LoginThrottle(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public bool IsBlocked(string? username)
    {
        // 查询不创建记录，避免探测任意用户名就持续扩大字典。
        var key = Normalize(username);
        if (key.Length == 0 || !_attempts.TryGetValue(key, out var attempts))
        {
            return false;
        }

        lock (attempts)
        {
            return attempts.LockedUntil > _timeProvider.GetUtcNow();
        }
    }

    public void RecordFailure(string? username)
    {
        var key = Normalize(username);
        if (key.Length == 0)
        {
            return;
        }

        // 每个账号单独加锁，保证并发失败请求不会绕过阈值。
        var now = _timeProvider.GetUtcNow();
        var attempts = _attempts.GetOrAdd(key, _ => new Attempts());
        lock (attempts)
        {
            if (now - attempts.LastFailure > FailureWindow)
            {
                attempts.Count = 0;
            }

            attempts.Count++;
            attempts.LastFailure = now;
            if (attempts.Count >= MaxFailures)
            {
                attempts.Count = 0;
                attempts.LockedUntil = now + LockoutDuration;
            }
        }

        PruneIfNeeded(now);
    }

    public void Reset(string? username)
    {
        var key = Normalize(username);
        // 认证成功后清除失败计数与锁定状态。
        if (key.Length > 0)
        {
            _attempts.TryRemove(key, out _);
        }
    }

    private void PruneIfNeeded(DateTimeOffset now)
    {
        if (_attempts.Count < PruneThreshold)
        {
            return;
        }

        // 仅在字典较大时清理过期项，避免每次失败都扫描全部账号。
        foreach (var pair in _attempts)
        {
            lock (pair.Value)
            {
                if (pair.Value.LockedUntil <= now && now - pair.Value.LastFailure > FailureWindow)
                {
                    _attempts.TryRemove(pair.Key, out _);
                }
            }
        }
    }

    private static string Normalize(string? username)
    {
        var trimmed = username?.Trim() ?? string.Empty;
        // 词典键来自请求体，限制长度避免超长用户名占用内存。
        return trimmed.Length > 60 ? trimmed[..60] : trimmed;
    }

    private sealed class Attempts
    {
        public int Count;
        public DateTimeOffset LastFailure;
        public DateTimeOffset LockedUntil;
    }
}
