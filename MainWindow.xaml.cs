using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ParcaBul.Audio;
using ParcaBul.Services;

namespace ParcaBul;

public partial class MainWindow : Window
{
    private const int FindHotkeyId = 1;
    private const int HotkeyMessage = 0x0312;
    private const int FirstCaptureSeconds = 8;
    private static readonly SolidColorBrush IdleDot = new(Color.FromRgb(157, 175, 164));
    private static readonly SolidColorBrush ErrorDot = new(Color.FromRgb(255, 182, 139));
    private readonly DispatcherTimer _captureStatus = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly ShazamClient _recognizer = new();
    private LoopbackCapture? _capture;
    private CancellationTokenSource? _request;
    private HwndSource? _windowSource;
    private bool _hotkeyRegistered;
    private bool _closed;

    public MainWindow()
    {
        InitializeComponent();
        bool instantCapture = AppSettings.Load().InstantCapture;
        CaptureToggle.IsChecked = instantCapture;
        if (instantCapture)
            _capture = new LoopbackCapture();

        _captureStatus.Tick += (_, _) => UpdateCaptureIndicator();
        _captureStatus.Start();
        UpdateCaptureIndicator();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _windowSource = (HwndSource)PresentationSource.FromVisual(this);
        _windowSource.AddHook(WindowMessage);
        const uint modifiers = 0x0001 | 0x0002 | 0x4000; // Alt, Ctrl, no repeat.
        _hotkeyRegistered = RegisterHotKey(_windowSource.Handle, FindHotkeyId, modifiers, (uint)KeyInterop.VirtualKeyFromKey(Key.M));
        ShortcutRow.Visibility = _hotkeyRegistered ? Visibility.Visible : Visibility.Collapsed;
        ShortcutWarning.Visibility = _hotkeyRegistered ? Visibility.Collapsed : Visibility.Visible;
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _captureStatus.Stop();
        _request?.Cancel();
        if (_capture is not null)
            _ = _capture.DisposeAsync();

        if (_hotkeyRegistered && _windowSource is not null)
            UnregisterHotKey(_windowSource.Handle, FindHotkeyId);

        base.OnClosed(e);
    }

    private IntPtr WindowMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == HotkeyMessage && wParam == FindHotkeyId)
        {
            handled = true;
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;

            Activate();
            _ = FindTrackAsync();
        }

        return IntPtr.Zero;
    }

    private async void FindClick(object sender, RoutedEventArgs e)
    {
        if (_request is not null)
        {
            _request.Cancel();
            return;
        }

        await FindTrackAsync();
    }

    private async Task FindTrackAsync()
    {
        if (_closed || _request is not null)
            return;

        using var request = new CancellationTokenSource();
        _request = request;
        SetSearching(true);
        TrackPanel.Visibility = Visibility.Collapsed;
        ListenPanel.Visibility = Visibility.Visible;
        try
        {
            byte[]? wave = null;
            if (_capture is { } active)
                wave = await Task.Run(active.GetRecentWave, request.Token);

            if (wave is null)
            {
                // Reuse the running capture when the buffer is still warming up.
                await using LoopbackCapture? temporary = _capture is null ? new LoopbackCapture() : null;
                LoopbackCapture source = _capture ?? temporary!;
                wave = await CollectWaveAsync(source, request.Token);
            }

            if (wave is null)
            {
                ShowFeedback("No audio detected. Check that your video is playing through the Windows default output and try again.");
                return;
            }

            RecognitionProgress.IsIndeterminate = true;
            ShowFeedback("Searching for the song…");
            Track? track = await _recognizer.RecognizeAsync(wave, request.Token);
            if (track is null)
                ShowFeedback("No match found. Try a section where the music is easier to hear.");
            else
            {
                ShowTrack(track);
                ShowFeedback(null);
            }
        }
        catch (OperationCanceledException)
        {
            if (!_closed)
                ShowFeedback(request.IsCancellationRequested ? "Search canceled." : "Shazam timed out. Try again.");
        }
        catch (Exception ex) when (ex is RecognitionException or JsonException)
        {
            if (!_closed)
                ShowFeedback(ex is RecognitionException ? ex.Message : "The recognition service returned an invalid response. Try again.");
        }
        finally
        {
            _request = null;
            if (!_closed)
                SetSearching(false);
        }
    }

    private async Task<byte[]?> CollectWaveAsync(LoopbackCapture source, CancellationToken token)
    {
        ShowFeedback("Connecting to the Windows audio output…");
        var timer = Stopwatch.StartNew();
        while (!source.IsReady && timer.Elapsed < TimeSpan.FromSeconds(3))
            await Task.Delay(100, token);

        if (!source.IsReady)
            throw new RecognitionException(source.Error ?? "Could not open the Windows audio output. Check your speakers or headphones.");

        ShowFeedback($"Capturing system audio for about {FirstCaptureSeconds} seconds…");
        RecognitionProgress.IsIndeterminate = false;
        timer.Restart();
        while (timer.Elapsed.TotalSeconds < FirstCaptureSeconds)
        {
            await Task.Delay(125, token);
            RecognitionProgress.Value = Math.Min(100, timer.Elapsed.TotalSeconds * 100 / FirstCaptureSeconds);
        }

        return await Task.Run(source.GetRecentWave, token);
    }

    private void SetSearching(bool searching)
    {
        CaptureToggle.IsEnabled = !searching;
        FindButton.Content = searching ? "Cancel" : "Find song";
        RecognitionProgress.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        RecognitionProgress.IsIndeterminate = searching;
        RecognitionProgress.Value = 0;
        UpdateCaptureIndicator();

        if (searching && SystemParameters.ClientAreaAnimation)
        {
            Waveform.BeginAnimation(OpacityProperty, new DoubleAnimation(0.44, 1, TimeSpan.FromMilliseconds(720))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            });
        }
        else
            Waveform.BeginAnimation(OpacityProperty, null);
    }

    private async void CaptureToggleClick(object sender, RoutedEventArgs e)
    {
        bool enabled = CaptureToggle.IsChecked == true;
        try
        {
            new AppSettings(enabled).Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowFeedback("Could not save this preference. The change applies until you close the app.");
        }

        LoopbackCapture? previous = _capture;
        _capture = enabled ? new LoopbackCapture() : null;
        UpdateCaptureIndicator();
        if (previous is not null)
            await previous.DisposeAsync();
    }

    private void UpdateCaptureIndicator()
    {
        string? captureError = _capture?.Error;
        bool ready = _capture?.IsReady == true;
        StatusDot.Fill = captureError is not null ? ErrorDot : ready || _request is not null ? (Brush)FindResource("Accent") : IdleDot;
        AudioIndicator.Text = _request is not null ? "Searching" : ready ? "Instant capture on" : "System audio";
        BufferDescription.Text = _capture is null ? "Turn on to keep the last 10 seconds in memory."
            : captureError ?? (ready ? "Keeping the last 10 seconds of system audio in memory." : "Opening the Windows audio output…");
    }

    private void ShowTrack(Track track)
    {
        TrackTitle.Text = track.Title;
        TrackArtist.Text = track.Artist;
        TrackAlbum.Text = track.Album ?? "";
        TrackAlbum.Visibility = string.IsNullOrWhiteSpace(track.Album) || track.Album == track.Title ? Visibility.Collapsed : Visibility.Visible;
        SetTrackLink(SpotifyButton, track.SpotifyLink);
        SetTrackLink(AppleMusicButton, track.AppleMusicLink);
        SetTrackLink(SongLinkButton, track.SongLink);
        CoverArt.Background = null;
        if (track.Cover is not null)
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = track.Cover;
                image.DecodePixelWidth = 240;
                image.EndInit();
                CoverArt.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException)
            {
                // The note icon remains available when artwork can't load.
            }
        }

        ListenPanel.Visibility = Visibility.Collapsed;
        TrackPanel.Visibility = Visibility.Visible;
        TrackPanel.BringIntoView();
    }

    private static void SetTrackLink(Button button, Uri? link)
    {
        button.Tag = link;
        button.Visibility = link is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowFeedback(string? message)
    {
        Feedback.Text = message ?? "";
        Feedback.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        UIElementAutomationPeer.FromElement(Feedback)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private static bool TryOpenLink(Uri link)
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private void OpenTrackLinkClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Uri link } && !TryOpenLink(link))
            ShowFeedback("Could not open the link in your browser.");
    }

    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
}
