using Microsoft.EntityFrameworkCore;
using Quartz;
using RoomControl.Api.Contracts;
using RoomControl.Api.Data;
using RoomControl.Api.Integrations;
using RoomControl.Api.Models;

namespace RoomControl.Api.Services;

/// <summary>
/// 以 JSON 备份站点配置，并在导入时整体替换现有配置。
/// 账号、操作日志与任务执行记录不属于配置，始终不参与导入导出。
/// </summary>
public sealed class ConfigTransferService(
    IDbContextFactory<ControlDbContext> contextFactory,
    ScheduleService scheduleService,
    ISchedulerFactory schedulerFactory,
    ILogger<ConfigTransferService> logger)
{
    /// <summary>读取全部业务配置并整理成可移植文档。</summary>
    public async Task<ConfigDocument> ExportAsync(CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await db.SiteSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == SiteSettings.SingletonId, cancellationToken);

        var buildings = await db.Buildings.AsNoTracking()
            .Include(building => building.Floors)
            .OrderBy(building => building.Name)
            .ToListAsync(cancellationToken);

        var rooms = await db.Rooms.AsNoTracking()
            .Include(room => room.Controller)
            .ThenInclude(controller => controller!.JoinMappings)
            .OrderBy(room => room.Building)
            .ThenBy(room => room.Floor)
            .ThenBy(room => room.Name)
            .ToListAsync(cancellationToken);

        var schedules = await db.Schedules.AsNoTracking()
            .Include(schedule => schedule.JoinMapping)
            .ThenInclude(mapping => mapping!.Controller)
            .ThenInclude(controller => controller.Room)
            .OrderBy(schedule => schedule.Name)
            .ThenBy(schedule => schedule.RoomName)
            .ToListAsync(cancellationToken);

        var manualTasks = await db.ManualTasks.AsNoTracking()
            .Include(task => task.Targets)
            .ThenInclude(target => target.JoinMapping)
            .ThenInclude(mapping => mapping!.Controller)
            .ThenInclude(controller => controller.Room)
            .OrderBy(task => task.SortOrder)
            .ThenBy(task => task.Name)
            .ToListAsync(cancellationToken);

        return new ConfigDocument(
            ConfigDocumentVersion.Current,
            DateTimeOffset.UtcNow,
            new ConfigBranding(
                settings?.DisplayName ?? SiteSettings.DefaultDisplayName,
                settings?.Subtitle ?? SiteSettings.DefaultSubtitle,
                settings?.LogoDataUrl),
            buildings
                .Select(building => new ConfigBuilding(
                    building.Name,
                    building.Floors
                        .OrderBy(floor => floor.Name)
                        .Select(floor => floor.Name)
                        .ToList()))
                .ToList(),
            rooms
                .Select(room => new ConfigRoom(
                    room.Name,
                    room.Building,
                    room.Floor,
                    room.Controller is null
                        ? null
                        : new ConfigController(
                            room.Controller.AdapterType,
                            room.Controller.Host,
                            room.Controller.Port,
                            room.Controller.Ipid,
                            room.Controller.Enabled,
                            room.Controller.JoinMappings
                                .Where(join => join.Kind == JoinKind.Digital)
                                .OrderBy(join => join.SortOrder)
                                .ThenBy(join => join.Name)
                                .Select(join => new ConfigJoin(
                                    join.Name,
                                    join.SendJoinNumber,
                                    join.StateJoinNumber,
                                    join.EnableJoinNumber,
                                    join.ShowConfirmationPopup))
                                .ToList())))
                .ToList(),
            ExportSchedules(schedules),
            manualTasks
                .Select(task => new ConfigManualTask(
                    task.Name,
                    task.ConfirmBeforeRun,
                    task.Targets
                        .Select(target => ToTarget(target.JoinMapping))
                        .OfType<ConfigTarget>()
                        .ToList()))
                .Where(task => task.Targets is { Count: > 0 })
                .ToList());
    }

    /// <summary>用文件内容整体替换现有配置；失败时不修改数据库。</summary>
    public async Task<ConfigImportSummary> ReplaceAsync(ConfigDocument? document, CancellationToken cancellationToken)
    {
        var plan = BuildPlan(document);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        // 旧任务删除后 Quartz Job 仍会按原计划触发，先记下 ID 供导入完成后清理。
        var previousScheduleIds = await db.Schedules.AsNoTracking()
            .Select(schedule => schedule.Id)
            .ToListAsync(cancellationToken);

        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            // 从子表到父表依次清空，兼容开启外键约束的 SQLite 与 PostgreSQL。
            await db.ScheduleExecutions.ExecuteDeleteAsync(cancellationToken);
            await db.Schedules.ExecuteDeleteAsync(cancellationToken);
            await db.ManualTaskExecutions.ExecuteDeleteAsync(cancellationToken);
            await db.ManualTaskTargets.ExecuteDeleteAsync(cancellationToken);
            await db.ManualTasks.ExecuteDeleteAsync(cancellationToken);
            await db.JoinSnapshots.ExecuteDeleteAsync(cancellationToken);
            await db.JoinMappings.ExecuteDeleteAsync(cancellationToken);
            await db.Controllers.ExecuteDeleteAsync(cancellationToken);
            await db.Rooms.ExecuteDeleteAsync(cancellationToken);
            await db.Floors.ExecuteDeleteAsync(cancellationToken);
            await db.Buildings.ExecuteDeleteAsync(cancellationToken);

            db.Buildings.AddRange(plan.Buildings);
            db.Floors.AddRange(plan.Floors);
            db.Rooms.AddRange(plan.Rooms);
            db.Controllers.AddRange(plan.Controllers);
            db.JoinMappings.AddRange(plan.Joins);
            db.Schedules.AddRange(plan.Schedules);
            db.ManualTasks.AddRange(plan.ManualTasks);
            db.ManualTaskTargets.AddRange(plan.ManualTargets);

            var settings = await db.SiteSettings
                .SingleOrDefaultAsync(item => item.Id == SiteSettings.SingletonId, cancellationToken);
            if (settings is null)
            {
                db.SiteSettings.Add(plan.Settings);
            }
            else
            {
                // 保留日志保留天数等非配置字段，仅覆盖品牌信息。
                settings.DisplayName = plan.Settings.DisplayName;
                settings.Subtitle = plan.Settings.Subtitle;
                settings.LogoDataUrl = plan.Settings.LogoDataUrl;
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        // 配置已提交；调度重建失败时保留新配置并记录日志，避免向界面返回误导性的失败。
        try
        {
            var scheduler = await schedulerFactory.GetScheduler(cancellationToken);
            foreach (var scheduleId in previousScheduleIds)
            {
                await scheduler.DeleteJob(new JobKey(scheduleId.ToString()), cancellationToken);
            }
            // 按导入后的定义重建调度；过期的一次性任务已在导入时停用，不会补发。
            await scheduleService.RestoreAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Failed to rebuild schedule jobs after configuration import.");
        }

        logger.LogInformation(
            "Configuration imported: {Buildings} buildings, {Rooms} rooms, {Controllers} controllers, {Joins} joins, {Schedules} schedules, {ManualTasks} manual tasks.",
            plan.Buildings.Count,
            plan.Rooms.Count,
            plan.Controllers.Count,
            plan.Joins.Count,
            plan.Schedules.Count,
            plan.ManualTasks.Count);

        return new ConfigImportSummary(
            plan.Buildings.Count,
            plan.Rooms.Count,
            plan.Controllers.Count,
            plan.Joins.Count,
            plan.Schedules.Count,
            plan.ManualTasks.Count);
    }

    private static IReadOnlyList<ConfigSchedule> ExportSchedules(IReadOnlyList<ControlSchedule> schedules)
    {
        var result = new List<ConfigSchedule>();
        // 同一计划任务的多间教室共享 GroupId，导出时合并回一个任务。
        foreach (var group in schedules.GroupBy(schedule => schedule.GroupId ?? schedule.Id))
        {
            var rows = group
                .Select(schedule => (Schedule: schedule, Target: ToTarget(schedule.JoinMapping)))
                .Where(row => row.Target is not null)
                .ToList();
            if (rows.Count == 0)
            {
                continue;
            }

            var first = rows[0].Schedule;
            result.Add(new ConfigSchedule(
                first.Name,
                first.Kind,
                first.RunAtUtc,
                first.WeekdayMask,
                first.LocalTime,
                rows.All(row => row.Schedule.Enabled),
                rows.Select(row => row.Target!).ToList()));
        }

        return result;
    }

    private static ConfigTarget? ToTarget(JoinMapping? mapping)
    {
        var room = mapping?.Controller?.Room;
        return mapping is null || room is null
            ? null
            : new ConfigTarget(room.Building, room.Floor, room.Name, mapping.Name);
    }

    private static ImportPlan BuildPlan(ConfigDocument? document)
    {
        if (document is null)
        {
            throw new ArgumentException("导入文件为空或不是有效的配置 JSON。");
        }
        if (document.Version != ConfigDocumentVersion.Current)
        {
            throw new ArgumentException($"不支持的配置文件版本（{document.Version}），请使用当前版本导出的文件。");
        }

        var buildings = new List<Building>();
        var floors = new List<Floor>();
        var buildingNames = new HashSet<string>(StringComparer.Ordinal);
        var locationKeys = new HashSet<(string Building, string Floor)>();
        foreach (var entry in document.Buildings ?? [])
        {
            var buildingName = ValidateName(entry?.Name, 60, "楼栋");
            if (!buildingNames.Add(buildingName))
            {
                throw new ArgumentException($"楼栋「{buildingName}」重复出现。");
            }

            var building = new Building { Name = buildingName };
            buildings.Add(building);
            var floorNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var floorEntry in entry?.Floors ?? [])
            {
                var floorName = ValidateName(floorEntry, 60, "楼层");
                if (!floorNames.Add(floorName))
                {
                    throw new ArgumentException($"楼栋「{buildingName}」下的楼层「{floorName}」重复出现。");
                }

                locationKeys.Add((buildingName, floorName));
                floors.Add(new Floor { BuildingId = building.Id, Name = floorName });
            }
        }

        var rooms = new List<Room>();
        var controllers = new List<Controller>();
        var joins = new List<JoinMapping>();
        var mappingsByTarget = new Dictionary<
            (string Building, string Floor, string Room, string Join),
            (JoinMapping Mapping, string RoomName)>();
        var roomKeys = new HashSet<(string Building, string Floor, string Room)>();
        foreach (var entry in document.Rooms ?? [])
        {
            var roomName = ValidateName(entry?.Name, 120, "教室");
            var buildingName = entry?.Building?.Trim() ?? string.Empty;
            var floorName = entry?.Floor?.Trim() ?? string.Empty;
            if (buildingName.Length > 60 || floorName.Length > 60)
            {
                throw new ArgumentException($"教室「{roomName}」的楼栋与楼层不能超过 60 个字符。");
            }
            if ((buildingName.Length == 0) != (floorName.Length == 0))
            {
                throw new ArgumentException($"教室「{roomName}」的楼栋与楼层需要同时填写或同时留空。");
            }
            if (buildingName.Length > 0 && !locationKeys.Contains((buildingName, floorName)))
            {
                throw new ArgumentException(
                    $"教室「{roomName}」引用的位置「{buildingName} · {floorName}」未在配置文件的楼栋与楼层中定义。");
            }
            if (!roomKeys.Add((buildingName, floorName, roomName)))
            {
                throw new ArgumentException($"教室「{roomName}」在「{buildingName} · {floorName}」中重复出现。");
            }

            var room = new Room { Name = roomName, Building = buildingName, Floor = floorName };
            rooms.Add(room);

            if (entry?.Controller is not { } controllerEntry)
            {
                continue;
            }

            var adapterType = ControllerAdapterTypes.Find(controllerEntry.AdapterType)
                ?? throw new ArgumentException($"教室「{roomName}」的中控类型「{controllerEntry.AdapterType}」不受支持。");
            var host = controllerEntry.Host?.Trim() ?? string.Empty;
            if (host.Length == 0)
            {
                throw new ArgumentException($"教室「{roomName}」的中控主机地址必填。");
            }
            if (host.Length > 255)
            {
                throw new ArgumentException($"教室「{roomName}」的中控主机地址不能超过 255 个字符。");
            }
            if (controllerEntry.Port is < 1 or > 65535 || controllerEntry.Ipid is < 0 or > 255)
            {
                throw new ArgumentException($"教室「{roomName}」的中控端口需在 1–65535 之间，IPID 需在 0–255 之间。");
            }

            var controller = new Controller
            {
                RoomId = room.Id,
                AdapterType = adapterType.Key,
                Name = adapterType.DisplayName,
                Host = host,
                Port = controllerEntry.Port,
                Ipid = controllerEntry.Ipid,
                Framing = CipFraming.Raw,
                Enabled = controllerEntry.Enabled,
            };
            controllers.Add(controller);

            var joinNames = new HashSet<string>(StringComparer.Ordinal);
            var sendJoinNumbers = new HashSet<int>();
            var sortOrder = 0;
            foreach (var joinEntry in controllerEntry.Joins ?? [])
            {
                var joinName = ValidateName(joinEntry?.Name, 120, "控制点");
                if (!joinNames.Add(joinName))
                {
                    throw new ArgumentException($"教室「{roomName}」的控制点「{joinName}」重复出现。");
                }
                if (joinEntry!.SendJoinNumber is < 1 or > 32768 ||
                    joinEntry.StateJoinNumber is < 1 or > 32768 ||
                    joinEntry.EnableJoinNumber is < 0 or > 32768)
                {
                    throw new ArgumentException(
                        $"控制点「{joinName}」的发送与状态 Join 需在 1–32768 之间，使能 Join 可为 0 或 1–32768。");
                }
                if (!sendJoinNumbers.Add(joinEntry.SendJoinNumber))
                {
                    throw new ArgumentException(
                        $"教室「{roomName}」的发送 Join {joinEntry.SendJoinNumber} 被多个控制点重复使用。");
                }

                var mapping = new JoinMapping
                {
                    ControllerId = controller.Id,
                    Name = joinName,
                    Kind = JoinKind.Digital,
                    SendJoinNumber = joinEntry.SendJoinNumber,
                    StateJoinNumber = joinEntry.StateJoinNumber,
                    EnableJoinNumber = joinEntry.EnableJoinNumber,
                    ShowConfirmationPopup = joinEntry.ShowConfirmationPopup,
                    // 文件中的控制点顺序即控制台按钮顺序。
                    SortOrder = sortOrder++,
                };
                joins.Add(mapping);
                mappingsByTarget[(buildingName, floorName, roomName, joinName)] = (mapping, roomName);
            }
        }

        var now = DateTimeOffset.UtcNow;
        var schedules = new List<ControlSchedule>();
        var scheduleNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in document.Schedules ?? [])
        {
            var name = ValidateName(entry?.Name, 120, "自动任务");
            if (!scheduleNames.Add(name))
            {
                throw new ArgumentException($"自动任务「{name}」重复出现。");
            }
            if (entry is null || !Enum.IsDefined(entry.Kind))
            {
                throw new ArgumentException($"自动任务「{name}」的类型不受支持。");
            }

            DateTimeOffset? runAtUtc = null;
            if (entry.Kind == ScheduleKind.Once)
            {
                runAtUtc = entry.RunAtUtc
                    ?? throw new ArgumentException($"一次性任务「{name}」缺少执行时间。");
            }
            else if (entry.WeekdayMask is < 1 or > 127 ||
                     entry.LocalTime < TimeSpan.Zero ||
                     entry.LocalTime >= TimeSpan.FromDays(1))
            {
                throw new ArgumentException($"每周任务「{name}」需要选择重复日与执行时间。");
            }

            var targets = ResolveTargets(entry.Targets, mappingsByTarget, $"自动任务「{name}」");
            // 已过期的一次性任务在导入时停用，避免恢复配置后立即补发。
            var enabled = entry.Enabled && (entry.Kind != ScheduleKind.Once || runAtUtc > now);
            var groupId = Guid.NewGuid();
            foreach (var target in targets)
            {
                schedules.Add(new ControlSchedule
                {
                    GroupId = groupId,
                    JoinMappingId = target.Mapping.Id,
                    RoomName = target.RoomName,
                    JoinName = target.Mapping.Name,
                    Name = name,
                    Enabled = enabled,
                    Kind = entry.Kind,
                    RunAtUtc = entry.Kind == ScheduleKind.Once ? runAtUtc : null,
                    WeekdayMask = entry.Kind == ScheduleKind.Weekly ? entry.WeekdayMask : 0,
                    LocalTime = entry.Kind == ScheduleKind.Weekly ? entry.LocalTime : TimeSpan.Zero,
                    NextRunAtUtc = !enabled
                        ? null
                        : entry.Kind == ScheduleKind.Once
                            ? runAtUtc
                            : WeeklyScheduleCalculator.GetNextOccurrence(now, entry.WeekdayMask, entry.LocalTime),
                });
            }
        }

        var manualTasks = new List<ManualTask>();
        var manualTargets = new List<ManualTaskTarget>();
        var manualTaskNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in document.ManualTasks ?? [])
        {
            var name = ValidateName(entry?.Name, 120, "手动任务");
            if (!manualTaskNames.Add(name))
            {
                throw new ArgumentException($"手动任务「{name}」重复出现。");
            }

            var targets = ResolveTargets(entry?.Targets, mappingsByTarget, $"手动任务「{name}」");
            var joinName = targets[0].Mapping.Name;
            if (targets.Any(target => !string.Equals(target.Mapping.Name, joinName, StringComparison.Ordinal)))
            {
                throw new ArgumentException($"手动任务「{name}」的目标必须使用同名控制点。");
            }

            var task = new ManualTask
            {
                Name = name,
                JoinName = joinName,
                ConfirmBeforeRun = entry!.ConfirmBeforeRun,
                SortOrder = manualTasks.Count,
            };
            manualTasks.Add(task);
            foreach (var target in targets)
            {
                manualTargets.Add(new ManualTaskTarget
                {
                    ManualTaskId = task.Id,
                    JoinMappingId = target.Mapping.Id,
                    RoomName = target.RoomName,
                    JoinName = target.Mapping.Name,
                });
            }
        }

        return new ImportPlan(
            BuildSettings(document.Branding),
            buildings,
            floors,
            rooms,
            controllers,
            joins,
            schedules,
            manualTasks,
            manualTargets);
    }

    private static SiteSettings BuildSettings(ConfigBranding? branding)
    {
        var displayName = branding?.DisplayName?.Trim() ?? string.Empty;
        if (displayName.Length == 0 || displayName.Length > 60)
        {
            throw new ArgumentException("站点名称必填且不能超过 60 个字符。");
        }

        var subtitle = branding?.Subtitle?.Trim() ?? string.Empty;
        if (subtitle.Length > 80)
        {
            throw new ArgumentException("副标题不能超过 80 个字符。");
        }

        var logo = string.IsNullOrWhiteSpace(branding?.LogoDataUrl) ? null : branding.LogoDataUrl;
        if (logo is not null &&
            (logo.Length > 300_000 || !logo.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("Logo 必须是上传的图片且不能超过 200 KB。");
        }

        return new SiteSettings
        {
            Id = SiteSettings.SingletonId,
            DisplayName = displayName,
            Subtitle = subtitle,
            LogoDataUrl = logo,
        };
    }

    private static IReadOnlyList<(JoinMapping Mapping, string RoomName)> ResolveTargets(
        IReadOnlyList<ConfigTarget>? targets,
        IReadOnlyDictionary<
            (string Building, string Floor, string Room, string Join),
            (JoinMapping Mapping, string RoomName)> mappings,
        string owner)
    {
        if (targets is null || targets.Count == 0)
        {
            throw new ArgumentException($"{owner}至少需要一个控制点。");
        }

        var resolved = new List<(JoinMapping Mapping, string RoomName)>(targets.Count);
        var seen = new HashSet<Guid>();
        foreach (var target in targets)
        {
            if (target is null)
            {
                throw new ArgumentException($"{owner}包含无效的控制点。");
            }

            var key = (
                Building: target.Building?.Trim() ?? string.Empty,
                Floor: target.Floor?.Trim() ?? string.Empty,
                Room: target.Room?.Trim() ?? string.Empty,
                Join: target.Join?.Trim() ?? string.Empty);
            if (!mappings.TryGetValue(key, out var found))
            {
                throw new ArgumentException(
                    $"{owner}引用的控制点「{key.Building} · {key.Floor} · {key.Room} · {key.Join}」不在配置文件的教室配置中。");
            }
            if (seen.Add(found.Mapping.Id))
            {
                resolved.Add(found);
            }
        }

        return resolved;
    }

    private static string ValidateName(string? value, int maxLength, string label)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 || trimmed.Length > maxLength)
        {
            throw new ArgumentException($"{label}名称必填且不能超过 {maxLength} 个字符。");
        }

        return trimmed;
    }

    private sealed record ImportPlan(
        SiteSettings Settings,
        IReadOnlyList<Building> Buildings,
        IReadOnlyList<Floor> Floors,
        IReadOnlyList<Room> Rooms,
        IReadOnlyList<Controller> Controllers,
        IReadOnlyList<JoinMapping> Joins,
        IReadOnlyList<ControlSchedule> Schedules,
        IReadOnlyList<ManualTask> ManualTasks,
        IReadOnlyList<ManualTaskTarget> ManualTargets);
}
