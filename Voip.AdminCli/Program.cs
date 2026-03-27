using System.Threading;
using Voip.Client.Core.Signaling;
using Voip.Shared;

if (args.Contains("--help"))
{
    PrintUsage();
    return;
}

if (args.Length > 0)
{
    var serverUrl = args[0];
    var roomFilter = args.Length > 1 ? args[1] : null;
    Environment.ExitCode = QueryRooms(serverUrl, roomFilter);
    return;
}

Console.WriteLine("VoIP Admin CLI");
Console.WriteLine("Type /help for commands.");

string? currentServerUrl = null;

while (true)
{
    Console.Write("> ");
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

    var parts = commandLine.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
    var command = parts[0];

    if (command.Equals("/help", StringComparison.OrdinalIgnoreCase))
    {
        PrintInteractiveHelp();
        continue;
    }

    if (command.Equals("/start", StringComparison.OrdinalIgnoreCase))
    {
        if (parts.Length < 2)
        {
            Console.WriteLine("Usage: /start ws://SERVER-IP:8181");
            continue;
        }

        currentServerUrl = NormalizeServerUrl(parts[1]);
        if (currentServerUrl is null)
        {
            Console.WriteLine("Use a valid server address like ws://YOUR-IP:8181.");
            continue;
        }

        Console.WriteLine($"Current server set to {currentServerUrl}");
        continue;
    }

    if (command.Equals("/rooms", StringComparison.OrdinalIgnoreCase))
    {
        if (EnsureServerSelected(currentServerUrl))
        {
            QueryRooms(currentServerUrl!);
        }
        continue;
    }

    if (command.Equals("/banned", StringComparison.OrdinalIgnoreCase))
    {
        if (EnsureServerSelected(currentServerUrl))
        {
            QueryBannedIps(currentServerUrl!);
        }
        continue;
    }

    if (command.Equals("/roomlists", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("/roomlist", StringComparison.OrdinalIgnoreCase))
    {
        if (EnsureServerSelected(currentServerUrl))
        {
            QueryRoomCatalog(currentServerUrl!);
        }
        continue;
    }

    if (command.Equals("/addroom", StringComparison.OrdinalIgnoreCase))
    {
        if (parts.Length < 2)
        {
            Console.WriteLine("Usage: /addroom ROOM_NAME");
            continue;
        }

        if (EnsureServerSelected(currentServerUrl))
        {
            SendAdminAction(currentServerUrl!, "admin_create_room", string.Empty, parts[1], "Add room");
        }
        continue;
    }

    if (command.Equals("/deleteroom", StringComparison.OrdinalIgnoreCase))
    {
        if (parts.Length < 2)
        {
            Console.WriteLine("Usage: /deleteroom ROOM_NAME");
            continue;
        }

        if (EnsureServerSelected(currentServerUrl))
        {
            SendAdminAction(currentServerUrl!, "admin_delete_room", string.Empty, parts[1], "Delete room");
        }
        continue;
    }

    if (command.Equals("/room", StringComparison.OrdinalIgnoreCase))
    {
        if (parts.Length < 2)
        {
            Console.WriteLine("Usage: /room ROOM_NAME");
            continue;
        }

        if (EnsureServerSelected(currentServerUrl))
        {
            QueryRooms(currentServerUrl!, parts[1]);
        }
        continue;
    }

    if (command.Equals("/banip", StringComparison.OrdinalIgnoreCase))
    {
        if (parts.Length < 2)
        {
            Console.WriteLine("Usage: /banip IP_ADDRESS");
            continue;
        }

        if (EnsureServerSelected(currentServerUrl))
        {
            SendAdminAction(currentServerUrl!, "admin_ban_ip", string.Empty, parts[1], "Ban IP");
        }
        continue;
    }

    if (command.Equals("/unbanip", StringComparison.OrdinalIgnoreCase))
    {
        if (parts.Length < 2)
        {
            Console.WriteLine("Usage: /unbanip IP_ADDRESS");
            continue;
        }

        if (EnsureServerSelected(currentServerUrl))
        {
            SendAdminAction(currentServerUrl!, "admin_unban_ip", string.Empty, parts[1], "Unban IP");
        }
        continue;
    }

    if (command.Equals("/kick", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("/disconnect", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("/mute", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("/unmute", StringComparison.OrdinalIgnoreCase))
    {
        if (parts.Length < 3)
        {
            Console.WriteLine($"Usage: {command} ROOM_NAME USER_NAME");
            continue;
        }

        if (EnsureServerSelected(currentServerUrl))
        {
            var actionType = command.Equals("/kick", StringComparison.OrdinalIgnoreCase)
                ? "admin_kick"
                : command.Equals("/disconnect", StringComparison.OrdinalIgnoreCase)
                    ? "admin_disconnect"
                    : command.Equals("/mute", StringComparison.OrdinalIgnoreCase)
                        ? "admin_mute"
                        : "admin_unmute";
            SendAdminAction(currentServerUrl!, actionType, parts[1], parts[2]);
        }
        continue;
    }

    if (command.Equals("/server", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine(currentServerUrl is null
            ? "No server selected. Use /start ws://SERVER-IP:8181"
            : $"Current server: {currentServerUrl}");
        continue;
    }

    if (command.Equals("/exit", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("quit", StringComparison.OrdinalIgnoreCase))
    {
        break;
    }

    Console.WriteLine("Unknown command. Type /help.");
}

static bool EnsureServerSelected(string? serverUrl)
{
    if (!string.IsNullOrWhiteSpace(serverUrl))
    {
        return true;
    }

    Console.WriteLine("Select a server first with /start ws://SERVER-IP:8181");
    return false;
}

static string? NormalizeServerUrl(string value)
{
    return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
           (uri.Scheme == Uri.UriSchemeWs || uri.Scheme == Uri.UriSchemeWss)
        ? value
        : null;
}

static int QueryRooms(string serverUrl, string? roomFilter = null)
{
    var normalizedServerUrl = NormalizeServerUrl(serverUrl);
    if (normalizedServerUrl is null)
    {
        Console.WriteLine("Use a valid server address like ws://YOUR-IP:8181.");
        return 1;
    }

    var signalClient = new SignalClient();
    var waitHandle = new ManualResetEventSlim(false);
    var exitCode = 0;

    signalClient.OnConnected += () =>
    {
        signalClient.Send(new SignalMessage
        {
            Type = "admin_list",
            Sender = "admin-cli"
        });
    };

    signalClient.OnConnectionError += message =>
    {
        Console.WriteLine($"Unable to connect: {message}");
        exitCode = 1;
        waitHandle.Set();
    };

    signalClient.OnMessage += message =>
    {
        if (message.Type != "rooms")
        {
            return;
        }

        PrintRooms(message.RoomParticipants, roomFilter);
        waitHandle.Set();
    };

    try
    {
        signalClient.Connect(normalizedServerUrl);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Unable to connect: {ex.Message}");
        exitCode = 1;
        waitHandle.Set();
    }

    if (!waitHandle.Wait(TimeSpan.FromSeconds(10)))
    {
        Console.WriteLine("Timed out waiting for the server response.");
        exitCode = 1;
    }

    signalClient.Dispose();
    return exitCode;
}

static int QueryRoomCatalog(string serverUrl)
{
    var normalizedServerUrl = NormalizeServerUrl(serverUrl);
    if (normalizedServerUrl is null)
    {
        Console.WriteLine("Use a valid server address like ws://YOUR-IP:8181.");
        return 1;
    }

    var signalClient = new SignalClient();
    var waitHandle = new ManualResetEventSlim(false);
    var exitCode = 0;

    signalClient.OnConnected += () =>
    {
        signalClient.Send(new SignalMessage
        {
            Type = "room_list",
            Sender = "admin-cli"
        });
    };

    signalClient.OnConnectionError += message =>
    {
        Console.WriteLine($"Unable to connect: {message}");
        exitCode = 1;
        waitHandle.Set();
    };

    signalClient.OnMessage += message =>
    {
        if (message.Type != "room_catalog")
        {
            return;
        }

        PrintRoomCatalog(message.Users);
        waitHandle.Set();
    };

    try
    {
        signalClient.Connect(normalizedServerUrl);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Unable to connect: {ex.Message}");
        exitCode = 1;
        waitHandle.Set();
    }

    if (!waitHandle.Wait(TimeSpan.FromSeconds(10)))
    {
        Console.WriteLine("Timed out waiting for the server response.");
        exitCode = 1;
    }

    signalClient.Dispose();
    return exitCode;
}

static int QueryBannedIps(string serverUrl)
{
    var normalizedServerUrl = NormalizeServerUrl(serverUrl);
    if (normalizedServerUrl is null)
    {
        Console.WriteLine("Use a valid server address like ws://YOUR-IP:8181.");
        return 1;
    }

    var signalClient = new SignalClient();
    var waitHandle = new ManualResetEventSlim(false);
    var exitCode = 0;

    signalClient.OnConnected += () =>
    {
        signalClient.Send(new SignalMessage
        {
            Type = "admin_list_banned",
            Sender = "admin-cli"
        });
    };

    signalClient.OnConnectionError += message =>
    {
        Console.WriteLine($"Unable to connect: {message}");
        exitCode = 1;
        waitHandle.Set();
    };

    signalClient.OnMessage += message =>
    {
        if (message.Type != "banned_list")
        {
            return;
        }

        PrintBannedIps(message.Users);
        waitHandle.Set();
    };

    try
    {
        signalClient.Connect(normalizedServerUrl);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Unable to connect: {ex.Message}");
        exitCode = 1;
        waitHandle.Set();
    }

    if (!waitHandle.Wait(TimeSpan.FromSeconds(10)))
    {
        Console.WriteLine("Timed out waiting for the server response.");
        exitCode = 1;
    }

    signalClient.Dispose();
    return exitCode;
}

static int SendAdminAction(string serverUrl, string actionType, string room, string userName, string? actionLabelOverride = null)
{
    var normalizedServerUrl = NormalizeServerUrl(serverUrl);
    if (normalizedServerUrl is null)
    {
        Console.WriteLine("Use a valid server address like ws://YOUR-IP:8181.");
        return 1;
    }

    var signalClient = new SignalClient();
    var waitHandle = new ManualResetEventSlim(false);
    var exitCode = 0;

    signalClient.OnConnected += () =>
    {
        signalClient.Send(new SignalMessage
        {
            Type = actionType,
            Sender = "admin-cli",
            Room = room,
            Data = userName
        });

        waitHandle.Set();
    };

    signalClient.OnConnectionError += message =>
    {
        Console.WriteLine($"Unable to connect: {message}");
        exitCode = 1;
        waitHandle.Set();
    };

    try
    {
        signalClient.Connect(normalizedServerUrl);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Unable to connect: {ex.Message}");
        exitCode = 1;
        waitHandle.Set();
    }

    if (!waitHandle.Wait(TimeSpan.FromSeconds(10)))
    {
        Console.WriteLine("Timed out waiting for the server response.");
        exitCode = 1;
    }
    else if (exitCode == 0)
    {
        var actionLabel = actionLabelOverride ?? actionType switch
        {
            "admin_kick" => "Kick",
            "admin_disconnect" => "Disconnect",
            "admin_mute" => "Mute",
            "admin_unmute" => "Unmute",
            "admin_ban_ip" => "Ban IP",
            "admin_unban_ip" => "Unban IP",
            "admin_create_room" => "Add room",
            "admin_delete_room" => "Delete room",
            _ => "Action"
        };
        Console.WriteLine(string.IsNullOrWhiteSpace(room)
            ? $"{actionLabel} request sent for '{userName}'."
            : $"{actionLabel} request sent for '{userName}' in room '{room}'.");
    }

    signalClient.Dispose();
    return exitCode;
}

static void PrintRooms(Dictionary<string, List<RoomUserStatus>> rooms, string? roomFilter)
{
    if (rooms.Count == 0)
    {
        Console.WriteLine("No active rooms.");
        return;
    }

    if (!string.IsNullOrWhiteSpace(roomFilter))
    {
        if (!rooms.TryGetValue(roomFilter, out var users))
        {
            Console.WriteLine($"Room '{roomFilter}' has no connected users.");
            return;
        }

        Console.WriteLine($"Room: {roomFilter}");
        foreach (var user in users)
        {
            Console.WriteLine($"  - {user.UserName} {DescribeUserState(user)} {DescribeIp(user)}");
        }
        return;
    }

    foreach (var room in rooms.OrderBy(pair => pair.Key))
    {
        Console.WriteLine($"Room: {room.Key}");
        foreach (var user in room.Value)
        {
            Console.WriteLine($"  - {user.UserName} {DescribeUserState(user)} {DescribeIp(user)}");
        }

        Console.WriteLine();
    }
}

static void PrintRoomCatalog(List<string> rooms)
{
    var roomList = rooms.Count == 0 ? ["general"] : rooms;

    Console.WriteLine("Rooms:");
    foreach (var room in roomList.OrderBy(room => room))
    {
        Console.WriteLine($"  - {room}");
    }
}

static void PrintBannedIps(List<string> ips)
{
    if (ips.Count == 0)
    {
        Console.WriteLine("No banned IP addresses.");
        return;
    }

    Console.WriteLine("Banned IP addresses:");
    foreach (var ip in ips.OrderBy(ip => ip))
    {
        Console.WriteLine($"  - {ip}");
    }
}

static string DescribeUserState(RoomUserStatus user)
{
    return user.IsMutedByServer ? "[SERVER MUTED]" : user.IsMicOn ? "[MIC]" : "[MUTED]";
}

static string DescribeIp(RoomUserStatus user)
{
    var publicIp = string.IsNullOrWhiteSpace(user.PublicIpAddress)
        ? "wan: unknown"
        : $"wan: {user.PublicIpAddress}";
    var remoteIp = string.IsNullOrWhiteSpace(user.ClientIpAddress)
        ? "remote: unknown"
        : $"remote: {user.ClientIpAddress}";
    return $"({publicIp}, {remoteIp})";
}

static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run --project Voip.AdminCli -- <server-url>");
    Console.WriteLine("  dotnet run --project Voip.AdminCli -- <server-url> <room-name>");
    Console.WriteLine("  dotnet run --project Voip.AdminCli");
}

static void PrintInteractiveHelp()
{
    Console.WriteLine("Commands:");
    Console.WriteLine("  /start ws://SERVER-IP:8181   Set the server address");
    Console.WriteLine("  /server                      Show the current server");
    Console.WriteLine("  /rooms                       List all active rooms and users");
    Console.WriteLine("  /banned                      List banned IP addresses");
    Console.WriteLine("  /roomlists                   List room names only");
    Console.WriteLine("  /addroom ROOM_NAME           Create a room in the server catalog");
    Console.WriteLine("  /deleteroom ROOM_NAME        Delete a room and move users to general");
    Console.WriteLine("  /room ROOM_NAME              List users in one room");
    Console.WriteLine("  /kick ROOM USER              Kick a user from a room");
    Console.WriteLine("  /disconnect ROOM USER        Disconnect a user from a room");
    Console.WriteLine("  /mute ROOM USER              Mute a user from the server");
    Console.WriteLine("  /unmute ROOM USER            Remove a server mute from a user");
    Console.WriteLine("  /banip IP_ADDRESS            Ban all connections from an IP");
    Console.WriteLine("  /unbanip IP_ADDRESS          Remove an IP from the ban list");
    Console.WriteLine("  /help                        Show this help");
    Console.WriteLine("  /exit                        Close the admin CLI");
}
