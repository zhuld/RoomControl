using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;
using RoomControl.Api.Data;
using RoomControl.Api.Services;

namespace RoomControl.Api.Tests;

/// <summary>
/// Quartz 的日志提供程序是进程级静态状态：多个测试各自 AddQuartz 并释放容器会让后建的调度器
/// 引用已释放的 LoggerFactory。这里让整个集合共享唯一一个调度器宿主，容器直到集合结束才释放。
/// </summary>
public sealed class QuartzSchedulerFixture : IDisposable
{
    private readonly ServiceProvider services;

    public QuartzSchedulerFixture()
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddQuartz();
        services = collection.BuildServiceProvider();
    }

    public ScheduleService CreateService(IDbContextFactory<ControlDbContext> contextFactory) =>
        new(
            contextFactory,
            services.GetRequiredService<ISchedulerFactory>(),
            services.GetRequiredService<ILogger<ScheduleService>>());

    public ConfigTransferService CreateConfigTransferService(
        IDbContextFactory<ControlDbContext> contextFactory,
        ScheduleService scheduleService) =>
        new(
            contextFactory,
            scheduleService,
            services.GetRequiredService<ISchedulerFactory>(),
            services.GetRequiredService<ILogger<ConfigTransferService>>());

    public void Dispose() => services.Dispose();
}
