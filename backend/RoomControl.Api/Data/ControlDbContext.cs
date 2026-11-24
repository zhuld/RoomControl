using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using RoomControl.Api.Models;

namespace RoomControl.Api.Data;

/// <summary>定义 RoomControl 的 EF Core 数据集、关系、索引和数据库类型映射。</summary>
public sealed class ControlDbContext(DbContextOptions<ControlDbContext> options) : DbContext(options)
{
    public DbSet<Room> Rooms => Set<Room>();
    public DbSet<Controller> Controllers => Set<Controller>();
    public DbSet<JoinMapping> JoinMappings => Set<JoinMapping>();
    public DbSet<JoinSnapshot> JoinSnapshots => Set<JoinSnapshot>();
    public DbSet<ControlSchedule> Schedules => Set<ControlSchedule>();
    public DbSet<ScheduleExecution> ScheduleExecutions => Set<ScheduleExecution>();
    public DbSet<SiteSettings> SiteSettings => Set<SiteSettings>();
    public DbSet<Building> Buildings => Set<Building>();
    public DbSet<Floor> Floors => Set<Floor>();
    public DbSet<User> Users => Set<User>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        if (Database.IsSqlite())
        {
            // SQLite 不原生支持 DateTimeOffset；以 UTC ticks 存储可保持精度和时区语义。
            var utcTicksConverter = new ValueConverter<DateTimeOffset, long>(
                value => value.UtcDateTime.Ticks,
                ticks => new DateTimeOffset(ticks, TimeSpan.Zero));
            foreach (var property in modelBuilder.Model.GetEntityTypes()
                         .SelectMany(entity => entity.GetProperties())
                         .Where(property =>
                             property.ClrType == typeof(DateTimeOffset) ||
                             property.ClrType == typeof(DateTimeOffset?)))
            {
                property.SetValueConverter(utcTicksConverter);
            }
        }

        // 删除教室时级联删除中控；删除映射时任务脱离映射但执行记录继续保留。
        modelBuilder.Entity<Room>()
            .HasOne(room => room.Controller)
            .WithOne(controller => controller.Room)
            .HasForeignKey<Controller>(controller => controller.RoomId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Floor>()
            .HasOne(floor => floor.Building)
            .WithMany(building => building.Floors)
            .HasForeignKey(floor => floor.BuildingId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Building>().HasIndex(building => building.Name).IsUnique();
        modelBuilder.Entity<Floor>().HasIndex(floor => new { floor.BuildingId, floor.Name }).IsUnique();
        modelBuilder.Entity<User>().HasIndex(user => user.Username).IsUnique();
        modelBuilder.Entity<AuditLog>().HasIndex(log => new { log.Category, log.OccurredAtUtc });
        // 保留策略与日期区间查询按时间过滤，单独索引避免扫全表。
        modelBuilder.Entity<AuditLog>().HasIndex(log => log.OccurredAtUtc);

        modelBuilder.Entity<JoinMapping>()
            .HasIndex(join => new { join.ControllerId, join.Kind, join.SendJoinNumber })
            .IsUnique();

        modelBuilder.Entity<ControlSchedule>()
            .HasOne(schedule => schedule.JoinMapping)
            .WithMany()
            .HasForeignKey(schedule => schedule.JoinMappingId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<ScheduleExecution>()
            .HasOne(execution => execution.Schedule)
            .WithMany()
            .HasForeignKey(execution => execution.ScheduleId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<JoinSnapshot>()
            .HasKey(snapshot => new { snapshot.ControllerId, snapshot.Kind, snapshot.JoinNumber });

        // 唯一约束防止并发创建重复名称、Join 发送点或同一任务时刻的执行记录。
        modelBuilder.Entity<ControlSchedule>()
            .HasIndex(schedule => new { schedule.Enabled, schedule.NextRunAtUtc });

        modelBuilder.Entity<ScheduleExecution>()
            .HasIndex(execution => new { execution.ScheduleId, execution.ScheduledAtUtc })
            .IsUnique();

        modelBuilder.Entity<Room>().Property(room => room.Name).HasMaxLength(120);
        modelBuilder.Entity<Room>().Property(room => room.Building).HasMaxLength(60);
        modelBuilder.Entity<Room>().Property(room => room.Floor).HasMaxLength(60);
        modelBuilder.Entity<Building>().Property(building => building.Name).HasMaxLength(60);
        modelBuilder.Entity<Floor>().Property(floor => floor.Name).HasMaxLength(60);
        modelBuilder.Entity<SiteSettings>().Property(settings => settings.DisplayName).HasMaxLength(60);
        modelBuilder.Entity<SiteSettings>().Property(settings => settings.Subtitle).HasMaxLength(80);
        modelBuilder.Entity<SiteSettings>().Property(settings => settings.LogoDataUrl).HasMaxLength(300_000);
        modelBuilder.Entity<Controller>().Property(controller => controller.Name).HasMaxLength(120);
        modelBuilder.Entity<Controller>().Property(controller => controller.Host).HasMaxLength(255);
        modelBuilder.Entity<JoinMapping>().Property(join => join.Name).HasMaxLength(120);
        modelBuilder.Entity<JoinMapping>()
            .Property(join => join.SendJoinNumber)
            .HasColumnName("JoinNumber");
        modelBuilder.Entity<JoinMapping>().Property(join => join.StateJoinNumber).HasDefaultValue(1);
        modelBuilder.Entity<ControlSchedule>().Property(schedule => schedule.Name).HasMaxLength(120);
        modelBuilder.Entity<ScheduleExecution>().Property(execution => execution.Error).HasMaxLength(2000);
        modelBuilder.Entity<AuditLog>().Property(log => log.Category).HasMaxLength(32);
        modelBuilder.Entity<AuditLog>().Property(log => log.Action).HasMaxLength(300);
        // 对象名称包含楼栋、楼层、教室与多个控制点名称，需为长名称留出空间。
        modelBuilder.Entity<AuditLog>().Property(log => log.Target).HasMaxLength(512);
        modelBuilder.Entity<AuditLog>().Property(log => log.Actor).HasMaxLength(60);
    }
}
