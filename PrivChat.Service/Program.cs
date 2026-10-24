using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

var options = RuntimeOptions.Parse(args);
var pairCode = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
var chat = new ChatHub(pairCode);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

builder.Logging.ClearProviders();
builder.WebHost.UseUrls(options.ListenUrl);

var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

app.MapGet("/", (HttpContext context) =>
{
    // The page ships inside the binary, so a cached copy survives updates and looks
    // like the fix never landed.
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(WebClient.Html, "text/html; charset=utf-8");
});
app.MapGet("/healthz", () => Results.NoContent());
app.MapGet("/api/local-info", (HttpContext context) =>
{
    if (!NetworkInfo.IsLoopback(context.Connection.RemoteIpAddress))
    {
        return Results.NotFound();
    }

    return Results.Json(new
    {
        pairCode,
        listenUrl = options.ListenUrl,
        phoneUrls = NetworkInfo.GetPhoneUrls(options.Port)
    });
});

app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var role = context.Request.Query["role"].ToString();
    if (role is not ("phone" or "overlay" or "peer"))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    // Phones and LAN PC peers both authenticate with the pair code shown on the
    // overlay; overlays stay loopback-only.
    if (role is "phone" or "peer" && !chat.AuthorizePhone(context))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    if (role == "overlay" && !NetworkInfo.IsLoopback(context.Connection.RemoteIpAddress))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    // A peer's messages carry its machine name so both overlays can tell senders
    // apart; bounded and stripped of everything but letters/digits.
    var senderLabel = role;
    if (role == "peer")
    {
        var raw = context.Request.Query["name"].ToString();
        var clean = new string(raw.Where(char.IsLetterOrDigit).Take(24).ToArray());
        senderLabel = clean.Length == 0 ? "pc-peer" : clean;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await chat.AcceptAsync(socket, role, senderLabel, context.RequestAborted);
});

async Task RunHostAsync(CancellationToken cancellationToken)
{
    var serverTask = app.RunAsync(cancellationToken);

    if (options.LaunchOverlay)
    {
        _ = Task.Run(() => OverlayLauncher.MonitorAsync(options, cancellationToken), cancellationToken);
    }

    // LAN room discovery: announce this machine's relay so other PrivChat overlays
    // can list it. Presence only — never the pair code.
    _ = Task.Run(() => RoomBeacon.LoopAsync(options.Port, cancellationToken), cancellationToken);

    await serverTask;
}

if (options.ServiceMode)
{
    return await NativeWindowsService.RunAsync("PrivChatLocal", RunHostAsync);
}

Console.WriteLine($"PrivChat listening on {options.ListenUrl}");
Console.WriteLine($"Pairing code: {pairCode}");
Console.WriteLine("Phone URLs:");
foreach (var url in NetworkInfo.GetPhoneUrls(options.Port))
{
    Console.WriteLine($"  {url}");
}

await RunHostAsync(CancellationToken.None);
return 0;

internal sealed record RuntimeOptions(int Port, string ListenUrl, bool ServiceMode, bool LaunchOverlay, string? OverlayPath)
{
    public static RuntimeOptions Parse(string[] args)
    {
        var port = GetInt(args, "--port") ?? GetInt(args, "-p") ?? 8787;
        var serviceMode = Has(args, "--service");
        var launchOverlay = !Has(args, "--no-overlay");
        var listenUrl = GetValue(args, "--listen") ?? $"http://0.0.0.0:{port}";
        var overlayPath = GetValue(args, "--overlay-path");

        // Phone URLs and the overlay handshake must use the port we actually bind,
        // which --listen can override.
        if (Uri.TryCreate(listenUrl, UriKind.Absolute, out var listenUri) && listenUri.Port > 0)
        {
            port = listenUri.Port;
        }

        return new RuntimeOptions(port, listenUrl, serviceMode, launchOverlay, overlayPath);
    }

    private static bool Has(string[] args, string name) =>
        args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));

    private static string? GetValue(string[] args, string name)
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

    private static int? GetInt(string[] args, string name) =>
        int.TryParse(GetValue(args, name), out var value) ? value : null;
}

internal sealed class ChatHub(string pairCode)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<Guid, ClientConnection> _clients = new();
    private readonly ConcurrentQueue<ChatMessage> _messages = new();
    private readonly ConcurrentDictionary<string, AuthWindow> _authFailures = new();

    private const int MaxFailuresPerWindow = 20;
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(10);

    public bool AuthorizePhone(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (IsLockedOut(remote))
        {
            return false;
        }

        var code = context.Request.Query["code"].ToString();
        if (CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(code),
                Encoding.UTF8.GetBytes(pairCode)))
        {
            _authFailures.TryRemove(remote, out _);
            return true;
        }

        _authFailures.AddOrUpdate(remote, _ => AuthWindow.New(), (_, prior) => prior.Add());
        return false;
    }

    private bool IsLockedOut(string remote)
    {
        DropExpiredFailures();
        return _authFailures.TryGetValue(remote, out var window) &&
               window.IsActive &&
               window.Count >= MaxFailuresPerWindow;
    }

    // ponytail: full scan per attempt. Entry count is bounded by LAN peers; index by
    // bucket only if that stops being true.
    private void DropExpiredFailures()
    {
        foreach (var (remote, window) in _authFailures)
        {
            if (!window.IsActive)
            {
                _authFailures.TryRemove(remote, out _);
            }
        }
    }

    public async Task AcceptAsync(WebSocket socket, string role, string senderLabel, CancellationToken cancellationToken)
    {
        var connection = new ClientConnection(Guid.NewGuid(), role, senderLabel, socket);
        _clients[connection.Id] = connection;

        try
        {
            await connection.SendAsync(new { type = "history", messages = _messages.ToArray() }, cancellationToken);
            await ReceiveLoopAsync(connection, cancellationToken);
        }
        finally
        {
            _clients.TryRemove(connection.Id, out _);
        }
    }

    private async Task ReceiveLoopAsync(ClientConnection connection, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        while (!cancellationToken.IsCancellationRequested && connection.Socket.State == WebSocketState.Open)
        {
            using var body = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await connection.Socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await connection.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", cancellationToken);
                    return;
                }

                body.Write(buffer, 0, result.Count);
                if (body.Length > 16_384)
                {
                    await connection.Socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "message too large", cancellationToken);
                    return;
                }
            }
            while (!result.EndOfMessage);

            var inbound = JsonSerializer.Deserialize<InboundMessage>(body.ToArray(), JsonOptions);
            if (inbound?.Type != "send")
            {
                continue;
            }

            var text = (inbound.Body ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                continue;
            }

            if (text.Length > 4000)
            {
                text = text[..4000];
            }

            // The wire protocol labels senders "pc"/"phone"; the overlay is the PC side
            // and LAN peers carry their machine name.
            var sender = connection.Role == "overlay" ? "pc" : connection.SenderLabel;
            var message = new ChatMessage(Guid.NewGuid(), sender, text, DateTimeOffset.UtcNow);
            _messages.Enqueue(message);
            while (_messages.Count > 200)
            {
                _messages.TryDequeue(out _);
            }

            await BroadcastAsync(new { type = "message", message }, cancellationToken);
        }
    }

    private Task BroadcastAsync(object payload, CancellationToken cancellationToken)
    {
        var tasks = _clients.Values
            .Where(client => client.Socket.State == WebSocketState.Open)
            .Select(client => client.SendAsync(payload, cancellationToken));
        return Task.WhenAll(tasks);
    }

    private sealed record InboundMessage(string? Type, string? Body);

    private sealed record AuthWindow(DateTimeOffset StartedAt, int Count)
    {
        public static AuthWindow New() => new(DateTimeOffset.UtcNow, 1);

        public bool IsActive => DateTimeOffset.UtcNow - StartedAt <= FailureWindow;

        public AuthWindow Add() => IsActive ? this with { Count = Count + 1 } : New();
    }
}

internal sealed record ChatMessage(Guid Id, string Sender, string Body, DateTimeOffset SentAt);

internal sealed record ClientConnection(Guid Id, string Role, string SenderLabel, WebSocket Socket)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public async Task SendAsync(object payload, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await Socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }
}

internal static class NetworkInfo
{
    public static bool IsLoopback(IPAddress? address) =>
        address is not null && (IPAddress.IsLoopback(address) || address.Equals(IPAddress.IPv6Loopback));    public static IReadOnlyList<string> GetPhoneUrls(int port)
    {
        var urls = new List<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(unicast.Address))
                {
                    urls.Add($"http://{unicast.Address}:{port}");
                }
            }
        }

        return urls.Distinct().Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}

// LAN presence beacon: a tiny UDP broadcast so other PrivChat instances on the same
// network can list this machine as a joinable room. Payload carries the machine name
// and relay port only — the pair code is never broadcast.
internal static class RoomBeacon
{
    private const int BeaconPort = 50508;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);

    public static async Task LoopAsync(int port, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            app = "privchat",
            machine = Environment.MachineName,
            port
        });

        using var udp = new UdpClient();
        udp.EnableBroadcast = true;
        var broadcast = new IPEndPoint(IPAddress.Broadcast, BeaconPort);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await udp.SendAsync(payload, broadcast, cancellationToken);
            }
            catch (SocketException)
            {
                // No network / firewall noise: keep announcing on the next tick.
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await Task.Delay(Interval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}

internal static class OverlayLauncher
{
    public static async Task MonitorAsync(RuntimeOptions options, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TryLaunch(options);
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }
    }

    public static void TryLaunch(RuntimeOptions options)
    {
        var overlayPath = ResolveOverlayPath(options);
        if (overlayPath is null || !File.Exists(overlayPath))
        {
            return;
        }

        if (Process.GetProcessesByName(Path.GetFileNameWithoutExtension(overlayPath)).Any())
        {
            return;
        }

        if (!NativeProcessLauncher.TryLaunchInActiveConsole(overlayPath, $"--url http://127.0.0.1:{options.Port}"))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = overlayPath,
                    Arguments = $"--url http://127.0.0.1:{options.Port}",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            catch
            {
                // Presentation is optional; the service and phone site still run.
            }
        }
    }

    private static string? ResolveOverlayPath(RuntimeOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.OverlayPath))
        {
            return Path.GetFullPath(options.OverlayPath);
        }

        var sibling = Path.Combine(AppContext.BaseDirectory, "PrivChat.Overlay.exe");
        if (File.Exists(sibling))
        {
            return sibling;
        }

        return Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "PrivChat.Overlay", "bin", "Debug", "net8.0-windows", "PrivChat.Overlay.exe"));
    }
}

internal static class NativeWindowsService
{
    private const int ServiceWin32OwnProcess = 0x00000010;
    private const int ServiceStartPending = 0x00000002;
    private const int ServiceStopPending = 0x00000003;
    private const int ServiceRunning = 0x00000004;
    private const int ServiceStopped = 0x00000001;
    private const int ServiceAcceptStop = 0x00000001;
    private const int ServiceAcceptShutdown = 0x00000004;
    private const int ErrorFailedServiceControllerConnect = 1063;

    // Must exceed the host's graceful shutdown time or the SCM gives up on the stop.
    private const int PendingWaitHintMs = 30_000;

    private static Func<CancellationToken, Task>? _runAsync;
    private static readonly CancellationTokenSource StopCts = new();
    private static readonly ManualResetEventSlim Stopped = new(false);
    private static IntPtr _statusHandle;
    private static int _checkPoint;
    private static string _serviceName = string.Empty;
    private static ServiceMainDelegate? _serviceMain;
    private static ServiceControlHandler? _handler;

    public static async Task<int> RunAsync(string serviceName, Func<CancellationToken, Task> runAsync)
    {
        _runAsync = runAsync;
        _serviceName = serviceName;
        _serviceMain = ServiceMain;

        var table = new[]
        {
            new ServiceTableEntry { ServiceName = serviceName, ServiceProc = _serviceMain },
            new ServiceTableEntry()
        };

        if (StartServiceCtrlDispatcher(table))
        {
            return 0;
        }

        if (Marshal.GetLastWin32Error() == ErrorFailedServiceControllerConnect)
        {
            await runAsync(CancellationToken.None);
            return 0;
        }

        return Marshal.GetLastWin32Error();
    }

    private static void ServiceMain(int argc, IntPtr argv)
    {
        _handler = ControlHandler;
        _statusHandle = RegisterServiceCtrlHandler(_serviceName, _handler);
        SetStatus(ServiceStartPending, 0);

        _ = Task.Run(async () =>
        {
            try
            {
                SetStatus(ServiceRunning, ServiceAcceptStop | ServiceAcceptShutdown);
                if (_runAsync is not null)
                {
                    await _runAsync(StopCts.Token);
                }
            }
            catch (Exception ex) when (!StopCts.IsCancellationRequested)
            {
                // Service mode has no console, so a startup failure would otherwise vanish.
                StartupLog.WriteFailure(ex);
            }
            finally
            {
                SetStatus(ServiceStopped, 0);
                Stopped.Set();
            }
        });

        Stopped.Wait();
    }

    private static void ControlHandler(int control)
    {
        if (control is 1 or 5)
        {
            SetStatus(ServiceStopPending, 0);
            StopCts.Cancel();
        }
    }

    private static void SetStatus(int state, int acceptedControls)
    {
        if (_statusHandle == IntPtr.Zero)
        {
            return;
        }

        var pending = state is ServiceStartPending or ServiceStopPending;
        var status = new ServiceStatus
        {
            ServiceType = ServiceWin32OwnProcess,
            CurrentState = state,
            ControlsAccepted = acceptedControls,
            Win32ExitCode = 0,
            ServiceSpecificExitCode = 0,
            // A pending state needs an advancing checkpoint and a nonzero wait hint.
            // Reporting zeroes makes the SCM treat the service as hung and reject
            // further control messages with error 2189.
            CheckPoint = pending ? ++_checkPoint : 0,
            WaitHint = pending ? PendingWaitHintMs : 0
        };
        SetServiceStatus(_statusHandle, ref status);
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool StartServiceCtrlDispatcher([In] ServiceTableEntry[] serviceTable);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr RegisterServiceCtrlHandler(string serviceName, ServiceControlHandler handlerProc);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetServiceStatus(IntPtr serviceStatusHandle, ref ServiceStatus serviceStatus);

    private delegate void ServiceMainDelegate(int argc, IntPtr argv);
    private delegate void ServiceControlHandler(int control);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServiceTableEntry
    {
        public string? ServiceName;
        public ServiceMainDelegate? ServiceProc;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public int ServiceType;
        public int CurrentState;
        public int ControlsAccepted;
        public int Win32ExitCode;
        public int ServiceSpecificExitCode;
        public int CheckPoint;
        public int WaitHint;
    }
}

internal static class StartupLog
{
    // Records why the service died, nothing else. Message bodies and pair codes are
    // never written here; the relay stays log-free by design.
    public static void WriteFailure(Exception exception)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "PrivChat");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "service-error.log"),
                $"{DateTimeOffset.UtcNow:o} {exception}{Environment.NewLine}");
        }
        catch
        {
            // If even the failure log fails there is nowhere left to report it.
        }
    }
}

internal static class NativeProcessLauncher
{
    private const uint TokenAssignPrimary = 0x0001;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenImpersonate = 0x0004;
    private const uint TokenQuery = 0x0008;
    private const uint TokenAdjustDefault = 0x0080;
    private const uint TokenAdjustSessionId = 0x0100;
    private const uint CreateUnicodeEnvironment = 0x00000400;

    public static bool TryLaunchInActiveConsole(string executablePath, string arguments)
    {
        var sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF)
        {
            return false;
        }

        if (!WTSQueryUserToken(sessionId, out var userToken))
        {
            return false;
        }

        try
        {
            var desiredAccess = TokenAssignPrimary | TokenDuplicate | TokenImpersonate |
                                TokenQuery | TokenAdjustDefault | TokenAdjustSessionId;
            if (!DuplicateTokenEx(userToken, desiredAccess, IntPtr.Zero, 2, 1, out var primaryToken))
            {
                return false;
            }

            try
            {
                CreateEnvironmentBlock(out var environment, primaryToken, false);
                try
                {
                    var startupInfo = new StartupInfo
                    {
                        Cb = Marshal.SizeOf<StartupInfo>(),
                        Desktop = "winsta0\\default"
                    };
                    var processInfo = new ProcessInformation();
                    var commandLine = $"\"{executablePath}\" {arguments}";
                    var ok = CreateProcessAsUser(
                        primaryToken,
                        null,
                        commandLine,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        false,
                        CreateUnicodeEnvironment,
                        environment,
                        Path.GetDirectoryName(executablePath),
                        ref startupInfo,
                        out processInfo);

                    if (processInfo.Process != IntPtr.Zero)
                    {
                        CloseHandle(processInfo.Process);
                    }

                    if (processInfo.Thread != IntPtr.Zero)
                    {
                        CloseHandle(processInfo.Thread);
                    }

                    return ok;
                }
                finally
                {
                    if (environment != IntPtr.Zero)
                    {
                        DestroyEnvironmentBlock(environment);
                    }
                }
            }
            finally
            {
                CloseHandle(primaryToken);
            }
        }
        finally
        {
            CloseHandle(userToken);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(
        IntPtr existingToken,
        uint desiredAccess,
        IntPtr tokenAttributes,
        int impersonationLevel,
        int tokenType,
        out IntPtr newToken);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(
        IntPtr token,
        string? applicationName,
        string commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Cb;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2;
        public IntPtr Reserved2Pointer;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }
}

internal static class WebClient
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
  <title>PrivChat</title>
  <style>
    :root {
      color-scheme: light dark;
      --bg: #101814;
      --panel: #f6f2ea;
      --received: #ffffff;
      --sent: #d9fdd3;
      --ink: #17211c;
      --muted: #66736d;
      --accent: #0b7f68;
      --line: rgba(18, 26, 22, .14);
    }
    * { box-sizing: border-box; }
    body {
      margin: 0;
      min-height: 100svh;
      font-family: "Segoe UI", system-ui, -apple-system, sans-serif;
      background: radial-gradient(circle at top left, #20342c, #0b1210 55%);
      color: var(--ink);
    }
    .app {
      min-height: 100svh;
      display: grid;
      grid-template-rows: auto 1fr auto;
      background: var(--panel);
      max-width: 720px;
      margin: 0 auto;
      box-shadow: 0 0 40px rgba(0,0,0,.32);
    }
    header {
      padding: 14px 16px;
      background: #ffffff;
      border-bottom: 1px solid var(--line);
      display: flex;
      align-items: center;
      gap: 12px;
    }
    .avatar {
      width: 40px;
      height: 40px;
      border-radius: 50%;
      background: linear-gradient(135deg, #0b7f68, #73c6a6);
      display: grid;
      place-items: center;
      color: white;
      font-weight: 700;
    }
    h1 { font-size: 17px; margin: 0; }
    .status { color: var(--muted); font-size: 13px; margin-top: 2px; }
    #messages {
      padding: 16px 12px 20px;
      overflow-y: auto;
      background:
        linear-gradient(rgba(246,242,234,.92), rgba(246,242,234,.92)),
        repeating-linear-gradient(45deg, rgba(11,127,104,.08) 0 2px, transparent 2px 12px);
    }
    .row { display: flex; margin: 7px 0; }
    .row.pc { justify-content: flex-start; }
    .row.phone { justify-content: flex-end; }
    .bubble {
      max-width: min(78%, 560px);
      padding: 9px 11px 7px;
      border-radius: 8px;
      background: var(--received);
      box-shadow: 0 1px 2px rgba(0,0,0,.12);
      overflow-wrap: anywhere;
      white-space: pre-wrap;
    }
    .phone .bubble { background: var(--sent); }
    .time {
      color: var(--muted);
      font-size: 11px;
      text-align: right;
      margin-top: 4px;
    }
    form {
      display: grid;
      grid-template-columns: 1fr auto;
      gap: 10px;
      padding: 10px;
      background: #f3eee5;
      border-top: 1px solid var(--line);
    }
    input, button {
      border: 0;
      border-radius: 999px;
      font: inherit;
    }
    input {
      min-width: 0;
      padding: 13px 15px;
      outline: 1px solid transparent;
      background: white;
      color: var(--ink);
    }
    input:focus { outline-color: rgba(11,127,104,.45); }
    button {
      width: 48px;
      height: 48px;
      background: var(--accent);
      color: white;
      font-size: 20px;
    }
    .pair {
      position: fixed;
      inset: 0;
      display: grid;
      place-items: center;
      background: var(--panel);
      padding: 24px;
    }
    /* Author display rules outrank the UA [hidden] rule, so state it explicitly. */
    .pair[hidden] { display: none; }
    .pair-error { margin: 0; color: #b3261e; font-size: 14px; }
    .pair-error[hidden] { display: none; }
    .pair-box {
      width: min(420px, 100%);
      display: grid;
      gap: 14px;
    }
    .pair-box h2 { margin: 0; font-size: 22px; }
    .pair-box p { margin: 0; color: var(--muted); }
    .pair-box input { border-radius: 8px; border: 1px solid var(--line); font-size: 24px; letter-spacing: 4px; text-align: center; }
    .pair-box button { width: 100%; border-radius: 8px; }
  </style>
</head>
<body>
  <main class="app">
    <header>
      <div class="avatar">PC</div>
      <div>
        <h1>Local PC Chat</h1>
        <div id="status" class="status">Disconnected</div>
      </div>
    </header>
    <section id="messages" aria-live="polite"></section>
    <form id="sendForm" autocomplete="off">
      <input id="text" maxlength="4000" placeholder="Message" disabled>
      <button id="send" title="Send" disabled>&#10148;</button>
    </form>
  </main>
  <div id="pair" class="pair">
    <form id="pairForm" class="pair-box">
      <h2>Enter pairing code</h2>
      <p>The current code is shown on the protected PC overlay.</p>
      <input id="code" inputmode="numeric" maxlength="12" autofocus>
      <p id="pairError" class="pair-error" role="alert" hidden>Could not pair. Check the code on the PC overlay and try again.</p>
      <button>Connect</button>
    </form>
  </div>
  <script>
    const pair = document.getElementById('pair');
    const pairForm = document.getElementById('pairForm');
    const codeInput = document.getElementById('code');
    const pairError = document.getElementById('pairError');
    const sendForm = document.getElementById('sendForm');
    const text = document.getElementById('text');
    const send = document.getElementById('send');
    const messages = document.getElementById('messages');
    const status = document.getElementById('status');
    let ws;

    function setConnected(value) {
      status.textContent = value ? 'Connected on local network' : 'Disconnected';
      text.disabled = !value;
      send.disabled = !value;
    }

    function renderMessage(message) {
      const row = document.createElement('div');
      row.className = `row ${message.sender}`;
      const bubble = document.createElement('div');
      bubble.className = 'bubble';
      const body = document.createElement('div');
      body.textContent = message.body;
      const time = document.createElement('div');
      time.className = 'time';
      time.textContent = new Date(message.sentAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
      bubble.append(body, time);
      row.append(bubble);
      messages.append(row);
      messages.scrollTop = messages.scrollHeight;
    }

    function connect(code) {
      const protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
      let opened = false;
      ws = new WebSocket(`${protocol}//${location.host}/ws?role=phone&code=${encodeURIComponent(code)}`);
      ws.onopen = () => { opened = true; pair.hidden = true; pairError.hidden = true; setConnected(true); text.focus(); };
      // Closing without ever opening means the relay rejected the code.
      ws.onclose = () => { setConnected(false); pair.hidden = false; pairError.hidden = opened; };
      ws.onerror = () => { setConnected(false); };
      ws.onmessage = event => {
        const data = JSON.parse(event.data);
        if (data.type === 'history') {
          messages.textContent = '';
          data.messages.forEach(renderMessage);
        }
        if (data.type === 'message') {
          renderMessage(data.message);
        }
      };
    }

    pairForm.addEventListener('submit', event => {
      event.preventDefault();
      connect(codeInput.value.trim());
    });

    sendForm.addEventListener('submit', event => {
      event.preventDefault();
      const body = text.value.trim();
      if (!body || !ws || ws.readyState !== WebSocket.OPEN) return;
      ws.send(JSON.stringify({ type: 'send', body }));
      text.value = '';
    });
  </script>
</body>
</html>
""";
}

