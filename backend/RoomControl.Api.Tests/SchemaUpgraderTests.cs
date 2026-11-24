using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RoomControl.Api.Data;

namespace RoomControl.Api.Tests;

/// <summary>以手工构造的旧版 SQLite 表验证启动时的兼容升级逻辑。</summary>
public sealed class SchemaUpgraderTests
{
    [Fact]
    public async Task UpgradeLegacyJoinMappings_CopiesOldDigitalJoinToStateAndDefaultsEnableToZero()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                CREATE TABLE "Rooms" ("Id" TEXT NOT NULL, "Building" TEXT NOT NULL, "Floor" TEXT NOT NULL);
                CREATE TABLE "JoinMappings" (
                    "Id" TEXT NOT NULL,
                    "ControllerId" TEXT NOT NULL,
                    "Kind" INTEGER NOT NULL,
                    "JoinNumber" INTEGER NOT NULL
                );
                INSERT INTO "JoinMappings" ("Id", "ControllerId", "Kind", "JoinNumber")
                VALUES ('digital', 'controller', 0, 27), ('analog', 'controller', 1, 41);
                """;
            await command.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<ControlDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new ControlDbContext(options);
        await SchemaUpgrader.UpgradeAsync(db, CancellationToken.None);

        await using var read = connection.CreateCommand();
        read.CommandText =
            """
            SELECT "JoinNumber", "StateJoinNumber", "EnableJoinNumber"
            FROM "JoinMappings" WHERE "Kind" = 0
            """;
        await using var reader = await read.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(27, reader.GetInt32(0));
        Assert.Equal(27, reader.GetInt32(1));
        Assert.Equal(0, reader.GetInt32(2));
        await reader.DisposeAsync();

        await using (var update = connection.CreateCommand())
        {
            update.CommandText = "UPDATE \"JoinMappings\" SET \"StateJoinNumber\" = 88 WHERE \"Kind\" = 0";
            await update.ExecuteNonQueryAsync();
        }
        await SchemaUpgrader.UpgradeAsync(db, CancellationToken.None);
        await using var verify = connection.CreateCommand();
        verify.CommandText = "SELECT \"StateJoinNumber\" FROM \"JoinMappings\" WHERE \"Kind\" = 0";
        Assert.Equal(88L, (long)(await verify.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task UpgradeBackfillsNullEnableJoinNumberToZero()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                CREATE TABLE "Rooms" ("Id" TEXT NOT NULL, "Building" TEXT NOT NULL, "Floor" TEXT NOT NULL);
                CREATE TABLE "JoinMappings" (
                    "Id" TEXT NOT NULL,
                    "ControllerId" TEXT NOT NULL,
                    "Kind" INTEGER NOT NULL,
                    "JoinNumber" INTEGER NOT NULL,
                    "StateJoinNumber" INTEGER NOT NULL DEFAULT 1,
                    "EnableJoinNumber" INTEGER NULL
                );
                INSERT INTO "JoinMappings" ("Id", "ControllerId", "Kind", "JoinNumber", "StateJoinNumber", "EnableJoinNumber")
                VALUES ('digital', 'controller', 0, 27, 28, NULL);
                """;
            await command.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<ControlDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new ControlDbContext(options);
        await SchemaUpgrader.UpgradeAsync(db, CancellationToken.None);

        await using var verify = connection.CreateCommand();
        verify.CommandText = "SELECT \"EnableJoinNumber\" FROM \"JoinMappings\" WHERE \"Kind\" = 0";
        Assert.Equal(0L, (long)(await verify.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task UpgradeDropsRemovedTargetValueColumn()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                CREATE TABLE "Rooms" ("Id" TEXT NOT NULL, "Building" TEXT NOT NULL, "Floor" TEXT NOT NULL);
                CREATE TABLE "JoinMappings" (
                    "Id" TEXT NOT NULL,
                    "ControllerId" TEXT NOT NULL,
                    "Kind" INTEGER NOT NULL,
                    "JoinNumber" INTEGER NOT NULL,
                    "StateJoinNumber" INTEGER NOT NULL DEFAULT 1,
                    "EnableJoinNumber" INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE "Schedules" ("Id" TEXT NOT NULL, "TargetValue" TEXT NOT NULL);
                """;
            await command.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<ControlDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new ControlDbContext(options);
        await SchemaUpgrader.UpgradeAsync(db, CancellationToken.None);

        var columns = new List<string>();
        await using var read = connection.CreateCommand();
        read.CommandText = "PRAGMA table_info(\"Schedules\")";
        await using var reader = await read.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }

        Assert.DoesNotContain("TargetValue", columns);
    }

    [Fact]
    public async Task UpgradeDropsRemovedTimeZoneColumns()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                CREATE TABLE "Rooms" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_Rooms" PRIMARY KEY,
                    "Name" TEXT NOT NULL,
                    "TimeZoneId" TEXT NOT NULL,
                    "Building" TEXT NOT NULL,
                    "Floor" TEXT NOT NULL
                );
                CREATE TABLE "Schedules" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_Schedules" PRIMARY KEY,
                    "TimeZoneId" TEXT NOT NULL
                );
                INSERT INTO "Rooms" ("Id", "Name", "TimeZoneId", "Building", "Floor")
                VALUES ('room', 'A101', 'Asia/Shanghai', '一号楼', '一层');
                INSERT INTO "Schedules" ("Id", "TimeZoneId") VALUES ('schedule', 'Asia/Shanghai');
                """;
            await command.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<ControlDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new ControlDbContext(options);
        await SchemaUpgrader.UpgradeAsync(db, CancellationToken.None);

        Assert.False(await HasColumnAsync(connection, "Rooms"));
        Assert.False(await HasColumnAsync(connection, "Schedules"));

        // 删除列后不再要求时区值，旧库可以继续写入教室和任务。
        await using var insert = connection.CreateCommand();
        insert.CommandText =
            """
            INSERT INTO "Rooms" ("Id", "Name", "Building", "Floor") VALUES ('room2', 'A102', '一号楼', '一层');
            INSERT INTO "Schedules" ("Id") VALUES ('schedule2');
            """;
        await insert.ExecuteNonQueryAsync();

        static async Task<bool> HasColumnAsync(SqliteConnection connection, string table)
        {
            await using var read = connection.CreateCommand();
            read.CommandText = $"PRAGMA table_info(\"{table}\")";
            await using var reader = await read.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (string.Equals(reader.GetString(1), "TimeZoneId", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }

    [Fact]
    public async Task UpgradeDropsArchiveColumnAndRemovesArchivedSchedules()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                CREATE TABLE "Rooms" ("Id" TEXT NOT NULL, "Building" TEXT NOT NULL, "Floor" TEXT NOT NULL);
                CREATE TABLE "JoinMappings" (
                    "Id" TEXT NOT NULL,
                    "ControllerId" TEXT NOT NULL,
                    "Kind" INTEGER NOT NULL,
                    "JoinNumber" INTEGER NOT NULL,
                    "StateJoinNumber" INTEGER NOT NULL DEFAULT 1,
                    "EnableJoinNumber" INTEGER NOT NULL DEFAULT 0,
                    "ShowConfirmationPopup" INTEGER NOT NULL DEFAULT 1,
                    "SortOrder" INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE "Schedules" (
                    "Id" TEXT NOT NULL,
                    "GroupId" TEXT NULL,
                    "Name" TEXT NOT NULL,
                    "IsArchived" INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE "ScheduleExecutions" (
                    "Id" TEXT NOT NULL,
                    "ScheduleId" TEXT NOT NULL
                );
                INSERT INTO "Schedules" ("Id", "Name", "IsArchived")
                VALUES ('live', 'live task', 0), ('archived', 'archived task', 1);
                INSERT INTO "ScheduleExecutions" ("Id", "ScheduleId")
                VALUES ('exec-live', 'live'), ('exec-archived', 'archived');
                """;
            await command.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<ControlDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new ControlDbContext(options);
        await SchemaUpgrader.UpgradeAsync(db, CancellationToken.None);

        var columns = new List<string>();
        await using (var read = connection.CreateCommand())
        {
            read.CommandText = "PRAGMA table_info(\"Schedules\")";
            await using var reader = await read.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(1));
            }
        }
        Assert.DoesNotContain("IsArchived", columns);

        await using (var verify = connection.CreateCommand())
        {
            verify.CommandText = "SELECT \"Id\" FROM \"Schedules\"";
            Assert.Equal("live", (string)(await verify.ExecuteScalarAsync())!);
        }

        await using (var executions = connection.CreateCommand())
        {
            executions.CommandText = "SELECT \"Id\" FROM \"ScheduleExecutions\"";
            Assert.Equal("exec-live", (string)(await executions.ExecuteScalarAsync())!);
        }
    }

    [Fact]
    public async Task UpgradeCreatesAuditLogTableForExistingDatabase()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE \"Rooms\" (\"Id\" TEXT NOT NULL)";
            await command.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<ControlDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new ControlDbContext(options);
        await SchemaUpgrader.UpgradeAsync(db, CancellationToken.None);

        await using var verify = connection.CreateCommand();
        verify.CommandText = "PRAGMA table_info(\"AuditLogs\")";
        var columns = new List<string>();
        await using var reader = await verify.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }

        Assert.Contains("Category", columns);
        Assert.Contains("Actor", columns);
        Assert.Contains("Target", columns);
        Assert.Contains("StatusCode", columns);
        Assert.Contains("OccurredAtUtc", columns);

        // Users 表与 EF 模型保持一致：DateTimeOffset 以 UTC ticks（INTEGER）存储。
        await using var usersCommand = connection.CreateCommand();
        usersCommand.CommandText = "PRAGMA table_info(\"Users\")";
        var userColumns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var usersReader = await usersCommand.ExecuteReaderAsync();
        while (await usersReader.ReadAsync())
        {
            userColumns[usersReader.GetString(1)] = usersReader.GetString(2);
        }

        Assert.Equal("INTEGER", userColumns["CreatedAt"]);
        Assert.Equal("INTEGER", userColumns["UpdatedAt"]);
    }

    [Fact]
    public async Task UpgradeAddsTargetColumnToExistingAuditLogs()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                CREATE TABLE "Rooms" ("Id" TEXT NOT NULL, "Building" TEXT NOT NULL, "Floor" TEXT NOT NULL);
                CREATE TABLE "AuditLogs" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_AuditLogs" PRIMARY KEY,
                    "Category" TEXT NOT NULL,
                    "Action" TEXT NOT NULL,
                    "Actor" TEXT NOT NULL,
                    "StatusCode" INTEGER NOT NULL,
                    "OccurredAtUtc" INTEGER NOT NULL
                );
                INSERT INTO "AuditLogs" ("Id", "Category", "Action", "Actor", "StatusCode", "OccurredAtUtc")
                VALUES ('log-1', 'configuration', 'PUT /api/joins/{mappingId:guid}', 'admin', 204, 0);
                """;
            await command.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<ControlDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new ControlDbContext(options);
        await SchemaUpgrader.UpgradeAsync(db, CancellationToken.None);

        await using var verify = connection.CreateCommand();
        verify.CommandText = "SELECT \"Target\" FROM \"AuditLogs\" WHERE \"Id\" = 'log-1'";
        Assert.Equal(string.Empty, (string)(await verify.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task UpgradeMovesDeviceStateChangesToStateCategory()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                CREATE TABLE "Rooms" ("Id" TEXT NOT NULL, "Building" TEXT NOT NULL, "Floor" TEXT NOT NULL);
                CREATE TABLE "AuditLogs" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_AuditLogs" PRIMARY KEY,
                    "Category" TEXT NOT NULL,
                    "Action" TEXT NOT NULL,
                    "Target" TEXT NOT NULL DEFAULT '',
                    "Actor" TEXT NOT NULL,
                    "StatusCode" INTEGER NOT NULL,
                    "OccurredAtUtc" INTEGER NOT NULL
                );
                INSERT INTO "AuditLogs" ("Id", "Category", "Action", "Actor", "StatusCode", "OccurredAtUtc")
                VALUES
                    ('state-log', 'control', '状态变更 Join 6：低电平 → 高电平', '设备', 0, 0),
                    ('pulse-log', 'control', '发送 Join 5', 'admin', 202, 0);
                """;
            await command.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<ControlDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new ControlDbContext(options);
        await SchemaUpgrader.UpgradeAsync(db, CancellationToken.None);

        await using var verify = connection.CreateCommand();
        verify.CommandText = "SELECT \"Category\" FROM \"AuditLogs\" WHERE \"Id\" = 'state-log'";
        Assert.Equal("state", (string)(await verify.ExecuteScalarAsync())!);

        await using var pulse = connection.CreateCommand();
        pulse.CommandText = "SELECT \"Category\" FROM \"AuditLogs\" WHERE \"Id\" = 'pulse-log'";
        Assert.Equal("control", (string)(await pulse.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task UpgradeMovesControllerConnectionLogsToConnectionCategory()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                CREATE TABLE "Rooms" ("Id" TEXT NOT NULL, "Building" TEXT NOT NULL, "Floor" TEXT NOT NULL);
                CREATE TABLE "AuditLogs" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_AuditLogs" PRIMARY KEY,
                    "Category" TEXT NOT NULL,
                    "Action" TEXT NOT NULL,
                    "Target" TEXT NOT NULL DEFAULT '',
                    "Actor" TEXT NOT NULL,
                    "StatusCode" INTEGER NOT NULL,
                    "OccurredAtUtc" INTEGER NOT NULL
                );
                INSERT INTO "AuditLogs" ("Id", "Category", "Action", "Actor", "StatusCode", "OccurredAtUtc")
                VALUES
                    ('disconnect', 'state', '中控连接断开', '系统', 0, 0),
                    ('join-state', 'state', '状态变更 Join 6：低电平 → 高电平', '设备', 0, 0);
                """;
            await command.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<ControlDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new ControlDbContext(options);
        await SchemaUpgrader.UpgradeAsync(db, CancellationToken.None);

        await using var disconnect = connection.CreateCommand();
        disconnect.CommandText = "SELECT \"Category\" FROM \"AuditLogs\" WHERE \"Id\" = 'disconnect'";
        Assert.Equal("connection", (string)(await disconnect.ExecuteScalarAsync())!);

        await using var joinState = connection.CreateCommand();
        joinState.CommandText = "SELECT \"Category\" FROM \"AuditLogs\" WHERE \"Id\" = 'join-state'";
        Assert.Equal("state", (string)(await joinState.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task UpgradeAddsSortOrderColumnDefaultingToZero()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                CREATE TABLE "Rooms" ("Id" TEXT NOT NULL, "Building" TEXT NOT NULL, "Floor" TEXT NOT NULL);
                CREATE TABLE "JoinMappings" (
                    "Id" TEXT NOT NULL,
                    "ControllerId" TEXT NOT NULL,
                    "Kind" INTEGER NOT NULL,
                    "JoinNumber" INTEGER NOT NULL,
                    "StateJoinNumber" INTEGER NOT NULL DEFAULT 1,
                    "EnableJoinNumber" INTEGER NOT NULL DEFAULT 0,
                    "ShowConfirmationPopup" INTEGER NOT NULL DEFAULT 1
                );
                INSERT INTO "JoinMappings" ("Id", "ControllerId", "Kind", "JoinNumber")
                VALUES ('digital', 'controller', 0, 27);
                """;
            await command.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<ControlDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new ControlDbContext(options);
        await SchemaUpgrader.UpgradeAsync(db, CancellationToken.None);

        await using var verify = connection.CreateCommand();
        verify.CommandText = "SELECT \"SortOrder\" FROM \"JoinMappings\" WHERE \"Id\" = 'digital'";
        Assert.Equal(0L, (long)(await verify.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task UpgradeAddsConfirmationPopupColumnEnabledForLegacyJoins()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                CREATE TABLE "Rooms" ("Id" TEXT NOT NULL, "Building" TEXT NOT NULL, "Floor" TEXT NOT NULL);
                CREATE TABLE "JoinMappings" (
                    "Id" TEXT NOT NULL,
                    "ControllerId" TEXT NOT NULL,
                    "Kind" INTEGER NOT NULL,
                    "JoinNumber" INTEGER NOT NULL,
                    "StateJoinNumber" INTEGER NOT NULL DEFAULT 1,
                    "EnableJoinNumber" INTEGER NOT NULL DEFAULT 0
                );
                INSERT INTO "JoinMappings" ("Id", "ControllerId", "Kind", "JoinNumber")
                VALUES ('digital', 'controller', 0, 27);
                """;
            await command.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<ControlDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new ControlDbContext(options);
        await SchemaUpgrader.UpgradeAsync(db, CancellationToken.None);

        await using var verify = connection.CreateCommand();
        verify.CommandText = "SELECT \"ShowConfirmationPopup\" FROM \"JoinMappings\" WHERE \"Id\" = 'digital'";
        Assert.Equal(1L, (long)(await verify.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task UpgradeBackfillsNullDigitalSnapshotsToLow()
    {
        // 旧版把未上报的使能 Join 存为 NULL；状态改为高低两态后升级为低电平。
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                CREATE TABLE "Rooms" ("Id" TEXT NOT NULL, "Building" TEXT NOT NULL, "Floor" TEXT NOT NULL);
                CREATE TABLE "JoinSnapshots" (
                    "ControllerId" TEXT NOT NULL,
                    "Kind" INTEGER NOT NULL,
                    "JoinNumber" INTEGER NOT NULL,
                    "DigitalValue" INTEGER NULL,
                    "LastReportedAtUtc" INTEGER NOT NULL,
                    CONSTRAINT "PK_JoinSnapshots" PRIMARY KEY ("ControllerId", "Kind", "JoinNumber")
                );
                INSERT INTO "JoinSnapshots" ("ControllerId", "Kind", "JoinNumber", "DigitalValue", "LastReportedAtUtc")
                VALUES ('controller', 0, 6, NULL, 0), ('controller', 0, 7, 1, 0);
                """;
            await command.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<ControlDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new ControlDbContext(options);
        await SchemaUpgrader.UpgradeAsync(db, CancellationToken.None);

        await using var verify = connection.CreateCommand();
        verify.CommandText = "SELECT \"DigitalValue\" FROM \"JoinSnapshots\" ORDER BY \"JoinNumber\"";
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0L, reader.GetInt64(0));
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
    }

    [Fact]
    public async Task UpgradeAddsDefaultLogRetentionDaysToExistingSiteSettings()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                CREATE TABLE "Rooms" ("Id" TEXT NOT NULL, "Building" TEXT NOT NULL, "Floor" TEXT NOT NULL);
                CREATE TABLE "SiteSettings" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_SiteSettings" PRIMARY KEY,
                    "DisplayName" TEXT NOT NULL,
                    "Subtitle" TEXT NOT NULL,
                    "LogoDataUrl" TEXT NULL
                );
                INSERT INTO "SiteSettings" ("Id", "DisplayName", "Subtitle")
                VALUES ('2f6d6a2e-1c2b-4c3f-9d38-6b1f0a5e7c41', 'RoomControl', '教室中控平台');
                """;
            await command.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<ControlDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new ControlDbContext(options);
        await SchemaUpgrader.UpgradeAsync(db, CancellationToken.None);

        await using var verify = connection.CreateCommand();
        verify.CommandText = "SELECT \"LogRetentionDays\" FROM \"SiteSettings\"";
        Assert.Equal(60L, (long)(await verify.ExecuteScalarAsync())!);

        // 升级可重复执行，已保存的天数不会被重置。
        await using (var update = connection.CreateCommand())
        {
            update.CommandText = "UPDATE \"SiteSettings\" SET \"LogRetentionDays\" = 15";
            await update.ExecuteNonQueryAsync();
        }
        await SchemaUpgrader.UpgradeAsync(db, CancellationToken.None);
        await using var confirm = connection.CreateCommand();
        confirm.CommandText = "SELECT \"LogRetentionDays\" FROM \"SiteSettings\"";
        Assert.Equal(15L, (long)(await confirm.ExecuteScalarAsync())!);
    }
}
