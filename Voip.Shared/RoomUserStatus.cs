namespace Voip.Shared;

public sealed class RoomUserStatus
{
    public string UserName { get; set; } = string.Empty;
    public string ClientIpAddress { get; set; } = string.Empty;
    public string PublicIpAddress { get; set; } = string.Empty;
    public bool IsMicOn { get; set; }
    public bool IsMutedByServer { get; set; }
}
