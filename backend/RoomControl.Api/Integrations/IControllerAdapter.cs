using RoomControl.Api.Models;

namespace RoomControl.Api.Integrations;

/// <summary>控制器传输适配器的连接、状态接收与数字 Join 控制抽象。</summary>
public interface IControllerAdapter : IAsyncDisposable
{
    /// <summary>Join 上报；处理器按报文顺序串行执行，用于保证状态回放与上报的先后关系。</summary>
    event Func<AdapterJoinUpdate, Task>? JoinUpdated;

    /// <summary>
    /// 一次状态回放结束（收到 Update 结束标记）时触发，参数为本次回放窗口内上报过的 Join 编号。
    /// 未出现在其中的 Join 按协议约定为低电平。
    /// </summary>
    event Func<IReadOnlyCollection<int>, Task>? StateRefreshCompleted;

    Task ConnectAsync(CancellationToken cancellationToken);
    Task RunAsync(CancellationToken cancellationToken);
    Task SendDigitalPulseAsync(int joinNumber, CancellationToken cancellationToken);
    Task RequestStateAsync(CancellationToken cancellationToken);
}

/// <summary>适配器收到的一次数字 Join 状态上报。</summary>
public sealed record AdapterJoinUpdate(int JoinNumber, bool Value);

/// <summary>根据持久化控制器配置创建对应品牌的协议适配器。</summary>
public interface IControllerAdapterFactory
{
    IControllerAdapter Create(Controller controller);
}

/// <summary>当前适配器注册表；新增控制器协议时在此分派其实现。</summary>
public sealed class ControllerAdapterFactory : IControllerAdapterFactory
{
    /// <summary>按适配器键分派；未知类型显式失败，避免连接以错误协议启动。</summary>
    public IControllerAdapter Create(Controller controller) =>
        controller.AdapterType switch
        {
            "crestron-cip" => new CrestronCipAdapter(controller),
            _ => throw new NotSupportedException($"不支持的中控适配器「{controller.AdapterType}」。")
        };
}
