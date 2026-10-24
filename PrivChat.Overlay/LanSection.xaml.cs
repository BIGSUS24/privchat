using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PrivChat.Overlay;

public partial class LanSection : UserControl
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Discovery beacon port; every running service announces its room here.
    private const int BeaconPort = 50508;

    public ObservableCollection<DiscoveredRoom> Rooms { get; } = new();

    private readonly record struct Beacon(string App, string Machine, int Port);

    public sealed record DiscoveredRoom(string Machine, string Host, int Port)
    {
        public string Name => Machine;
        public string Address => $"{Host}:{Port}";
    }

    public LanSection()
    {
        InitializeComponent();
        DataContext = this;
        RoomList.ItemsSource = Rooms;
        _ = Task.Run(ListenForRoomsAsync);
    }

    public event EventHandler<string>? JoinRequested;

    private async Task ListenForRoomsAsync()
    {
        while (true)
        {
            UdpClient? udp = null;
            try
            {
                udp = new UdpClient(BeaconPort);
                while (true)
                {
                    var result = await udp.ReceiveAsync();
                    var beacon = JsonSerializer.Deserialize<Beacon>(result.Buffer, JsonOptions);
                    if (beacon.App != "privchat" || beacon.Port <= 0)
                    {
                        continue;
                    }

                    var host = result.RemoteEndPoint.Address.ToString();
                    var room = new DiscoveredRoom(beacon.Machine, host, beacon.Port);
                    Dispatcher.Invoke(() =>
                    {
                        var existing = Rooms.FirstOrDefault(r =>
                            r.Host == room.Host && r.Port == room.Port);
                        if (existing is not null)
                        {
                            // Refresh liveness ordering without duplicating entries.
                            Rooms.Remove(existing);
                        }

                        Rooms.Insert(0, room);
                        while (Rooms.Count > 20)
                        {
                            Rooms.RemoveAt(Rooms.Count - 1);
                        }
                    });
                }
            }
            catch (SocketException)
            {
                // Another instance on this machine may already hold the beacon port.
                await Task.Delay(TimeSpan.FromSeconds(10));
            }
            catch
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
            finally
            {
                udp?.Dispose();
            }
        }
    }

    private void RoomList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RoomList.SelectedItem is DiscoveredRoom room)
        {
            HostBox.Text = room.Address;
            CodeBox.Focus();
        }
    }

    private async void Join_Click(object sender, RoutedEventArgs e)
    {
        var address = HostBox.Text.Trim();
        var code = CodeBox.Text.Trim();

        if (!address.Contains(':'))
        {
            SetStatus("Enter host as IP:port (or double-click a room).");
            return;
        }

        if (code.Length == 0)
        {
            SetStatus("Enter the pair code shown on the host's overlay.");
            return;
        }

        SetStatus($"Connecting to {address}...");
        JoinButton.IsEnabled = false;
        try
        {
            await Task.Delay(1); // let status paint before the socket work begins
            JoinRequested?.Invoke(this, $"{address}|{code}");
        }
        finally
        {
            JoinButton.IsEnabled = true;
        }
    }

    private void Leave_Click(object sender, RoutedEventArgs e) =>
        LeaveRequested?.Invoke(this, EventArgs.Empty);

    public event EventHandler? LeaveRequested;

    public void SetStatus(string text) => StatusText.Text = text;

    public void SetJoined(string room)
    {
        StatusText.Text = $"Connected to room {room}. Your chat now goes to that PC.";
    }

    internal static async Task SendDiscoveryPingAsync()
    {
        // Reserved for future "manual refresh"; rooms arrive via the passive listener.
        await Task.CompletedTask;
    }
}
