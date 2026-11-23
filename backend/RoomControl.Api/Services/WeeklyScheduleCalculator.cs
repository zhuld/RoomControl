namespace RoomControl.Api.Services;

/// <summary>依据星期位掩码和本地时间计算下一次任务执行时刻。</summary>
public static class WeeklyScheduleCalculator
{
    /// <summary>
    /// 以服务器本地时间从给定时刻之后搜索下一个匹配日期。
    /// </summary>
    public static DateTimeOffset GetNextOccurrence(
        DateTimeOffset after,
        int weekdayMask,
        TimeSpan localTime)
    {
        if (weekdayMask is < 1 or > 127)
        {
            throw new ArgumentOutOfRangeException(nameof(weekdayMask));
        }

        if (localTime < TimeSpan.Zero || localTime >= TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(localTime));
        }

        var afterLocal = after.ToLocalTime();
        for (var daysAhead = 0; daysAhead < 8; daysAhead++)
        {
            var date = afterLocal.Date.AddDays(daysAhead);
            var localDateTime = DateTime.SpecifyKind(date.Add(localTime), DateTimeKind.Unspecified);
            var dayBit = 1 << (int)localDateTime.DayOfWeek;
            if ((weekdayMask & dayBit) == 0)
            {
                continue;
            }

            var next = new DateTimeOffset(localDateTime);
            if (next > after)
            {
                return next.ToUniversalTime();
            }
        }

        throw new InvalidOperationException("Could not calculate the next weekly schedule occurrence.");
    }
}
