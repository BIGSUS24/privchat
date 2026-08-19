using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace PrivChat.Overlay;

public partial class AiSection : UserControl
{
    private const int MaxHistoryTurns = 16;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(90) };
    private readonly List<AiTurn> _history = new();
    private AiConfig? _config;
    private byte[]? _attachedJpeg;
    private bool _busy;

    public ObservableCollection<AiBubble> Messages { get; } = new();

    public AiSection()
    {
        InitializeComponent();
        DataContext = this;
        LoadConfig();
    }

    private void LoadConfig()
    {
        _config = AiConfig.TryLoad();
        var ready = _config is not null;

        KeyPanel.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        AiScroll.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        InputBar.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        EditKeyButton.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        CancelKeyButton.Visibility = Visibility.Collapsed;
        KeyError.Visibility = Visibility.Collapsed;
        KeyBox.Text = _config?.ApiKey ?? string.Empty;
        ModelBox.Text = _config?.Model ?? AiConfig.DefaultModel;
        AttachmentStrip.Visibility = ready && _attachedJpeg is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void EditKey_Click(object sender, RoutedEventArgs e)
    {
        KeyPanel.Visibility = Visibility.Visible;
        AiScroll.Visibility = Visibility.Collapsed;
        InputBar.Visibility = Visibility.Collapsed;
        EditKeyButton.Visibility = Visibility.Collapsed;
        CancelKeyButton.Visibility = _config is not null ? Visibility.Visible : Visibility.Collapsed;
        AttachmentStrip.Visibility = Visibility.Collapsed;
        BusyText.Visibility = Visibility.Collapsed;
    }

    private void CancelKey_Click(object sender, RoutedEventArgs e) => LoadConfig();

    private void SaveKey_Click(object sender, RoutedEventArgs e)
    {
        var key = KeyBox.Text.Trim();
        if (key.Length == 0)
        {
            KeyError.Text = "Enter an API key first.";
            KeyError.Visibility = Visibility.Visible;
            return;
        }

        var model = AiConfig.NormalizeModel(ModelBox.Text);
        try
        {
            AiConfig.Save(new AiConfig(key, model));
        }
        catch (Exception ex)
        {
            KeyError.Text = $"Could not save: {ex.Message}";
            KeyError.Visibility = Visibility.Visible;
            return;
        }

        LoadConfig();
        if (_config is not null)
        {
            PromptBox.Focus();
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
            jpeg = ScreenGrabber.CaptureVirtualScreenJpeg(hwnd);
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
        if (_busy || _config is null)
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
            if (image is not null)
            {
                for (var i = 0; i < _history.Count; i++)
                {
                    if (_history[i].Image is not null)
                    {
                        _history[i] = _history[i] with { Image = null };
                    }
                }
            }

            var answer = await GeminiClient.AskAsync(_http, _config, _history, prompt, image);
            _history.Add(new AiTurn("user", prompt, image));
            _history.Add(new AiTurn("model", answer));
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
        EditKeyButton.IsEnabled = !busy;
    }

    private void AddBubble(string role, string body)
    {
        Messages.Add(new AiBubble(role, body, DateTimeOffset.UtcNow));
        while (Messages.Count > 100)
        {
            Messages.RemoveAt(0);
        }
        AiScroll.ScrollToEnd();
    }
}

public sealed record AiBubble(string Role, string Body, DateTimeOffset SentAt)
{
    public string TimeText => SentAt.ToLocalTime().ToString("HH:mm");
}
