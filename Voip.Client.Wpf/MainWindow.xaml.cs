using System.Collections.ObjectModel;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Voip.Client.Core.Audio;
using Voip.Client.Core.Signaling;
using Voip.Shared;

namespace Voip.Client.Wpf;

public partial class MainWindow : Window
{
    private readonly AudioCapture capture = new();
    private readonly AudioPlayback playback = new();
    private readonly OpusCodec codec = new();
    private readonly SignalClient signal = new();
    private readonly ObservableCollection<string> availableRooms = [];
    private readonly ObservableCollection<RoomUserStatus> connectedUsers = [];
    private readonly ObservableCollection<string> logEntries = [];
    private readonly ClientSettings settings;

    private LogWindow? logWindow;
    private bool pushToTalkActive;
    private bool micLatched;
    private bool lastReportedMicState;
    private bool isServerMuted;
    private int voicedFrameHangover;
    private string currentRoom = string.Empty;
    private string currentUserName = string.Empty;
    private string currentPublicIp = string.Empty;
    private readonly string senderId = Environment.MachineName + "-" + Guid.NewGuid().ToString("N")[..6];

    public MainWindow()
    {
        InitializeComponent();

        settings = ClientSettings.Load();
        ServerBox.Text = settings.ServerUrl;
        UserNameBox.Text = settings.UserName;
        RoomBox.ItemsSource = availableRooms;
        UsersList.ItemsSource = connectedUsers;
        SetAvailableRooms([settings.Room, "general"]);
        SelectRoom(settings.Room);
        AddLog("Client started.");

        capture.OnAudioCaptured += data =>
        {
            if (!IsTransmitting() || !signal.IsConnected || string.IsNullOrWhiteSpace(currentRoom))
            {
                return;
            }

            if (!ShouldSendAudioFrame(data))
            {
                return;
            }

            var encoded = codec.Encode(data);
            signal.Send(new SignalMessage
            {
                Type = "audio",
                Room = currentRoom,
                Sender = senderId,
                Data = Convert.ToBase64String(encoded)
            });
        };

        signal.OnConnected += () =>
        {
            AddLog($"Socket connected to {ServerBox.Text.Trim()}");
            signal.Send(new SignalMessage
            {
                Type = "join",
                Room = currentRoom,
                Sender = senderId,
                Data = currentUserName,
                PublicIpAddress = currentPublicIp
            });
        };

        signal.OnConnectionError += message =>
        {
            AddLog($"Connection error: {message}");
            Dispatcher.Invoke(() =>
            {
                ResetConnectionState();
                Status.Text = BuildConnectionErrorMessage(message);
            });
        };

        signal.OnDisconnected += details =>
        {
            AddLog($"Socket disconnected. {details}");
            Dispatcher.Invoke(() =>
            {
                ResetConnectionState();
                if (string.IsNullOrWhiteSpace(Status.Text) || Status.Text == "Connecting...")
                {
                    Status.Text = $"Disconnected from server. {details}";
                }
            });
        };

        signal.OnMessage += message =>
        {
            if (message.Type != "audio")
            {
                AddLog($"Received message '{message.Type}' for room '{message.Room}'.");
            }

            if (message.Type == "connected")
            {
                Dispatcher.Invoke(() =>
                {
                    TalkButton.IsEnabled = true;
                    MicToggleButton.IsEnabled = true;
                    ConnectButton.IsEnabled = false;
                    DisconnectButton.IsEnabled = true;
                    ServerBox.IsEnabled = false;
                    UserNameBox.IsEnabled = false;
                    RoomBox.IsEnabled = true;
                    ChangeRoomButton.IsEnabled = true;
                    Status.Text = message.Data;

                    signal.Send(new SignalMessage
                    {
                        Type = "room_list",
                        Sender = senderId
                    });
                });
                return;
            }

            if (message.Type is "kicked" or "banned")
            {
                Dispatcher.Invoke(() =>
                {
                    ResetConnectionState();
                    Status.Text = message.Data;
                });
                return;
            }

            if (message.Type == "room_catalog")
            {
                Dispatcher.Invoke(() =>
                {
                    var selectedRoom = string.IsNullOrWhiteSpace(currentRoom)
                        ? settings.Room
                        : currentRoom;
                    SetAvailableRooms(message.Users.Count > 0 ? message.Users : ["general"]);
                    SelectRoom(selectedRoom);
                });
                return;
            }

            if (message.Type == "room_changed")
            {
                Dispatcher.Invoke(() =>
                {
                    currentRoom = message.Room;
                    pushToTalkActive = false;
                    micLatched = false;
                    lastReportedMicState = false;
                    MicToggleButton.IsChecked = false;
                    MicToggleButton.Content = "Mic Off";
                    settings.Room = currentRoom;
                    settings.Save();
                    SelectRoom(currentRoom);
                    Status.Text = message.Data;
                });
                return;
            }

            if (message.Type == "mute_state")
            {
                Dispatcher.Invoke(() =>
                {
                    isServerMuted = message.Data == "muted";
                    if (isServerMuted)
                    {
                        pushToTalkActive = false;
                        micLatched = false;
                        lastReportedMicState = false;
                        MicToggleButton.IsChecked = false;
                        MicToggleButton.Content = "Mic Off";
                        MicToggleButton.IsEnabled = false;
                        TalkButton.IsEnabled = false;
                        Status.Text = "Your microphone was muted by the server.";
                    }
                    else if (signal.IsConnected)
                    {
                        MicToggleButton.IsEnabled = true;
                        TalkButton.IsEnabled = true;
                        Status.Text = "Your microphone was unmuted by the server.";
                    }
                });
                return;
            }

            if (message.Type == "users")
            {
                Dispatcher.Invoke(() =>
                {
                    connectedUsers.Clear();
                    foreach (var participant in message.Participants.OrderBy(user => user.UserName))
                    {
                        connectedUsers.Add(participant);
                    }
                });
                return;
            }

            if (message.Type == "system")
            {
                Dispatcher.Invoke(() => Status.Text = message.Data);
                return;
            }

            if (message.Type != "audio" || string.IsNullOrWhiteSpace(message.Data))
            {
                return;
            }

            var encodedAudio = Convert.FromBase64String(message.Data);
            var decodedAudio = codec.Decode(encodedAudio);
            playback.Play(decodedAudio);
        };
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        currentUserName = UserNameBox.Text.Trim();
        currentRoom = GetSelectedRoom();
        var serverUrl = ServerBox.Text.Trim();
        AddLog($"Connect requested. Server={serverUrl}, User={currentUserName}, Room={currentRoom}");

        if (string.IsNullOrWhiteSpace(currentUserName))
        {
            Status.Text = "Enter a username before connecting.";
            return;
        }

        if (string.IsNullOrWhiteSpace(currentRoom))
        {
            Status.Text = "Enter a room name before connecting.";
            return;
        }

        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeWs && uri.Scheme != Uri.UriSchemeWss))
        {
            Status.Text = "Use a valid server address like ws://YOUR-IP:8181.";
            return;
        }

        if (availableRooms.Count == 0)
        {
            await RefreshRoomsAsync(serverUrl);
            currentRoom = GetSelectedRoom();
        }

        currentPublicIp = await TryGetPublicIpAsync();

        settings.ServerUrl = serverUrl;
        settings.UserName = currentUserName;
        settings.Room = currentRoom;
        settings.Save();

        connectedUsers.Clear();

        try
        {
            capture.Start();
            signal.Connect(serverUrl);
            Status.Text = "Connecting...";
            AddLog("Audio capture started and websocket connect initiated.");
        }
        catch (Exception ex)
        {
            capture.Stop();
            Status.Text = BuildConnectionErrorMessage(ex.Message);
            AddLog($"Connect failed before socket open: {ex.Message}");
        }
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        Status.Text = "Disconnecting...";
        AddLog("Manual disconnect requested.");
        signal.Disconnect();
    }

    private void LogsButton_Click(object sender, RoutedEventArgs e)
    {
        if (logWindow is null || !logWindow.IsLoaded)
        {
            logWindow = new LogWindow(logEntries);
            logWindow.Owner = this;
            logWindow.Closed += (_, _) => logWindow = null;
            logWindow.Show();
            AddLog("Log window opened.");
            return;
        }

        if (logWindow.WindowState == WindowState.Minimized)
        {
            logWindow.WindowState = WindowState.Normal;
        }

        logWindow.Activate();
    }

    private void ChangeRoom_Click(object sender, RoutedEventArgs e)
    {
        if (!signal.IsConnected)
        {
            return;
        }

        var selectedRoom = GetSelectedRoom();
        if (string.IsNullOrWhiteSpace(selectedRoom))
        {
            Status.Text = "Select a room before changing rooms.";
            return;
        }

        if (string.Equals(selectedRoom, currentRoom, StringComparison.OrdinalIgnoreCase))
        {
            Status.Text = $"You are already in room '{currentRoom}'.";
            return;
        }

        pushToTalkActive = false;
        micLatched = false;
        lastReportedMicState = false;
        MicToggleButton.IsChecked = false;
        MicToggleButton.Content = "Mic Off";
        signal.Send(new SignalMessage
        {
            Type = "change_room",
            Room = selectedRoom,
            Sender = senderId,
            Data = currentUserName
        });
        Status.Text = $"Changing room to '{selectedRoom}'...";
        AddLog($"Requested room change from '{currentRoom}' to '{selectedRoom}'.");
    }

    private void StartTalk(object sender, MouseButtonEventArgs e)
    {
        if (!signal.IsConnected)
        {
            return;
        }

        pushToTalkActive = true;
        ReportMicStateIfNeeded();
        UpdateStatus();
    }

    private void StopTalk(object sender, RoutedEventArgs e)
    {
        pushToTalkActive = false;
        ReportMicStateIfNeeded();
        UpdateStatus();
    }

    private void TalkLostMouseCapture(object sender, MouseEventArgs e)
    {
        pushToTalkActive = false;
        ReportMicStateIfNeeded();
        UpdateStatus();
    }

    private void MicToggleChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton toggle)
        {
            return;
        }

        micLatched = toggle.IsChecked == true;
        toggle.Content = micLatched ? "Mic On" : "Mic Off";
        ReportMicStateIfNeeded();
        UpdateStatus();
    }

    private bool IsTransmitting()
    {
        return pushToTalkActive || micLatched;
    }

    private void UpdateStatus()
    {
        if (!signal.IsConnected)
        {
            return;
        }

        if (isServerMuted)
        {
            Status.Text = "Your microphone is muted by the server.";
            return;
        }

        if (IsTransmitting())
        {
            Status.Text = micLatched && !pushToTalkActive
                ? $"{currentUserName} microphone is live."
                : "Transmitting...";
            return;
        }

        Status.Text = $"{currentUserName} is connected to room '{currentRoom}'.";
    }

    private void ResetConnectionState()
    {
        TalkButton.IsEnabled = false;
        MicToggleButton.IsEnabled = false;
        MicToggleButton.IsChecked = false;
        MicToggleButton.Content = "Mic Off";
        ConnectButton.IsEnabled = true;
        DisconnectButton.IsEnabled = false;
        ServerBox.IsEnabled = true;
        UserNameBox.IsEnabled = true;
        RoomBox.IsEnabled = true;
        ChangeRoomButton.IsEnabled = false;
        pushToTalkActive = false;
        micLatched = false;
        lastReportedMicState = false;
        voicedFrameHangover = 0;
        isServerMuted = false;
        connectedUsers.Clear();
        capture.Stop();
    }

    private void ReportMicStateIfNeeded()
    {
        if (!signal.IsConnected || isServerMuted)
        {
            return;
        }

        var micIsOn = IsTransmitting();
        if (micIsOn == lastReportedMicState)
        {
            return;
        }

        lastReportedMicState = micIsOn;
        signal.Send(new SignalMessage
        {
            Type = "mic_state",
            Room = currentRoom,
            Sender = senderId,
            Data = micIsOn ? "on" : "off"
        });
    }

    private string BuildConnectionErrorMessage(string details)
    {
        return $"Unable to reach the server. Check the public IP/domain, port 8181 forwarding, and Windows Firewall. Details: {details}";
    }

    private void AddLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";

        Dispatcher.Invoke(() =>
        {
            logEntries.Add(line);
            while (logEntries.Count > 500)
            {
                logEntries.RemoveAt(0);
            }
        });
    }

    private bool ShouldSendAudioFrame(byte[] data)
    {
        var sampleCount = data.Length / 2;
        if (sampleCount == 0)
        {
            return false;
        }

        long totalLevel = 0;
        for (var index = 0; index < data.Length; index += 2)
        {
            var sample = BitConverter.ToInt16(data, index);
            totalLevel += Math.Abs(sample);
        }

        var averageLevel = totalLevel / sampleCount;
        var isVoiceDetected = averageLevel >= 600;

        if (isVoiceDetected)
        {
            voicedFrameHangover = 8;
            return true;
        }

        if (voicedFrameHangover <= 0)
        {
            return false;
        }

        voicedFrameHangover--;
        return true;
    }

    protected override void OnClosed(EventArgs e)
    {
        capture.Dispose();
        playback.Dispose();
        signal.Dispose();
        base.OnClosed(e);
    }

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        AddLog("Main window rendered. Attempting initial room refresh.");
        await RefreshRoomsAsync(ServerBox.Text.Trim(), silentOnFailure: true);
    }

    private string GetSelectedRoom()
    {
        return RoomBox.SelectedItem as string ??
               RoomBox.Text?.Trim() ??
               string.Empty;
    }

    private void SetAvailableRooms(IEnumerable<string> rooms)
    {
        var uniqueRooms = rooms
            .Where(room => !string.IsNullOrWhiteSpace(room))
            .Select(room => room.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(room => room)
            .ToList();

        availableRooms.Clear();
        foreach (var room in uniqueRooms)
        {
            availableRooms.Add(room);
        }
    }

    private void SelectRoom(string roomName)
    {
        if (string.IsNullOrWhiteSpace(roomName))
        {
            RoomBox.SelectedIndex = availableRooms.Count > 0 ? 0 : -1;
            return;
        }

        var existingRoom = availableRooms.FirstOrDefault(room =>
            string.Equals(room, roomName, StringComparison.OrdinalIgnoreCase));

        if (existingRoom is null)
        {
            availableRooms.Add(roomName);
            existingRoom = roomName;
        }

        RoomBox.SelectedItem = existingRoom;
    }

    private async Task RefreshRoomsAsync(string serverUrl, bool silentOnFailure = false)
    {
        try
        {
            AddLog($"Refreshing room list from {serverUrl}.");
            var rooms = await FetchRoomsAsync(serverUrl);
            if (rooms is null || rooms.Count == 0)
            {
                SetAvailableRooms(["general"]);
                SelectRoom(settings.Room);
                if (!silentOnFailure)
                {
                    Status.Text = "No active rooms were reported. Using 'general'.";
                }
                AddLog("Room refresh returned no rooms. Falling back to 'general'.");
                return;
            }

            SetAvailableRooms(rooms);

            var preferredRoom = !string.IsNullOrWhiteSpace(settings.Room) && rooms.Any(room =>
                string.Equals(room, settings.Room, StringComparison.OrdinalIgnoreCase))
                ? settings.Room
                : rooms[0];

            SelectRoom(preferredRoom);
            if (!silentOnFailure)
            {
                Status.Text = $"Loaded {rooms.Count} room(s) from the server.";
            }
            AddLog($"Room refresh succeeded. Rooms={string.Join(", ", rooms)}");
        }
        catch (Exception ex)
        {
            SetAvailableRooms([settings.Room, "general"]);
            SelectRoom(settings.Room);
            if (!silentOnFailure)
            {
                Status.Text = BuildConnectionErrorMessage(ex.Message);
            }
            AddLog($"Room refresh failed: {ex.Message}");
        }
    }

    private static Task<List<string>> FetchRoomsAsync(string serverUrl)
    {
        return Task.Run(() =>
        {
            if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeWs && uri.Scheme != Uri.UriSchemeWss))
            {
                throw new InvalidOperationException("Use a valid server address like ws://YOUR-IP:8181.");
            }

            using var roomClient = new SignalClient();
            using var waitHandle = new ManualResetEventSlim(false);

            List<string>? rooms = null;
            string? error = null;

            roomClient.OnConnected += () =>
            {
                roomClient.Send(new SignalMessage
                {
                    Type = "room_list",
                    Sender = "wpf-client"
                });
            };

            roomClient.OnConnectionError += message =>
            {
                error = message;
                waitHandle.Set();
            };

            roomClient.OnMessage += message =>
            {
                if (message.Type != "room_catalog")
                {
                    return;
                }

                rooms = message.Users.Count == 0 ? ["general"] : [.. message.Users];
                waitHandle.Set();
            };

            roomClient.Connect(serverUrl);

            if (!waitHandle.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("Timed out waiting for room list.");
            }

            roomClient.Disconnect();

            if (!string.IsNullOrWhiteSpace(error))
            {
                throw new InvalidOperationException(error);
            }

            return rooms is null || rooms.Count == 0 ? ["general"] : rooms;
        });
    }

    private static async Task<string> TryGetPublicIpAsync()
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
            return string.Empty;
        }
    }
}
