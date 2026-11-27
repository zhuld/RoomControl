using Microsoft.EntityFrameworkCore;
using RoomControl.Api.Data;
using RoomControl.Api.Models;

namespace RoomControl.Api.Services;

/// <summary>维护手动任务定义，并在总览按钮触发时批量下发控制脉冲、保存执行摘要。</summary>
public sealed class ManualTaskService(
    IDbContextFactory<ControlDbContext> contextFactory,
    IControlCommandSender connectionService,
    AuditLogWriter auditLogWriter,
    ILogger<ManualTaskService> logger)
{
    /// <summary>读取全部手动任务及其目标，供设置页与总览按钮渲染。</summary>
    public async Task<IReadOnlyList<ManualTask>> GetAllAsync(CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.ManualTasks.AsNoTracking()
            .Include(task => task.Targets)
            .ThenInclude(target => target.JoinMapping)
            .ThenInclude(mapping => mapping!.Controller)
            .OrderBy(task => task.SortOrder)
            .ThenBy(task => task.Name)
            .ToListAsync(cancellationToken);
    }

    /// <summary>创建手动任务；目标必须来自同一控制点名称，每个控制点保留教室名称快照。</summary>
    public async Task<ManualTask> CreateAsync(
        IReadOnlyList<Guid>? mappingIds,
        string name,
        bool confirmBeforeRun,
        CancellationToken cancellationToken)
    {
        var mappings = await LoadMappingsAsync(mappingIds, cancellationToken);
        var normalizedName = NormalizeName(name);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        // 手动任务显示为总览按钮，重名会让按钮无法区分。
        if (await db.ManualTasks.AnyAsync(item => item.Name == normalizedName, cancellationToken))
        {
            throw new InvalidOperationException($"手动任务「{normalizedName}」已存在，请使用其他名称。");
        }

        var task = new ManualTask
        {
            Name = normalizedName,
            JoinName = mappings[0].Name,
            ConfirmBeforeRun = confirmBeforeRun,
            SortOrder = (await db.ManualTasks.MaxAsync(item => (int?)item.SortOrder, cancellationToken) ?? -1) + 1
        };
        foreach (var mapping in mappings)
        {
            task.Targets.Add(NewTarget(task.Id, mapping));
        }
        db.ManualTasks.Add(task);
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(db, task.Id, cancellationToken);
    }

    /// <summary>覆盖任务定义与目标集合；执行记录保留。</summary>
    public async Task<ManualTask> UpdateAsync(
        Guid taskId,
        IReadOnlyList<Guid>? mappingIds,
        string name,
        bool confirmBeforeRun,
        CancellationToken cancellationToken)
    {
        var mappings = await LoadMappingsAsync(mappingIds, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var task = await db.ManualTasks
            .SingleOrDefaultAsync(item => item.Id == taskId, cancellationToken)
            ?? throw new KeyNotFoundException("手动任务不存在，可能已被删除。");

        var normalizedName = NormalizeName(name);
        if (await db.ManualTasks.AnyAsync(
                item => item.Id != taskId && item.Name == normalizedName,
                cancellationToken))
        {
            throw new InvalidOperationException($"手动任务「{normalizedName}」已存在，请使用其他名称。");
        }

        task.Name = normalizedName;
        task.JoinName = mappings[0].Name;
        task.ConfirmBeforeRun = confirmBeforeRun;

        // 定义更新以请求中的控制点集合为准，直接重建目标列表。
        await db.ManualTaskTargets
            .Where(item => item.ManualTaskId == taskId)
            .ExecuteDeleteAsync(cancellationToken);
        foreach (var mapping in mappings)
        {
            db.ManualTaskTargets.Add(NewTarget(task.Id, mapping));
        }

        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(db, task.Id, cancellationToken);
    }

    /// <summary>删除任务及其全部目标与执行记录。</summary>
    public async Task DeleteAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var task = await db.ManualTasks.SingleOrDefaultAsync(item => item.Id == taskId, cancellationToken)
            ?? throw new KeyNotFoundException("手动任务不存在，可能已被删除。");
        await DeleteTasksAsync(db, [task], cancellationToken);
    }

    /// <summary>按提交的完整顺序重排全部任务，让总览按钮与设置页列表保持一致。</summary>
    public async Task ReorderAsync(IReadOnlyList<Guid>? taskIds, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var tasks = await db.ManualTasks.ToListAsync(cancellationToken);
        if (tasks.Count == 0)
        {
            throw new KeyNotFoundException("还没有手动任务。");
        }

        var requestedIds = taskIds ?? [];
        if (requestedIds.Count != tasks.Count ||
            requestedIds.Distinct().Count() != tasks.Count ||
            requestedIds.Any(id => tasks.All(task => task.Id != id)))
        {
            throw new ArgumentException("排序必须包含全部手动任务且不能重复。");
        }

        var byId = tasks.ToDictionary(task => task.Id);
        for (var index = 0; index < requestedIds.Count; index++)
        {
            byId[requestedIds[index]].SortOrder = index;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>读取某任务最近的执行摘要，按开始时间倒序。</summary>
    public async Task<IReadOnlyList<ManualTaskExecution>> GetExecutionsAsync(
        Guid taskId,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.ManualTaskExecutions.AsNoTracking()
            .Where(item => item.ManualTaskId == taskId)
            .OrderByDescending(item => item.StartedAtUtc)
            .Take(100)
            .ToListAsync(cancellationToken);
    }

    /// <summary>逐个教室下发脉冲并记录汇总；单间失败不影响其余教室，每条指令单独写入操作记录。</summary>
    public async Task<ManualTaskRunResult> RunAsync(
        Guid taskId,
        string actor,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var task = await db.ManualTasks.AsNoTracking()
            .Include(item => item.Targets)
            .ThenInclude(target => target.JoinMapping)
            .SingleOrDefaultAsync(item => item.Id == taskId, cancellationToken)
            ?? throw new KeyNotFoundException("手动任务不存在，可能已被删除。");
        if (task.Targets.Count == 0)
        {
            throw new InvalidOperationException("手动任务没有可执行的控制点，请先编辑任务。");
        }

        var startedAt = DateTimeOffset.UtcNow;
        var results = new List<ManualTaskRunTargetResult>(task.Targets.Count);
        foreach (var target in task.Targets.OrderBy(item => item.RoomName, StringComparer.Ordinal))
        {
            if (target.JoinMappingId is not { } mappingId)
            {
                results.Add(new ManualTaskRunTargetResult(
                    target.Id,
                    target.RoomName,
                    target.JoinName,
                    "Failed",
                    "控制点已删除。"));
                await TryWriteCommandAuditAsync(
                    task,
                    target,
                    null,
                    "执行手动任务（控制点已删除）",
                    AuditLogResults.TargetMissing,
                    actor);
                continue;
            }

            var sendJoinNumber = target.JoinMapping?.SendJoinNumber;
            try
            {
                // 手动任务由总览按钮触发，与自动任务一致，不受使能 Join 限制。
                await connectionService.SendDigitalPulseAsync(mappingId, enforceEnableJoin: false, cancellationToken);
                results.Add(new ManualTaskRunTargetResult(
                    target.Id,
                    target.RoomName,
                    target.JoinName,
                    "Succeeded",
                    null));
                await TryWriteCommandAuditAsync(
                    task,
                    target,
                    sendJoinNumber,
                    "执行手动任务",
                    AuditLogResults.CommandAccepted,
                    actor);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Manual task {TaskId} failed for target {TargetId}.", task.Id, target.Id);
                results.Add(new ManualTaskRunTargetResult(
                    target.Id,
                    target.RoomName,
                    target.JoinName,
                    "Failed",
                    exception.Message));
                await TryWriteCommandAuditAsync(
                    task,
                    target,
                    sendJoinNumber,
                    "执行手动任务",
                    AuditLogResults.FromException(exception),
                    actor);
            }
        }

        var succeededCount = results.Count(item => item.Status == "Succeeded");
        var failedCount = results.Count - succeededCount;
        var completedAt = DateTimeOffset.UtcNow;
        var execution = new ManualTaskExecution
        {
            ManualTaskId = task.Id,
            StartedAtUtc = startedAt,
            CompletedAtUtc = completedAt,
            SucceededCount = succeededCount,
            FailedCount = failedCount,
            Status = failedCount == 0 ? "Succeeded" : succeededCount == 0 ? "Failed" : "Partial",
            Error = FormatErrorSummary(results)
        };
        db.ManualTaskExecutions.Add(execution);
        await db.SaveChangesAsync(cancellationToken);

        return new ManualTaskRunResult(
            execution.Id,
            task.Name,
            startedAt,
            completedAt,
            succeededCount,
            failedCount,
            results);
    }

    /// <summary>控制点被删除时移除对应目标，并清理不再覆盖任何教室的任务。</summary>
    public async Task DeleteForJoinMappingAsync(Guid mappingId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var targets = await db.ManualTaskTargets
            .Where(item => item.JoinMappingId == mappingId)
            .ToListAsync(cancellationToken);
        if (targets.Count == 0)
        {
            return;
        }

        var taskIds = targets.Select(item => item.ManualTaskId).Distinct().ToArray();
        db.ManualTaskTargets.RemoveRange(targets);
        await db.SaveChangesAsync(cancellationToken);

        var emptyTasks = await db.ManualTasks
            .Where(item => taskIds.Contains(item.Id) && !item.Targets.Any())
            .ToListAsync(cancellationToken);
        await DeleteTasksAsync(db, emptyTasks, cancellationToken);
    }

    /// <summary>中控或教室被删除时移除其控制点上的目标，并清理不再覆盖任何教室的任务。</summary>
    public async Task DeleteForControllerAsync(Guid controllerId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var mappingIds = await db.JoinMappings.AsNoTracking()
            .Where(item => item.ControllerId == controllerId)
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);
        if (mappingIds.Count == 0)
        {
            return;
        }

        var targets = await db.ManualTaskTargets
            .Where(item => item.JoinMappingId != null && mappingIds.Contains(item.JoinMappingId.Value))
            .ToListAsync(cancellationToken);
        if (targets.Count == 0)
        {
            return;
        }

        var taskIds = targets.Select(item => item.ManualTaskId).Distinct().ToArray();
        db.ManualTaskTargets.RemoveRange(targets);
        await db.SaveChangesAsync(cancellationToken);

        var emptyTasks = await db.ManualTasks
            .Where(item => taskIds.Contains(item.Id) && !item.Targets.Any())
            .ToListAsync(cancellationToken);
        await DeleteTasksAsync(db, emptyTasks, cancellationToken);
    }

    /// <summary>操作记录是旁路留痕：写入失败只记服务端日志，不影响其余教室继续下发。</summary>
    private async Task TryWriteCommandAuditAsync(
        ManualTask task,
        ManualTaskTarget target,
        int? sendJoinNumber,
        string action,
        int statusCode,
        string actor)
    {
        try
        {
            await auditLogWriter.WriteAsync(
                AuditLogCategories.Control,
                sendJoinNumber is { } number ? $"{action} Join {number}" : action,
                $"教室「{target.RoomName}」· 控制点「{target.JoinName}」· 手动任务「{task.Name}」",
                actor,
                statusCode);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Failed to write command audit log for manual task {TaskId}.",
                task.Id);
        }
    }

    private static async Task<ManualTask> GetAsync(
        ControlDbContext db,
        Guid taskId,
        CancellationToken cancellationToken) =>
        await db.ManualTasks.AsNoTracking()
            .Include(task => task.Targets)
            .ThenInclude(target => target.JoinMapping)
            .ThenInclude(mapping => mapping!.Controller)
            .SingleAsync(task => task.Id == taskId, cancellationToken);

    private async Task<List<JoinMapping>> LoadMappingsAsync(
        IReadOnlyList<Guid>? mappingIds,
        CancellationToken cancellationToken)
    {
        var ids = (mappingIds ?? []).Distinct().ToArray();
        if (ids.Length == 0)
        {
            throw new ArgumentException("手动任务至少需要一个控制点。");
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var mappings = await db.JoinMappings.AsNoTracking()
            .Include(item => item.Controller)
            .ThenInclude(controller => controller.Room)
            .Where(item => ids.Contains(item.Id))
            .ToListAsync(cancellationToken);
        if (mappings.Count != ids.Length)
        {
            throw new KeyNotFoundException("控制点不存在，可能已被删除。");
        }
        if (mappings.Any(item => item.Kind != JoinKind.Digital))
        {
            throw new InvalidOperationException("手动任务需要数字控制点。");
        }

        // 一个按钮只对应一个控制点；多间教室必须使用同名控制点。
        if (mappings.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() > 1)
        {
            throw new ArgumentException("一个手动任务只能对应同名控制点，请调整教室范围。");
        }

        return mappings;
    }

    private static ManualTaskTarget NewTarget(Guid taskId, JoinMapping mapping) =>
        new()
        {
            ManualTaskId = taskId,
            JoinMappingId = mapping.Id,
            RoomName = mapping.Controller.Room.Name,
            JoinName = mapping.Name
        };

    private static string NormalizeName(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 || trimmed.Length > 120)
        {
            throw new ArgumentException("手动任务名称必填且不能超过 120 个字符。");
        }

        return trimmed;
    }

    private static string? FormatErrorSummary(IEnumerable<ManualTaskRunTargetResult> results)
    {
        var failures = results
            .Where(item => item.Status == "Failed")
            .Select(item => $"{item.RoomName}：{item.Error ?? "发送失败"}")
            .ToArray();
        if (failures.Length == 0)
        {
            return null;
        }

        var summary = string.Join("；", failures);
        return summary.Length <= 2000 ? summary : $"{summary[..1997]}…";
    }

    private static async Task DeleteTasksAsync(
        ControlDbContext db,
        IReadOnlyCollection<ManualTask> tasks,
        CancellationToken cancellationToken)
    {
        if (tasks.Count == 0)
        {
            return;
        }

        var ids = tasks.Select(item => item.Id).ToArray();
        await db.ManualTaskExecutions
            .Where(item => ids.Contains(item.ManualTaskId))
            .ExecuteDeleteAsync(cancellationToken);
        await db.ManualTaskTargets
            .Where(item => ids.Contains(item.ManualTaskId))
            .ExecuteDeleteAsync(cancellationToken);
        db.ManualTasks.RemoveRange(tasks);
        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>单个教室的下发结果。</summary>
public sealed record ManualTaskRunTargetResult(
    Guid TargetId,
    string RoomName,
    string JoinName,
    string Status,
    string? Error);

/// <summary>一次手动任务执行的结果与逐教室明细。</summary>
public sealed record ManualTaskRunResult(
    Guid RunId,
    string TaskName,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    int SucceededCount,
    int FailedCount,
    IReadOnlyList<ManualTaskRunTargetResult> Targets);
