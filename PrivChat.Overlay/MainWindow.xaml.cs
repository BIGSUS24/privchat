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
    // Room (PC-to-PC over LAN) state: while joined, chat traffic routes to the host's
    // relay and the local loopback client loop is paused so the two feeds never mix.
    private ClientWebSocket? _roomSocket;
    private bool _roomJoined;
    private Task? _roomLoopTask;

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
        RegisterHideHotkey();
        ChatCache.LoadInto(Messages);
        LanSection.JoinRequested += Lan_JoinRequested;
        LanSection.LeaveRequested += Lan_LeaveRequested;
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
        // NOACTIVATE: clicking the overlay must not steal foreground from the app below,
        // otherwise a fullscreen video in Chrome drops out of fullscreen and shows the taskbar.
        style |= WsExToolWindow | WsExNoActivate;
        style &= ~WsExAppWindow;
        SetWindowLong(hwnd, GwlExStyle, style);

        // The overlay never activates, so Windows never routes keys to it. While a text box
        // is "armed" (clicked), low-level hooks take keystrokes from the foreground app and
        // type them into the box. Esc, a click outside, or Ctrl+H disarms.
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        PreviewMouseDown += (_, e) =>
            SetTypingTarget(e.OriginalSource is DependencyObject d ? FindParent<System.Windows.Controls.TextBox>(d) : null);
        Closed += (_, _) => SetTypingTarget(null);

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

    // Ctrl+H toggles the overlay via DWM cloak: invisible on this screen AND in every
    // capture, with zero visibility-state churn (hide/show would trip the black-box
    // capture defect; cloak provably does not — see ScreenGrabber notes).
    private const int HotkeyIdHide = 0x504348; // "PCH"
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkKeyH = 0x48;
    private const int WmHotkey = 0x0312;

    private void RegisterHideHotkey()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        // WndProc is hooked in ConfigureWindow.
        if (hwnd != IntPtr.Zero)
        {
            RegisterHotKey(hwnd, HotkeyIdHide, ModControl | ModNoRepeat, VkKeyH);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmMouseActivate)
        {
            handled = true;
            return (IntPtr)MaNoActivate;
        }

        if (msg == WmHotkey && wParam.ToInt64() == HotkeyIdHide)
        {
            ToggleCloaked();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void ToggleCloaked()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var wasCloaked = WindowCloak.IsCloaked;
        if (wasCloaked)
        {
            WindowCloak.Release(hwnd);
        }
        else
        {
            SetTypingTarget(null); // hidden overlay must not keep swallowing keys
            WindowCloak.Acquire(hwnd);
        }

        // Reveal path: climb back over any rival topmost window immediately instead of
        // waiting for the next topmost-guard tick, so Ctrl+H always brings the overlay
        // back on top.
        if (wasCloaked)
        {
            SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0,
                SwpNoSize | SwpNoMove | SwpNoActivate | SwpNoOwnerZOrder);
        }
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
            if (_roomJoined)
            {
                // Chat is served by the host relay; keep the local feed parked so
                // its history/message frames never interleave with the room's.
                try
                {
                    await Task.Delay(RetryDelay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

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
                    ChatCache.SaveFrom(Messages);
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
                    ChatCache.SaveFrom(Messages);
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
        if (body.Length == 0)
        {
            return;
        }

        if (_roomJoined && _roomSocket?.State == WebSocketState.Open)
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(new { type = "send", body }, JsonOptions);
            await _roomSocket.SendAsync(payload, WebSocketMessageType.Text, true, _shutdown.Token);
            MessageBox.Clear();
            return;
        }

        if (_socket?.State != WebSocketState.Open)
        {
            return;
        }

        var localPayload = JsonSerializer.SerializeToUtf8Bytes(new { type = "send", body }, JsonOptions);
        await _socket.SendAsync(localPayload, WebSocketMessageType.Text, true, _shutdown.Token);
        MessageBox.Clear();
    }

    // Manual drag instead of DragMove(): the OS modal move loop (SC_MOVE) presents
    // frames through a DWM drag path that can leak the window's outline into screen
    // captures despite WDA_EXCLUDEFROMCAPTURE. Moving via Left/Top keeps every frame
    // in the normal composition path, where the capture exclusion always holds.
    private bool _dragging;
    private Point _dragCursorOriginPx;
    private Point _dragWindowOrigin;

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_dragging || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        _dragging = true;
        _ = GetCursorPos(out var cursor);
        _dragCursorOriginPx = new Point(cursor.X, cursor.Y);
        _dragWindowOrigin = new Point(Left, Top);
        ((UIElement)sender).CaptureMouse();
    }

    private void Header_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        _ = GetCursorPos(out var cursor);
        var dpi = VisualTreeHelper.GetDpi(this);
        var left = _dragWindowOrigin.X + (cursor.X - _dragCursorOriginPx.X) * 96.0 / dpi.PixelsPerInchX;
        var top = _dragWindowOrigin.Y + (cursor.Y - _dragCursorOriginPx.Y) * 96.0 / dpi.PixelsPerInchY;

        Left = Clamp(left,
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - ActualWidth);
        Top = Clamp(top,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - ActualHeight);
    }

    private void Header_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        ((UIElement)sender).ReleaseMouseCapture();
        OverlayPlacement.Save(new OverlayPlacement(Left, Top, Width, Height));
        ReassertCaptureProtection();
    }

    private void Header_LostMouseCapture(object sender, MouseEventArgs e)
    {
        // Covers abnormal drag ends (alt-tab, focus steal): the normal mouse-up path
        // clears _dragging first, so this only fires for interrupted drags.
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        OverlayPlacement.Save(new OverlayPlacement(Left, Top, Width, Height));
        ReassertCaptureProtection();
    }

    private void ReassertCaptureProtection()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture);
        }
    }

    private void ChatTab_Click(object sender, RoutedEventArgs e) => ShowTab(0);

    private void AiTab_Click(object sender, RoutedEventArgs e) => ShowTab(1);

    private void LlmTab_Click(object sender, RoutedEventArgs e) => ShowTab(2);

    private void LanTab_Click(object sender, RoutedEventArgs e) => ShowTab(3);

    private async void Lan_JoinRequested(object? sender, string request)
    {
        var parts = request.Split('|', 2);
        if (parts.Length != 2)
        {
            return;
        }

        var endpoint = parts[0];
        var code = parts[1];

        try
        {
            var machine = endpoint.Split(':')[0];
            await JoinRoomAsync(endpoint, code);
            _roomJoined = true;
            ShowTab(0);
            LanSection.SetJoined(machine);
        }
        catch (Exception ex)
        {
            LanSection.SetStatus($"Join failed: {ex.Message}");
        }
    }

    private async Task JoinRoomAsync(string endpoint, string code)
    {
        var host = endpoint;
        if (Uri.TryCreate($"http://{endpoint}", UriKind.Absolute, out var parsed))
        {
            host = $"{parsed.Host}:{parsed.Port}";
        }

        _roomSocket?.Dispose();
        _roomSocket = new ClientWebSocket();
        var name = SanitizeRoomName(Environment.MachineName);
        var uri = new Uri($"ws://{host}/ws?role=peer&code={Uri.EscapeDataString(code)}&name={Uri.EscapeDataString(name)}");
        await _roomSocket.ConnectAsync(uri, _shutdown.Token);

        // Park the local feed only after the room handshake succeeded.
        _socket?.Dispose();
        _socket = null;
        SetConnected(false);

        _roomLoopTask = ReceiveRoomLoopAsync(_roomSocket, _shutdown.Token);
    }

    private void Lan_LeaveRequested(object? sender, EventArgs e)
    {
        try
        {
            _roomSocket?.Dispose();
            _roomSocket = null;
            // Clearing the flag lets the still-running client loop resume the local
            // relay connection within one retry cycle; no second loop needed.
            _roomJoined = false;
            LanSection.SetStatus("Not connected to a room.");
        }
        catch (Exception ex)
        {
            LanSection.SetStatus($"Leave failed: {ex.Message}");
        }
    }

    private static string SanitizeRoomName(string name)
    {
        var clean = new string(name.Where(char.IsLetterOrDigit).Take(24).ToArray());
        return clean.Length == 0 ? "pc-peer" : clean;
    }

    private async Task ReceiveRoomLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        try
        {
            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                using var body = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
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
                        ChatCache.SaveFrom(Messages);
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
                        ChatCache.SaveFrom(Messages);
                        ScrollToEnd();
                    });
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_roomSocket == socket)
            {
                Dispatcher.Invoke(() =>
                {
                    if (_roomJoined)
                    {
                        _roomJoined = false;
                        LanSection.SetStatus("Room disconnected. Reconnected to your local chat.");
                    }
                });
            }
        }
    }

    private void ShowTab(int tab)
    {
        var active = new SolidColorBrush(Color.FromRgb(11, 127, 104));
        var inactive = new SolidColorBrush(Color.FromRgb(42, 54, 48));
        ChatTabButton.Background = tab == 0 ? active : inactive;
        AiTabButton.Background = tab == 1 ? active : inactive;
        LlmTabButton.Background = tab == 2 ? active : inactive;
        MessageScroll.Visibility = tab == 0 ? Visibility.Visible : Visibility.Collapsed;
        ChatInputBar.Visibility = tab == 0 ? Visibility.Visible : Visibility.Collapsed;
        AiSection.Visibility = tab == 1 ? Visibility.Visible : Visibility.Collapsed;
        LlmSection.Visibility = tab == 2 ? Visibility.Visible : Visibility.Collapsed;
        LanSection.Visibility = tab == 3 ? Visibility.Visible : Visibility.Collapsed;
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

    // ---- typing without activation ----
    private System.Windows.Controls.TextBox? _typingTarget;
    private IntPtr _kbHook, _mouseHook;
    private HookProc? _kbProc, _mouseProc; // fields keep the delegates alive for the native side

    private void SetTypingTarget(System.Windows.Controls.TextBox? tb)
    {
        _typingTarget = tb;
        if (tb is not null && _kbHook == IntPtr.Zero)
        {
            var mod = GetModuleHandle(null);
            _kbHook = SetWindowsHookEx(WhKeyboardLl, _kbProc ??= KeyboardHook, mod, 0);
            _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc ??= MouseHook, mod, 0);
        }
        else if (tb is null && _kbHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_kbHook);
            UnhookWindowsHookEx(_mouseHook);
            _kbHook = _mouseHook = IntPtr.Zero;
        }
    }

    private IntPtr KeyboardHook(int code, IntPtr wParam, IntPtr lParam)
    {
        var tb = _typingTarget;
        if (code < 0 || tb is null)
        {
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        var k = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
        var key = KeyInterop.KeyFromVirtualKey((int)k.VkCode);
        var ctrl = (GetAsyncKeyState(VkControl) & 0x8000) != 0;
        var altOrWin = (k.Flags & LlkhfAltDown) != 0
            || (GetAsyncKeyState(VkLWin) & 0x8000) != 0 || (GetAsyncKeyState(VkRWin) & 0x8000) != 0;

        // Modifiers, Alt/Win chords and non-editing Ctrl chords (incl. the Ctrl+H hotkey) pass through.
        if (altOrWin
            || key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
                   or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.CapsLock
            || (ctrl && key is not (Key.A or Key.C or Key.V or Key.X or Key.Z)))
        {
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        if (wParam == (IntPtr)WmKeyDown)
        {
            var text = ctrl ? null : ToText(k);
            Dispatcher.BeginInvoke(() => TypeInto(tb, key, ctrl, text)); // hook must return fast
        }

        return (IntPtr)1; // swallowed: the foreground app never sees it
    }

    private void TypeInto(System.Windows.Controls.TextBox tb, Key key, bool ctrl, string? text)
    {
        if (_typingTarget != tb)
        {
            return;
        }

        if (ctrl)
        {
            switch (key)
            {
                case Key.A: tb.SelectAll(); break;
                case Key.C: tb.Copy(); break;
                case Key.V: tb.Paste(); break;
                case Key.X: tb.Cut(); break;
                case Key.Z: tb.Undo(); break;
            }

            return;
        }

        var start = tb.SelectionStart;
        var len = tb.SelectionLength;
        switch (key)
        {
            case Key.Escape:
                SetTypingTarget(null);
                return;
            case Key.Enter:
                // Run the box's own Enter-to-send handler.
                tb.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(tb), 0, key)
                    { RoutedEvent = Keyboard.KeyDownEvent });
                return;
            case Key.Back when len > 0:
            case Key.Delete when len > 0:
                tb.Text = tb.Text.Remove(start, len);
                tb.CaretIndex = start;
                return;
            case Key.Back when start > 0:
                tb.Text = tb.Text.Remove(start - 1, 1);
                tb.CaretIndex = start - 1;
                return;
            case Key.Delete when start < tb.Text.Length:
                tb.Text = tb.Text.Remove(start, 1);
                tb.CaretIndex = start;
                return;
            case Key.Left: tb.CaretIndex = Math.Max(0, (len > 0 ? start : start - 1)); return;
            case Key.Right: tb.CaretIndex = Math.Min(tb.Text.Length, start + Math.Max(len, 1)); return;
            case Key.Home: tb.CaretIndex = 0; return;
            case Key.End: tb.CaretIndex = tb.Text.Length; return;
        }

        if (text is { Length: > 0 } && !char.IsControl(text[0]))
        {
            var room = tb.MaxLength > 0 ? tb.MaxLength - (tb.Text.Length - len) : int.MaxValue;
            if (room <= 0)
            {
                return;
            }

            text = text.Length > room ? text[..room] : text;
            tb.Text = tb.Text.Remove(start, len).Insert(start, text);
            tb.CaretIndex = start + text.Length;
        }
    }

    // ponytail: no IME/dead keys/AltGr; Shift+arrow selection and Tab not handled.
    private static string? ToText(KbdLlHookStruct k)
    {
        var state = new byte[256];
        if ((GetAsyncKeyState(VkShift) & 0x8000) != 0) state[VkShift] = 0x80;
        if ((GetKeyState(VkCapital) & 1) != 0) state[VkCapital] = 1;
        var sb = new StringBuilder(8);
        // flag 0x4: don't mutate the kernel dead-key buffer (Win10 1607+)
        var n = ToUnicodeEx(k.VkCode, k.ScanCode, state, sb, sb.Capacity, 0x4, GetKeyboardLayout(0));
        return n > 0 ? sb.ToString(0, n) : null;
    }

    private IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && (wParam == (IntPtr)WmLButtonDown || wParam == (IntPtr)WmRButtonDown))
        {
            var pt = Marshal.PtrToStructure<NativePoint>(lParam); // MSLLHOOKSTRUCT starts with POINT
            if (GetAncestor(WindowFromPoint(pt), GaRoot) != new WindowInteropHelper(this).Handle)
            {
                Dispatcher.BeginInvoke(() => SetTypingTarget(null)); // clicked elsewhere: stop typing mode
            }
        }

        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private const int WmMouseActivate = 0x0021;
    private const int MaNoActivate = 3;
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int WmKeyDown = 0x0100;
    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonDown = 0x0204;
    private const uint LlkhfAltDown = 0x20;
    private const uint GaRoot = 2;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkCapital = 0x14;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct { public uint VkCode, ScanCode, Flags, Time; public IntPtr ExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] private static extern short GetKeyState(int vk);
    [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint threadId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ToUnicodeEx(uint vk, uint scan, byte[] state, StringBuilder buf, int cap, uint flags, IntPtr hkl);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint pt);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExAppWindow = 0x00040000;
    private const int WsExNoActivate = 0x08000000;

    private static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        for (DependencyObject? c = d; c is not null; c = c is System.Windows.Media.Visual
                 ? System.Windows.Media.VisualTreeHelper.GetParent(c)
                 : LogicalTreeHelper.GetParent(c))
        {
            if (c is T t) return t;
        }
        return default;
    }
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}

// Local message cache: the overlay shows the last conversation immediately after a
// restart, before the relay answers. Plain file under the user profile; nothing
// leaves the machine.
internal static class ChatCache
{
    private static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PrivChat", "chat-cache.json");

    public static void LoadInto(ObservableCollection<OverlayMessage> target)
    {
        try
        {
            if (!File.Exists(CachePath))
            {
                return;
            }

            var messages = JsonSerializer.Deserialize<OverlayMessage[]>(File.ReadAllText(CachePath));
            if (messages is null)
            {
                return;
            }

            foreach (var message in messages)
            {
                target.Add(message);
            }
        }
        catch
        {
            // A corrupt cache must never block startup.
        }
    }

    public static void SaveFrom(ObservableCollection<OverlayMessage> source)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(source.ToArray()));
        }
        catch
        {
            // Caching is best-effort by design.
        }
    }
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


