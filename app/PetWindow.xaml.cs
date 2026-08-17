using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopPetUi.Core;
using DesktopPetUi.Native;

namespace DesktopPetUi;

public partial class PetWindow : Window, ISpeakHost
{
    private readonly AppConfig _config;
    private MouseHook? _mouseHook;
    private IntPtr _hwnd;

    private bool _currentPass = true;
    private bool _sampling;
    private POINT? _pendingSample;
    private long _lastSample;
    private bool? _clickThroughOverride;
    private bool? _dragPrevOverride;

    // PNG mode state
    private int _imgPxW;
    private int _imgPxH;
    private byte[]? _alphaMask;
    private double _dispScale;
    private double _dispLeft;
    private double _dispTop;
    private double? _previewScale;
    private int _bodyMinX = int.MaxValue;
    private int _bodyMaxX = -1;
    private int _bodyMinY = int.MaxValue;
    private double _dpiScale = 1.0;
    private string? _currentImagePath;
    private readonly Random _rng = new();
    private DispatcherTimer? _idleCycleTimer;
    private System.Windows.Input.Cursor? _currentCursor;
    private DispatcherTimer? _bubbleTimer;
    private DispatcherTimer? _idleResetTimer;
    private System.Media.SoundPlayer? _currentPlayer;
    private System.IO.Stream? _currentStream;
    private CancellationTokenSource? _streamCts;
    private bool _pngDragging;
    private bool _pngDragMoved;
    private System.Windows.Point _pngLastScreen;

    private const long WS_EX_NOACTIVATE = 0x08000000;
    private const int GWL_EXSTYLE = -20;
    private const double MinVisiblePx = 80;

    public AppConfig Config => _config;

    public Action? ChatRequested { get; set; }

    public Rect? GetWindowRect()
    {
        if (Visibility != Visibility.Visible) return null;
        if (_dispScale > 0 && _imgPxW > 0 && _imgPxH > 0)
            return new Rect(Left + _dispLeft, Top + _dispTop, _imgPxW * _dispScale, _imgPxH * _dispScale);
        return new Rect(Left, Top, Width, Height);
    }

    public PetWindow(AppConfig config)
    {
        _config = config;
        InitializeComponent();

        Width = config.Character.Width;
        Height = config.Character.Height + Math.Max(0, config.Character.BubbleReserve);
        Topmost = config.Topmost;
        Opacity = 1.0;

        ApplyInitialPosition();
    }

    private void ApplyInitialPosition()
    {
        if (double.IsNaN(_config.X) || double.IsNaN(_config.Y))
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Left + (wa.Width - Width) / 2;
            Top = wa.Top + (wa.Height - Height) / 2;
        }
        else
        {
            Left = _config.X;
            Top = _config.Y;
        }
        ClampToWorkArea();
    }

    private void ClampToWorkArea()
    {
        var vs = GetVirtualScreen();
        if (vs is not Rect v) return;
        var min = Math.Min(MinVisiblePx, Width / 2);
        Left = Math.Clamp(Left, v.Left - (Width - min), v.Right - min);
        Top = Math.Clamp(Top, v.Top - (Height - min), v.Bottom - min);
    }

    private Rect? GetVirtualScreen()
    {
        try
        {
            var vs = System.Windows.Forms.SystemInformation.VirtualScreen;
            var dpi = VisualTreeHelper.GetDpi(this);
            var s = dpi.DpiScaleX;
            return new Rect(vs.Left / s, vs.Top / s, vs.Width / s, vs.Height / s);
        }
        catch
        {
            var wa = SystemParameters.WorkArea;
            return new Rect(wa.Left, wa.Top, wa.Width, wa.Height);
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        var style = ClickThrough.GetStylePtr(_hwnd, GWL_EXSTYLE).ToInt64();
        ClickThrough.SetStylePtr(_hwnd, GWL_EXSTYLE, new IntPtr(style | WS_EX_NOACTIVATE));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        InitializePng();
    }

    private void InitializePng()
    {
        Log.Info("InitializePng: start");
        AttachPngMouse();
        ApplyEmotion(null);
        StartIdleCycle();
        StartClickThrough();
        Log.Info("PNG mode initialized");
    }

    // ---------------- PNG mode ----------------

    private void AttachPngMouse()
    {
        MouseLeftButtonDown += OnPngMouseLeftButtonDown;
        MouseMove += OnPngMouseMove;
        MouseLeftButtonUp += OnPngMouseLeftButtonUp;
        SizeChanged += (_, _) => LayoutImage();
    }

    private void OnPngMouseLeftButtonDown(object? sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(this);
        if (SamplePngAlpha(pos.X, pos.Y) < _config.AlphaThreshold) return;
        _pngDragging = true;
        _pngDragMoved = false;
        _pngLastScreen = PointToScreen(pos);
        _dragPrevOverride = _clickThroughOverride;
        _clickThroughOverride = false;
        SetPassThrough(false);
        CaptureMouse();
        e.Handled = true;
    }

    private void OnPngMouseMove(object? sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_pngDragging) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndPngDrag();
            return;
        }
        var screen = PointToScreen(e.GetPosition(this));
        var dx = (screen.X - _pngLastScreen.X) / _dpiScale;
        var dy = (screen.Y - _pngLastScreen.Y) / _dpiScale;
        _pngLastScreen = screen;
        if (!_pngDragMoved && Math.Abs(dx) < 1 && Math.Abs(dy) < 1) return;
        _pngDragMoved = true;
        Left += dx;
        Top += dy;
        ClampToWorkArea();
        e.Handled = true;
    }

    private void OnPngMouseLeftButtonUp(object? sender, MouseButtonEventArgs e)
    {
        if (!_pngDragging) return;
        var moved = _pngDragMoved;
        EndPngDrag();
        e.Handled = true;
        if (!moved)
        {
            ApplyEmotion(null);
            StartIdleCycle();
            if (_config.Chat.Enabled) ChatRequested?.Invoke();
        }
    }

    private void EndPngDrag()
    {
        _pngDragging = false;
        ReleaseMouseCapture();
        if (_dragPrevOverride is bool pb)
        {
            _clickThroughOverride = pb;
            SetPassThrough(pb);
        }
        else
        {
            _clickThroughOverride = null;
            SetPassThrough(true);
        }
    }

    private string? ResolveImagePath(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) name = _config.Character.IdleEmotion;
        var root = _config.CharacterDir;
        var charDir = string.IsNullOrWhiteSpace(_config.Character.Current)
            ? root
            : Path.Combine(root, _config.Character.Current);
        return PickRandom(charDir, name) ?? PickRandom(charDir, _config.Character.IdleEmotion);
    }

    private string? PickRandom(string charDir, string name)
    {
        try
        {
            var dir = Path.Combine(charDir, name);
            if (Directory.Exists(dir))
            {
                var files = Directory.GetFiles(dir, "*.png");
                if (files.Length == 0) return null;
                if (files.Length > 1 && _currentImagePath != null)
                {
                    var others = files.Where(f => !string.Equals(f, _currentImagePath, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (others.Length > 0) return others[_rng.Next(others.Length)];
                }
                return files[_rng.Next(files.Length)];
            }
            var direct = Path.Combine(charDir, name + ".png");
            return File.Exists(direct) ? direct : null;
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource? LoadPng(string path, out int w, out int h, out byte[]? alpha)
    {
        w = 0; h = 0; alpha = null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            w = bmp.PixelWidth;
            h = bmp.PixelHeight;
            alpha = ReadAlpha(bmp, w, h);
            return bmp;
        }
        catch (Exception ex)
        {
            Log.Error("LoadPng failed: " + path, ex);
            return null;
        }
    }

    private static byte[]? ReadAlpha(BitmapSource bmp, int w, int h)
    {
        try
        {
            var bgra = new byte[w * h * 4];
            bmp.CopyPixels(bgra, w * 4, 0);
            var alpha = new byte[w * h];
            for (var i = 0; i < w * h; i++) alpha[i] = bgra[i * 4 + 3];
            return alpha;
        }
        catch
        {
            return null;
        }
    }

    public void ApplyEmotion(string? emotion)
    {
        var name = string.IsNullOrWhiteSpace(emotion) ? _config.Character.IdleEmotion : emotion;
        name = string.Concat(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
        var path = ResolveImagePath(name);
        if (path != null && path == _currentImagePath) return;
        _currentImagePath = path;
        if (path == null)
        {
            CharacterImage.Source = null;
            _alphaMask = null;
            _imgPxW = _imgPxH = 0;
            _bodyMinX = int.MaxValue;
            _bodyMaxX = -1;
            _bodyMinY = int.MaxValue;
            return;
        }
        var bmp = LoadPng(path, out var w, out var h, out var alpha);
        var oldSrc = CharacterImage.Source;
        bool doFade = _config.Character.CrossFade && oldSrc != null;
        double oldW = 0, oldH = 0, oldLeft = 0, oldTop = 0;
        if (doFade)
        {
            oldW = CharacterImage.Width;
            oldH = CharacterImage.Height;
            oldLeft = Canvas.GetLeft(CharacterImage);
            oldTop = Canvas.GetTop(CharacterImage);
        }

        CharacterImage.Source = bmp;
        _imgPxW = w;
        _imgPxH = h;
        _alphaMask = alpha;
        ComputeBodyBounds();
        if (doFade)
        {
            CharacterImageOld.Source = oldSrc;
            CharacterImage.Opacity = 0;
        }
        else
        {
            CharacterImage.Opacity = 1;
            if (CharacterImageOld.Source != null) CharacterImageOld.Source = null;
        }
        LayoutImage();
        if (doFade && !double.IsNaN(oldW) && oldW > 0)
        {
            // keep the outgoing image exactly where it was so it doesn't zoom during the fade
            CharacterImageOld.Width = oldW;
            CharacterImageOld.Height = oldH;
            Canvas.SetLeft(CharacterImageOld, oldLeft);
            Canvas.SetTop(CharacterImageOld, oldTop);
            Canvas.SetZIndex(CharacterImageOld, 0);
            CrossFade();
        }
    }

    private void CrossFade()
    {
        var dur = TimeSpan.FromSeconds(0.5);
        var easing = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var fadeIn = new DoubleAnimation(0.0, 1.0, dur)
        {
            FillBehavior = FillBehavior.HoldEnd,
            EasingFunction = easing,
        };
        var fadeOut = new DoubleAnimation(1.0, 0.0, dur)
        {
            FillBehavior = FillBehavior.HoldEnd,
            EasingFunction = easing,
        };
        CharacterImage.BeginAnimation(OpacityProperty, fadeIn);
        CharacterImageOld.BeginAnimation(OpacityProperty, fadeOut);
        var timer = new DispatcherTimer { Interval = dur.Add(TimeSpan.FromMilliseconds(80)) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (CharacterImageOld.Source != null) CharacterImageOld.Source = null;
        };
        timer.Start();
    }

    private void LayoutImage()
    {
        if (_imgPxW <= 0 || _imgPxH <= 0) return;
        var winW = ActualWidth;
        var winH = ActualHeight;
        if (winW <= 0 || winH <= 0) return;
        _dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var reserved = Math.Max(0, _config.Character.BubbleReserve);
        var availH = Math.Max(1, winH - reserved);
        var fit = Math.Min(winW / _imgPxW, availH / _imgPxH);
        if (fit <= 0 || !double.IsFinite(fit)) fit = 1;
        fit *= _previewScale ?? _config.EffectiveScale;
        _dispScale = fit;
        var dispW = _imgPxW * fit;
        var dispH = _imgPxH * fit;
        _dispLeft = (winW - dispW) / 2;
        _dispTop = winH - dispH;
        CharacterImage.Width = dispW;
        CharacterImage.Height = dispH;
        Canvas.SetLeft(CharacterImage, _dispLeft);
        Canvas.SetTop(CharacterImage, _dispTop);
        Canvas.SetZIndex(CharacterImage, 1);
        if (Bubble.Visibility == Visibility.Visible) LayoutBubble();
    }

    private double SamplePngAlpha(double cssX, double cssY)
    {
        if (_dispScale <= 0 || _imgPxW <= 0 || _imgPxH <= 0) return 0;
        var px = (cssX - _dispLeft) / _dispScale;
        var py = (cssY - _dispTop) / _dispScale;
        if (px < 0 || py < 0) return 0;
        var x = (int)px;
        var y = (int)py;
        if (x >= _imgPxW || y >= _imgPxH) return 0;
        if (_alphaMask == null) return 255; // no mask: treat image bounds as opaque
        return _alphaMask[y * _imgPxW + x];
    }

    /// <summary>计算立绘不透明像素的外接矩形（像素坐标），用于让气泡贴着头顶、不覆盖立绘。</summary>
    private void ComputeBodyBounds()
    {
        _bodyMinX = int.MaxValue;
        _bodyMaxX = -1;
        _bodyMinY = int.MaxValue;
        if (_alphaMask == null || _imgPxW <= 0 || _imgPxH <= 0) return;
        for (var y = 0; y < _imgPxH; y++)
        {
            var row = y * _imgPxW;
            for (var x = 0; x < _imgPxW; x++)
            {
                if (_alphaMask[row + x] < 8) continue; // 忽略接近透明的噪点
                if (x < _bodyMinX) _bodyMinX = x;
                if (x > _bodyMaxX) _bodyMaxX = x;
                if (y < _bodyMinY) _bodyMinY = y;
            }
        }
        if (_bodyMinX > _bodyMaxX)
        {
            _bodyMinX = 0;
            _bodyMaxX = _imgPxW - 1;
            _bodyMinY = 0;
        }
    }

    public void ShowBubble(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        BubbleText.Text = text.Length > 200 ? text[..200] + "…" : text;
        LayoutBubble();
        Bubble.Visibility = Visibility.Visible;
    }

    /// <summary>按语音时长安排气泡消失与立绘恢复空闲（无语音时按配置时长兜底）。</summary>
    private void ScheduleSpeechVisuals(double durationSec)
    {
        var sec = Math.Max(1, durationSec);
        if (_bubbleTimer == null)
        {
            _bubbleTimer = new DispatcherTimer();
            _bubbleTimer.Tick += (_, _) =>
            {
                Bubble.Visibility = Visibility.Collapsed;
                SpeechFinished?.Invoke();
            };
        }
        _bubbleTimer.Stop();
        _bubbleTimer.Interval = TimeSpan.FromSeconds(sec);
        _bubbleTimer.Start();

        if (_idleResetTimer == null)
        {
            _idleResetTimer = new DispatcherTimer();
            _idleResetTimer.Tick += (_, _) =>
            {
                _idleResetTimer.Stop();
                ShowIdle();
            };
        }
        _idleResetTimer.Stop();
        _idleResetTimer.Interval = TimeSpan.FromSeconds(sec);
        _idleResetTimer.Start();
    }

    /// <summary>语音（流式）播完后立即收起气泡、恢复空闲表情。</summary>
    private void EndSpeechVisuals()
    {
        _bubbleTimer?.Stop();
        Bubble.Visibility = Visibility.Collapsed;
        StopIdleReset();
        ShowIdle();
    }

    private void StopIdleReset()
    {
        _idleResetTimer?.Stop();
    }

    /// <summary>停止上一次语音残留的气泡/空闲定时器，避免旧定时器隐藏新一次回复的气泡。</summary>
    private void StopSpeechTimers()
    {
        _bubbleTimer?.Stop();
        StopIdleReset();
        StopIdleCycle();
    }

    private void ShowIdle()
    {
        ApplyEmotion(null);
        StartIdleCycle();
    }

    private void StartIdleCycle()
    {
        if (_idleCycleTimer == null)
        {
            _idleCycleTimer = new DispatcherTimer();
            _idleCycleTimer.Tick += (_, _) => ApplyEmotion(null);
        }
        var sec = _config.Character.IdleIntervalSec;
        if (sec <= 0)
        {
            _idleCycleTimer.Stop();
            return;
        }
        _idleCycleTimer.Interval = TimeSpan.FromSeconds(sec);
        _idleCycleTimer.Stop();
        _idleCycleTimer.Start();
    }

    private void StopIdleCycle()
    {
        _idleCycleTimer?.Stop();
    }

    public void RefreshIdleCycle()
    {
        StartIdleCycle();
    }

    public void ApplyWindowConfig()
    {
        Height = _config.Character.Height + Math.Max(0, _config.Character.BubbleReserve);
        LayoutImage();
    }

    /// <summary>实时预览立绘缩放（设置窗口中拖动滑条时调用）。null 表示使用当前配置的生效缩放。</summary>
    public void PreviewScale(double? scale)
    {
        _previewScale = scale;
        LayoutImage();
    }

    /// <summary>清除缩放预览，恢复使用配置文件里的生效缩放。</summary>
    public void ClearScalePreview()
    {
        _previewScale = null;
        LayoutImage();
    }

    private void SetCursorState(bool over)
    {
        var target = over ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow;
        if (_currentCursor == target) return;
        _currentCursor = target;
        try
        {
            Dispatcher.BeginInvoke(() => Cursor = target);
        }
        catch { }
    }

    private void LayoutBubble()
    {
        Bubble.Visibility = Visibility.Visible;
        Bubble.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        Bubble.UpdateLayout();
        var bw = Bubble.ActualWidth;
        var bh = Bubble.ActualHeight;
        var winW = ActualWidth;

        // 头顶位置：不透明像素外接框顶部；无遮罩时退回立绘显示区顶部
        double headTopY, headCenterX;
        if (_bodyMinY < int.MaxValue && _dispScale > 0)
        {
            headTopY = _dispTop + _bodyMinY * _dispScale;
            headCenterX = _dispLeft + (_bodyMinX + _bodyMaxX) / 2.0 * _dispScale;
        }
        else
        {
            headTopY = _dispTop;
            headCenterX = winW / 2.0;
        }

        const double headGap = 4;        // 尾巴尖到头顶的间距
        const double tailExtent = 12;    // 尾巴在气泡底部之下伸出的长度（与 XAML 中的 Margin 对应）

        var left = Math.Max(4, Math.Min(winW - bw - 4, headCenterX - bw / 2));
        var top = Math.Max(4, headTopY - bh - headGap - tailExtent);
        Canvas.SetLeft(Bubble, left);
        Canvas.SetTop(Bubble, top);
        Canvas.SetZIndex(Bubble, 5);
    }

    public List<string> GetCharacters() => _config.ListCharacters();

    public string GetCharacter() => _config.Character.Current;

    public void SetCharacter(string current)
    {
        _config.Character.Current = current ?? "";
        _config.LoadActiveCharacter();
        _config.Save();
        _currentImagePath = null;
        _previewScale = null;
        Bubble.Visibility = Visibility.Collapsed;
        StopIdleReset();
        StopIdleCycle();
        ApplyEmotion(null);
        StartIdleCycle();
    }

    // ---------------- click-through ----------------

    private void StartClickThrough()
    {
        SetPassThrough(true);
        _mouseHook = new MouseHook();
        _mouseHook.MouseMoved += OnMouseMoved;
        if (!_mouseHook.Start())
        {
            Log.Error("MouseHook start failed; falling back to always-interactive window");
            SetPassThrough(false);
        }
    }

    private void OnMouseMoved(POINT pt)
    {
        var rect = WindowUtil.GetRect(_hwnd);
        if (rect == null) return;
        if (!rect.Value.Contains(pt))
        {
            SetPassThrough(true);
            _pendingSample = null;
            return;
        }
        RequestSample(pt.X - rect.Value.Left, pt.Y - rect.Value.Top);
    }

    private void RequestSample(int lx, int ly)
    {
        if (_clickThroughOverride is bool o)
        {
            SetPassThrough(o);
            return;
        }
        if (!_config.ClickThroughAuto)
        {
            SetPassThrough(false);
            return;
        }
        if (_sampling)
        {
            _pendingSample = new POINT { X = lx, Y = ly };
            return;
        }
        if (Environment.TickCount64 - _lastSample < _config.SampleThrottleMs)
        {
            _pendingSample = new POINT { X = lx, Y = ly };
            return;
        }
        _lastSample = Environment.TickCount64;
        _sampling = true;
        SampleAsync(lx, ly);
    }

    private void SampleAsync(int lx, int ly)
    {
        try
        {
            var cssX = lx / _dpiScale;
            var cssY = ly / _dpiScale;
            var alpha = SamplePngAlpha(cssX, cssY);
            var over = alpha >= _config.AlphaThreshold;
            SetCursorState(over);
            SetPassThrough(!over);
        }
        catch { /* ignore sampling errors */ }
        finally
        {
            _sampling = false;
            if (_pendingSample is POINT p)
            {
                _pendingSample = null;
                RequestSample(p.X, p.Y);
            }
        }
    }

    private void SetPassThrough(bool pass)
    {
        if (_currentPass == pass) return;
        _currentPass = pass;
        if (_hwnd != IntPtr.Zero) ClickThrough.SetPassThrough(_hwnd, pass);
    }

    // ---------------- speak ----------------

    /// <summary>角色开始说话（气泡显示）时触发，用于主动搭话计时清零。</summary>
    public Action? SpeechStarted { get; set; }

    /// <summary>当前话说完且气泡消失后触发，用于主动搭话计时重新开始。</summary>
    public Action? SpeechFinished { get; set; }

    private int _speechSeq;

    public async Task SpeakAsync(string? text, byte[]? audio, string? emotion, string? expression)
    {
        var seq = Interlocked.Increment(ref _speechSeq);
        CancelStream();
        StopSpeechTimers();
        var imgEmotion = string.IsNullOrWhiteSpace(emotion) ? expression : emotion;
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            ApplyEmotion(imgEmotion);
            if (!string.IsNullOrWhiteSpace(text)) ShowBubble(text);
            SpeechStarted?.Invoke();
        });
        if (audio != null && audio.Length > 0)
        {
            _ = Task.Run(() =>
            {
                PlaySegment(audio);
                NotifySpeechFinished(seq, true);
            });
        }
        else
        {
            ScheduleSpeechVisuals(_config.Character.BubbleDurationSec);
            NotifySpeechFinished(seq, false);
        }
    }

    public async Task SpeakStreamAsync(string? text, IAsyncEnumerable<byte[]> audioSegments, string? emotion, string? expression)
    {
        var seq = Interlocked.Increment(ref _speechSeq);
        CancelStream();
        StopSpeechTimers();
        var imgEmotion = string.IsNullOrWhiteSpace(emotion) ? expression : emotion;
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            ApplyEmotion(imgEmotion);
            if (!string.IsNullOrWhiteSpace(text)) ShowBubble(text);
            SpeechStarted?.Invoke();
        });
        _ = PlayStreamAsync(audioSegments, seq);
    }

    private void CancelStream()
    {
        var old = _streamCts;
        _streamCts = null;
        if (old != null)
        {
            try { old.Cancel(); } catch { }
            old.Dispose();
        }
        StopCurrentPlayback();
    }

    private async Task PlayStreamAsync(IAsyncEnumerable<byte[]> segments, int seq)
    {
        var cts = new CancellationTokenSource();
        _streamCts = cts;
        await Task.Run(async () =>
        {
            try
            {
                await foreach (var seg in segments.WithCancellation(cts.Token))
                {
                    if (cts.IsCancellationRequested) break;
                    if (seg == null || seg.Length == 0) continue;
                    PlaySegment(seg);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Error("Stream TTS playback failed", ex);
            }
            finally
            {
                if (ReferenceEquals(_streamCts, cts)) _streamCts = null;
                NotifySpeechFinished(seq, true);
            }
        }, cts.Token);
    }

    /// <summary>同步播放一段 wav 直到结束（PlaySync），播完才返回，避免估算计时截断语音尾部。</summary>
    private void PlaySegment(byte[] wav)
    {
        StopCurrentPlayback();
        try
        {
            var ms = new MemoryStream(wav);
            var sp = new System.Media.SoundPlayer(ms);
            _currentPlayer = sp;
            _currentStream = ms;
            sp.PlaySync();
        }
        catch (Exception ex)
        {
            Log.Error("SoundPlayer playback failed", ex);
        }
        finally
        {
            if (_currentStream != null)
            {
                try { _currentStream.Dispose(); } catch { }
                _currentStream = null;
            }
            if (_currentPlayer != null) _currentPlayer = null;
        }
    }

    private void NotifySpeechFinished(int seq, bool endVisuals)
    {
        if (seq != Volatile.Read(ref _speechSeq)) return;
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (endVisuals)
                {
                    EndSpeechVisuals();
                    SpeechFinished?.Invoke();
                }
            });
        }
        catch { }
    }

    private void StopCurrentPlayback()
    {
        var old = _currentPlayer;
        _currentPlayer = null;
        if (old != null)
        {
            try { old.Stop(); } catch { }
            try { old.Dispose(); } catch { }
        }
        if (_currentStream != null)
        {
            try { _currentStream.Dispose(); } catch { }
            _currentStream = null;
        }
    }

    public void ShutdownSafely()
    {
        try
        {
            _config.X = Left;
            _config.Y = Top;
            _config.Save();
        }
        catch { }
        _mouseHook?.Dispose();
    }

    protected override void OnClosed(EventArgs e)
    {
        _mouseHook?.Dispose();
        base.OnClosed(e);
    }
}
