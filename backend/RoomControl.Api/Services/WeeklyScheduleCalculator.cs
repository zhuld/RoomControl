namespace RoomControl.Api.Services;

/// <summary>依据星期位掩码、当地时间和时区计算下一次任务执行时刻。</summary>
public static class WeeklyScheduleCalculator
{
    /// <summary>
    /// 从给定 UTC 时刻之后搜索下一个匹配日期；夏令时跳过的当地时间顺延一小时。
    /// </summary>
    public static DateTimeOffset GetNextOccurrence(
        DateTimeOffset afterUtc,
        int weekdayMask,
        TimeSpan localTime,
        TimeZoneInfo timeZone)
    {
        if (weekdayMask is < 1 or > 127)
        {
            throw new ArgumentOutOfRangeException(nameof(weekdayMask));
        }

        if (localTime < TimeSpan.Zero || localTime >= TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(localTime));
        }

        var afterLocal = TimeZoneInfo.ConvertTime(afterUtc, timeZone);
        for (var daysAhead = 0; daysAhead < 8; daysAhead++)
        {
            var date = afterLocal.Date.AddDays(daysAhead);
            var localDateTime = DateTime.SpecifyKind(date.Add(localTime), DateTimeKind.Unspecified);
            var dayBit = 1 << (int)localDateTime.DayOfWeek;
            if ((weekdayMask & dayBit) == 0)
            {
                continue;
            }

            if (timeZone.IsInvalidTime(localDateTime))
            {
                localDateTime = localDateTime.AddHours(1);
            }

            var nextUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localDateTime, timeZone), TimeSpan.Zero);
            if (nextUtc > afterUtc)
            {
                return nextUtc;
            }
        }

        throw new InvalidOperationException("Could not calculate the next weekly schedule occurrence.");
    }
}
