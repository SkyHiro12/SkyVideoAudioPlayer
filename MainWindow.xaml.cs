using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.FileProperties;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FluentPlayer
{
    public class SubtitleCue
    {
        public TimeSpan Start { get; set; }
        public TimeSpan End { get; set; }
        public string Text { get; set; } = "";
    }

    public class SubtitleTrackItem
    {
        public int TrackNum { get; set; }
        public string Name { get; set; } = "";
        public List<SubtitleCue> Cues { get; set; } = new();
    }

    public class MediaTrackInfo
    {
        public int TrackNum { get; set; }
        public int TrackType { get; set; } // 1 = Video, 2 = Audio, 17 = Subtitle
        public string Name { get; set; } = "";
        public string Lang { get; set; } = "";
    }

    public sealed partial class MainWindow : Window
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool SetSystemCursor(IntPtr hcur, uint id);

        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
        private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

        [DllImport("user32.dll")]
        private static extern IntPtr CreateCursor(IntPtr hInst, int xHotSpot, int yHotSpot, int nWidth, int nHeight, byte[] pvANDPlane, byte[] pvXORPlane);

        private const uint OCR_NORMAL = 32512;
        private const uint OCR_HAND = 32526;
        private const uint SPI_SETCURSORS = 0x0057;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OPENFILENAME
        {
            public int lStructSize;
            public IntPtr hwndOwner;
            public IntPtr hInstance;
            public string lpstrFilter;
            public string lpstrCustomFilter;
            public int nMaxCustFilter;
            public int nFilterIndex;
            public IntPtr lpstrFile;
            public int nMaxFile;
            public string lpstrFileTitle;
            public int nMaxFileTitle;
            public string lpstrInitialDir;
            public string lpstrTitle;
            public int Flags;
            public short nFileOffset;
            public short nFileExtension;
            public string lpstrDefExt;
            public IntPtr lCustData;
            public IntPtr lpfnHook;
            public string lpTemplateName;
            public IntPtr pvReserved;
            public int dwReserved;
            public int FlagsEx;
        }

        [DllImport("comdlg32.dll", EntryPoint = "GetOpenFileNameW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetOpenFileName(ref OPENFILENAME ofn);

        private const int OFN_FILEMUSTEXIST = 0x00001000;
        private const int OFN_PATHMUSTEXIST = 0x00000800;
        private const int OFN_EXPLORER = 0x00080000;

        private string? PickFileWin32(string title, string filter)
        {
            var ofn = new OPENFILENAME();
            ofn.lStructSize = Marshal.SizeOf<OPENFILENAME>();
            ofn.hwndOwner = _hwnd;
            ofn.lpstrTitle = title;
            ofn.lpstrFilter = filter.Replace('|', '\0') + "\0\0";
            ofn.nFilterIndex = 1;
            ofn.nMaxFile = 2048;
            ofn.lpstrFile = Marshal.AllocCoTaskMem(2048 * sizeof(char));
            Marshal.Copy(new char[2048], 0, ofn.lpstrFile, 2048);
            ofn.Flags = OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST;

            try
            {
                if (GetOpenFileName(ref ofn))
                {
                    return Marshal.PtrToStringUni(ofn.lpstrFile);
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(ofn.lpstrFile);
            }
            return null;
        }

        private bool _isWindowActive = true;
        private bool _isCursorHidden = false;
        private Windows.Foundation.Point _lastPointerPos = new Windows.Foundation.Point(-1000, -1000);

        private readonly Windows.Media.Playback.MediaPlayer _player;
        private MediaSource? _mediaSource;
        private MediaPlaybackItem? _playbackItem;
        private readonly DispatcherTimer _uiHideTimer;
        private readonly DispatcherTimer _playbackTimer;
        private bool _isFullScreen = false;
        private bool _isUpdatingTracks = false;
        private bool _isUserSeeking = false;
        private DateTime _lastSeekRequestTime = DateTime.MinValue;
        private bool _showRemainingTime = false;
        private double _uiScale = 1.0;
        private bool _isUpdatingUiScaleControls = false;
        private DispatcherTimer? _osdTimer;
        private int _skipSeconds = 10;
        private TimeSpan _subDelay = TimeSpan.Zero;
        private readonly IntPtr _hwnd;

        private readonly List<SubtitleTrackItem> _subtitles = new();
        private int _activeSubtitleIndex = -1;
        private List<MediaTrackInfo> _parsedTracks = new();
        private CancellationTokenSource? _subExtractorCts;
        private static readonly object _subLock = new();

        public MainWindow()
        {
            this.InitializeComponent();

            _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

            this.Title = "SkyPlayer";
            this.ExtendsContentIntoTitleBar = true;

            if (AppWindowTitleBar.IsCustomizationSupported())
            {
                var titleBar = this.AppWindow.TitleBar;
                titleBar.ButtonBackgroundColor = Colors.Transparent;
                titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
                titleBar.ButtonForegroundColor = Colors.White;
                titleBar.ButtonInactiveForegroundColor = ColorHelper.FromArgb(255, 160, 160, 160);
                titleBar.ButtonHoverBackgroundColor = ColorHelper.FromArgb(50, 255, 255, 255);
                titleBar.ButtonHoverForegroundColor = Colors.White;
                titleBar.ButtonPressedBackgroundColor = ColorHelper.FromArgb(80, 255, 255, 255);
                titleBar.ButtonPressedForegroundColor = Colors.White;
            }

            try
            {
                string iconPath = Path.Combine(AppContext.BaseDirectory, "icon.ico");
                if (File.Exists(iconPath))
                {
                    this.AppWindow.SetIcon(iconPath);
                }
            }
            catch { }

            _player = new Windows.Media.Playback.MediaPlayer();
            PlayerElement.SetMediaPlayer(_player);

            _uiHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _uiHideTimer.Tick += (s, e) => HideControls();
            ResetHideTimer();

            _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _playbackTimer.Tick += OnPlaybackTimerTick;
            _playbackTimer.Start();

            _player.PlaybackSession.PlaybackStateChanged += (s, e) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    PlayPauseIcon.Glyph = _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing 
                        ? "\uE769" 
                        : "\uE768";
                });
            };

            ApplyTheme("Dark");
            SetUiScale(1.0, showOsd: false);

            RootGrid.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnPointerMoved), true);
            RootGrid.PointerExited += (s, e) => ShowControls();

            this.Activated += (s, e) =>
            {
                if (e.WindowActivationState == WindowActivationState.Deactivated)
                {
                    _isWindowActive = false;
                    RestoreSystemCursor();
                    ShowControls();
                }
                else
                {
                    _isWindowActive = true;
                    ResetHideTimer();
                }
            };

            this.Closed += (s, e) => RestoreSystemCursor();

            RootGrid.Loaded += async (s, e) =>
            {
                try
                {
                    string[] args = Environment.GetCommandLineArgs();
                    string? fileArg = args.Skip(1).FirstOrDefault(File.Exists);
                    if (!string.IsNullOrEmpty(fileArg))
                    {
                        var file = await StorageFile.GetFileFromPathAsync(fileArg);
                        await PlayFileAsync(file);
                    }
                }
                catch { }
            };
        }

        // ================== ИНТЕРАКТИВНАЯ ДОРОЖКА ВРЕМЕНИ ==================

        private TimeSpan GetMediaDuration()
        {
            if (_player == null) return TimeSpan.Zero;
            if (_player.PlaybackSession != null && _player.PlaybackSession.NaturalDuration > TimeSpan.Zero)
                return _player.PlaybackSession.NaturalDuration;
            return _player.NaturalDuration;
        }

        private void SeekTo(TimeSpan targetTime)
        {
            if (_player == null) return;
            var dur = GetMediaDuration();
            if (dur <= TimeSpan.Zero) return;

            if (targetTime < TimeSpan.Zero) targetTime = TimeSpan.Zero;
            if (targetTime > dur) targetTime = dur;

            _player.Position = targetTime;
            _lastSeekRequestTime = DateTime.UtcNow;

            UpdateTimelineVisuals(targetTime, dur);
        }

        private void Timeline_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            try
            {
                var dur = GetMediaDuration();
                if (dur <= TimeSpan.Zero) return;

                _isUserSeeking = true;
                TimelineTrackContainer.CapturePointer(e.Pointer);
                ResetHideTimer();

                var pt = e.GetCurrentPoint(TimelineTrackContainer).Position;
                double width = TimelineTrackContainer.ActualWidth;
                if (width > 0 && !double.IsNaN(width) && !double.IsInfinity(width))
                {
                    double clampedX = Math.Clamp(pt.X, 0.0, width);
                    double ratio = clampedX / width;
                    var targetTime = TimeSpan.FromSeconds(ratio * dur.TotalSeconds);
                    SeekTo(targetTime);
                    UpdateHoverBadgePosition(new Windows.Foundation.Point(clampedX, pt.Y), dur);
                }
                e.Handled = true;
            }
            catch { }
        }

        private void Timeline_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            try
            {
                var dur = GetMediaDuration();
                if (dur <= TimeSpan.Zero) return;

                var pt = e.GetCurrentPoint(TimelineTrackContainer).Position;
                double width = TimelineTrackContainer.ActualWidth;
                if (width <= 0 || double.IsNaN(width) || double.IsInfinity(width)) return;

                double clampedX = Math.Clamp(pt.X, 0.0, width);
                double ratio = clampedX / width;
                var hoverTime = TimeSpan.FromSeconds(ratio * dur.TotalSeconds);
                UpdateHoverBadgePosition(new Windows.Foundation.Point(clampedX, pt.Y), dur);

                // Если зажата мышь (перетаскивание) — обновляем визуал и позицию
                if (_isUserSeeking)
                {
                    UpdateTimelineVisuals(hoverTime, dur);

                    var now = DateTime.UtcNow;
                    if ((now - _lastSeekRequestTime).TotalMilliseconds > 45)
                    {
                        _player.Position = hoverTime;
                        _lastSeekRequestTime = now;
                    }
                }

                ResetHideTimer();
                e.Handled = true;
            }
            catch { }
        }

        private void UpdateHoverBadgePosition(Windows.Foundation.Point ptInTrack, TimeSpan dur)
        {
            try
            {
                if (dur <= TimeSpan.Zero || TimelineTrackContainer == null || TimelineHoverBadge == null || RootGrid == null)
                    return;

                double width = TimelineTrackContainer.ActualWidth;
                if (width <= 0 || double.IsNaN(width) || double.IsInfinity(width)) return;

                double clampedX = Math.Clamp(ptInTrack.X, 0.0, width);
                double ratio = width > 0 ? (clampedX / width) : 0.0;
                var hoverTime = TimeSpan.FromSeconds(ratio * dur.TotalSeconds);

                // Обновляем визуальную полосу предпросмотра при наведении (строго в допустимом диапазоне [0, width])
                if (TimelineHoverTrack != null)
                {
                    TimelineHoverTrack.Width = clampedX;
                    TimelineHoverTrack.Visibility = Visibility.Visible;
                }

                // Обновляем текст бейджа
                if (TimelineHoverBadgeText != null)
                    TimelineHoverBadgeText.Text = FormatTime(hoverTime, dur);
                TimelineHoverBadge.Visibility = Visibility.Visible;

                var trackTransform = TimelineTrackContainer.TransformToVisual(RootGrid);
                var cursorInRoot = trackTransform.TransformPoint(new Windows.Foundation.Point(clampedX, ptInTrack.Y));

                TimelineHoverBadge.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                double badgeWidth = TimelineHoverBadge.ActualWidth > 0 ? TimelineHoverBadge.ActualWidth : (TimelineHoverBadge.DesiredSize.Width > 0 ? TimelineHoverBadge.DesiredSize.Width : 56);
                double badgeHeight = TimelineHoverBadge.ActualHeight > 0 ? TimelineHoverBadge.ActualHeight : (TimelineHoverBadge.DesiredSize.Height > 0 ? TimelineHoverBadge.DesiredSize.Height : 26);

                double rootWidth = RootGrid.ActualWidth > 0 ? RootGrid.ActualWidth : 800;
                double badgeLeft = Math.Clamp(cursorInRoot.X - (badgeWidth / 2.0), 12.0, Math.Max(12.0, rootWidth - badgeWidth - 12.0));

                double panelTopInRoot = cursorInRoot.Y - 20;
                if (BottomPanel != null)
                {
                    var panelTransform = BottomPanel.TransformToVisual(RootGrid);
                    panelTopInRoot = panelTransform.TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
                }

                double badgeTop = panelTopInRoot - badgeHeight - 8;
                if (badgeTop < 10) badgeTop = 10;

                TimelineHoverBadge.Margin = new Thickness(badgeLeft, badgeTop, 0, 0);
            }
            catch
            {
            }
        }

        private void Timeline_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            try
            {
                if (_isUserSeeking)
                {
                    _isUserSeeking = false;
                    TimelineTrackContainer.ReleasePointerCapture(e.Pointer);

                    var dur = GetMediaDuration();
                    if (dur > TimeSpan.Zero)
                    {
                        var pt = e.GetCurrentPoint(TimelineTrackContainer).Position;
                        double width = TimelineTrackContainer.ActualWidth;
                        if (width > 0 && !double.IsNaN(width) && !double.IsInfinity(width))
                        {
                            double ratio = Math.Clamp(pt.X / width, 0.0, 1.0);
                            var targetTime = TimeSpan.FromSeconds(ratio * dur.TotalSeconds);
                            SeekTo(targetTime);
                        }
                    }

                    ResetHideTimer();
                    e.Handled = true;
                }
            }
            catch { }
        }

        private void Timeline_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            _isUserSeeking = false;
            if (TimelineHoverTrack != null) TimelineHoverTrack.Visibility = Visibility.Collapsed;
            if (TimelineHoverBadge != null) TimelineHoverBadge.Visibility = Visibility.Collapsed;
            SetTimelineHoverState(false);
            ResetHideTimer();
        }

        private void Timeline_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            SetTimelineHoverState(true);
        }

        private void Timeline_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (!_isUserSeeking)
            {
                if (TimelineHoverTrack != null) TimelineHoverTrack.Visibility = Visibility.Collapsed;
                if (TimelineHoverBadge != null) TimelineHoverBadge.Visibility = Visibility.Collapsed;
                SetTimelineHoverState(false);
            }
        }

        private void Timeline_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            var props = e.GetCurrentPoint(TimelineTrackContainer).Properties;
            int delta = props.MouseWheelDelta;
            var dur = GetMediaDuration();
            if (dur <= TimeSpan.Zero || _player == null) return;

            int stepSec = 5;
            var target = delta > 0 
                ? _player.Position + TimeSpan.FromSeconds(stepSec) 
                : _player.Position - TimeSpan.FromSeconds(stepSec);

            if (target < TimeSpan.Zero) target = TimeSpan.Zero;
            if (target > dur) target = dur;

            SeekTo(target);
            ShowOsd(delta > 0 ? "\uEB9D" : "\uEB9E", delta > 0 ? $"+{stepSec} сек" : $"-{stepSec} сек");
            e.Handled = true;
            ResetHideTimer();
        }

        private void TimelineTrackContainer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var dur = GetMediaDuration();
            if (_player != null && dur > TimeSpan.Zero)
            {
                UpdateTimelineVisuals(_player.Position, dur);
            }
        }

        private void SetTimelineHoverState(bool isHovered)
        {
            double trackHeight = isHovered ? 7.0 : 5.0;
            double thumbSize = isHovered ? 15.0 : 13.0;
            double thumbTop = (26.0 - thumbSize) / 2.0;

            TimelineBgTrack.Height = trackHeight;
            TimelineBgTrack.CornerRadius = new CornerRadius(trackHeight / 2.0);

            TimelineBufferTrack.Height = trackHeight;
            TimelineBufferTrack.CornerRadius = new CornerRadius(trackHeight / 2.0);

            TimelineHoverTrack.Height = trackHeight;
            TimelineHoverTrack.CornerRadius = new CornerRadius(trackHeight / 2.0);

            TimelineProgressTrack.Height = trackHeight;
            TimelineProgressTrack.CornerRadius = new CornerRadius(trackHeight / 2.0);

            TimelineThumb.Width = thumbSize;
            TimelineThumb.Height = thumbSize;
            TimelineThumb.CornerRadius = new CornerRadius(thumbSize / 2.0);
            Canvas.SetTop(TimelineThumb, thumbTop);

            var dur = GetMediaDuration();
            if (_player != null && dur > TimeSpan.Zero)
            {
                UpdateTimelineVisuals(_player.Position, dur);
            }
        }

        private void UpdateTimelineVisuals(TimeSpan pos, TimeSpan dur)
        {
            try
            {
                if (dur <= TimeSpan.Zero)
                {
                    if (TimelineProgressTrack != null) TimelineProgressTrack.Width = 0;
                    if (TimelineThumb != null) Canvas.SetLeft(TimelineThumb, -6.5);
                    if (CurrentTimeText != null) CurrentTimeText.Text = "00:00";
                    if (TotalTimeText != null) TotalTimeText.Text = "00:00";
                    return;
                }

                double width = TimelineTrackContainer != null ? TimelineTrackContainer.ActualWidth : 0;
                if (width > 0 && !double.IsNaN(width) && !double.IsInfinity(width))
                {
                    double ratio = Math.Clamp(pos.TotalSeconds / dur.TotalSeconds, 0.0, 1.0);
                    if (double.IsNaN(ratio)) ratio = 0.0;
                    double progressWidth = Math.Clamp(ratio * width, 0.0, width);
                    if (TimelineProgressTrack != null) TimelineProgressTrack.Width = progressWidth;
                    if (TimelineThumb != null)
                    {
                        double thumbLeft = progressWidth - (TimelineThumb.Width / 2.0);
                        Canvas.SetLeft(TimelineThumb, thumbLeft);
                    }

                    double buff = _player?.PlaybackSession?.DownloadProgress ?? 0;
                    if (double.IsNaN(buff)) buff = 0;
                    if (TimelineBufferTrack != null)
                    {
                        double bufferWidth = Math.Clamp(Math.Clamp(buff, 0.0, 1.0) * width, 0.0, width);
                        TimelineBufferTrack.Width = bufferWidth;
                    }
                }

                if (CurrentTimeText != null) CurrentTimeText.Text = FormatTime(pos, dur);
                UpdateTotalTimeText(pos, dur);
            }
            catch { }
        }

        private void TotalTimeText_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _showRemainingTime = !_showRemainingTime;
            var dur = GetMediaDuration();
            if (_player != null && dur > TimeSpan.Zero)
            {
                UpdateTotalTimeText(_player.Position, dur);
            }
            ShowOsd("\uE823", _showRemainingTime ? "Оставшееся время" : "Общая длительность");
        }

        private void UpdateTotalTimeText(TimeSpan pos, TimeSpan dur)
        {
            if (dur <= TimeSpan.Zero)
            {
                TotalTimeText.Text = "00:00";
                return;
            }

            if (_showRemainingTime)
            {
                var remaining = dur - pos;
                if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
                TotalTimeText.Text = "-" + FormatTime(remaining, dur);
            }
            else
            {
                TotalTimeText.Text = FormatTime(dur, dur);
            }
        }

        private void OnPlaybackTimerTick(object? sender, object e)
        {
            if (_player == null) return;

            var pos = _player.Position;
            var dur = GetMediaDuration();

            // Субтитры
            if (_activeSubtitleIndex >= 0 && _activeSubtitleIndex < _subtitles.Count)
            {
                var targetSubPos = pos - _subDelay;
                SubtitleCue? cue = null;
                lock (_subLock)
                {
                    var cues = _subtitles[_activeSubtitleIndex].Cues;
                    cue = cues.FirstOrDefault(c => targetSubPos >= c.Start && targetSubPos <= c.End);
                }

                if (cue != null && !string.IsNullOrWhiteSpace(cue.Text))
                {
                    SubtitleTextBlock.Text = cue.Text;
                    SubtitleContainer.Visibility = Visibility.Visible;
                }
                else
                {
                    SubtitleContainer.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                SubtitleContainer.Visibility = Visibility.Collapsed;
            }

            // Дорожка времени (синхронизируется когда нет активного перетаскивания и перемотка завершена)
            if (dur > TimeSpan.Zero)
            {
                if (!_isUserSeeking && (DateTime.UtcNow - _lastSeekRequestTime).TotalMilliseconds >= 350)
                {
                    UpdateTimelineVisuals(pos, dur);
                }
            }
            else
            {
                CurrentTimeText.Text = FormatTime(pos, dur);
            }
        }

        private static string FormatTime(TimeSpan t, TimeSpan total)
        {
            return total.TotalHours >= 1 
                ? t.ToString(@"hh\:mm\:ss") 
                : t.ToString(@"mm\:ss");
        }

        // ================== БАЗОВЫЕ НАСТРОЙКИ ==================

        private void OnAlwaysOnTopToggled(object sender, RoutedEventArgs e)
        {
            if (this.AppWindow.Presenter is OverlappedPresenter presenter)
                presenter.IsAlwaysOnTop = AlwaysOnTopToggle.IsOn;
        }

        private void OnLoopToggled(object sender, RoutedEventArgs e)
        {
            if (_player != null) _player.IsLoopingEnabled = LoopToggle.IsOn;
        }

        private void PlayerElement_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (ClickPauseToggle.IsOn && SettingsOverlay.Visibility != Visibility.Visible)
            {
                PlayPause_Click(this, new RoutedEventArgs());
                ShowControls();
            }
        }

        private void OnSubDelayChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SubDelayCombo.SelectedItem is ComboBoxItem item && double.TryParse(item.Tag?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double sec))
                _subDelay = TimeSpan.FromSeconds(sec);
        }

        // ================== БЕЗОПАСНОЕ УПРАВЛЕНИЕ КУРСОРОМ ==================

        private bool IsCursorInsideWindow()
        {
            if (GetCursorPos(out POINT pt) && GetWindowRect(_hwnd, out RECT rc))
                return pt.X >= rc.Left && pt.X <= rc.Right && pt.Y >= rc.Top && pt.Y <= rc.Bottom;
            return false;
        }

        private void HideSystemCursor()
        {
            if (_isCursorHidden || !_isWindowActive || GetForegroundWindow() != _hwnd || !IsCursorInsideWindow())
                return;

            _isCursorHidden = true;
            try
            {
                byte[] andMask = new byte[128];
                for (int i = 0; i < andMask.Length; i++) andMask[i] = 0xFF;
                byte[] xorMask = new byte[128];

                IntPtr blankNormal = CreateCursor(IntPtr.Zero, 0, 0, 32, 32, andMask, xorMask);
                SetSystemCursor(blankNormal, OCR_NORMAL);

                IntPtr blankHand = CreateCursor(IntPtr.Zero, 0, 0, 32, 32, andMask, xorMask);
                SetSystemCursor(blankHand, OCR_HAND);
            }
            catch { }
        }

        private void RestoreSystemCursor()
        {
            if (!_isCursorHidden) return;
            _isCursorHidden = false;
            try
            {
                SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, 0);
            }
            catch { }
        }

        private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            var p = e.GetCurrentPoint(RootGrid).Position;
            if (Math.Abs(p.X - _lastPointerPos.X) < 2 && Math.Abs(p.Y - _lastPointerPos.Y) < 2) return;

            _lastPointerPos = p;
            ShowControls();
        }

        private void ShowControls()
        {
            RestoreSystemCursor();
            BottomPanel.Opacity = 1;
            BottomPanel.IsHitTestVisible = true;
            UpdateSubtitlePosition();
            ResetHideTimer();
        }

        private void HideControls()
        {
            if (SettingsOverlay.Visibility == Visibility.Visible || _isUserSeeking) return;
            _uiHideTimer.Stop();

            if (_isWindowActive && GetForegroundWindow() == _hwnd && IsCursorInsideWindow())
                HideSystemCursor();
            else
                RestoreSystemCursor();

            BottomPanel.Opacity = 0;
            BottomPanel.IsHitTestVisible = false;
            if (TimelineHoverBadge != null) TimelineHoverBadge.Visibility = Visibility.Collapsed;
            if (TimelineHoverTrack != null) TimelineHoverTrack.Visibility = Visibility.Collapsed;
            UpdateSubtitlePosition();
        }

        private void ResetHideTimer()
        {
            _uiHideTimer.Stop();
            _uiHideTimer.Start();
        }

        // ================== ОТКРЫТИЕ И ВОСПРОИЗВЕДЕНИЕ ==================

        private async void OpenFile_Click(object sender, RoutedEventArgs e)
        {
            StorageFile? file = null;

            // 1. Попытка открыть через современный WinUI FileOpenPicker с валидным списком расширений
            try
            {
                var picker = new FileOpenPicker();
                WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
                picker.SuggestedStartLocation = PickerLocationId.VideosLibrary;

                string[] exts = { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".flv", ".m4v", ".ts", ".mp3", ".flac", ".wav", ".aac", ".ogg", ".m4a", ".opus", ".*" };
                foreach (var ext in exts) picker.FileTypeFilter.Add(ext);

                file = await picker.PickSingleFileAsync();
            }
            catch
            {
                file = null;
            }

            // 2. Если FileOpenPicker вернул ошибку COMException (в unpackaged режиме) — нативный Win32 диалог
            if (file == null)
            {
                try
                {
                    string filter = "Все медиафайлы|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.webm;*.flv;*.m4v;*.ts;*.mp3;*.flac;*.wav;*.aac;*.ogg;*.m4a;*.opus|Видеофайлы (*.mp4, *.mkv, ...)|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.webm;*.flv;*.m4v;*.ts|Аудиофайлы (*.mp3, *.flac, ...)|*.mp3;*.flac;*.wav;*.aac;*.ogg;*.m4a;*.opus|Все файлы (*.*)|*.*";
                    string? path = PickFileWin32("Открыть медиафайл", filter);
                    if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    {
                        file = await StorageFile.GetFileFromPathAsync(path);
                    }
                }
                catch { }
            }

            if (file != null)
            {
                await PlayFileAsync(file);
            }
        }

        private async Task PlayFileAsync(StorageFile file)
        {
            _subExtractorCts?.Cancel();
            _subExtractorCts = new CancellationTokenSource();

            lock (_subLock) _subtitles.Clear();
            _activeSubtitleIndex = -1;
            SubtitleContainer.Visibility = Visibility.Collapsed;

            string ext = Path.GetExtension(file.Path).ToLowerInvariant();
            bool isAudio = ext is ".mp3" or ".flac" or ".wav" or ".wma" or ".aac" or ".ogg" or ".m4a" or ".alac" or ".opus";

            if (isAudio)
            {
                _parsedTracks = new List<MediaTrackInfo>
                {
                    new MediaTrackInfo { TrackNum = 1, TrackType = 2, Name = file.DisplayName, Lang = "" }
                };
            }
            else
            {
                try
                {
                    using var fileStream = await file.OpenStreamForReadAsync();
                    _parsedTracks = ParseContainerTracks(fileStream, _subtitles);
                }
                catch { }

                DetectLocalSubtitlesDirect(file.Path);
            }

            _mediaSource = MediaSource.CreateFromStorageFile(file);
            await _mediaSource.OpenAsync();

            _playbackItem = new MediaPlaybackItem(_mediaSource);
            _player.Source = _playbackItem;
            _player.IsMuted = false;
            _player.Play();

            UpdateTimelineVisuals(TimeSpan.Zero, TimeSpan.Zero);
            UpdateVolumeIcon();

            PopulateTracks(isAudio);
            await InspectMediaAsync(file, isAudio);
            ShowControls();
            ShowOsd("\uED25", file.DisplayName);
        }

        private void PopulateTracks(bool isAudio)
        {
            if (_playbackItem == null) return;

            _isUpdatingTracks = true;
            try
            {
                if (isAudio)
                {
                    AudioTracksCombo.Visibility = Visibility.Collapsed;
                    SubtitlesCombo.Visibility = Visibility.Collapsed;
                    AddSubtitleButton.Visibility = Visibility.Collapsed;
                    VideoOnlySettingsPanel.Visibility = Visibility.Collapsed;
                    return;
                }

                AudioTracksCombo.Visibility = Visibility.Visible;
                SubtitlesCombo.Visibility = Visibility.Visible;
                AddSubtitleButton.Visibility = Visibility.Visible;
                VideoOnlySettingsPanel.Visibility = Visibility.Visible;

                AudioTracksCombo.Items.Clear();
                AudioTracksCombo.Items.Add(new ComboBoxItem { Content = "отключить", Tag = -1 });

                var parsedAudio = _parsedTracks.Where(t => t.TrackType == 2).ToList();
                int totalAudio = Math.Max(_playbackItem.AudioTracks.Count, parsedAudio.Count);

                for (int i = 0; i < totalAudio; i++)
                {
                    string title = "";
                    string lang = "";

                    if (i < parsedAudio.Count)
                    {
                        title = parsedAudio[i].Name;
                        lang = parsedAudio[i].Lang;
                    }

                    if (string.IsNullOrWhiteSpace(title) && i < _playbackItem.AudioTracks.Count && !string.IsNullOrWhiteSpace(_playbackItem.AudioTracks[i].Label))
                        title = _playbackItem.AudioTracks[i].Label;

                    if (string.IsNullOrWhiteSpace(lang) && i < _playbackItem.AudioTracks.Count && !string.IsNullOrWhiteSpace(_playbackItem.AudioTracks[i].Language))
                        lang = _playbackItem.AudioTracks[i].Language;

                    if (string.IsNullOrWhiteSpace(title))
                        title = $"Дорожка {i + 1}";

                    string langText = FormatLanguageName(lang);
                    string displayText = title;

                    if (!string.IsNullOrWhiteSpace(langText) && !title.Contains(langText, StringComparison.OrdinalIgnoreCase))
                        displayText = $"{title} - [{langText}]";

                    AudioTracksCombo.Items.Add(new ComboBoxItem { Content = displayText, Tag = i });
                }

                if (_player.IsMuted)
                {
                    AudioTracksCombo.SelectedIndex = 0;
                }
                else if (totalAudio > 0)
                {
                    int selected = (_playbackItem.AudioTracks.Count > 0) ? _playbackItem.AudioTracks.SelectedIndex : 0;
                    AudioTracksCombo.SelectedIndex = (selected >= 0 && selected < totalAudio) ? selected + 1 : 1;
                }

                SubtitlesCombo.Items.Clear();
                SubtitlesCombo.Items.Add(new ComboBoxItem { Content = "отключить", Tag = -1 });

                lock (_subLock)
                {
                    for (int i = 0; i < _subtitles.Count; i++)
                        SubtitlesCombo.Items.Add(new ComboBoxItem { Content = _subtitles[i].Name, Tag = i });
                }

                if (_subtitles.Count > 0)
                {
                    _activeSubtitleIndex = 0;
                    SubtitlesCombo.SelectedIndex = 1;
                }
                else
                {
                    _activeSubtitleIndex = -1;
                    SubtitlesCombo.SelectedIndex = 0;
                }
            }
            finally
            {
                _isUpdatingTracks = false;
            }
        }

        private void OnAudioTrackChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingTracks || _playbackItem == null) return;

            if (AudioTracksCombo.SelectedItem is ComboBoxItem item && item.Tag is int index)
            {
                try
                {
                    if (index == -1)
                    {
                        _player.IsMuted = true;
                    }
                    else if (index >= 0 && index < _playbackItem.AudioTracks.Count)
                    {
                        _player.IsMuted = false;
                        _playbackItem.AudioTracks.SelectedIndex = index;
                    }
                }
                catch { }
            }
        }

        private void OnSubtitleChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingTracks) return;

            if (SubtitlesCombo.SelectedItem is ComboBoxItem item && item.Tag is int index)
            {
                _activeSubtitleIndex = index;
                if (index < 0) SubtitleContainer.Visibility = Visibility.Collapsed;
            }
        }

        private static string FormatLanguageName(string? langCode)
        {
            if (string.IsNullOrWhiteSpace(langCode)) return "";
            string code = langCode.Trim().ToLowerInvariant();

            return code switch
            {
                "rus" or "ru" => "Русский",
                "jpn" or "ja" => "Японский",
                "eng" or "en" => "Английский",
                "ger" or "de" => "Немецкий",
                "fra" or "fre" or "fr" => "Французский",
                "ita" or "it" => "Итальянский",
                "spa" or "es" => "Испанский",
                "chi" or "zho" or "zh" => "Китайский",
                "kor" or "ko" => "Корейский",
                "ukr" or "uk" => "Украинский",
                "und" => "",
                _ => langCode.ToUpperInvariant()
            };
        }

        private void DetectLocalSubtitlesDirect(string videoPath)
        {
            try
            {
                string? dir = Path.GetDirectoryName(videoPath);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

                var validExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".ass", ".ssa", ".srt", ".vtt" };
                var subFiles = Directory.GetFiles(dir).Where(f => validExts.Contains(Path.GetExtension(f))).ToList();

                foreach (var sFile in subFiles)
                {
                    string label = Path.GetFileName(sFile);
                    string content = File.ReadAllText(sFile);
                    var cues = ParseSubtitleContent(content);
                    if (cues.Count > 0)
                    {
                        lock (_subLock)
                            _subtitles.Add(new SubtitleTrackItem { Name = label, Cues = cues });
                    }
                }
            }
            catch { }
        }

        private async void AddSubtitle_Click(object sender, RoutedEventArgs e)
        {
            StorageFile? subFile = null;

            try
            {
                var picker = new FileOpenPicker();
                WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
                picker.SuggestedStartLocation = PickerLocationId.VideosLibrary;
                picker.FileTypeFilter.Add(".ass");
                picker.FileTypeFilter.Add(".ssa");
                picker.FileTypeFilter.Add(".srt");
                picker.FileTypeFilter.Add(".vtt");

                subFile = await picker.PickSingleFileAsync();
            }
            catch
            {
                subFile = null;
            }

            if (subFile == null)
            {
                try
                {
                    string filter = "Файлы субтитров (*.ass, *.ssa, *.srt, *.vtt)|*.ass;*.ssa;*.srt;*.vtt|Все файлы (*.*)|*.*";
                    string? path = PickFileWin32("Добавить субтитры", filter);
                    if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    {
                        subFile = await StorageFile.GetFileFromPathAsync(path);
                    }
                }
                catch { }
            }

            if (subFile != null)
            {
                try
                {
                    string content = await FileIO.ReadTextAsync(subFile);
                    var cues = ParseSubtitleContent(content);
                    if (cues.Count > 0)
                    {
                        lock (_subLock)
                        {
                            _subtitles.Add(new SubtitleTrackItem { Name = subFile.DisplayName, Cues = cues });
                            _activeSubtitleIndex = _subtitles.Count - 1;
                        }
                        PopulateTracks(false);
                    }
                }
                catch { }
                ShowControls();
            }
        }

        private static List<SubtitleCue> ParseSubtitleContent(string content)
        {
            var list = new List<SubtitleCue>();
            var lines = content.Replace("\r\n", "\n").Split('\n');

            bool isAss = lines.Take(60).Any(l => l.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase) || l.StartsWith("[Events]", StringComparison.OrdinalIgnoreCase));

            if (isAss)
            {
                foreach (var line in lines)
                {
                    if (line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = line.Split(',', 10);
                        if (parts.Length >= 10)
                        {
                            if (TryParseTime(parts[1].Trim(), out var start) && TryParseTime(parts[2].Trim(), out var end))
                            {
                                string text = CleanAssTags(parts[9].Trim());
                                if (!string.IsNullOrWhiteSpace(text))
                                    list.Add(new SubtitleCue { Start = start, End = end, Text = text });
                            }
                        }
                    }
                }
            }
            else
            {
                var blocks = content.Replace("\r\n", "\n").Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var block in blocks)
                {
                    var bLines = block.Trim().Split('\n');
                    int timeIdx = -1;
                    for (int i = 0; i < bLines.Length; i++)
                    {
                        if (bLines[i].Contains("-->")) { timeIdx = i; break; }
                    }
                    if (timeIdx == -1 || timeIdx + 1 >= bLines.Length) continue;

                    var parts = bLines[timeIdx].Split(new[] { "-->" }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length != 2) continue;

                    if (TryParseTime(parts[0].Trim(), out var start) && TryParseTime(parts[1].Trim(), out var end))
                    {
                        string text = string.Join("\n", bLines.Skip(timeIdx + 1)).Trim();
                        list.Add(new SubtitleCue { Start = start, End = end, Text = text });
                    }
                }
            }
            return list;
        }

        private static string CleanAssTags(string text)
        {
            var sb = new StringBuilder();
            bool inTag = false;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '{') inTag = true;
                else if (text[i] == '}') inTag = false;
                else if (!inTag) sb.Append(text[i]);
            }
            return sb.ToString().Replace("\\N", "\n").Replace("\\n", "\n").Replace("\\h", " ").Trim();
        }

        private static bool TryParseTime(string s, out TimeSpan time)
        {
            s = s.Replace(',', '.');
            var parts = s.Split(':');
            if (parts.Length == 3 && 
                double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double sec) &&
                int.TryParse(parts[1], out int min) && 
                int.TryParse(parts[0], out int hr))
            {
                time = TimeSpan.FromHours(hr) + TimeSpan.FromMinutes(min) + TimeSpan.FromSeconds(sec);
                return true;
            }
            else if (parts.Length == 2 &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double ssec) &&
                int.TryParse(parts[0], out int mmin))
            {
                time = TimeSpan.FromMinutes(mmin) + TimeSpan.FromSeconds(ssec);
                return true;
            }
            time = TimeSpan.Zero;
            return false;
        }

        // ================== ПАРСИНГ КОНТЕЙНЕРОВ ==================

        private static List<MediaTrackInfo> ParseContainerTracks(Stream stream, List<SubtitleTrackItem> subList)
        {
            stream.Seek(0, SeekOrigin.Begin);
            byte[] sig = new byte[8];
            int r = stream.Read(sig, 0, 8);

            if (r >= 4 && sig[0] == 0x1A && sig[1] == 0x45 && sig[2] == 0xDF && sig[3] == 0xA3)
                return ParseMkv(stream);

            return ParseMp4(stream, subList);
        }

        private static List<MediaTrackInfo> ParseMp4(Stream stream, List<SubtitleTrackItem> subList)
        {
            var tracks = new List<MediaTrackInfo>();
            try
            {
                stream.Seek(0, SeekOrigin.Begin);
                long fileLen = stream.Length;
                long pos = 0;
                long moovOffset = -1;
                long moovSize = -1;

                while (pos + 8 <= fileLen)
                {
                    stream.Seek(pos, SeekOrigin.Begin);
                    byte[] hdr = new byte[8];
                    if (stream.Read(hdr, 0, 8) < 8) break;

                    long sz = (uint)((hdr[0] << 24) | (hdr[1] << 16) | (hdr[2] << 8) | hdr[3]);
                    string atype = Encoding.ASCII.GetString(hdr, 4, 4);
                    long hdrLen = 8;

                    if (sz == 1)
                    {
                        byte[] ext = new byte[8];
                        if (stream.Read(ext, 0, 8) < 8) break;
                        sz = (long)ReadUint64BigEndian(ext, 0);
                        hdrLen = 16;
                    }
                    else if (sz == 0) sz = fileLen - pos;

                    if (atype == "moov")
                    {
                        moovOffset = pos + hdrLen;
                        moovSize = sz - hdrLen;
                        break;
                    }

                    if (sz <= 0) break;
                    pos += sz;
                }

                if (moovOffset == -1 || moovSize <= 0) return tracks;

                int readLen = (int)Math.Min(moovSize, 64 * 1024 * 1024);
                stream.Seek(moovOffset, SeekOrigin.Begin);
                byte[] moovData = new byte[readLen];
                int totalRead = 0;
                while (totalRead < readLen)
                {
                    int rd = stream.Read(moovData, totalRead, readLen - totalRead);
                    if (rd <= 0) break;
                    totalRead += rd;
                }

                var traks = FindMp4Children(moovData, 0, totalRead, "trak");
                int trackIdx = 0;

                foreach (var (trakStart, trakEnd) in traks)
                {
                    var tInfo = new MediaTrackInfo { TrackNum = ++trackIdx };
                    var mdias = FindMp4Children(moovData, trakStart, trakEnd, "mdia");

                    foreach (var (mdiaStart, mdiaEnd) in mdias)
                    {
                        var mdhds = FindMp4Children(moovData, mdiaStart, mdiaEnd, "mdhd");
                        if (mdhds.Count > 0)
                        {
                            int ms = mdhds[0].start;
                            int me = mdhds[0].end;
                            byte ver = moovData[ms];
                            int langOff = ms + (ver == 1 ? 28 : 20);
                            if (langOff + 2 <= me)
                            {
                                ushort langCode = (ushort)((moovData[langOff] << 8) | moovData[langOff + 1]);
                                tInfo.Lang = DecodeMp4Language(langCode);
                            }
                        }

                        var hdlrs = FindMp4Children(moovData, mdiaStart, mdiaEnd, "hdlr");
                        if (hdlrs.Count > 0)
                        {
                            int hs = hdlrs[0].start;
                            int he = hdlrs[0].end;
                            if (hs + 12 <= he)
                            {
                                string htype = Encoding.ASCII.GetString(moovData, hs + 8, 4);
                                if (htype == "soun") tInfo.TrackType = 2;
                                else if (htype == "vide") tInfo.TrackType = 1;
                                else if (htype == "sbtl" || htype == "text" || htype == "subt" || htype == "clcp") tInfo.TrackType = 17;
                            }

                            if (hs + 24 < he)
                            {
                                int nameLen = he - (hs + 24);
                                string rawName = moovData[hs + 24] == nameLen - 1 && nameLen > 1
                                    ? Encoding.UTF8.GetString(moovData, hs + 25, nameLen - 1).TrimEnd('\0').Trim()
                                    : Encoding.UTF8.GetString(moovData, hs + 24, nameLen).TrimEnd('\0').Trim();

                                if (!string.Equals(rawName, "SubtitleHandler", StringComparison.OrdinalIgnoreCase) &&
                                    !string.Equals(rawName, "SoundHandler", StringComparison.OrdinalIgnoreCase) &&
                                    !string.Equals(rawName, "VideoHandler", StringComparison.OrdinalIgnoreCase) &&
                                    !string.Equals(rawName, "Handler", StringComparison.OrdinalIgnoreCase))
                                {
                                    tInfo.Name = rawName;
                                }
                            }
                        }
                    }

                    var udtas = FindMp4Children(moovData, trakStart, trakEnd, "udta");
                    foreach (var (uStart, uEnd) in udtas)
                    {
                        var names = FindMp4Children(moovData, uStart, uEnd, "name");
                        if (names.Count > 0)
                        {
                            string n = Encoding.UTF8.GetString(moovData, names[0].start, names[0].end - names[0].start).TrimEnd('\0').Trim();
                            if (!string.IsNullOrWhiteSpace(n)) tInfo.Name = n;
                        }
                    }

                    if (tInfo.TrackType > 0) tracks.Add(tInfo);

                    if (tInfo.TrackType == 17)
                    {
                        if (string.IsNullOrWhiteSpace(tInfo.Name)) continue;

                        var tempCues = new List<SubtitleCue>();
                        ExtractMp4Tx3gSubtitles(stream, moovData, trakStart, trakEnd, tempCues);

                        if (tempCues.Count > 0)
                        {
                            string langName = FormatLanguageName(tInfo.Lang);
                            string subTitle = tInfo.Name;
                            if (!string.IsNullOrWhiteSpace(langName) && !subTitle.Contains(langName, StringComparison.OrdinalIgnoreCase))
                                subTitle += $" - [{langName}]";

                            var item = new SubtitleTrackItem { Name = subTitle, Cues = tempCues };
                            lock (_subLock) subList.Add(item);
                        }
                    }
                }
            }
            catch { }
            return tracks;
        }

        private static void ExtractMp4Tx3gSubtitles(Stream stream, byte[] moovData, int trakStart, int trakEnd, List<SubtitleCue> cues)
        {
            try
            {
                var mdias = FindMp4Children(moovData, trakStart, trakEnd, "mdia");
                if (mdias.Count == 0) return;

                int mdiaStart = mdias[0].start;
                var mdhds = FindMp4Children(moovData, mdiaStart, mdias[0].end, "mdhd");
                if (mdhds.Count == 0) return;

                byte ver = moovData[mdhds[0].start];
                int tsOff = mdhds[0].start + (ver == 1 ? 20 : 12);
                uint timescale = (uint)((moovData[tsOff] << 24) | (moovData[tsOff + 1] << 16) | (moovData[tsOff + 2] << 8) | moovData[tsOff + 3]);
                if (timescale == 0) timescale = 1000;

                var minfs = FindMp4Children(moovData, mdiaStart, mdias[0].end, "minf");
                if (minfs.Count == 0) return;

                var stbls = FindMp4Children(moovData, minfs[0].start, minfs[0].end, "stbl");
                if (stbls.Count == 0) return;

                int stblStart = stbls[0].start, stblEnd = stbls[0].end;
                var sttss = FindMp4Children(moovData, stblStart, stblEnd, "stts");
                var stszs = FindMp4Children(moovData, stblStart, stblEnd, "stsz");
                var stcos = FindMp4Children(moovData, stblStart, stblEnd, "stco");
                var co64s = FindMp4Children(moovData, stblStart, stblEnd, "co64");

                if (sttss.Count == 0 || stszs.Count == 0 || (stcos.Count == 0 && co64s.Count == 0)) return;

                int sttsP = sttss[0].start + 4;
                uint sttsCount = ReadUint32(moovData, ref sttsP);
                var durations = new List<uint>();
                for (int i = 0; i < sttsCount && sttsP + 8 <= sttss[0].end; i++)
                {
                    uint sc = ReadUint32(moovData, ref sttsP);
                    uint sd = ReadUint32(moovData, ref sttsP);
                    for (int k = 0; k < sc; k++) durations.Add(sd);
                }

                int stszP = stszs[0].start + 4;
                uint defSize = ReadUint32(moovData, ref stszP);
                uint sampleCount = ReadUint32(moovData, ref stszP);
                var sizes = new List<uint>();
                if (defSize > 0)
                {
                    for (int i = 0; i < sampleCount; i++) sizes.Add(defSize);
                }
                else
                {
                    for (int i = 0; i < sampleCount && stszP + 4 <= stszs[0].end; i++)
                        sizes.Add(ReadUint32(moovData, ref stszP));
                }

                var chunkOffsets = new List<long>();
                bool isCo64 = co64s.Count > 0;
                int coP = isCo64 ? co64s[0].start + 4 : stcos[0].start + 4;
                int coEnd = isCo64 ? co64s[0].end : stcos[0].end;
                uint chunkCount = ReadUint32(moovData, ref coP);
                for (int i = 0; i < chunkCount && coP + (isCo64 ? 8 : 4) <= coEnd; i++)
                {
                    if (isCo64)
                    {
                        chunkOffsets.Add((long)ReadUint64BigEndian(moovData, coP));
                        coP += 8;
                    }
                    else
                    {
                        chunkOffsets.Add(ReadUint32(moovData, ref coP));
                    }
                }

                long curTime = 0;
                int sIdx = 0;
                foreach (long chunkOff in chunkOffsets)
                {
                    if (sIdx >= sizes.Count) break;
                    long fileOffset = chunkOff;
                    uint sz = sizes[sIdx];
                    uint dur = sIdx < durations.Count ? durations[sIdx] : 1000;

                    if (sz > 2)
                    {
                        stream.Seek(fileOffset, SeekOrigin.Begin);
                        byte[] b = new byte[sz];
                        stream.Read(b, 0, (int)sz);
                        int textLen = (b[0] << 8) | b[1];
                        if (textLen > 0 && textLen + 2 <= sz)
                        {
                            string text = Encoding.UTF8.GetString(b, 2, textLen).Trim();
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                long startMs = curTime * 1000 / timescale;
                                long endMs = (curTime + dur) * 1000 / timescale;
                                cues.Add(new SubtitleCue
                                {
                                    Start = TimeSpan.FromMilliseconds(startMs),
                                    End = TimeSpan.FromMilliseconds(endMs),
                                    Text = text
                                });
                            }
                        }
                    }

                    curTime += dur;
                    sIdx++;
                }
            }
            catch { }
        }

        private static uint ReadUint32(byte[] b, ref int p)
        {
            uint v = (uint)((b[p] << 24) | (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3]);
            p += 4;
            return v;
        }

        private static List<(int start, int end)> FindMp4Children(byte[] buf, int offset, int end, string targetType)
        {
            var list = new List<(int, int)>();
            int p = offset;
            while (p + 8 <= end && p + 8 <= buf.Length)
            {
                uint sz = (uint)((buf[p] << 24) | (buf[p + 1] << 16) | (buf[p + 2] << 8) | buf[p + 3]);
                string type = Encoding.ASCII.GetString(buf, p + 4, 4);
                int hdrLen = 8;

                if (sz == 1)
                {
                    if (p + 16 > end || p + 16 > buf.Length) break;
                    ulong ext = ReadUint64BigEndian(buf, p + 8);
                    sz = (uint)Math.Min(ext, (ulong)int.MaxValue);
                    hdrLen = 16;
                }
                else if (sz == 0) sz = (uint)(end - p);

                if (sz < hdrLen) break;
                int childEnd = (int)Math.Min((long)end, p + sz);

                if (type == targetType) list.Add((p + hdrLen, childEnd));
                p += (int)sz;
            }
            return list;
        }

        private static string DecodeMp4Language(ushort val)
        {
            char c1 = (char)(((val >> 10) & 0x1F) + 0x60);
            char c2 = (char)(((val >> 5) & 0x1F) + 0x60);
            char c3 = (char)((val & 0x1F) + 0x60);
            if (c1 >= 'a' && c1 <= 'z' && c2 >= 'a' && c2 <= 'z' && c3 >= 'a' && c3 <= 'z')
                return $"{c1}{c2}{c3}";
            return "";
        }

        private static ulong ReadUint64BigEndian(byte[] b, int offset)
        {
            return ((ulong)b[offset] << 56) | ((ulong)b[offset + 1] << 48) | ((ulong)b[offset + 2] << 40) | ((ulong)b[offset + 3] << 32)
                 | ((ulong)b[offset + 4] << 24) | ((ulong)b[offset + 5] << 16) | ((ulong)b[offset + 6] << 8)  | (ulong)b[offset + 7];
        }

        private static List<MediaTrackInfo> ParseMkv(Stream stream)
        {
            var tracks = new List<MediaTrackInfo>();
            try
            {
                stream.Seek(0, SeekOrigin.Begin);
                int bytesToRead = (int)Math.Min(stream.Length, 32 * 1024 * 1024);
                byte[] data = new byte[bytesToRead];
                int totalRead = stream.Read(data, 0, bytesToRead);

                int pos = 0;
                while (pos < totalRead - 4)
                {
                    int idx = IndexOfBytes(data, new byte[] { 0x16, 0x54, 0xAE, 0x6B }, pos, totalRead);
                    if (idx == -1) break;

                    if (idx + 5 < totalRead && data[idx + 4] == 0x53 && data[idx + 5] == 0xAC)
                    {
                        pos = idx + 4;
                        continue;
                    }

                    int p = idx + 4;
                    long tracksSize = ReadVintInMemory(data, ref p);
                    int tracksEnd = (int)Math.Min((long)totalRead, p + tracksSize);

                    while (p < tracksEnd - 2)
                    {
                        uint elId = ReadElementIdInMemory(data, ref p);
                        long elSize = ReadVintInMemory(data, ref p);
                        if (elId == 0 || elSize < 0) break;

                        if (elId == 0xAE)
                        {
                            int entryEnd = (int)Math.Min((long)tracksEnd, p + elSize);
                            var t = new MediaTrackInfo();

                            while (p < entryEnd - 1)
                            {
                                uint subId = ReadElementIdInMemory(data, ref p);
                                long subSize = ReadVintInMemory(data, ref p);
                                if (subId == 0 || subSize < 0) break;

                                int subEnd = (int)Math.Min((long)entryEnd, p + subSize);

                                if (subId == 0xD7) t.TrackNum = (int)ReadUintInMemory(data, p, (int)subSize);
                                else if (subId == 0x83) t.TrackType = (int)ReadUintInMemory(data, p, (int)subSize);
                                else if (subId == 0x536E) t.Name = Encoding.UTF8.GetString(data, p, (int)subSize).TrimEnd('\0');
                                else if (subId == 0x22B59C || subId == 0x22B59D)
                                {
                                    string l = Encoding.ASCII.GetString(data, p, (int)subSize).TrimEnd('\0');
                                    if (string.IsNullOrWhiteSpace(t.Lang)) t.Lang = l;
                                }

                                p = subEnd;
                            }

                            if (t.TrackType > 0) tracks.Add(t);
                            p = entryEnd;
                        }
                        else
                        {
                            p = (int)Math.Min((long)tracksEnd, p + elSize);
                        }
                    }

                    if (tracks.Count > 0) break;
                    pos = idx + 4;
                }
            }
            catch { }
            return tracks;
        }

        private static int IndexOfBytes(byte[] source, byte[] pattern, int start, int count)
        {
            for (int i = start; i <= count - pattern.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (source[i + j] != pattern[j]) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
        }

        private static long ReadVintInMemory(byte[] data, ref int pos)
        {
            if (pos >= data.Length) return -1;
            byte b0 = data[pos++];
            int len = 1;
            int mask = 0x80;
            while (len <= 8 && (b0 & mask) == 0) { len++; mask >>= 1; }
            if (len > 8 || pos + len - 1 > data.Length) return -1;

            long val = b0 & (mask - 1);
            for (int i = 1; i < len; i++) val = (val << 8) | data[pos++];
            return val;
        }

        private static uint ReadElementIdInMemory(byte[] data, ref int pos)
        {
            if (pos >= data.Length) return 0;
            byte b0 = data[pos++];
            if ((b0 & 0x80) != 0) return b0;
            if (pos >= data.Length) return 0;
            byte b1 = data[pos++];
            if ((b0 & 0x40) != 0) return (uint)((b0 << 8) | b1);
            if (pos >= data.Length) return 0;
            byte b2 = data[pos++];
            if ((b0 & 0x20) != 0) return (uint)((b0 << 16) | (b1 << 8) | b2);
            if (pos >= data.Length) return 0;
            byte b3 = data[pos++];
            return (uint)((b0 << 24) | (b1 << 16) | (b2 << 8) | b3);
        }

        private static long ReadUintInMemory(byte[] data, int pos, int size)
        {
            long val = 0;
            for (int i = 0; i < size && pos + i < data.Length; i++)
                val = (val << 8) | data[pos + i];
            return val;
        }

        // ================== ИНТЕРФЕЙС И УПРАВЛЕНИЕ ==================

        private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen(!_isFullScreen);

        private void ToggleFullscreen(bool enable)
        {
            if (enable)
            {
                this.AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
                _isFullScreen = true;
                FullscreenIcon.Glyph = "\uE73F";
            }
            else
            {
                this.AppWindow.SetPresenter(AppWindowPresenterKind.Default);
                _isFullScreen = false;
                FullscreenIcon.Glyph = "\uE740";
            }
        }

        private async Task InspectMediaAsync(StorageFile file, bool isAudio)
        {
            if (!isAudio)
            {
                AudioInfoPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                AudioInfoPanel.Visibility = Visibility.Visible;

                try
                {
                    var musicProps = await file.Properties.GetMusicPropertiesAsync();
                    TrackTitleText.Text = string.IsNullOrWhiteSpace(musicProps.Title) ? file.DisplayName : musicProps.Title;
                    TrackArtistText.Text = string.IsNullOrWhiteSpace(musicProps.Artist) ? "Неизвестный исполнитель" : musicProps.Artist;
                    TrackAlbumText.Text = string.IsNullOrWhiteSpace(musicProps.Album) ? string.Empty : musicProps.Album;

                    AlbumCoverImage.Visibility = Visibility.Collapsed;
                    VinylPlaceholderGrid.Visibility = Visibility.Visible;

                    using var thumb = await file.GetThumbnailAsync(ThumbnailMode.MusicView, 300);
                    if (thumb != null && thumb.Type == ThumbnailType.Image && thumb.Size > 0)
                    {
                        var bmp = new BitmapImage();
                        await bmp.SetSourceAsync(thumb);
                        AlbumCoverImage.Source = bmp;
                        AlbumCoverImage.Visibility = Visibility.Visible;
                        VinylPlaceholderGrid.Visibility = Visibility.Collapsed;
                    }
                }
                catch
                {
                    TrackTitleText.Text = file.DisplayName;
                    TrackArtistText.Text = "Аудиозапись";
                    TrackAlbumText.Text = string.Empty;
                    AlbumCoverImage.Visibility = Visibility.Collapsed;
                    VinylPlaceholderGrid.Visibility = Visibility.Visible;
                }
            }
        }

        private void PlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
                _player.Pause();
            else
                _player.Play();
        }

        private void SkipBack_Click(object sender, RoutedEventArgs e)
        {
            var dur = GetMediaDuration();
            var newPos = _player.Position - TimeSpan.FromSeconds(_skipSeconds);
            var target = newPos < TimeSpan.Zero ? TimeSpan.Zero : newPos;
            SeekTo(target);
            ShowOsd("\uEB9E", $"-{_skipSeconds} сек");
        }

        private void SkipForward_Click(object sender, RoutedEventArgs e)
        {
            var dur = GetMediaDuration();
            var newPos = _player.Position + TimeSpan.FromSeconds(_skipSeconds);
            var target = (dur > TimeSpan.Zero && newPos > dur) ? dur : newPos;
            SeekTo(target);
            ShowOsd("\uEB9D", $"+{_skipSeconds} сек");
        }

        private void OnVolumeChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_player != null)
            {
                _player.Volume = e.NewValue / 100.0;
                if (_player.Volume > 0 && _player.IsMuted)
                    _player.IsMuted = false;
                UpdateVolumeIcon();
            }
        }

        private void MuteToggle_Click(object sender, RoutedEventArgs e)
        {
            ToggleMute();
        }

        private void ToggleMute()
        {
            if (_player == null) return;
            _player.IsMuted = !_player.IsMuted;
            UpdateVolumeIcon();
            ShowOsd(_player.IsMuted ? "\uE74F" : "\uE767", _player.IsMuted ? "Звук выключен" : $"Громкость: {Math.Round(VolumeSlider.Value)}%");
        }

        private void UpdateVolumeIcon()
        {
            if (VolumeIcon == null || _player == null) return;
            if (_player.IsMuted || VolumeSlider.Value == 0)
                VolumeIcon.Glyph = "\uE74F";
            else if (VolumeSlider.Value < 33)
                VolumeIcon.Glyph = "\uE992";
            else if (VolumeSlider.Value < 66)
                VolumeIcon.Glyph = "\uE993";
            else
                VolumeIcon.Glyph = "\uE767";
        }

        private void PlayerElement_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            ToggleFullscreen(!_isFullScreen);
            e.Handled = true;
        }

        // ================== МАСШТАБИРОВАНИЕ ИНТЕРФЕЙСА (UI SCALE) ==================

        public double UiScale => _uiScale;

        public void SetUiScale(double scale, bool showOsd = true)
        {
            scale = Math.Clamp(Math.Round(scale, 2), 0.70, 2.00);
            _uiScale = scale;

            if (BottomPanelScale != null)
            {
                BottomPanelScale.ScaleX = scale;
                BottomPanelScale.ScaleY = scale;
            }

            if (AudioInfoScale != null)
            {
                AudioInfoScale.ScaleX = scale;
                AudioInfoScale.ScaleY = scale;
            }

            // Панель настроек остается стабильной и компактной (не раздувается за пределы экрана),
            // а ее нижний отступ безопасно учитывает высоту панели управления, но не выталкивает за верх окна
            if (SettingsOverlay != null)
            {
                double rootHeight = RootGrid != null && RootGrid.ActualHeight > 0 ? RootGrid.ActualHeight : 720;
                double bottomMargin = Math.Min((68.0 * scale) + 16.0, Math.Max(20.0, rootHeight - 480.0));
                SettingsOverlay.Margin = new Thickness(0, 0, 24, bottomMargin);
            }

            UpdateSubtitlePosition();
            UpdateUiScaleControls();

            if (showOsd)
            {
                ShowOsd("\uE799", $"Масштаб UI: {Math.Round(scale * 100)}%");
            }
        }

        private void UpdateSubtitlePosition()
        {
            if (SubtitleContainer == null) return;
            // Субтитры не масштабируются от UI scale, но их нижний отступ адаптируется,
            // чтобы при отображении панели управления субтитры располагались над ней,
            // а при скрытии панели — на стандартной высоте для просмотра видео.
            double bottomMargin = (BottomPanel != null && BottomPanel.Opacity > 0.05)
                ? Math.Max(70, (110 * _uiScale) + 36)
                : 48;
            SubtitleContainer.Margin = new Thickness(40, 0, 40, bottomMargin);
        }

        private void UpdateUiScaleControls()
        {
            _isUpdatingUiScaleControls = true;
            try
            {
                int percent = (int)Math.Round(_uiScale * 100);

                if (UiScaleValueText != null)
                {
                    UiScaleValueText.Text = $"{percent}%";
                }

                if (UiScaleSlider != null)
                {
                    UiScaleSlider.Value = Math.Clamp(percent, 70, 200);
                }
            }
            finally
            {
                _isUpdatingUiScaleControls = false;
            }
        }

        private void OnUiScaleSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_isUpdatingUiScaleControls) return;
            double scale = Math.Round(e.NewValue / 100.0, 2);
            SetUiScale(scale, showOsd: false);
        }

        private void OnUiScalePreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && double.TryParse(btn.Tag?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double preset))
            {
                SetUiScale(preset, showOsd: true);
            }
        }

        private void UiScaleMinus_Click(object sender, RoutedEventArgs e)
        {
            SetUiScale(_uiScale - 0.05, showOsd: true);
        }

        private void UiScalePlus_Click(object sender, RoutedEventArgs e)
        {
            SetUiScale(_uiScale + 0.05, showOsd: true);
        }

        private void UiScaleReset_Click(object sender, RoutedEventArgs e)
        {
            SetUiScale(1.00, showOsd: true);
        }

        // ================== ВСПЛЫВАЮЩИЕ OSD УВЕДОМЛЕНИЯ ==================

        private void ShowOsd(string glyph, string text)
        {
            if (OsdBadge == null || OsdIcon == null || OsdText == null) return;

            OsdIcon.Glyph = glyph;
            OsdText.Text = text;
            OsdBadge.Visibility = Visibility.Visible;
            OsdBadge.Opacity = 1.0;

            _osdTimer?.Stop();
            _osdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1300) };
            _osdTimer.Tick += (s, e) =>
            {
                _osdTimer.Stop();
                OsdBadge.Visibility = Visibility.Collapsed;
            };
            _osdTimer.Start();
        }

        // ================== DRAG & DROP ПОДДЕРЖКА ==================

        private void OnRootDragOver(object sender, DragEventArgs e)
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Воспроизвести в SkyPlayer";
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsContentVisible = true;
        }

        private async void OnRootDrop(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();
                var file = items.OfType<StorageFile>().FirstOrDefault();
                if (file != null)
                {
                    string ext = Path.GetExtension(file.Path).ToLowerInvariant();
                    if (ext is ".ass" or ".ssa" or ".srt" or ".vtt")
                    {
                        try
                        {
                            string content = await FileIO.ReadTextAsync(file);
                            var cues = ParseSubtitleContent(content);
                            if (cues.Count > 0)
                            {
                                lock (_subLock)
                                {
                                    _subtitles.Add(new SubtitleTrackItem { Name = file.DisplayName, Cues = cues });
                                    _activeSubtitleIndex = _subtitles.Count - 1;
                                }
                                PopulateTracks(false);
                                ShowOsd("\uE710", $"Субтитры: {file.DisplayName}");
                            }
                        }
                        catch { }
                    }
                    else
                    {
                        await PlayFileAsync(file);
                        ShowOsd("\uED25", $"Открыт: {file.DisplayName}");
                    }
                }
            }
        }

        // ================== ОБРАБОТКА МЫШИ НА ОКНЕ ==================

        private void OnRootPointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            var props = e.GetCurrentPoint(RootGrid).Properties;
            int delta = props.MouseWheelDelta;
            bool isCtrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

            if (isCtrl)
            {
                if (delta > 0)
                    SetUiScale(_uiScale + 0.05);
                else if (delta < 0)
                    SetUiScale(_uiScale - 0.05);

                e.Handled = true;
            }
            else
            {
                if (delta > 0)
                    VolumeSlider.Value = Math.Min(100, VolumeSlider.Value + 5);
                else if (delta < 0)
                    VolumeSlider.Value = Math.Max(0, VolumeSlider.Value - 5);

                ShowOsd("\uE767", $"Громкость: {Math.Round(VolumeSlider.Value)}%");
                e.Handled = true;
            }
            ShowControls();
        }

        private void SettingsToggle_Click(object sender, RoutedEventArgs e)
        {
            bool willShow = SettingsOverlay.Visibility != Visibility.Visible;
            if (willShow)
            {
                UpdateUiScaleControls();
                double rootHeight = RootGrid != null && RootGrid.ActualHeight > 0 ? RootGrid.ActualHeight : 720;
                double bottomMargin = Math.Min((68.0 * _uiScale) + 16.0, Math.Max(20.0, rootHeight - 480.0));
                SettingsOverlay.Margin = new Thickness(0, 0, 24, bottomMargin);
                SettingsOverlay.Visibility = Visibility.Visible;
            }
            else
            {
                SettingsOverlay.Visibility = Visibility.Collapsed;
            }
            ShowControls();
        }

        private void SettingsClose_Click(object sender, RoutedEventArgs e)
        {
            SettingsOverlay.Visibility = Visibility.Collapsed;
        }

        private void OnSpeedChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SpeedCombo.SelectedItem is ComboBoxItem item && double.TryParse(item.Tag?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double rate))
            {
                if (_player != null) _player.PlaybackSession.PlaybackRate = rate;
            }
        }

        private void OnSubtitleSizeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SubtitleSizeCombo.SelectedItem is ComboBoxItem item && double.TryParse(item.Tag?.ToString(), out double size))
            {
                if (SubtitleTextBlock != null) SubtitleTextBlock.FontSize = size;
            }
        }

        private void OnStretchChanged(object sender, SelectionChangedEventArgs e)
        {
            if (StretchCombo.SelectedItem is ComboBoxItem item && item.Tag is string stretchStr)
            {
                if (PlayerElement != null)
                {
                    PlayerElement.Stretch = stretchStr switch
                    {
                        "UniformToFill" => Stretch.UniformToFill,
                        "Fill" => Stretch.Fill,
                        _ => Stretch.Uniform
                    };
                }
            }
        }

        private void OnSkipIntervalChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SkipIntervalCombo.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out int val))
                _skipSeconds = val;
        }

        private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_contentLoaded) return;
            if (ThemeCombo?.SelectedItem is ComboBoxItem item)
                ApplyTheme(item.Tag?.ToString() ?? "Dark");
        }

        private void ApplyTheme(string theme)
        {
            if (RootGrid == null) return;

            switch (theme)
            {
                case "Light":
                    RootGrid.RequestedTheme = ElementTheme.Light;
                    RootGrid.Background = new SolidColorBrush(ColorHelper.FromArgb(255, 243, 243, 245));
                    if (BottomPanel != null) BottomPanel.Background = new SolidColorBrush(ColorHelper.FromArgb(215, 255, 255, 255));
                    if (SettingsOverlay != null) SettingsOverlay.Background = new SolidColorBrush(ColorHelper.FromArgb(242, 255, 255, 255));
                    if (TrackTitleText != null) TrackTitleText.Foreground = new SolidColorBrush(Colors.Black);
                    if (CurrentTimeText != null) CurrentTimeText.Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 34, 34, 34));
                    if (TotalTimeText != null) TotalTimeText.Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 102, 102, 102));

                    if (TimelineBgTrack != null) TimelineBgTrack.Background = new SolidColorBrush(ColorHelper.FromArgb(38, 0, 0, 0));
                    if (TimelineBufferTrack != null) TimelineBufferTrack.Background = new SolidColorBrush(ColorHelper.FromArgb(50, 0, 0, 0));
                    if (TimelineHoverTrack != null) TimelineHoverTrack.Background = new SolidColorBrush(ColorHelper.FromArgb(64, 0, 0, 0));
                    SetTimelineGradient(ColorHelper.FromArgb(255, 0, 103, 192), ColorHelper.FromArgb(255, 0, 120, 212));
                    if (TimelineThumb != null) TimelineThumb.BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 0, 103, 192));
                    if (TimelineHoverBadge != null) TimelineHoverBadge.Background = new SolidColorBrush(ColorHelper.FromArgb(240, 255, 255, 255));
                    if (TimelineHoverBadgeText != null) TimelineHoverBadgeText.Foreground = new SolidColorBrush(Colors.Black);
                    break;

                case "Ultraviolet":
                    RootGrid.RequestedTheme = ElementTheme.Dark;
                    RootGrid.Background = new SolidColorBrush(ColorHelper.FromArgb(255, 14, 8, 26));
                    if (BottomPanel != null) BottomPanel.Background = new SolidColorBrush(ColorHelper.FromArgb(180, 42, 14, 74));
                    if (SettingsOverlay != null) SettingsOverlay.Background = new SolidColorBrush(ColorHelper.FromArgb(235, 38, 9, 70));
                    if (TrackTitleText != null) TrackTitleText.Foreground = new SolidColorBrush(Colors.White);
                    if (CurrentTimeText != null) CurrentTimeText.Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 226, 232, 240));
                    if (TotalTimeText != null) TotalTimeText.Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 167, 139, 250));

                    if (TimelineBgTrack != null) TimelineBgTrack.Background = new SolidColorBrush(ColorHelper.FromArgb(43, 255, 255, 255));
                    if (TimelineBufferTrack != null) TimelineBufferTrack.Background = new SolidColorBrush(ColorHelper.FromArgb(64, 255, 255, 255));
                    if (TimelineHoverTrack != null) TimelineHoverTrack.Background = new SolidColorBrush(ColorHelper.FromArgb(85, 255, 255, 255));
                    SetTimelineGradient(ColorHelper.FromArgb(255, 217, 70, 239), ColorHelper.FromArgb(255, 139, 92, 246));
                    if (TimelineThumb != null) TimelineThumb.BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 217, 70, 239));
                    if (TimelineHoverBadge != null) TimelineHoverBadge.Background = new SolidColorBrush(ColorHelper.FromArgb(230, 27, 11, 51));
                    if (TimelineHoverBadgeText != null) TimelineHoverBadgeText.Foreground = new SolidColorBrush(Colors.White);
                    break;

                case "Dark":
                default:
                    RootGrid.RequestedTheme = ElementTheme.Dark;
                    RootGrid.Background = new SolidColorBrush(ColorHelper.FromArgb(255, 10, 10, 10));
                    if (BottomPanel != null) BottomPanel.Background = new SolidColorBrush(ColorHelper.FromArgb(190, 20, 20, 20));
                    if (SettingsOverlay != null) SettingsOverlay.Background = new SolidColorBrush(ColorHelper.FromArgb(235, 20, 20, 20));
                    if (TrackTitleText != null) TrackTitleText.Foreground = new SolidColorBrush(Colors.White);
                    if (CurrentTimeText != null) CurrentTimeText.Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 204, 204, 204));
                    if (TotalTimeText != null) TotalTimeText.Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 136, 136, 136));

                    if (TimelineBgTrack != null) TimelineBgTrack.Background = new SolidColorBrush(ColorHelper.FromArgb(43, 255, 255, 255));
                    if (TimelineBufferTrack != null) TimelineBufferTrack.Background = new SolidColorBrush(ColorHelper.FromArgb(51, 255, 255, 255));
                    if (TimelineHoverTrack != null) TimelineHoverTrack.Background = new SolidColorBrush(ColorHelper.FromArgb(68, 255, 255, 255));
                    SetTimelineGradient(ColorHelper.FromArgb(255, 124, 58, 237), ColorHelper.FromArgb(255, 59, 130, 246));
                    if (TimelineThumb != null) TimelineThumb.BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 59, 130, 246));
                    if (TimelineHoverBadge != null) TimelineHoverBadge.Background = new SolidColorBrush(ColorHelper.FromArgb(230, 24, 24, 24));
                    if (TimelineHoverBadgeText != null) TimelineHoverBadgeText.Foreground = new SolidColorBrush(Colors.White);
                    break;
            }
        }

        private void SetTimelineGradient(Windows.UI.Color startColor, Windows.UI.Color endColor)
        {
            if (TimelineProgressTrack == null) return;
            var brush = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 0) };
            brush.GradientStops.Add(new GradientStop { Color = startColor, Offset = 0.0 });
            brush.GradientStops.Add(new GradientStop { Color = endColor, Offset = 1.0 });
            TimelineProgressTrack.Background = brush;
        }

        private void OnKeyDown(object sender, KeyRoutedEventArgs e)
        {
            ShowControls();

            bool isCtrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

            if (isCtrl)
            {
                if (e.Key == Windows.System.VirtualKey.Add || (int)e.Key == 187) // + or =
                {
                    SetUiScale(_uiScale + 0.05);
                    e.Handled = true;
                    return;
                }
                if (e.Key == Windows.System.VirtualKey.Subtract || (int)e.Key == 189) // - or _
                {
                    SetUiScale(_uiScale - 0.05);
                    e.Handled = true;
                    return;
                }
                if (e.Key == Windows.System.VirtualKey.Number0 || e.Key == Windows.System.VirtualKey.NumberPad0)
                {
                    SetUiScale(1.0);
                    e.Handled = true;
                    return;
                }
            }

            if (e.Key == Windows.System.VirtualKey.Left || e.Key == Windows.System.VirtualKey.J)
            {
                SkipBack_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Right || e.Key == Windows.System.VirtualKey.L)
            {
                SkipForward_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Up)
            {
                VolumeSlider.Value = Math.Min(100, VolumeSlider.Value + 5);
                ShowOsd("\uE767", $"Громкость: {Math.Round(VolumeSlider.Value)}%");
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Down)
            {
                VolumeSlider.Value = Math.Max(0, VolumeSlider.Value - 5);
                ShowOsd("\uE767", $"Громкость: {Math.Round(VolumeSlider.Value)}%");
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Space || e.Key == Windows.System.VirtualKey.K)
            {
                PlayPause_Click(this, new RoutedEventArgs());
                ShowOsd(_player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing ? "\uE768" : "\uE769", 
                        _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing ? "Пауза" : "Воспроизведение");
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.M)
            {
                ToggleMute();
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.F || e.Key == Windows.System.VirtualKey.F11)
            {
                ToggleFullscreen(!_isFullScreen);
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Escape)
            {
                if (SettingsOverlay.Visibility == Visibility.Visible)
                    SettingsOverlay.Visibility = Visibility.Collapsed;
                else if (_isFullScreen)
                    ToggleFullscreen(false);
                e.Handled = true;
            }
            else if ((int)e.Key == 219) // [ : Уменьшить скорость
            {
                AdjustSpeed(-0.25);
                e.Handled = true;
            }
            else if ((int)e.Key == 221) // ] : Увеличить скорость
            {
                AdjustSpeed(+0.25);
                e.Handled = true;
            }
            else if ((int)e.Key == 190) // . : Кадр вперед
            {
                if (_player.PlaybackSession.PlaybackState != MediaPlaybackState.Playing)
                {
                    _player.Position += TimeSpan.FromMilliseconds(40);
                    ShowOsd("\uE769", "Кадр вперед (+0.04с)");
                }
                e.Handled = true;
            }
            else if ((int)e.Key == 188) // , : Кадр назад
            {
                if (_player.PlaybackSession.PlaybackState != MediaPlaybackState.Playing)
                {
                    var prev = _player.Position - TimeSpan.FromMilliseconds(40);
                    _player.Position = prev < TimeSpan.Zero ? TimeSpan.Zero : prev;
                    ShowOsd("\uE769", "Кадр назад (-0.04с)");
                }
                e.Handled = true;
            }
        }

        private void AdjustSpeed(double delta)
        {
            if (_player == null) return;
            double cur = _player.PlaybackSession.PlaybackRate;
            double next = Math.Clamp(Math.Round(cur + delta, 2), 0.25, 3.0);
            _player.PlaybackSession.PlaybackRate = next;
            ShowOsd("\uEC57", $"Скорость: {next:0.##}x");

            // Синхронизируем комбобокс
            for (int i = 0; i < SpeedCombo.Items.Count; i++)
            {
                if (SpeedCombo.Items[i] is ComboBoxItem item && double.TryParse(item.Tag?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double tagRate))
                {
                    if (Math.Abs(tagRate - next) < 0.05)
                    {
                        SpeedCombo.SelectedIndex = i;
                        break;
                    }
                }
            }
        }
    }
}