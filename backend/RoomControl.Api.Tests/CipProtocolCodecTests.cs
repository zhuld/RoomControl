using RoomControl.Api.Integrations;

namespace RoomControl.Api.Tests;

/// <summary>验证 CIP Raw 报文编码、Join 解析及协议边界条件。</summary>
public sealed class CipProtocolCodecTests
{
    [Fact]
    public void BuildSignOn_UsesShortFormatAndPlacesIpidInExpectedPayloadByte()
    {
        var message = CipProtocolCodec.BuildSignOn(3);

        Assert.Equal(0x0A, message[0]);
        Assert.Equal(0x00, message[1]);
        Assert.Equal(11, message[2]);
        Assert.Equal(3, message[4]);
    }

    [Fact]
    public void BuildDigitalPulseCommand_UsesButtonJoinEventForPressAndRelease()
    {
        var message = CipProtocolCodec.BuildDigitalPulseCommand(5);

        Assert.Equal(
            new byte[]
            {
                0x05, 0x00, 0x06, 0x00, 0x00, 0x03, 0x27, 0x04, 0x00,
                0x05, 0x00, 0x06, 0x00, 0x00, 0x03, 0x27, 0x04, 0x80
            },
            message);
    }

    [Fact]
    public void ParseJoinUpdate_ParsesDigitalJoinAndIgnoresOtherJoinTypes()
    {
        var digital = CipProtocolCodec.ParseJoinUpdate(
            new CipMessage(0x05, [0x00, 0x00, 0x03, 0x00, 0x04, 0x00]));
        var analog = CipProtocolCodec.ParseJoinUpdate(
            new CipMessage(0x05, [0x00, 0x00, 0x05, 0x14, 0x00, 0x04, 0x01, 0xF4]));
        var serial = CipProtocolCodec.ParseJoinUpdate(
            new CipMessage(0x05, [0x00, 0x00, 0x06, 0x15, 0x00, 0x01, 0x03, 0x4F, 0x4B]));

        Assert.Equal(new AdapterJoinUpdate(5, true), digital);
        Assert.Null(analog);
        Assert.Null(serial);
    }

    [Fact]
    public void IsUpdateEnded_MatchesUpdateEndedMarkerOnly()
    {
        Assert.True(CipProtocolCodec.IsUpdateEnded(new CipMessage(0x05, [0x00, 0x00, 0x02, 0x03, 0x16])));
        Assert.False(CipProtocolCodec.IsUpdateEnded(new CipMessage(0x05, [0x00, 0x00, 0x02, 0x03, 0x1C])));
        Assert.False(CipProtocolCodec.IsUpdateEnded(new CipMessage(0x05, [0x00, 0x00, 0x03, 0x00, 0x04, 0x00])));
    }

    [Fact]
    public void BuildDigitalPulseCommand_RejectsOutOfRangeNumbers()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CipProtocolCodec.BuildDigitalPulseCommand(0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CipProtocolCodec.BuildDigitalPulseCommand(32769));
    }

}
