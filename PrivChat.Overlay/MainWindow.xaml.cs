using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace PrivChat.Overlay;

public partial class MainWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly CancellationTokenSource _shutdown = new();
    private readonly DispatcherTimer _topmostGuard = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private readonly string _baseUrl;
    private ClientWebSocket? _socket;
    private string _pairCode = "------";
    private bool _allowClose;

    public ObservableCollection<OverlayMessage> Messages { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _baseUrl = Args.GetValue(Environment.GetCommandLineArgs(), "--url") ?? "http://127.0.0.1:8787";
        Loaded += MainWindow_Loaded;

        // The overlay refuses casual close requests (WM_CLOSE from other apps): it only
        // ends via the stop script's process kill, or a logoff/shutdown. While the relay
        // service runs it respawns within five seconds anyway.
        Closing += (_, e) =>
        {
            if (_allowClose)
            {
                _shutdown.Cancel();
            }
            else
            {
                e.Cancel = true;
            }
        };
        SystemEvents.SessionEnding += (_, _) =>
        {
            _allowClose = true;
            Close();
        };
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RestorePlacement();
        ConfigureWindow();
        await RunClientLoopAsync(_shutdown.Token);
    }

    private void ConfigureWindow()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLong(hwnd, GwlExStyle);
        style |= WsExToolWindow;
        style &= ~WsExAppWindow;
        SetWindowLong(hwnd, GwlExStyle, style);

        SetWindowPos(hwnd, HwndTopmost, (int)Left, (int)Top, (int)Width, (int)Height,
            SwpNoActivate | SwpShowWindow | SwpFrameChanged);

        var protectedFromCapture = SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture);
        if (!protectedFromCapture)
        {
            SetWindowDisplayAffinity(hwnd, WdaMonitor);
        }

        // Topmost is "last writer wins": another app that claims the topmost band later
        // would sit above us forever. Re-assert it periodically so the overlay always
        // climbs back over any rival topmost window.
        _topmostGuard.Tick += (_, _) =>
            SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate | SwpNoOwnerZOrder);
        _topmostGuard.Start();
    }

    private void RestorePlacement()
    {
        var saved = OverlayPlacement.TryLoad();
        if (saved is not null && saved.Width > 260 && saved.Height > 260)
        {
            Width = saved.Width;
            Height = saved.Height;

            // Clamp to the whole virtual desktop, not the primary screen, so a placement
            // on a secondary monitor survives.
            Left = Clamp(saved.Left,
                SystemParameters.VirtualScreenLeft,
                SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width);
            Top = Clamp(saved.Top,
                SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Height);
            return;
        }

        Width = 360;
        Height = 500;
        Left = Math.Max(0, SystemParameters.WorkArea.Right - Width - 24);
        Top = Math.Max(0, SystemParameters.WorkArea.Top + 48);
    }

    private async Task RunClientLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RefreshLocalInfoAsync(cancellationToken);
                await ConnectOverlayAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                SetConnected(false);
                PairText.Text = "Waiting for local relay";
            }

            // Also delay after a clean disconnect, otherwise a socket that closes
            // immediately spins this loop.
            try
            {
                await Task.Delay(RetryDelay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RefreshLocalInfoAsync(CancellationToken cancellationToken)
    {
        var json = await Http.GetStringAsync($"{_baseUrl.TrimEnd('/')}/api/local-info", cancellationToken);
        var info = JsonSerializer.Deserialize<LocalInfo>(json, JsonOptions);
        _pairCode = info?.PairCode ?? "------";
        var firstUrl = info?.PhoneUrls?.FirstOrDefault();
        PairText.Text = firstUrl is null ? $"Pair code {_pairCode}" : $"{_pairCode}  {firstUrl}";
    }

    private async Task ConnectOverlayAsync(CancellationToken cancellationToken)
    {
        _socket?.Dispose();
        _socket = new ClientWebSocket();
        var wsBase = _baseUrl.TrimEnd('/').Replace("http://", "ws://", StringComparison.OrdinalIgnoreCase).Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase);
        await _socket.ConnectAsync(new Uri($"{wsBase}/ws?role=overlay"), cancellationToken);
        SetConnected(true);
        await ReceiveLoopAsync(_socket, cancellationToken);
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            using var body = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    SetConnected(false);
                    return;
                }

                body.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            var frame = JsonSerializer.Deserialize<ServerFrame>(body.ToArray(), JsonOptions);
            if (frame?.Type == "history" && frame.Messages is not null)
            {
                Dispatcher.Invoke(() =>
                {
                    Messages.Clear();
                    foreach (var message in frame.Messages)
                    {
                        Messages.Add(OverlayMessage.FromChatMessage(message));
                    }
                    ScrollToEnd();
                });
            }
            else if (frame?.Type == "message" && frame.Message is not null)
            {
                Dispatcher.Invoke(() =>
                {
                    Messages.Add(OverlayMessage.FromChatMessage(frame.Message));
                    while (Messages.Count > 200)
                    {
                        Messages.RemoveAt(0);
                    }
                    ScrollToEnd();
                });
            }
        }
    }

    private async void Send_Click(object sender, RoutedEventArgs e) => await SendCurrentAsync();

    private async void MessageBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            await SendCurrentAsync();
        }
    }

    private async Task SendCurrentAsync()
    {
        var body = MessageBox.Text.Trim();
        if (body.Length == 0 || _socket?.State != WebSocketState.Open)
        {
            return;
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(new { type = "send", body }, JsonOptions);
        await _socket.SendAsync(payload, WebSocketMessageType.Text, true, _shutdown.Token);
        MessageBox.Clear();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
            OverlayPlacement.Save(new OverlayPlacement(Left, Top, Width, Height));
        }
    }

    private void ChatTab_Click(object sender, RoutedEventArgs e) => ShowTab(chat: true);

    private void AiTab_Click(object sender, RoutedEventArgs e) => ShowTab(chat: false);

    private void ShowTab(bool chat)
    {
        var active = new SolidColorBrush(Color.FromRgb(11, 127, 104));
        var inactive = new SolidColorBrush(Color.FromRgb(42, 54, 48));
        ChatTabButton.Background = chat ? active : inactive;
        AiTabButton.Background = chat ? inactive : active;
        MessageScroll.Visibility = chat ? Visibility.Visible : Visibility.Collapsed;
        ChatInputBar.Visibility = chat ? Visibility.Visible : Visibility.Collapsed;
        AiSection.Visibility = chat ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetConnected(bool connected)
    {
        Dispatcher.Invoke(() =>
        {
            StatusDot.Fill = new SolidColorBrush(connected ? Color.FromRgb(55, 213, 137) : Color.FromRgb(155, 168, 161));
            if (!connected && _pairCode != "------")
            {
                PairText.Text = $"Pair code {_pairCode}";
            }
        });
    }

    private void ScrollToEnd() => Dispatcher.BeginInvoke(() => MessageScroll.ScrollToEnd(), DispatcherPriority.Background);

    private static double Clamp(double value, double min, double max) => Math.Min(Math.Max(value, min), Math.Max(min, max));

    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExAppWindow = 0x00040000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const uint WdaMonitor = 0x00000001;
    private const uint WdaExcludeFromCapture = 0x00000011;
    private static readonly IntPtr HwndTopmost = new(-1);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
}

public sealed record OverlayMessage(string Sender, string Body, DateTimeOffset SentAt)
{
    public string TimeText => SentAt.ToLocalTime().ToString("HH:mm");

    public static OverlayMessage FromChatMessage(ChatMessage message) =>
        new(message.Sender, message.Body, message.SentAt);
}

public sealed record ChatMessage(Guid Id, string Sender, string Body, DateTimeOffset SentAt);
internal sealed record ServerFrame(string? Type, ChatMessage[]? Messages, ChatMessage? Message);
internal sealed record LocalInfo(string PairCode, string ListenUrl, string[] PhoneUrls);

internal sealed record OverlayPlacement(double Left, double Top, double Width, double Height)
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PrivChat", "overlay.json");

    public static OverlayPlacement? TryLoad()
    {
        try
        {
            return File.Exists(ConfigPath)
                ? JsonSerializer.Deserialize<OverlayPlacement>(File.ReadAllText(ConfigPath))
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static void Save(OverlayPlacement placement)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(placement));
        }
        catch
        {
            // Position persistence is noncritical.
        }
    }
}

internal static class Args
{
    public static string? GetValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}


