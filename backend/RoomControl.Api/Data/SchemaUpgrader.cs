using System.Data;
using Microsoft.EntityFrameworkCore;

namespace RoomControl.Api.Data;

/// <summary>
/// 应用 <c>EnsureCreated</c> 在数据库已存在时不会执行的增量结构变更，
/// 让早期版本创建的数据库升级到当前版本时无需手工迁移。
/// </summary>
public static class SchemaUpgrader
{
    /// <summary>补齐旧库缺失的列并清理已移除的字段；可在每次启动时重复执行。</summary>
    public static async Task UpgradeAsync(ControlDbContext db, CancellationToken cancellationToken)
    {
        if (db.Database.IsSqlite())
        {
            // EnsureCreated 遇到非空旧库不会补齐字段；先检查 Rooms 是否存在以兼容未初始化数据库。
            var columns = await ReadSqliteColumnsAsync(db, "Rooms", cancellationToken);
            if (columns.Count == 0)
            {
                return;
            }

            if (!columns.Contains("Building"))
            {
                await db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE \"Rooms\" ADD COLUMN \"Building\" TEXT NOT NULL DEFAULT ''",
                    cancellationToken);
            }
            if (!columns.Contains("Floor"))
            {
                await db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE \"Rooms\" ADD COLUMN \"Floor\" TEXT NOT NULL DEFAULT ''",
                    cancellationToken);
            }
            // 时区配置已移除：教室统一按服务器本地时间执行，删除遗留列。
            if (columns.Contains("TimeZoneId"))
            {
                await db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE \"Rooms\" DROP COLUMN \"TimeZoneId\"",
                    cancellationToken);
            }

            // 旧版本把发送 Join 与状态 Join 共用一个字段，升级时复制旧值保持既有行为。
            var joinMappingColumns = await ReadSqliteColumnsAsync(db, "JoinMappings", cancellationToken);
            if (joinMappingColumns.Count > 0 && !joinMappingColumns.Contains("StateJoinNumber"))
            {
                await db.Database.ExecuteSqlRawAsync(
                    """
                    ALTER TABLE "JoinMappings" ADD COLUMN "StateJoinNumber" INTEGER NOT NULL DEFAULT 1;
                    UPDATE "JoinMappings" SET "StateJoinNumber" = "JoinNumber" WHERE "Kind" = 0;
                    """,
                    cancellationToken);
            }
            if (joinMappingColumns.Count > 0 && !joinMappingColumns.Contains("EnableJoinNumber"))
            {
                await db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE \"JoinMappings\" ADD COLUMN \"EnableJoinNumber\" INTEGER NOT NULL DEFAULT 0",
                    cancellationToken);
            }
            else if (joinMappingColumns.Count > 0)
            {
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE \"JoinMappings\" SET \"EnableJoinNumber\" = 0 WHERE \"EnableJoinNumber\" IS NULL",
                    cancellationToken);
            }
            // 旧库没有确认弹窗开关，补齐字段并默认开启以保留删除前的确认行为。
            if (joinMappingColumns.Count > 0 && !joinMappingColumns.Contains("ShowConfirmationPopup"))
            {
                await db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE \"JoinMappings\" ADD COLUMN \"ShowConfirmationPopup\" INTEGER NOT NULL DEFAULT 1",
                    cancellationToken);
            }
            // 旧库没有排序字段，统一为 0 后仍按名称排序；拖拽排序后才会写入具体序号。
            if (joinMappingColumns.Count > 0 && !joinMappingColumns.Contains("SortOrder"))
            {
                await db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE \"JoinMappings\" ADD COLUMN \"SortOrder\" INTEGER NOT NULL DEFAULT 0",
                    cancellationToken);
            }

            // 状态快照只有高低两态：旧库把未上报的使能 Join 存为 NULL，升级时统一迁移为低电平。
            var snapshotColumns = await ReadSqliteColumnsAsync(db, "JoinSnapshots", cancellationToken);
            if (snapshotColumns.Count > 0)
            {
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE \"JoinSnapshots\" SET \"DigitalValue\" = 0 WHERE \"DigitalValue\" IS NULL",
                    cancellationToken);
            }

            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE IF NOT EXISTS "SiteSettings" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_SiteSettings" PRIMARY KEY,
                    "DisplayName" TEXT NOT NULL,
                    "Subtitle" TEXT NOT NULL,
                    "LogoDataUrl" TEXT NULL,
                    "LogRetentionDays" INTEGER NOT NULL DEFAULT 60
                )
                """,
                cancellationToken);

            // 旧版站点设置没有日志保留天数，补齐为默认的 60 天。
            var siteSettingsColumns = await ReadSqliteColumnsAsync(db, "SiteSettings", cancellationToken);
            if (siteSettingsColumns.Count > 0 && !siteSettingsColumns.Contains("LogRetentionDays"))
            {
                await db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE \"SiteSettings\" ADD COLUMN \"LogRetentionDays\" INTEGER NOT NULL DEFAULT 60",
                    cancellationToken);
            }

            // 使用 IF NOT EXISTS 使启动升级可重复执行，不依赖单独维护迁移历史。
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE IF NOT EXISTS "Buildings" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_Buildings" PRIMARY KEY,
                    "Name" TEXT NOT NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_Buildings_Name" ON "Buildings" ("Name");
                CREATE TABLE IF NOT EXISTS "Floors" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_Floors" PRIMARY KEY,
                    "BuildingId" TEXT NOT NULL,
                    "Name" TEXT NOT NULL,
                    CONSTRAINT "FK_Floors_Buildings_BuildingId" FOREIGN KEY ("BuildingId") REFERENCES "Buildings" ("Id") ON DELETE CASCADE
                );
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_Floors_BuildingId_Name" ON "Floors" ("BuildingId", "Name");
                CREATE TABLE IF NOT EXISTS "Users" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_Users" PRIMARY KEY,
                    "Username" TEXT NOT NULL,
                    "PasswordHash" BLOB NOT NULL,
                    "PasswordSalt" BLOB NOT NULL,
                    "Role" INTEGER NOT NULL DEFAULT 2,
                    "IsActive" INTEGER NOT NULL DEFAULT 1,
                    "CreatedAt" TEXT NOT NULL,
                    "UpdatedAt" TEXT NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_Users_Username" ON "Users" ("Username");
                CREATE TABLE IF NOT EXISTS "AuditLogs" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_AuditLogs" PRIMARY KEY,
                    "Category" TEXT NOT NULL,
                    "Action" TEXT NOT NULL,
                    "Target" TEXT NOT NULL,
                    "Actor" TEXT NOT NULL,
                    "StatusCode" INTEGER NOT NULL,
                    "OccurredAtUtc" INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS "IX_AuditLogs_Category_OccurredAtUtc"
                    ON "AuditLogs" ("Category", "OccurredAtUtc");
                CREATE INDEX IF NOT EXISTS "IX_AuditLogs_OccurredAtUtc"
                    ON "AuditLogs" ("OccurredAtUtc");
                """,
                cancellationToken);

            var auditLogColumns = await ReadSqliteColumnsAsync(db, "AuditLogs", cancellationToken);
            if (auditLogColumns.Count > 0 && !auditLogColumns.Contains("Target"))
            {
                // 旧日志没有对象列，补空值；新记录写入时解析具体名称。
                await db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE \"AuditLogs\" ADD COLUMN \"Target\" TEXT NOT NULL DEFAULT ''",
                    cancellationToken);
            }
            // 早期版本把设备状态变化记在「控制」分类，统一归入新的「状态」分类。
            if (auditLogColumns.Count > 0)
            {
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE \"AuditLogs\" SET \"Category\" = 'state' WHERE \"Category\" = 'control' AND \"Action\" LIKE '状态变更%'",
                    cancellationToken);
                // 中控连接记录已从「状态」拆分为「连接」分类，旧记录同步迁移。
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE \"AuditLogs\" SET \"Category\" = 'connection' WHERE \"Category\" = 'state' AND \"Action\" IN ('中控连接成功', '中控连接断开')",
                    cancellationToken);
            }

            var scheduleColumns = await ReadSqliteColumnsAsync(db, "Schedules", cancellationToken);
            if (scheduleColumns.Count > 0 && !scheduleColumns.Contains("GroupId"))
            {
                // 多教室计划任务：同一次创建的目标共享 GroupId。
                await db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE \"Schedules\" ADD COLUMN \"GroupId\" TEXT NULL",
                    cancellationToken);
            }
            if (scheduleColumns.Contains("TargetValue"))
            {
                await db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE \"Schedules\" DROP COLUMN \"TargetValue\"",
                    cancellationToken);
            }
            if (scheduleColumns.Contains("IsArchived"))
            {
                // 归档功能已取消：历史的归档任务连同执行记录一并清理，再移除该列。
                await db.Database.ExecuteSqlRawAsync(
                    """
                    DELETE FROM "ScheduleExecutions"
                    WHERE "ScheduleId" IN (SELECT "Id" FROM "Schedules" WHERE "IsArchived" = 1);
                    DELETE FROM "Schedules" WHERE "IsArchived" = 1;
                    """,
                    cancellationToken);
                await db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE \"Schedules\" DROP COLUMN \"IsArchived\"",
                    cancellationToken);
            }
            if (scheduleColumns.Contains("TimeZoneId"))
            {
                // 任务统一按服务器本地时间执行，删除遗留的时区列。
                await db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE \"Schedules\" DROP COLUMN \"TimeZoneId\"",
                    cancellationToken);
            }
            return;
        }

        // PostgreSQL 使用与 SQLite 等价的幂等 DDL；此分支仅在 PostgreSQL Provider 下调用。
        await db.Database.ExecuteSqlRawAsync(
            """
            DO $$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_name = 'JoinMappings' AND column_name = 'StateJoinNumber'
                ) THEN
                    ALTER TABLE "JoinMappings" ADD COLUMN "StateJoinNumber" integer NOT NULL DEFAULT 1;
                    UPDATE "JoinMappings" SET "StateJoinNumber" = "JoinNumber" WHERE "Kind" = 0;
                END IF;
                IF NOT EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_name = 'JoinMappings' AND column_name = 'EnableJoinNumber'
                ) THEN
                    ALTER TABLE "JoinMappings" ADD COLUMN "EnableJoinNumber" integer NOT NULL DEFAULT 0;
                END IF;
                IF NOT EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_name = 'JoinMappings' AND column_name = 'ShowConfirmationPopup'
                ) THEN
                    ALTER TABLE "JoinMappings" ADD COLUMN "ShowConfirmationPopup" boolean NOT NULL DEFAULT true;
                END IF;
                IF NOT EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_name = 'JoinMappings' AND column_name = 'SortOrder'
                ) THEN
                    ALTER TABLE "JoinMappings" ADD COLUMN "SortOrder" integer NOT NULL DEFAULT 0;
                END IF;
                UPDATE "JoinMappings" SET "EnableJoinNumber" = 0 WHERE "EnableJoinNumber" IS NULL;
            END $$;
            ALTER TABLE "Rooms" ADD COLUMN IF NOT EXISTS "Building" character varying(60) NOT NULL DEFAULT '';
            ALTER TABLE "Rooms" ADD COLUMN IF NOT EXISTS "Floor" character varying(60) NOT NULL DEFAULT '';
            ALTER TABLE IF EXISTS "Rooms" DROP COLUMN IF EXISTS "TimeZoneId";
            ALTER TABLE IF EXISTS "Schedules" DROP COLUMN IF EXISTS "TimeZoneId";
            CREATE TABLE IF NOT EXISTS "SiteSettings" (
                "Id" uuid NOT NULL CONSTRAINT "PK_SiteSettings" PRIMARY KEY,
                "DisplayName" character varying(60) NOT NULL,
                "Subtitle" character varying(80) NOT NULL,
                "LogoDataUrl" text NULL,
                "LogRetentionDays" integer NOT NULL DEFAULT 60
            );
            ALTER TABLE IF EXISTS "SiteSettings"
                ADD COLUMN IF NOT EXISTS "LogRetentionDays" integer NOT NULL DEFAULT 60;
            CREATE TABLE IF NOT EXISTS "Buildings" (
                "Id" uuid NOT NULL CONSTRAINT "PK_Buildings" PRIMARY KEY,
                "Name" character varying(60) NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Buildings_Name" ON "Buildings" ("Name");
            CREATE TABLE IF NOT EXISTS "Floors" (
                "Id" uuid NOT NULL CONSTRAINT "PK_Floors" PRIMARY KEY,
                "BuildingId" uuid NOT NULL,
                "Name" character varying(60) NOT NULL,
                CONSTRAINT "FK_Floors_Buildings_BuildingId" FOREIGN KEY ("BuildingId") REFERENCES "Buildings" ("Id") ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Floors_BuildingId_Name" ON "Floors" ("BuildingId", "Name");
            CREATE TABLE IF NOT EXISTS "Users" (
                "Id" uuid NOT NULL CONSTRAINT "PK_Users" PRIMARY KEY,
                "Username" character varying(60) NOT NULL,
                "PasswordHash" bytea NOT NULL,
                "PasswordSalt" bytea NOT NULL,
                "Role" integer NOT NULL DEFAULT 2,
                "IsActive" boolean NOT NULL DEFAULT 1,
                "CreatedAt" timestamp with time zone NOT NULL,
                "UpdatedAt" timestamp with time zone NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Users_Username" ON "Users" ("Username");
            CREATE TABLE IF NOT EXISTS "AuditLogs" (
                "Id" uuid NOT NULL CONSTRAINT "PK_AuditLogs" PRIMARY KEY,
                "Category" character varying(32) NOT NULL,
                "Action" character varying(300) NOT NULL,
                "Target" character varying(200) NOT NULL DEFAULT '',
                "Actor" character varying(60) NOT NULL,
                "StatusCode" integer NOT NULL,
                "OccurredAtUtc" timestamp with time zone NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_AuditLogs_Category_OccurredAtUtc"
                ON "AuditLogs" ("Category", "OccurredAtUtc");
            CREATE INDEX IF NOT EXISTS "IX_AuditLogs_OccurredAtUtc"
                ON "AuditLogs" ("OccurredAtUtc");
            ALTER TABLE IF EXISTS "AuditLogs" ADD COLUMN IF NOT EXISTS "Target" character varying(200) NOT NULL DEFAULT '';
            UPDATE "AuditLogs" SET "Category" = 'state' WHERE "Category" = 'control' AND "Action" LIKE '状态变更%';
            UPDATE "AuditLogs" SET "Category" = 'connection' WHERE "Category" = 'state' AND "Action" IN ('中控连接成功', '中控连接断开');
            UPDATE "JoinSnapshots" SET "DigitalValue" = false WHERE "DigitalValue" IS NULL;
            ALTER TABLE IF EXISTS "Schedules" DROP COLUMN IF EXISTS "TargetValue";
            ALTER TABLE IF EXISTS "Schedules" ADD COLUMN IF NOT EXISTS "GroupId" uuid NULL;
            DO $$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_name = 'Schedules' AND column_name = 'IsArchived'
                ) THEN
                    DELETE FROM "ScheduleExecutions"
                    WHERE "ScheduleId" IN (SELECT "Id" FROM "Schedules" WHERE "IsArchived" = true);
                    DELETE FROM "Schedules" WHERE "IsArchived" = true;
                    ALTER TABLE "Schedules" DROP COLUMN "IsArchived";
                END IF;
            END $$;
            """,
            cancellationToken);
    }

    private static async Task<HashSet<string>> ReadSqliteColumnsAsync(
        ControlDbContext db,
        string table,
        CancellationToken cancellationToken)
    {
        // 仅在本方法打开连接时负责关闭，避免影响调用方已经打开的事务连接。
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info(\"{table}\")";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(reader.GetString(1));
            }
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync();
            }
        }

        return columns;
    }
}
