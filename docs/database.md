# RoomControl 数据库表结构

本文档列出 RoomControl 的全部数据库表及其职责、关键列、约束与删除行为，便于排查数据问题与评估结构变更。

- 实体与映射定义：[ControlDbContext.cs](../backend/RoomControl.Api/Data/ControlDbContext.cs)
- 实体模型：`backend/RoomControl.Api/Models/`
- 增量升级 DDL：[SchemaUpgrader.cs](../backend/RoomControl.Api/Data/SchemaUpgrader.cs)
- 接口字段与业务规则：[docs/api.md](./api.md)

## 目录

- [1. 维护方式](#1-维护方式)
- [2. 表清单](#2-表清单)
- [3. 位置与教室](#3-位置与教室)
- [4. 控制点与设备状态](#4-控制点与设备状态)
- [5. 自动任务](#5-自动任务)
- [6. 手动任务](#6-手动任务)
- [7. 站点、账号与审计](#7-站点账号与审计)
- [8. 关系与删除行为](#8-关系与删除行为)
- [9. 通用约定](#9-通用约定)
- [10. 变更须知](#10-变更须知)

## 1. 维护方式

- 数据库结构由 EF Core 的 `EnsureCreatedAsync()` 首次创建，**不使用迁移包**，因此库中不存在 `__EFMigrationsHistory` 表。
- 已有数据库的增量变更由 `SchemaUpgrader.UpgradeAsync()` 在每次启动时以幂等 DDL 补齐，分别提供 SQLite 与 PostgreSQL 两套语句。
- 表名由 EF 按 `DbSet` 复数化生成（如 `Schedules`、`ManualTaskTargets`）；列名为 PascalCase。
- 支持两种 Provider：`Sqlite`（默认开发/单站点部署）与 `PostgreSQL`（`Database:Provider`），两者表结构等价。

## 2. 表清单

| 表 | 实体 | 职责 |
| --- | --- | --- |
| `Buildings` | `Building` | 楼栋选项 |
| `Floors` | `Floor` | 楼层选项（归属楼栋） |
| `Rooms` | `Room` | 教室 |
| `Controllers` | `Controller` | 教室接入的中控设备与连接配置 |
| `JoinMappings` | `JoinMapping` | 控制点（界面按钮 ↔ Join 映射） |
| `JoinSnapshots` | `JoinSnapshot` | 设备最近一次上报的 Join 值 |
| `Schedules` | `ControlSchedule` | 自动（定时）任务的执行目标 |
| `ScheduleExecutions` | `ScheduleExecution` | 自动任务的执行记录 |
| `ManualTasks` | `ManualTask` | 手动任务定义 |
| `ManualTaskTargets` | `ManualTaskTarget` | 手动任务覆盖的教室与控制点 |
| `ManualTaskExecutions` | `ManualTaskExecution` | 手动任务的执行摘要 |
| `SiteSettings` | `SiteSettings` | 站点品牌与日志保留天数（单行表） |
| `Users` | `User` | 登录账号 |
| `AuditLogs` | `AuditLog` | 操作日志（追加写） |

共 14 张表。

## 3. 位置与教室

### `Buildings` 楼栋

| 列 | 类型 | 说明 |
| --- | --- | --- |
| `Id` | GUID / uuid | 主键 |
| `Name` | string(60) | 楼栋名称，全局唯一（唯一索引 `IX_Buildings_Name`） |

1 栋楼 → 多个楼层（`Floors.BuildingId`，级联删除）。

### `Floors` 楼层

| 列 | 类型 | 说明 |
| --- | --- | --- |
| `Id` | GUID / uuid | 主键 |
| `BuildingId` | GUID / uuid | 所属楼栋，FK → `Buildings`（Cascade） |
| `Name` | string(60) | 楼层名称，同一楼栋内唯一（唯一索引 `IX_Floors_BuildingId_Name`） |

### `Rooms` 教室

| 列 | 类型 | 说明 |
| --- | --- | --- |
| `Id` | GUID / uuid | 主键 |
| `Name` | string(120) | 教室名称 |
| `Building` | string(60) | 楼栋名称**快照**；重命名楼栋时由接口同步更新 |
| `Floor` | string(60) | 楼层名称**快照**；重命名楼层时由接口同步更新 |

1 教室 ↔ 0..1 中控（`Controllers.RoomId` 唯一，级联删除）。

### `Controllers` 中控

| 列 | 类型 | 说明 |
| --- | --- | --- |
| `Id` | GUID / uuid | 主键 |
| `RoomId` | GUID / uuid | 所属教室，FK → `Rooms`（Cascade），每间教室最多一台 |
| `Name` | string(120) | 显示名称，由适配器类型决定 |
| `AdapterType` | string | 适配器键，默认 `crestron-cip` |
| `Host` | string(255) | 中控地址 |
| `Port` | int | 端口，默认 `41794` |
| `Framing` | int（枚举） | 报文封装，当前仅 `Raw` |
| `Ipid` | int | CIP 标识（0–255），默认 `3` |
| `Enabled` | bool | 是否允许后台建立连接 |
| `IsOnline` | bool | 运行态：当前是否已连接；进程启动时统一重置为 `false` |
| `LastError` | string \| null | 运行态：最近一次连接失败原因，连接成功后清空 |
| `UpdatedAtUtc` | 时间 | 状态最后更新时间 |

## 4. 控制点与设备状态

### `JoinMappings` 控制点

| 列 | 类型 | 说明 |
| --- | --- | --- |
| `Id` | GUID / uuid | 主键 |
| `ControllerId` | GUID / uuid | 所属中控，FK → `Controllers`（级联删除） |
| `Name` | string(120) | 控制点名称（界面按钮文字） |
| `Kind` | int（枚举） | 信号类型，当前仅 `Digital` |
| `JoinNumber` | int | **发送** Join 编号（实体属性为 `SendJoinNumber`，列名保留历史值） |
| `StateJoinNumber` | int | 状态接收 Join，默认 `1` |
| `EnableJoinNumber` | int | 使能接收 Join，`0` 表示未配置（手动操作默认放行） |
| `ShowConfirmationPopup` | bool | 下发/删除前是否二次确认，默认 `true` |
| `SortOrder` | int | 控制台按钮顺序，由配置页拖拽维护，默认 `0` |

唯一索引 `(ControllerId, Kind, JoinNumber)`：同一中控下同类型发送 Join 不可重复。

### `JoinSnapshots` Join 状态快照

| 列 | 类型 | 说明 |
| --- | --- | --- |
| `ControllerId` | GUID / uuid | 复合主键之一，所属中控 |
| `Kind` | int（枚举） | 复合主键之一，信号类型 |
| `JoinNumber` | int | 复合主键之一，设备上报的 Join 编号 |
| `DigitalValue` | bool | 最近值；从未上报或启动重置后为 `false`（低电平） |
| `LastReportedAtUtc` | 时间 | 最近一次收到上报的时间 |

- 按「中控 + 信号类型 + Join 编号」而不是按控制点存储，因此多个控制点可共享同一状态 Join。
- **无外键约束**，删除中控时由接口显式清理，避免残留快照。
- 运行态数据：进程启动时全部在线状态与高电平快照会被重置，等待设备重新上报。

## 5. 自动任务

### `Schedules` 自动任务目标

同一份计划覆盖多间教室时，每间教室各生成一行，共享同一个 `GroupId`（旧版单目标任务该列为空）。

| 列 | 类型 | 说明 |
| --- | --- | --- |
| `Id` | GUID / uuid | 主键（Quartz 任务键也使用该值） |
| `GroupId` | GUID / uuid \| null | 任务组标识；同组目标一起暂停、恢复、修改与删除 |
| `JoinMappingId` | GUID / uuid \| null | 目标控制点，FK → `JoinMappings`（**SetNull**） |
| `RoomName` | string | 教室名称快照 |
| `JoinName` | string | 控制点名称快照 |
| `Name` | string(120) | 任务名称 |
| `Enabled` | bool | 是否启用；暂停后保留定义与执行记录 |
| `Kind` | int（枚举） | `Once`（一次性）或 `Weekly`（按周重复） |
| `RunAtUtc` | 时间 \| null | 一次性任务的执行时刻（按服务器本地时间换算后的 UTC） |
| `WeekdayMask` | int | 每周重复的星期位掩码，位号与 `DayOfWeek` 一致（周日为位 0） |
| `LocalTime` | time | 每周重复的本地执行时刻 |
| `NextRunAtUtc` | 时间 \| null | 下一次执行时间；暂停或一次性任务执行完毕后为 `null` |

复合索引 `(Enabled, NextRunAtUtc)`：供调度扫描待触发任务。

### `ScheduleExecutions` 自动任务执行记录

| 列 | 类型 | 说明 |
| --- | --- | --- |
| `Id` | GUID / uuid | 主键 |
| `ScheduleId` | GUID / uuid | 所属目标行，FK → `Schedules`（**Restrict**，接口删除任务前显式清理） |
| `ScheduledAtUtc` | 时间 | 计划触发时刻 |
| `CompletedAtUtc` | 时间 \| null | 完成时刻；`null` 表示仍在执行 |
| `Status` | string | `Pending` / `Succeeded` / `Failed` |
| `Error` | string(2000) \| null | 失败原因（如中控离线） |

唯一索引 `(ScheduleId, ScheduledAtUtc)`：同一目标同一时刻只会产生一条记录，避免 Quartz 重复触发写脏数据。

## 6. 手动任务

### `ManualTasks` 手动任务定义

| 列 | 类型 | 说明 |
| --- | --- | --- |
| `Id` | GUID / uuid | 主键 |
| `Name` | string(120) | 任务名称（总览按钮文字） |
| `JoinName` | string(120) | 聚合的控制点名称快照（一个任务只对应同名控制点） |
| `ConfirmBeforeRun` | bool | 执行前是否二次确认，默认 `true` |
| `SortOrder` | int | 新建任务追加在末尾时使用的顺序号 |

### `ManualTaskTargets` 手动任务目标

| 列 | 类型 | 说明 |
| --- | --- | --- |
| `Id` | GUID / uuid | 主键 |
| `ManualTaskId` | GUID / uuid | 所属任务，FK → `ManualTasks`（Cascade） |
| `JoinMappingId` | GUID / uuid \| null | 目标控制点，FK → `JoinMappings`（**SetNull**） |
| `RoomName` | string(120) | 教室名称快照 |
| `JoinName` | string(120) | 控制点名称快照 |

索引：`(ManualTaskId)`、`(JoinMappingId)`。控制点被删除后目标保留快照但无法执行，接口会在删除前清理引用它的目标。

### `ManualTaskExecutions` 手动任务执行摘要

| 列 | 类型 | 说明 |
| --- | --- | --- |
| `Id` | GUID / uuid | 主键 |
| `ManualTaskId` | GUID / uuid | 所属任务，FK → `ManualTasks`（Cascade，随任务删除） |
| `StartedAtUtc` | 时间 | 开始时刻 |
| `CompletedAtUtc` | 时间 \| null | 完成时刻 |
| `SucceededCount` | int | 成功教室数 |
| `FailedCount` | int | 失败教室数 |
| `Status` | string(16) | `Succeeded`（全部成功）/ `Partial`（部分失败）/ `Failed`（全部失败） |
| `Error` | string(2000) \| null | 失败教室与原因摘要，全部成功时为 `null` |

索引 `(ManualTaskId, StartedAtUtc)`，接口按开始时间倒序读取最近 100 次。

## 7. 站点、账号与审计

### `SiteSettings` 站点设置（单行表）

| 列 | 类型 | 说明 |
| --- | --- | --- |
| `Id` | GUID / uuid | 固定单例 ID `2f6d6a2e-1c2b-4c3f-9d38-6b1f0a5e7c41` |
| `DisplayName` | string(60) | 站点名称，默认 `RoomControl` |
| `Subtitle` | string(80) | 副标题，默认「教室中控平台」 |
| `LogoDataUrl` | string(300000) \| null | Logo 的 Data URL |
| `LogRetentionDays` | int | 操作日志保留天数，默认 `60`，`0` 表示永久保留 |

### `Users` 账号

| 列 | 类型 | 说明 |
| --- | --- | --- |
| `Id` | GUID / uuid | 主键 |
| `Username` | string | 登录名，唯一索引 `IX_Users_Username`；长度与字符校验由 `UserService` 负责（≤60，不含控制字符） |
| `PasswordHash` | BLOB / bytea | PBKDF2-SHA256 派生结果（210,000 次迭代，32 字节） |
| `PasswordSalt` | BLOB / bytea | 每个账号独立的 16 字节随机盐 |
| `Role` | int（枚举） | `0` admin、`1` operator、`2` viewer |
| `IsActive` | bool | 是否启用；禁用或删除后现有 Cookie 立即失效 |
| `CreatedAt` | 时间 | 创建时间 |
| `UpdatedAt` | 时间 \| null | 最后修改时间 |

### `AuditLogs` 操作日志

| 列 | 类型 | 说明 |
| --- | --- | --- |
| `Id` | GUID / uuid | 主键 |
| `Category` | string(32) | 分类，见下表 |
| `Action` | string(300) | 可读操作名（如「发送 Join 5」「自动执行定时任务 Join 7」） |
| `Target` | string(512) | 操作对象可读名称（含教室、控制点等），无法解析时为空串 |
| `Actor` | string(60) | 操作者账号、「设备」或「系统」 |
| `StatusCode` | int | HTTP 结果码；后台写入时使用等价码（成功 `202`、离线 `503` 等） |
| `OccurredAtUtc` | 时间 | 发生时刻 |

分类取值：

| 值 | 界面标签 | 内容 |
| --- | --- | --- |
| `authentication` | 认证 | 登录、退出、修改密码；对象栏为来源 IP |
| `configuration` | 配置 | 站点、楼栋/楼层、教室、中控、控制点、账号变更 |
| `control` | 控制 | 人工下发脉冲，含手动任务执行（汇总一条 + 每个目标教室一条） |
| `schedule` | 任务 | 自动任务与手动任务的创建/修改/暂停/恢复/删除，以及自动任务每次下发指令 |
| `state` | 状态 | 设备上报引起的电平变化（仅在真正变化时记录） |
| `connection` | 连接 | 中控连接成功/断开 |

索引 `IX_AuditLogs_Category_OccurredAtUtc`、`IX_AuditLogs_OccurredAtUtc`；按 `SiteSettings.LogRetentionDays` 每天 03:00 清理，应用启动时也会清理一次。

## 8. 关系与删除行为

```text
Buildings 1 ──* Floors            (Cascade)
Rooms     1 ──0..1 Controllers    (Cascade)
Controllers 1 ──* JoinMappings    (Cascade)
Controllers 1 ──* JoinSnapshots   (无外键，接口显式清理)
JoinMappings 1 ──* Schedules               (SetNull)
Schedules    1 ──* ScheduleExecutions      (Restrict，接口先删记录)
ManualTasks  1 ──* ManualTaskTargets       (Cascade)
ManualTasks  1 ──* ManualTaskExecutions    (Cascade)
JoinMappings 1 ──* ManualTaskTargets       (SetNull)
```

- 删除教室 → 连带中控 → 连带控制点；接口在删除前会清理引用这些控制点的自动任务、手动任务与执行记录。
- 删除控制点时，`Schedules.JoinMappingId` 与 `ManualTaskTargets.JoinMappingId` 会置空（`SetNull`），保留历史行；正常路径由接口主动删除任务，因此不会出现无法执行的目标。
- 删除中控/教室前会删除 `JoinSnapshots` 中对应行（无外键约束）。
- 手动任务失去全部目标时会被自动删除，其执行记录一并清除。

## 9. 通用约定

- **时间存储**：SQLite 不支持 `DateTimeOffset`，统一通过 ValueConverter 存为 **UTC ticks（INTEGER）**；PostgreSQL 存 `timestamptz`。
- **名称快照**：`Rooms.Building/Floor`、`Schedules.RoomName/JoinName`、`ManualTaskTargets.RoomName/JoinName` 为冗余快照，保证楼栋/楼层/控制点删除后历史记录仍可读；重命名由接口同步。
- **运行态数据**：`Controllers.IsOnline/LastError` 与 `JoinSnapshots` 会在进程启动时重置（离线 / 低电平），不属于持久业务数据。
- **单行表**：`SiteSettings` 只存在固定 ID 的一行，读写都按单例处理。
- **追加写**：`AuditLogs` 与执行记录只插入、不修改，仅由保留策略或所属任务删除时清理。

## 10. 变更须知

新增或修改实体时，请同步以下位置，保持 SQLite 与 PostgreSQL 结构一致：

1. `Models/` 中的实体属性（含中文注释）。
2. `ControlDbContext.OnModelCreating`：键、关系、索引、长度与列名映射。
3. `SchemaUpgrader.UpgradeAsync` 的 **SQLite 与 PostgreSQL 两个分支**：`CREATE TABLE IF NOT EXISTS` / `ALTER TABLE ... ADD COLUMN` 等幂等语句。
4. `RoomControl.Api.Tests/SchemaUpgraderTests.cs`：覆盖旧库升级路径。
5. 本文档与 [docs/api.md](./api.md)（若对外字段有变化）。
