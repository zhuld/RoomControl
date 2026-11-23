using Microsoft.EntityFrameworkCore;
using Quartz;
using RoomControl.Api.Data;
using RoomControl.Api.Models;

namespace RoomControl.Api.Services;

/// <summary>保存任务定义并将一次性或每周任务同步到 Quartz 调度器。</summary>
public sealed class ScheduleService(
    IDbContextFactory<ControlDbContext> contextFactory,
    ISchedulerFactory schedulerFactory,
    ILogger<ScheduleService> logger)
{
    public async Task RestoreAsync(CancellationToken cancellationToken)
    {
        // 应用启动时恢复已启用任务；已错过的一次性任务记为失败，不自动补发。
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var unsupportedSchedules = await db.Schedules
            .Include(item => item.JoinMapping)
            .Where(item => item.JoinMapping != null &&
                item.JoinMapping.Kind != JoinKind.Digital)
            .ToListAsync(cancellationToken);
        await DeleteAsync(db, unsupportedSchedules, cancellationToken);

        var schedules = await db.Schedules
            .Where(item => item.Enabled && item.JoinMappingId != null)
            .ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        foreach (var schedule in schedules)
        {
            if (schedule.Kind == ScheduleKind.Once && schedule.RunAtUtc <= now)
            {
                await RecordMissedOneTimeScheduleAsync(db, schedule, now, cancellationToken);
                continue;
            }

            if (schedule.Kind == ScheduleKind.Weekly)
            {
                schedule.NextRunAtUtc = WeeklyScheduleCalculator.GetNextOccurrence(
                    now, schedule.WeekdayMask, schedule.LocalTime);
            }

            await ScheduleAsync(schedule, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ControlSchedule>> CreateGroupAsync(
        IReadOnlyList<Guid> mappingIds,
        string name,
        ScheduleKind kind,
        DateTimeOffset? runAtUtc,
        int weekdayMask,
        TimeSpan localTime,
        CancellationToken cancellationToken)
    {
        var mappings = await LoadMappingsAsync(mappingIds, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // 同一任务的多间教室共享 GroupId，各自保留独立 Job 与执行记录。
        var groupId = Guid.NewGuid();
        var schedules = mappings
            .Select(mapping => NewSchedule(groupId, mapping, name, kind, runAtUtc, weekdayMask, localTime))
            .ToList();
        db.Schedules.AddRange(schedules);
        await db.SaveChangesAsync(cancellationToken);

        foreach (var schedule in schedules)
        {
            await ScheduleAsync(schedule, cancellationToken);
        }

        return schedules;
    }

    public async Task<IReadOnlyList<ControlSchedule>> UpdateGroupAsync(
        Guid groupId,
        IReadOnlyList<Guid> mappingIds,
        string name,
        ScheduleKind kind,
        DateTimeOffset? runAtUtc,
        int weekdayMask,
        TimeSpan localTime,
        CancellationToken cancellationToken)
    {
        var mappings = await LoadMappingsAsync(mappingIds, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var existing = await LoadGroupAsync(db, groupId, cancellationToken);
        if (existing.Count == 0)
        {
            throw new KeyNotFoundException("Schedule was not found.");
        }

        var wantedMappings = mappings.ToDictionary(mapping => mapping.Id);
        var removed = existing
            .Where(item => item.JoinMappingId is null || !wantedMappings.ContainsKey(item.JoinMappingId.Value))
            .ToList();
        var kept = existing.Except(removed).ToList();

        // 从任务中移除的教室直接删除目标与其执行记录。
        if (removed.Count > 0)
        {
            var removedIds = removed.Select(item => item.Id).ToArray();
            await db.ScheduleExecutions
                .Where(item => removedIds.Contains(item.ScheduleId))
                .ExecuteDeleteAsync(cancellationToken);
            db.Schedules.RemoveRange(removed);
        }

        foreach (var schedule in kept)
        {
            schedule.GroupId = groupId;
            ApplyDefinition(schedule, name, kind, runAtUtc, weekdayMask, localTime);
        }

        var keptMappingIds = kept
            .Select(item => item.JoinMappingId!.Value)
            .ToHashSet();
        // 新增的教室沿用该组当前的启用状态，避免修改暂停中的任务时把个别目标悄悄启用。
        var groupEnabled = kept.Count > 0 ? kept[0].Enabled : true;
        var created = mappings
            .Where(mapping => !keptMappingIds.Contains(mapping.Id))
            .Select(mapping => NewSchedule(groupId, mapping, name, kind, runAtUtc, weekdayMask, localTime))
            .ToList();
        foreach (var schedule in created)
        {
            schedule.Enabled = groupEnabled;
            if (!groupEnabled) schedule.NextRunAtUtc = null;
        }
        db.Schedules.AddRange(created);

        await db.SaveChangesAsync(cancellationToken);

        var scheduler = await schedulerFactory.GetScheduler(cancellationToken);
        foreach (var schedule in removed)
        {
            await scheduler.DeleteJob(new JobKey(schedule.Id.ToString()), cancellationToken);
        }
        foreach (var schedule in kept.Concat(created))
        {
            if (schedule.Enabled)
            {
                await ScheduleAsync(schedule, cancellationToken);
            }
            else
            {
                await scheduler.DeleteJob(new JobKey(schedule.Id.ToString()), cancellationToken);
            }
        }

        return kept.Concat(created).ToList();
    }

    public async Task<IReadOnlyList<ControlSchedule>> GetAllAsync(CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Schedules.AsNoTracking()
            .Include(item => item.JoinMapping)
            .ThenInclude(item => item!.Controller)
            .OrderBy(item => item.NextRunAtUtc)
            .ToListAsync(cancellationToken);
    }

    /// <summary>暂停或恢复整组任务；恢复时重算下一次执行时间。</summary>
    public async Task SetGroupEnabledAsync(Guid groupId, bool enabled, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var schedules = await LoadGroupAsync(db, groupId, cancellationToken);
        if (schedules.Count == 0)
        {
            throw new KeyNotFoundException("Schedule was not found.");
        }

        var scheduler = await schedulerFactory.GetScheduler(cancellationToken);
        foreach (var schedule in schedules)
        {
            if (enabled)
            {
                Resume(schedule);
                await ScheduleAsync(schedule, cancellationToken);
            }
            else
            {
                schedule.Enabled = false;
                schedule.NextRunAtUtc = null;
                await scheduler.DeleteJob(new JobKey(schedule.Id.ToString()), cancellationToken);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteGroupAsync(Guid groupId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var schedules = await LoadGroupAsync(db, groupId, cancellationToken);
        if (schedules.Count == 0)
        {
            throw new KeyNotFoundException("Schedule was not found.");
        }

        await DeleteAsync(db, schedules, cancellationToken);
    }

    public async Task DeleteAsync(Guid scheduleId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var schedules = await db.Schedules
            .Where(item => item.Id == scheduleId)
            .ToListAsync(cancellationToken);
        if (schedules.Count == 0)
        {
            throw new KeyNotFoundException("Schedule was not found.");
        }

        await DeleteAsync(db, schedules, cancellationToken);
    }

    public async Task DeleteForJoinMappingAsync(Guid mappingId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var schedules = await db.Schedules
            .Where(item => item.JoinMappingId == mappingId)
            .ToListAsync(cancellationToken);
        await DeleteAsync(db, schedules, cancellationToken);
    }

    public async Task DeleteForControllerAsync(Guid controllerId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var schedules = await db.Schedules
            .Where(item => item.JoinMapping != null && item.JoinMapping.ControllerId == controllerId)
            .ToListAsync(cancellationToken);
        await DeleteAsync(db, schedules, cancellationToken);
    }

    private static Task<List<ControlSchedule>> LoadGroupAsync(
        ControlDbContext db,
        Guid groupId,
        CancellationToken cancellationToken) =>
        db.Schedules
            .Where(item => item.GroupId == groupId || (item.GroupId == null && item.Id == groupId))
            .ToListAsync(cancellationToken);
    private async Task<List<JoinMapping>> LoadMappingsAsync(
        IReadOnlyList<Guid> mappingIds,
        CancellationToken cancellationToken)
    {
        var ids = mappingIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            throw new ArgumentException("At least one control point is required.");
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var mappings = await db.JoinMappings.AsNoTracking()
            .Include(item => item.Controller)
            .ThenInclude(item => item.Room)
            .Where(item => ids.Contains(item.Id))
            .ToListAsync(cancellationToken);
        if (mappings.Count != ids.Length)
        {
            throw new KeyNotFoundException("Join mapping was not found.");
        }
        if (mappings.Any(item => item.Kind != JoinKind.Digital))
        {
            throw new InvalidOperationException("Schedules require a digital control point.");
        }

        return mappings;
    }

    // 保留教室和 Join 名称快照，使映射删除后历史任务仍可识别。
    private static ControlSchedule NewSchedule(
        Guid groupId,
        JoinMapping mapping,
        string name,
        ScheduleKind kind,
        DateTimeOffset? runAtUtc,
        int weekdayMask,
        TimeSpan localTime)
    {
        var schedule = new ControlSchedule
        {
            GroupId = groupId,
            JoinMappingId = mapping.Id,
            JoinName = mapping.Name,
            RoomName = mapping.Controller.Room.Name,
            Enabled = true,
        };
        ApplyDefinition(schedule, name, kind, runAtUtc, weekdayMask, localTime);
        return schedule;
    }

    // 只更新任务定义；启停状态由暂停/恢复与新建流程决定。
    private static void ApplyDefinition(
        ControlSchedule schedule,
        string name,
        ScheduleKind kind,
        DateTimeOffset? runAtUtc,
        int weekdayMask,
        TimeSpan localTime)
    {
        schedule.Name = name;
        schedule.Kind = kind;
        schedule.RunAtUtc = kind == ScheduleKind.Once ? runAtUtc : null;
        schedule.WeekdayMask = kind == ScheduleKind.Weekly ? weekdayMask : 0;
        schedule.LocalTime = kind == ScheduleKind.Weekly ? localTime : TimeSpan.Zero;
        schedule.NextRunAtUtc = !schedule.Enabled
            ? null
            : kind == ScheduleKind.Once
                ? runAtUtc
                : WeeklyScheduleCalculator.GetNextOccurrence(
                    DateTimeOffset.UtcNow,
                    weekdayMask,
                    localTime);
    }

    private async Task ScheduleAsync(ControlSchedule schedule, CancellationToken cancellationToken)
    {
        var scheduler = await schedulerFactory.GetScheduler(cancellationToken);
        var jobKey = new JobKey(schedule.Id.ToString());
        await scheduler.DeleteJob(jobKey, cancellationToken);
        var job = JobBuilder.Create<ScheduledControlJob>()
            .WithIdentity(jobKey)
            .UsingJobData("scheduleId", schedule.Id.ToString())
            .Build();

        // 每次更新先删除同 ID 旧 Job，避免重复触发器残留。
        var triggerBuilder = TriggerBuilder.Create()
            .WithIdentity($"schedule-{schedule.Id}")
            .ForJob(job);
        if (schedule.Kind == ScheduleKind.Once)
        {
            triggerBuilder.StartAt(schedule.RunAtUtc!.Value);
        }
        else
        {
            var cron = BuildWeeklyCron(schedule.WeekdayMask, schedule.LocalTime);
            triggerBuilder
                .StartAt(DateTimeOffset.UtcNow)
                .WithCronSchedule(cron);
        }

        await scheduler.ScheduleJob(job, triggerBuilder.Build(), cancellationToken);
    }

    private static string BuildWeeklyCron(int weekdayMask, TimeSpan localTime)
    {
        // Quartz 的星期编号从 1（周日）开始，而应用位掩码从 DayOfWeek 的 0 开始。
        var days = Enumerable.Range(0, 7)
            .Where(day => (weekdayMask & (1 << day)) != 0)
            .Select(day => (day + 1).ToString())
            .ToArray();
        return $"{localTime.Seconds} {localTime.Minutes} {localTime.Hours} ? * {string.Join(",", days)}";
    }

    // 删除任务同时清理其执行记录与 Quartz Job。
    private async Task DeleteAsync(
        ControlDbContext db,
        IReadOnlyCollection<ControlSchedule> schedules,
        CancellationToken cancellationToken)
    {
        if (schedules.Count == 0)
        {
            return;
        }

        var ids = schedules.Select(item => item.Id).ToArray();
        await db.ScheduleExecutions
            .Where(item => ids.Contains(item.ScheduleId))
            .ExecuteDeleteAsync(cancellationToken);
        db.Schedules.RemoveRange(schedules);
        await db.SaveChangesAsync(cancellationToken);

        var scheduler = await schedulerFactory.GetScheduler(cancellationToken);
        foreach (var schedule in schedules)
        {
            await scheduler.DeleteJob(new JobKey(schedule.Id.ToString()), cancellationToken);
        }
    }

    // 恢复任务时重算下一次执行时间；一次性任务的时间已过则拒绝恢复。
    private static void Resume(ControlSchedule schedule)
    {
        if (schedule.Kind == ScheduleKind.Once)
        {
            if (schedule.RunAtUtc is not { } runAt || runAt <= DateTimeOffset.UtcNow)
            {
                throw new InvalidOperationException($"任务「{schedule.Name}」的一次性执行时间已过，请先修改执行时间再恢复。");
            }

            schedule.NextRunAtUtc = runAt;
        }
        else
        {
            schedule.NextRunAtUtc = WeeklyScheduleCalculator.GetNextOccurrence(
                DateTimeOffset.UtcNow,
                schedule.WeekdayMask,
                schedule.LocalTime);
        }

        schedule.Enabled = true;
    }

    private async Task RecordMissedOneTimeScheduleAsync(
        ControlDbContext db,
        ControlSchedule schedule,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        schedule.Enabled = false;
        schedule.NextRunAtUtc = null;
        if (schedule.RunAtUtc is not { } scheduledAt ||
            await db.ScheduleExecutions.AnyAsync(
                item => item.ScheduleId == schedule.Id && item.ScheduledAtUtc == scheduledAt,
                cancellationToken))
        {
            return;
        }

        db.ScheduleExecutions.Add(new ScheduleExecution
        {
            ScheduleId = schedule.Id,
            ScheduledAtUtc = scheduledAt,
            CompletedAtUtc = now,
            Status = "Failed",
            Error = "任务到期时平台未运行，未自动补发。"
        });
        logger.LogWarning("One-time schedule {ScheduleId} expired while the platform was stopped.", schedule.Id);
    }
}
