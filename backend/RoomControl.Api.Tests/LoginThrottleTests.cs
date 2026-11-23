using RoomControl.Api.Services;

namespace RoomControl.Api.Tests;

/// <summary>使用可控时钟验证失败阈值、锁定时长和过期计数清理。</summary>
public sealed class LoginThrottleTests
{
    [Fact]
    public void RecordFailure_BlocksOnlyAfterThresholdAndClearsOnSuccess()
    {
        var time = new FakeTimeProvider();
        var throttle = new LoginThrottle(time);

        for (var i = 0; i < LoginThrottle.MaxFailures - 1; i++)
        {
            throttle.RecordFailure("alice");
        }
        Assert.False(throttle.IsBlocked("alice"));

        throttle.Reset("alice");
        throttle.RecordFailure("alice");
        Assert.False(throttle.IsBlocked("alice"));

        for (var i = 1; i < LoginThrottle.MaxFailures; i++)
        {
            throttle.RecordFailure("alice");
        }
        Assert.True(throttle.IsBlocked("alice"));
        Assert.False(throttle.IsBlocked("bob"));
    }

    [Fact]
    public void IsBlocked_ReleasesAfterLockoutDuration()
    {
        var time = new FakeTimeProvider();
        var throttle = new LoginThrottle(time);
        for (var i = 0; i < LoginThrottle.MaxFailures; i++)
        {
            throttle.RecordFailure("alice");
        }
        Assert.True(throttle.IsBlocked("alice"));

        time.Advance(TimeSpan.FromMinutes(4));
        Assert.True(throttle.IsBlocked("alice"));

        time.Advance(TimeSpan.FromMinutes(2));
        Assert.False(throttle.IsBlocked("alice"));
    }

    [Fact]
    public void RecordFailure_ForgetsStaleFailures()
    {
        var time = new FakeTimeProvider();
        var throttle = new LoginThrottle(time);
        for (var i = 0; i < LoginThrottle.MaxFailures - 1; i++)
        {
            throttle.RecordFailure("alice");
        }

        time.Advance(TimeSpan.FromMinutes(20));
        throttle.RecordFailure("alice");

        Assert.False(throttle.IsBlocked("alice"));
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        // 测试推进时间而不等待真实锁定窗口。
        public void Advance(TimeSpan delta) => _now += delta;
    }
}
