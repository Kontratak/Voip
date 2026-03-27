using Fleck;
using Voip.Shared;

namespace Voip.SignalingServer;

internal sealed class RoomManager
{
    private readonly Dictionary<string, List<RoomParticipant>> rooms = new();
    private readonly HashSet<string> roomCatalog = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock syncRoot = new();

    public RoomManager()
    {
        roomCatalog.Add("general");
        rooms["general"] = [];
    }

    public bool CreateRoom(string room)
    {
        if (string.IsNullOrWhiteSpace(room))
        {
            return false;
        }

        lock (syncRoot)
        {
            var normalizedRoom = room.Trim();
            var isNewRoom = roomCatalog.Add(normalizedRoom);

            if (!rooms.ContainsKey(normalizedRoom))
            {
                rooms[normalizedRoom] = [];
            }

            return isNewRoom;
        }
    }

    public bool TryDeleteRoom(string room, out List<RoomParticipantSummary> movedParticipants)
    {
        movedParticipants = [];
        if (string.IsNullOrWhiteSpace(room) ||
            string.Equals(room.Trim(), "general", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        lock (syncRoot)
        {
            var normalizedRoom = room.Trim();
            if (!roomCatalog.Contains(normalizedRoom) || !rooms.TryGetValue(normalizedRoom, out var participants))
            {
                return false;
            }

            if (!rooms.TryGetValue("general", out var generalParticipants))
            {
                generalParticipants = [];
                rooms["general"] = generalParticipants;
            }

            foreach (var participant in participants)
            {
                participant.Room = "general";
                generalParticipants.Add(participant);
                movedParticipants.Add(new RoomParticipantSummary(
                    participant.Socket,
                    "general",
                    participant.UserName,
                    participant.ClientIpAddress));
            }

            rooms.Remove(normalizedRoom);
            roomCatalog.Remove(normalizedRoom);
            return true;
        }
    }

    public void Join(string room, IWebSocketConnection socket, string userName, string clientIpAddress, string publicIpAddress)
    {
        lock (syncRoot)
        {
            roomCatalog.Add(room);

            if (!rooms.TryGetValue(room, out var participants))
            {
                participants = [];
                rooms[room] = participants;
            }

            RoomParticipant? existing = null;
            List<RoomParticipant>? previousParticipants = null;

            foreach (var pair in rooms)
            {
                existing = pair.Value.FirstOrDefault(participant => participant.Socket == socket);
                if (existing is not null)
                {
                    previousParticipants = pair.Value;
                    break;
                }
            }

            if (existing is not null)
            {
                if (!ReferenceEquals(previousParticipants, participants))
                {
                    previousParticipants!.Remove(existing);
                    participants.Add(existing);
                }

                existing.UserName = userName;
                existing.Room = room;
                existing.ClientIpAddress = clientIpAddress;
                existing.PublicIpAddress = publicIpAddress;
                return;
            }

            participants.Add(new RoomParticipant(socket, room, userName, clientIpAddress, publicIpAddress));
        }
    }

    public bool TryMoveToRoom(IWebSocketConnection socket, string newRoom, out string oldRoom, out string userName)
    {
        lock (syncRoot)
        {
            oldRoom = string.Empty;
            userName = string.Empty;

            roomCatalog.Add(newRoom);
            if (!rooms.TryGetValue(newRoom, out var newParticipants))
            {
                newParticipants = [];
                rooms[newRoom] = newParticipants;
            }

            foreach (var pair in rooms)
            {
                var participant = pair.Value.FirstOrDefault(candidate => candidate.Socket == socket);
                if (participant is null)
                {
                    continue;
                }

                oldRoom = participant.Room;
                userName = participant.UserName;

                if (string.Equals(oldRoom, newRoom, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                pair.Value.Remove(participant);
                participant.Room = newRoom;
                participant.IsMicOn = false;
                newParticipants.Add(participant);
                return true;
            }

            return false;
        }
    }

    public bool TrySetMicState(IWebSocketConnection socket, bool isMicOn, out string room)
    {
        lock (syncRoot)
        {
            foreach (var pair in rooms)
            {
                var participant = pair.Value.FirstOrDefault(candidate => candidate.Socket == socket);
                if (participant is null)
                {
                    continue;
                }

                participant.IsMicOn = participant.IsMutedByServer ? false : isMicOn;
                room = participant.Room;
                return true;
            }
        }

        room = string.Empty;
        return false;
    }

    public bool TrySetServerMute(string room, string userName, bool isMuted, out IWebSocketConnection? socket)
    {
        lock (syncRoot)
        {
            if (!rooms.TryGetValue(room, out var participants))
            {
                socket = null;
                return false;
            }

            var participant = participants.FirstOrDefault(candidate =>
                string.Equals(candidate.UserName, userName, StringComparison.OrdinalIgnoreCase));

            if (participant is null)
            {
                socket = null;
                return false;
            }

            participant.IsMutedByServer = isMuted;
            if (isMuted)
            {
                participant.IsMicOn = false;
            }

            socket = participant.Socket;
            return true;
        }
    }

    public bool TryGetParticipantState(IWebSocketConnection socket, out string room, out string userName, out bool isMutedByServer)
    {
        lock (syncRoot)
        {
            foreach (var pair in rooms)
            {
                var participant = pair.Value.FirstOrDefault(candidate => candidate.Socket == socket);
                if (participant is null)
                {
                    continue;
                }

                room = participant.Room;
                userName = participant.UserName;
                isMutedByServer = participant.IsMutedByServer;
                return true;
            }
        }

        room = string.Empty;
        userName = string.Empty;
        isMutedByServer = false;
        return false;
    }

    public bool TryPrepareKick(string room, string userName, string reason, out IWebSocketConnection? socket)
    {
        lock (syncRoot)
        {
            if (!rooms.TryGetValue(room, out var participants))
            {
                socket = null;
                return false;
            }

            var participant = participants.FirstOrDefault(candidate =>
                string.Equals(candidate.UserName, userName, StringComparison.OrdinalIgnoreCase));

            if (participant is null)
            {
                socket = null;
                return false;
            }

            participant.WasKicked = true;
            participant.KickReason = reason;
            socket = participant.Socket;
            return true;
        }
    }

    public List<RoomParticipantSummary> PrepareBanIp(string clientIpAddress, string reason)
    {
        var targets = new List<RoomParticipantSummary>();

        lock (syncRoot)
        {
            foreach (var participants in rooms.Values)
            {
                foreach (var participant in participants.Where(candidate =>
                    string.Equals(candidate.ClientIpAddress, clientIpAddress, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(candidate.PublicIpAddress, clientIpAddress, StringComparison.OrdinalIgnoreCase)))
                {
                    participant.WasKicked = true;
                    participant.KickReason = reason;
                    targets.Add(new RoomParticipantSummary(participant.Socket, participant.Room, participant.UserName, participant.ClientIpAddress));
                }
            }
        }

        return targets;
    }

    public bool TryGetDisconnectInfo(IWebSocketConnection socket, out string room, out string userName, out bool wasKicked, out string kickReason)
    {
        lock (syncRoot)
        {
            foreach (var pair in rooms)
            {
                var participant = pair.Value.FirstOrDefault(candidate => candidate.Socket == socket);
                if (participant is null)
                {
                    continue;
                }

                room = participant.Room;
                userName = participant.UserName;
                wasKicked = participant.WasKicked;
                kickReason = participant.KickReason;
                return true;
            }
        }

        room = string.Empty;
        userName = string.Empty;
        wasKicked = false;
        kickReason = string.Empty;
        return false;
    }

    public void Broadcast(string room, IWebSocketConnection sender, string message)
    {
        List<IWebSocketConnection> recipients;

        lock (syncRoot)
        {
            if (!rooms.TryGetValue(room, out var participants))
            {
                return;
            }

            recipients = [.. participants
                .Where(participant => participant.Socket != sender)
                .Select(participant => participant.Socket)];
        }

        foreach (var client in recipients)
        {
            client.Send(message);
        }
    }

    public List<string> GetRoomNames()
    {
        lock (syncRoot)
        {
            roomCatalog.Add("general");
            if (!rooms.ContainsKey("general"))
            {
                rooms["general"] = [];
            }

            return [.. roomCatalog.OrderBy(name => name)];
        }
    }

    public List<RoomUserStatus> GetParticipants(string room)
    {
        lock (syncRoot)
        {
            if (!rooms.TryGetValue(room, out var participants))
            {
                return [];
            }

            return [.. participants
                .OrderBy(participant => participant.UserName)
                .Select(participant => new RoomUserStatus
                {
                    UserName = participant.UserName,
                    ClientIpAddress = participant.ClientIpAddress,
                    PublicIpAddress = participant.PublicIpAddress,
                    IsMicOn = participant.IsMicOn,
                    IsMutedByServer = participant.IsMutedByServer
                })];
        }
    }

    public void BroadcastToRoom(string room, string message)
    {
        List<IWebSocketConnection> recipients;

        lock (syncRoot)
        {
            if (!rooms.TryGetValue(room, out var participants))
            {
                return;
            }

            recipients = [.. participants.Select(participant => participant.Socket)];
        }

        foreach (var client in recipients)
        {
            client.Send(message);
        }
    }

    public Dictionary<string, List<RoomUserStatus>> GetRooms()
    {
        lock (syncRoot)
        {
            return rooms.ToDictionary(
                pair => pair.Key,
                pair => pair.Value
                    .OrderBy(participant => participant.UserName)
                    .Select(participant => new RoomUserStatus
                    {
                        UserName = participant.UserName,
                        ClientIpAddress = participant.ClientIpAddress,
                        PublicIpAddress = participant.PublicIpAddress,
                        IsMicOn = participant.IsMicOn,
                        IsMutedByServer = participant.IsMutedByServer
                    })
                    .ToList());
        }
    }

    public void Leave(IWebSocketConnection socket)
    {
        lock (syncRoot)
        {
            foreach (var participants in rooms.Values)
            {
                participants.RemoveAll(participant => participant.Socket == socket);
            }
        }
    }

    public List<IWebSocketConnection> GetConnectedSockets()
    {
        lock (syncRoot)
        {
            return [.. rooms.Values
                .SelectMany(participants => participants)
                .Select(participant => participant.Socket)
                .Distinct()];
        }
    }

    internal sealed class RoomParticipantSummary(
        IWebSocketConnection socket,
        string room,
        string userName,
        string clientIpAddress)
    {
        public IWebSocketConnection Socket { get; } = socket;
        public string Room { get; } = room;
        public string UserName { get; } = userName;
        public string ClientIpAddress { get; } = clientIpAddress;
    }

    private sealed class RoomParticipant(IWebSocketConnection socket, string room, string userName, string clientIpAddress, string publicIpAddress)
    {
        public IWebSocketConnection Socket { get; } = socket;
        public string Room { get; set; } = room;
        public string UserName { get; set; } = userName;
        public string ClientIpAddress { get; set; } = clientIpAddress;
        public string PublicIpAddress { get; set; } = publicIpAddress;
        public bool IsMicOn { get; set; }
        public bool IsMutedByServer { get; set; }
        public bool WasKicked { get; set; }
        public string KickReason { get; set; } = string.Empty;
    }
}
