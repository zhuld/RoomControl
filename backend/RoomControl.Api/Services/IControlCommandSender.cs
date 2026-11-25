namespace RoomControl.Api.Services;

/// <summary>向单个控制点下发脉冲的发送能力；自动任务与手动任务共用同一实现。</summary>
public interface IControlCommandSender
{
    Task SendDigitalPulseAsync(Guid mappingId, bool enforceEnableJoin, CancellationToken cancellationToken);
}
