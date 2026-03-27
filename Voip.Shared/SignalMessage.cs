namespace Voip.Shared;

public sealed class SignalMessage
{
    public string Type { get; set; } = string.Empty;
    public string Room { get; set; } = string.Empty;
    public string Sender { get; set; } = string.Empty;
    public string Data { get; set; } = string.Empty;
    public string PublicIpAddress { get; set; } = string.Empty;
    public List<string> Users { get; set; } = [];
    public Dictionary<string, List<string>> Rooms { get; set; } = [];
    public List<RoomUserStatus> Participants { get; set; } = [];
    public Dictionary<string, List<RoomUserStatus>> RoomParticipants { get; set; } = [];
}
