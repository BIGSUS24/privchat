using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace PrivChat.Overlay;

public partial class LlmSection : UserControl
{
    // Small budget on purpose: the local model runs an 8k context and every image
    // costs hundreds of tokens. History stays text-only; only the sent turn has one.
    private const int MaxHistoryTurns = 8;
    // Matches the local setup's measured sweet spot — bigger images take minutes on CPU.
    private const int CaptureMaxWidth = 1024;
    // Local generation is slow (~13 tok/s); give long answers room.
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(180) };
    private readonly List<LlmTurn> _history = new();
    private string _endpoint = LocalLlmConfig.DefaultEndpoint;
    private byte[]? _attachedJpeg;
    private bool _busy;

    // Reuses the AI tab's bubble record: same layout, distinct colors via the template.
    public ObservableCollection<AiBubble> Messages { get; } = new();

    public LlmSection()
    {
        InitializeComponent();
        DataContext = this;
        _endpoint = LocalLlmConfig.TryLoad().Endpoint;
        EndpointBox.Text = _endpoint;
    }

    private void EditEndpoint_Click(object sender, RoutedEventArgs e)
    {
        EndpointPanel.Visibility = Visibility.Visible;
        LlmScroll.Visibility = Visibility.Collapsed;
        InputBar.Visibility = Visibility.Collapsed;
        EditEndpointButton.Visibility = Visibility.Collapsed;
        CancelEndpointButton.Visibility = Visibility.Visible;
        AttachmentStrip.Visibility = Visibility.Collapsed;
        BusyText.Visibility = Visibility.Collapsed;
    }

    private void CancelEndpoint_Click(object sender, RoutedEventArgs e) => CloseEndpointPanel();

    private void SaveEndpoint_Click(object sender, RoutedEventArgs e)
    {
        var endpoint = EndpointBox.Text.Trim().TrimEnd('/');
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            EndpointError.Text = "Enter a full URL, e.g. http://127.0.0.1:1234";
            EndpointError.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            LocalLlmConfig.Save(new LocalLlmConfig(endpoint));
        }
        catch (Exception ex)
        {
            EndpointError.Text = $"Could not save: {ex.Message}";
            EndpointError.Visibility = Visibility.Visible;
            return;
        }

        _endpoint = endpoint;
        CloseEndpointPanel();
    }

    private void CloseEndpointPanel()
    {
        EndpointPanel.Visibility = Visibility.Collapsed;
        LlmScroll.Visibility = Visibility.Visible;
        InputBar.Visibility = Visibility.Visible;
        EditEndpointButton.Visibility = Visibility.Visible;
        CancelEndpointButton.Visibility = Visibility.Collapsed;
        EndpointError.Visibility = Visibility.Collapsed;
        EndpointBox.Text = _endpoint;
        if (_busy)
        {
            BusyText.Visibility = Visibility.Visible;
        }
    }

    private void Camera_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        var window = Window.GetWindow(this);
        var hwnd = window is null ? IntPtr.Zero : new WindowInteropHelper(window).Handle;
        byte[] jpeg;
        try
        {
            jpeg = ScreenGrabber.CaptureVirtualScreenJpeg(hwnd, CaptureMaxWidth);
        }
        catch (Exception ex)
        {
            AddBubble("error", $"Screenshot failed: {ex.Message}");
            return;
        }

        _attachedJpeg = jpeg;
        var image = new BitmapImage();
        using (var stream = new MemoryStream(jpeg))
        {
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
        }
        image.Freeze();
        Thumbnail.Source = image;
        AttachmentStrip.Visibility = Visibility.Visible;
    }

    private void RemoveAttachment_Click(object sender, RoutedEventArgs e)
    {
        _attachedJpeg = null;
        Thumbnail.Source = null;
        AttachmentStrip.Visibility = Visibility.Collapsed;
    }

    private async void Send_Click(object sender, RoutedEventArgs e) => await SendCurrentAsync();

    private async void PromptBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            await SendCurrentAsync();
        }
    }

    private async Task SendCurrentAsync()
    {
        if (_busy)
        {
            return;
        }

        var prompt = PromptBox.Text.Trim();
        if (prompt.Length == 0 && _attachedJpeg is null)
        {
            return;
        }
        if (prompt.Length == 0)
        {
            prompt = "What's on this screen?";
        }

        var image = _attachedJpeg;
        _attachedJpeg = null;
        Thumbnail.Source = null;
        AttachmentStrip.Visibility = Visibility.Collapsed;
        PromptBox.Clear();

        AddBubble("user", image is null ? prompt : $"{prompt}  📷");
        SetBusy(true);
        try
        {
            var answer = await LocalLlmClient.AskAsync(_http, _endpoint, _history, prompt, image);
            _history.Add(new LlmTurn("user", prompt));
            _history.Add(new LlmTurn("model", answer));
            while (_history.Count > MaxHistoryTurns)
            {
                _history.RemoveAt(0);
            }
            AddBubble("ai", answer);
        }
        catch (Exception ex)
        {
            AddBubble("error", ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BusyText.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CameraButton.IsEnabled = !busy;
        SendButton.IsEnabled = !busy;
        PromptBox.IsEnabled = !busy;
        EditEndpointButton.IsEnabled = !busy;
    }

    private void AddBubble(string role, string body)
    {
        Messages.Add(new AiBubble(role, body, DateTimeOffset.UtcNow));
        while (Messages.Count > 100)
        {
            Messages.RemoveAt(0);
        }
        LlmScroll.ScrollToEnd();
    }
}
