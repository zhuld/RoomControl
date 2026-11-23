using RoomControl.Api.Services;

namespace RoomControl.Api.Tests;

/// <summary>验证每周任务跳过已过时刻并拒绝无效星期掩码。</summary>
public sealed class WeeklyScheduleCalculatorTests
{
    [Fact]
    public void GetNextOccurrence_UsesLocalWeekdayAndSkipsElapsedTime()
    {
        var wednesdayAtNoon = new DateTimeOffset(new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Local));
        var weekdayMask = 1 << (int)DayOfWeek.Wednesday;

        var next = WeeklyScheduleCalculator.GetNextOccurrence(
            wednesdayAtNoon, weekdayMask, new TimeSpan(8, 30, 0));

        Assert.Equal(
            new DateTimeOffset(new DateTime(2026, 11, 25, 8, 30, 0, DateTimeKind.Local)),
            next);
    }

    [Fact]
    public void GetNextOccurrence_RejectsAnEmptyWeekdayMask()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WeeklyScheduleCalculator.GetNextOccurrence(
                DateTimeOffset.UtcNow, 0, new TimeSpan(8, 30, 0)));
    }
}
