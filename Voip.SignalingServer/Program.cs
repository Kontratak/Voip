using System.Net.Http;
using System.Text.Json;
using Fleck;
using Voip.Shared;
using Voip.SignalingServer;

var roomManager = new RoomManager();
var port = Environment.GetEnvironmentVariable("PORT") ?? "8181";
var server = new WebSocketServer($"ws://0.0.0.0:{port}");
var bannedIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
var bannedIpsSync = new Lock();

void BroadcastRoster(string room)
{
    var participants = roomManager.GetParticipants(room);
    roomManager.BroadcastToRoom(room, JsonSerializer.Serialize(new SignalMessage
    {
        Type = "users",
        Room = room,
        Sender = "server",
        Users = [.. participants.Select(participant => participant.UserName)],
        Participants = participants
    }));
}

string Timestamp() => $"[{DateTime.Now:HH:mm:ss}]";

List<string> GetBannedIps()
{
    lock (bannedIpsSync)
    {
        return [.. bannedIps.OrderBy(ip => ip)];
    }
}

void BroadcastRoomCatalog()
{
    var payload = JsonSerializer.Serialize(new SignalMessage
    {
        Type = "room_catalog",
        Sender = "server",
        Users = roomManager.GetRoomNames()
    });

    foreach (var socket in roomManager.GetConnectedSockets())
    {
        socket.Send(payload);
    }
}

void PrintRoomsSnapshot()
{
    var rooms = roomManager.GetRooms();
    if (rooms.Count == 0)
    {
        Console.WriteLine($"{Timestamp()} No active rooms.");
        return;
    }

    Console.WriteLine($"{Timestamp()} Active rooms:");
    foreach (var room in rooms.OrderBy(pair => pair.Key))
    {
        var users = room.Value.Select(user =>
            $"{user.UserName} ({DescribeIp(user)}){(user.IsMutedByServer ? " [SERVER-MUTED]" : user.IsMicOn ? " [MIC]" : " [MUTED]")}");
        Console.WriteLine($"  {room.Key}: {string.Join(", ", users)}");
    }
}

string DescribeIp(RoomUserStatus user)
{
    var publicIp = string.IsNullOrWhiteSpace(user.PublicIpAddress) ? "wan:unknown" : $"wan:{user.PublicIpAddress}";
    var remoteIp = string.IsNullOrWhiteSpace(user.ClientIpAddress) ? "remote:unknown" : $"remote:{user.ClientIpAddress}";
    return $"{publicIp}, {remoteIp}";
}

void PrintRoomCatalog()
{
    var rooms = roomManager.GetRoomNames();
    Console.WriteLine($"{Timestamp()} Rooms: {string.Join(", ", rooms.Count == 0 ? ["general"] : rooms)}");
}

void PrintBannedIps()
{
    var ips = GetBannedIps();
    if (ips.Count == 0)
    {
        Console.WriteLine($"{Timestamp()} No banned IP addresses.");
        return;
    }

    Console.WriteLine($"{Timestamp()} Banned IP addresses:");
    foreach (var ip in ips)
    {
        Console.WriteLine($"  {ip}");
    }
}

async Task<string?> TryGetPublicIpAsync()
{
    try
    {
        using var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        return (await httpClient.GetStringAsync("https://api.ipify.org")).Trim();
    }
    catch
    {
        return null;
    }
}

bool TryKickUser(string room, string userName, string reason, string initiatedBy)
{
    if (!roomManager.TryPrepareKick(room, userName, reason, out var socket) || socket is null)
    {
        Console.WriteLine($"{Timestamp()} User '{userName}' was not found in room '{room}'.");
        return false;
    }

    socket.Send(JsonSerializer.Serialize(new SignalMessage
    {
        Type = "kicked",
        Room = room,
        Sender = "server",
        Data = reason
    }));

    Console.WriteLine($"{Timestamp()} {initiatedBy} removed {userName} from room '{room}'.");
    socket.Close();
    return true;
}

bool TrySetServerMute(string room, string userName, bool isMuted, string initiatedBy)
{
    if (!roomManager.TrySetServerMute(room, userName, isMuted, out var socket) || socket is null)
    {
        Console.WriteLine($"{Timestamp()} User '{userName}' was not found in room '{room}'.");
        return false;
    }

    socket.Send(JsonSerializer.Serialize(new SignalMessage
    {
        Type = "mute_state",
        Room = room,
        Sender = "server",
        Data = isMuted ? "muted" : "unmuted"
    }));

    Console.WriteLine($"{Timestamp()} {initiatedBy} {(isMuted ? "muted" : "unmuted")} {userName} in room '{room}'.");
    BroadcastRoster(room);
    return true;
}

bool TryCreateRoom(string roomName, string initiatedBy)
{
    if (string.IsNullOrWhiteSpace(roomName))
    {
        Console.WriteLine($"{Timestamp()} Usage: /addroom <room-name>");
        return false;
    }

    var wasCreated = roomManager.CreateRoom(roomName);
    if (!wasCreated)
    {
        Console.WriteLine($"{Timestamp()} Room '{roomName}' already exists.");
        return false;
    }

    Console.WriteLine($"{Timestamp()} {initiatedBy} created room '{roomName}'.");
    BroadcastRoomCatalog();
    return true;
}

bool TryDeleteRoom(string roomName, string initiatedBy)
{
    if (string.IsNullOrWhiteSpace(roomName))
    {
        Console.WriteLine($"{Timestamp()} Usage: /deleteroom <room-name>");
        return false;
    }

    if (string.Equals(roomName.Trim(), "general", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"{Timestamp()} Room 'general' is always available and cannot be deleted.");
        return false;
    }

    if (!roomManager.TryDeleteRoom(roomName, out var movedParticipants))
    {
        Console.WriteLine($"{Timestamp()} Room '{roomName}' was not found.");
        return false;
    }

    foreach (var participant in movedParticipants)
    {
        participant.Socket.Send(JsonSerializer.Serialize(new SignalMessage
        {
            Type = "room_changed",
            Room = "general",
            Sender = "server",
            Data = $"Room '{roomName}' was deleted. You were moved to room 'general'."
        }));
    }

    Console.WriteLine($"{Timestamp()} {initiatedBy} deleted room '{roomName}'.");
    BroadcastRoster("general");
    BroadcastRoomCatalog();
    return true;
}

bool IsIpBanned(string ipAddress)
{
    if (string.IsNullOrWhiteSpace(ipAddress))
    {
        return false;
    }

    lock (bannedIpsSync)
    {
        return bannedIps.Contains(ipAddress);
    }
}

bool TryBanIp(string ipAddress, string initiatedBy)
{
    if (string.IsNullOrWhiteSpace(ipAddress))
    {
        Console.WriteLine($"{Timestamp()} Usage: /banip <ip-address>");
        return false;
    }

    var added = false;
    lock (bannedIpsSync)
    {
        added = bannedIps.Add(ipAddress);
    }

    if (!added)
    {
        Console.WriteLine($"{Timestamp()} IP address '{ipAddress}' is already banned.");
        return false;
    }

    var targets = roomManager.PrepareBanIp(ipAddress, $"You were banned by IP address ({ipAddress}).");
    foreach (var target in targets)
    {
        target.Socket.Send(JsonSerializer.Serialize(new SignalMessage
        {
            Type = "banned",
            Room = target.Room,
            Sender = "server",
            Data = $"You were banned by IP address ({ipAddress})."
        }));

        target.Socket.Close();
    }

    Console.WriteLine($"{Timestamp()} {initiatedBy} banned IP address '{ipAddress}'.");
    foreach (var room in targets.Select(target => target.Room).Distinct(StringComparer.OrdinalIgnoreCase))
    {
        BroadcastRoster(room);
    }

    return true;
}

bool TryUnbanIp(string ipAddress, string initiatedBy)
{
    if (string.IsNullOrWhiteSpace(ipAddress))
    {
        Console.WriteLine($"{Timestamp()} Usage: /unbanip <ip-address>");
        return false;
    }

    lock (bannedIpsSync)
    {
        if (!bannedIps.Remove(ipAddress))
        {
            Console.WriteLine($"{Timestamp()} IP address '{ipAddress}' is not in the ban list.");
            return false;
        }
    }

    Console.WriteLine($"{Timestamp()} {initiatedBy} unbanned IP address '{ipAddress}'.");
    return true;
}

server.Start(socket =>
{
    var clientIpAddress = socket.ConnectionInfo.ClientIpAddress ?? string.Empty;
    if (IsIpBanned(clientIpAddress))
    {
        Console.WriteLine($"{Timestamp()} Rejected banned connection from remote IP '{clientIpAddress}'.");
        socket.OnOpen = () =>
        {
            socket.Send(JsonSerializer.Serialize(new SignalMessage
            {
                Type = "banned",
                Sender = "server",
                Data = $"You cannot connect to this server because your IP address is banned. Remote IP: {clientIpAddress}"
            }));
            socket.Close();
        };
        return;
    }

    socket.OnMessage = payload =>
    {
        var signal = JsonSerializer.Deserialize<SignalMessage>(payload);
        if (signal is null)
        {
            return;
        }

        if (signal.Type == "room_list")
        {
            var rooms = roomManager.GetRoomNames();
            socket.Send(JsonSerializer.Serialize(new SignalMessage
            {
                Type = "room_catalog",
                Sender = "server",
                Users = rooms.Count == 0 ? ["general"] : rooms
            }));
            return;
        }

        if (signal.Type == "admin_list")
        {
            var rooms = roomManager.GetRooms();
            socket.Send(JsonSerializer.Serialize(new SignalMessage
            {
                Type = "rooms",
                Sender = "server",
                Rooms = rooms.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Select(user => user.UserName).ToList()),
                RoomParticipants = rooms
            }));
            return;
        }

        if (signal.Type == "admin_list_banned")
        {
            socket.Send(JsonSerializer.Serialize(new SignalMessage
            {
                Type = "banned_list",
                Sender = "server",
                Users = GetBannedIps()
            }));
            return;
        }

        if (signal.Type == "admin_ban_ip")
        {
            TryBanIp(signal.Data, "Admin CLI");
            return;
        }

        if (signal.Type == "admin_unban_ip")
        {
            TryUnbanIp(signal.Data, "Admin CLI");
            return;
        }

        if (signal.Type == "admin_create_room")
        {
            TryCreateRoom(signal.Data, "Admin CLI");
            return;
        }

        if (signal.Type == "admin_delete_room")
        {
            TryDeleteRoom(signal.Data, "Admin CLI");
            return;
        }

        if (signal.Type is "admin_kick" or "admin_disconnect")
        {
            var action = signal.Type == "admin_kick" ? "kicked" : "disconnected";
            var reason = signal.Type == "admin_kick"
                ? $"You were kicked from room '{signal.Room}'."
                : $"You were disconnected from room '{signal.Room}'.";

            TryKickUser(signal.Room, signal.Data, reason, $"Admin CLI {action}");
            return;
        }

        if (signal.Type is "admin_mute" or "admin_unmute")
        {
            TrySetServerMute(signal.Room, signal.Data, signal.Type == "admin_mute", "Admin CLI");
            return;
        }

        if (signal.Type == "join")
        {
            var joinRoom = signal.Room;
            var joinUserName = string.IsNullOrWhiteSpace(signal.Data) ? signal.Sender : signal.Data;
            var publicIpAddress = signal.PublicIpAddress;

            if (IsIpBanned(publicIpAddress))
            {
                Console.WriteLine($"{Timestamp()} Rejected banned connection for user '{joinUserName}' in room '{joinRoom}'. Remote IP: '{clientIpAddress}', WAN IP: '{publicIpAddress}'.");
                socket.Send(JsonSerializer.Serialize(new SignalMessage
                {
                    Type = "banned",
                    Room = joinRoom,
                    Sender = "server",
                    Data = $"You cannot connect to this server because your WAN IP address is banned. WAN IP: {publicIpAddress}"
                }));
                socket.Close();
                return;
            }

            roomManager.Join(joinRoom, socket, joinUserName, clientIpAddress, publicIpAddress);

            Console.WriteLine($"{Timestamp()} {joinUserName} joined room '{joinRoom}' from remote {clientIpAddress} / wan {publicIpAddress}.");

            socket.Send(JsonSerializer.Serialize(new SignalMessage
            {
                Type = "connected",
                Room = joinRoom,
                Sender = "server",
                Data = $"{joinUserName}, you are connected to room '{joinRoom}'."
            }));

            roomManager.Broadcast(joinRoom, socket, JsonSerializer.Serialize(new SignalMessage
            {
                Type = "system",
                Room = joinRoom,
                Sender = "server",
                Data = $"{joinUserName} joined the room."
            }));

            BroadcastRoster(joinRoom);
            BroadcastRoomCatalog();
            return;
        }

        if (signal.Type == "change_room")
        {
            var targetRoom = signal.Room;
            if (!roomManager.TryMoveToRoom(socket, targetRoom, out var previousRoom, out var movingUserName))
            {
                return;
            }

            socket.Send(JsonSerializer.Serialize(new SignalMessage
            {
                Type = "room_changed",
                Room = targetRoom,
                Sender = "server",
                Data = $"{movingUserName}, you moved to room '{targetRoom}'."
            }));

            if (!string.IsNullOrWhiteSpace(previousRoom) &&
                !string.Equals(previousRoom, targetRoom, StringComparison.OrdinalIgnoreCase))
            {
                roomManager.BroadcastToRoom(previousRoom, JsonSerializer.Serialize(new SignalMessage
                {
                    Type = "system",
                    Room = previousRoom,
                    Sender = "server",
                    Data = $"{movingUserName} left for room '{targetRoom}'."
                }));

                roomManager.BroadcastToRoom(targetRoom, JsonSerializer.Serialize(new SignalMessage
                {
                    Type = "system",
                    Room = targetRoom,
                    Sender = "server",
                    Data = $"{movingUserName} joined from room '{previousRoom}'."
                }));

                BroadcastRoster(previousRoom);
            }

            BroadcastRoster(targetRoom);
            BroadcastRoomCatalog();
            Console.WriteLine($"{Timestamp()} {movingUserName} moved from room '{previousRoom}' to room '{targetRoom}'.");
            return;
        }

        if (signal.Type == "mic_state")
        {
            if (roomManager.TrySetMicState(socket, signal.Data == "on", out var room))
            {
                BroadcastRoster(room);

                if (roomManager.TryGetDisconnectInfo(socket, out _, out var currentUserName, out _, out _))
                {
                    Console.WriteLine($"{Timestamp()} {currentUserName} microphone {(signal.Data == "on" ? "enabled" : "disabled")} in room '{room}'.");
                }
            }
            return;
        }

        if (!roomManager.TryGetParticipantState(socket, out var currentRoom, out _, out var isMutedByServer))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(currentRoom) || isMutedByServer)
        {
            return;
        }

        roomManager.Broadcast(currentRoom, socket, payload);
    };

    socket.OnClose = () =>
    {
        if (!roomManager.TryGetDisconnectInfo(socket, out var currentRoom, out var currentUserName, out var wasKicked, out _))
        {
            roomManager.Leave(socket);
            return;
        }

        roomManager.Leave(socket);

        if (string.IsNullOrWhiteSpace(currentRoom))
        {
            return;
        }

        if (wasKicked)
        {
            Console.WriteLine($"{Timestamp()} {currentUserName} removed from room '{currentRoom}'.");
            roomManager.BroadcastToRoom(currentRoom, JsonSerializer.Serialize(new SignalMessage
            {
                Type = "system",
                Room = currentRoom,
                Sender = "server",
                Data = $"{currentUserName} was removed from the room."
            }));
        }
        else
        {
            Console.WriteLine($"{Timestamp()} {currentUserName} disconnected from room '{currentRoom}'.");
            roomManager.BroadcastToRoom(currentRoom, JsonSerializer.Serialize(new SignalMessage
            {
                Type = "system",
                Room = currentRoom,
                Sender = "server",
                Data = $"{currentUserName} left the room."
            }));
        }

        BroadcastRoster(currentRoom);
        BroadcastRoomCatalog();
    };
});

var publicIp = await TryGetPublicIpAsync();
Console.WriteLine($"Signaling server running on ws://0.0.0.0:{port}");
if (publicIp is null)
{
    Console.WriteLine("Public IP: unavailable (internet lookup failed)");
    Console.WriteLine($"Remote clients should connect to ws://YOUR_PUBLIC_IP:{port}");
}
else
{
    Console.WriteLine($"Public IP: {publicIp}");
    Console.WriteLine($"Remote clients should connect to ws://{publicIp}:{port}");
}
Console.WriteLine("Commands: /rooms, /roomlists, /banned, /addroom <room>, /deleteroom <room>, /kick <room> <user>, /disconnect <room> <user>, /mute <room> <user>, /unmute <room> <user>, /banip <ip>, /unbanip <ip>, /help, exit");

if (Console.IsInputRedirected)
{
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return;
}

while (true)
{
    var input = Console.ReadLine();
    if (input is null)
    {
        break;
    }

    var commandLine = input.Trim();
    if (string.IsNullOrWhiteSpace(commandLine))
    {
        continue;
    }

    var parts = commandLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
    var command = parts[0];

    if (command.Equals("/rooms", StringComparison.OrdinalIgnoreCase))
    {
        PrintRoomsSnapshot();
        continue;
    }

    if (command.Equals("/roomlists", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("/roomlist", StringComparison.OrdinalIgnoreCase))
    {
        PrintRoomCatalog();
        continue;
    }

    if (command.Equals("/banned", StringComparison.OrdinalIgnoreCase))
    {
        PrintBannedIps();
        continue;
    }

    if (command.Equals("/addroom", StringComparison.OrdinalIgnoreCase))
    {
        if (parts.Length < 2)
        {
            Console.WriteLine($"{Timestamp()} Usage: /addroom <room-name>");
            continue;
        }

        TryCreateRoom(parts[1], "Server console");
        continue;
    }

    if (command.Equals("/deleteroom", StringComparison.OrdinalIgnoreCase))
    {
        if (parts.Length < 2)
        {
            Console.WriteLine($"{Timestamp()} Usage: /deleteroom <room-name>");
            continue;
        }

        TryDeleteRoom(parts[1], "Server console");
        continue;
    }

    if (command.Equals("/kick", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("/disconnect", StringComparison.OrdinalIgnoreCase))
    {
        if (parts.Length < 3)
        {
            Console.WriteLine($"{Timestamp()} Usage: {command} <room> <user>");
            continue;
        }

        var reason = command.Equals("/kick", StringComparison.OrdinalIgnoreCase)
            ? $"You were kicked from room '{parts[1]}'."
            : $"You were disconnected from room '{parts[1]}'.";

        TryKickUser(parts[1], parts[2], reason, "Server console");
        continue;
    }

    if (command.Equals("/mute", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("/unmute", StringComparison.OrdinalIgnoreCase))
    {
        if (parts.Length < 3)
        {
            Console.WriteLine($"{Timestamp()} Usage: {command} <room> <user>");
            continue;
        }

        TrySetServerMute(parts[1], parts[2], command.Equals("/mute", StringComparison.OrdinalIgnoreCase), "Server console");
        continue;
    }

    if (command.Equals("/banip", StringComparison.OrdinalIgnoreCase))
    {
        if (parts.Length < 2)
        {
            Console.WriteLine($"{Timestamp()} Usage: /banip <ip-address>");
            continue;
        }

        TryBanIp(parts[1], "Server console");
        continue;
    }

    if (command.Equals("/unbanip", StringComparison.OrdinalIgnoreCase))
    {
        if (parts.Length < 2)
        {
            Console.WriteLine($"{Timestamp()} Usage: /unbanip <ip-address>");
            continue;
        }

        TryUnbanIp(parts[1], "Server console");
        continue;
    }

    if (command.Equals("/help", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("Commands: /rooms, /roomlists, /banned, /addroom <room>, /deleteroom <room>, /kick <room> <user>, /disconnect <room> <user>, /mute <room> <user>, /unmute <room> <user>, /banip <ip>, /unbanip <ip>, /help, exit");
        continue;
    }

    if (command.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("quit", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"{Timestamp()} Shutting down server.");
        break;
    }

    Console.WriteLine($"{Timestamp()} Unknown command. Use /help.");
}
