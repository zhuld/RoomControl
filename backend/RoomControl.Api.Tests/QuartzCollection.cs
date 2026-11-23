using Xunit;

namespace RoomControl.Api.Tests;

/// <summary>启动 Quartz 调度器的测试共享同一宿主，并禁止与其他集合并行运行。</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class QuartzCollection : ICollectionFixture<QuartzSchedulerFixture>
{
    public const string Name = "Quartz";
}
