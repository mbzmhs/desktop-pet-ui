using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    private TaskCompletionSource<ConfirmResult>? _confirmTcs;
    private TaskCompletionSource<AskResult>? _askTcs;
    private bool? _confirmPrevOverride;
    private string? _confirmTrustDir; // 本次确认可一键信任的目录（null=不显示该按钮）
    private AskRequest? _askReq;         // 当前提问（opencode 式多问）
    private List<string>? _askAnswers;   // 每问答案（空=未答；多选以、连接）
    private List<List<System.Windows.Controls.Button>>? _askOptBtns; // 每问的选项按钮（重绘选中态用）
    private DispatcherTimer? _confirmTimer; // 确认/提问气泡共用超时定时器
    private double _dialogGrowDelta;    // 本次对话框为容纳内容临时向上扩大的窗口高度（关闭时还原）
    private double _dialogExtraReserve; // 与 _dialogGrowDelta 配套的额外气泡预留（扩大时立绘尺寸不变）
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
        var reserved = Math.Max(0, _config.Character.BubbleReserve) + _dialogExtraReserve;
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
        // 普通语音气泡：窄版纯文本，清掉可能残留的确认对话框结构化行
        BubbleTitleRow.Visibility = Visibility.Collapsed;
        BubbleRiskNote.Visibility = Visibility.Collapsed;
        BubbleDetailBox.Visibility = Visibility.Collapsed;
        BubbleText.Visibility = Visibility.Visible;
        Bubble.MaxWidth = Math.Max(240, Math.Min(280, ActualWidth - 12));
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
            _bubbleTimer.Tick += (_, _) => EndSpeechVisuals();
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

    /// <summary>语音（流式）播完、气泡定时到点或切换角色时收起气泡、恢复空闲表情，并触发 SpeechFinished 以重启主动搭话计时。</summary>
    private void EndSpeechVisuals()
    {
        if (_confirmTcs != null || _askTcs != null) return; // 确认/提问气泡等待用户操作期间不受语音定时器影响
        _bubbleTimer?.Stop();
        Bubble.Visibility = Visibility.Collapsed;
        StopIdleReset();
        ShowIdle();
        SpeechFinished?.Invoke();
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
        var (headTopY, headCenterX) = HeadAnchor();

        const double headGap = 4;        // 尾巴尖到头顶的间距
        const double tailExtent = 12;    // 尾巴在气泡底部之下伸出的长度（与 XAML 中的 Margin 对应）

        var left = Math.Max(4, Math.Min(winW - bw - 4, headCenterX - bw / 2));
        var top = Math.Max(4, headTopY - bh - headGap - tailExtent);
        Canvas.SetLeft(Bubble, left);
        Canvas.SetTop(Bubble, top);
        Canvas.SetZIndex(Bubble, 5);
    }

    /// <summary>头顶锚点（窗口坐标）：不透明像素外接框顶部与中心；无遮罩时退回立绘显示区顶部。</summary>
    private (double Top, double CenterX) HeadAnchor()
    {
        if (_bodyMinY < int.MaxValue && _dispScale > 0)
            return (_dispTop + _bodyMinY * _dispScale, _dispLeft + (_bodyMinX + _bodyMaxX) / 2.0 * _dispScale);
        return (_dispTop, ActualWidth / 2.0);
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
        EndSpeechVisuals();
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

    public async Task SpeakSegmentsAsync(string? fullText, IReadOnlyList<SpeechSegmentSpec> segments)
    {
        var seq = Interlocked.Increment(ref _speechSeq);
        CancelStream();
        StopSpeechTimers();
        var firstEmotion = segments.Count > 0 ? segments[0].Emotion : null;
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            ApplyEmotion(firstEmotion);
            if (!string.IsNullOrWhiteSpace(fullText)) ShowBubble(fullText);
            SpeechStarted?.Invoke();
        });
        if (segments.Count == 0)
        {
            ScheduleSpeechVisuals(_config.Character.BubbleDurationSec);
            NotifySpeechFinished(seq, false);
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var tts = _config.EffectiveTts();
                var isStream = string.Equals(tts.Provider, "gptsovits", StringComparison.OrdinalIgnoreCase) && tts.Streaming;
                if (isStream)
                    await PlaySegmentsStreamingAsync(seq, segments, tts);
                else if (!string.Equals(tts.Provider, "none", StringComparison.OrdinalIgnoreCase))
                    await PlaySegmentsSynthesizedAsync(seq, segments, tts);
                else
                    await PlaySegmentsProportionalAsync(seq, segments, _config.Character.BubbleDurationSec);
                NotifySpeechFinished(seq, true);
            }
            catch (Exception ex)
            {
                Log.Error("Segmented speech failed", ex);
                NotifySpeechFinished(seq, true);
            }
        });
    }

    // ---------------- agent 确认气泡 ----------------

    private const int ConfirmTimeoutSec = 120;

    /// <summary>确认重定向：聊天窗可见时由它接管确认；返回 null 表示回退到本窗气泡。</summary>
    public Func<ConfirmRequest, Task<ConfirmResult>?>? ConfirmRedirect { get; set; }

    /// <summary>提问重定向：聊天窗可见时由它接管 opencode 式提问；返回 null 表示回退到本窗气泡。</summary>
    public Func<AskRequest, Task<AskResult>?>? AskRedirect { get; set; }

    /// <summary>弹出带 [确认][取消]（必要时另有[信任该目录]）按钮的气泡，等待用户点击；超时按取消处理。</summary>
    public async Task<ConfirmResult> ConfirmAsync(ConfirmRequest request)
    {
        if (_confirmTcs != null || _askTcs != null)
            return new ConfirmResult { Allowed = false }; // 已有气泡在等用户，直接按拒绝返回

        // 聊天窗开着时在聊天窗内确认（不弹宠物气泡）
        var redirect = ConfirmRedirect;
        if (redirect != null)
        {
            try
            {
                var task = redirect(request);
                if (task != null) return await task;
            }
            catch { /* 重定向失败则回退气泡 */ }
        }

        var tcs = new TaskCompletionSource<ConfirmResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _confirmTcs = tcs;
        _confirmTrustDir = request?.TrustableDir;
        try
        {
            EnsureDialogTimer();
            _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                ShowConfirmChrome(request);
                ConfirmYesBtn.Visibility = Visibility.Visible;
                ConfirmTrustBtn.Visibility = _confirmTrustDir != null ? Visibility.Visible : Visibility.Collapsed;
                TextInputRow.Visibility = Visibility.Collapsed;
                ApplyEmotion("shy"); // 请求许可的小心表情（缺失时回退 idle）
            });
        }
        catch (Exception ex)
        {
            Log.Error("ConfirmAsync dispatch failed", ex);
            tcs.TrySetResult(new ConfirmResult { Allowed = false });
        }
        return await tcs.Task;
    }

    /// <summary>opencode 式提问（选项按钮+输入，一次可多问）；聊天窗可见时由它接管，否则回退本窗气泡。超时/关闭按未回答处理（已填部分保留）。</summary>
    public async Task<AskResult> AskUserAsync(AskRequest request)
    {
        var n = request?.Questions.Count ?? 0;
        if (_confirmTcs != null || _askTcs != null)
            return new AskResult { Answered = false, Answers = EmptyAnswers(n) }; // 已有气泡在等用户

        // 聊天窗开着时在聊天窗内提问（不弹宠物气泡）
        var redirect = AskRedirect;
        if (redirect != null)
        {
            try
            {
                var task = redirect(request);
                if (task != null) return await task;
            }
            catch { /* 重定向失败则回退气泡 */ }
        }

        var tcs = new TaskCompletionSource<AskResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _askTcs = tcs;
        _askReq = request;
        _askAnswers = EmptyAnswers(n);
        try
        {
            EnsureDialogTimer();
            System.Windows.Application.Current.Dispatcher.InvokeAsync(() => ShowAskChrome(request));
        }
        catch (Exception ex)
        {
            Log.Error("AskUserAsync dispatch failed", ex);
            CleanupAskState();
            tcs.TrySetResult(new AskResult { Answered = false, Answers = EmptyAnswers(n) });
        }
        return await tcs.Task;
    }

    private static List<string> EmptyAnswers(int n)
    {
        var l = new List<string>();
        for (var i = 0; i < Math.Max(0, n); i++) l.Add("");
        return l;
    }

    private void CleanupAskState()
    {
        _askReq = null;
        _askAnswers = null;
        _askOptBtns = null;
    }

    /// <summary>提问气泡（可多问）：编号问题列表 + 每问选项按钮（点击选择/多选切换）+ 共享输入行（文本记入第一个未答的问题）。</summary>
    private void ShowAskChrome(AskRequest req)
    {
        BeginDialogChrome();
        BubbleTitleRow.Visibility = Visibility.Collapsed;
        BubbleRiskNote.Visibility = Visibility.Collapsed;
        BubbleDetailBox.Visibility = Visibility.Collapsed;

        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < req.Questions.Count; i++)
        {
            var q = req.Questions[i];
            if (req.Questions.Count > 1) sb.Append(i + 1).Append(". ");
            sb.AppendLine(q.Question);
            if (q.Options.Count > 0) sb.AppendLine("选项：" + string.Join(" / ", q.Options));
        }
        BubbleText.Visibility = Visibility.Visible;
        BubbleText.Text = CapText(sb.ToString().TrimEnd(), 1200);

        // 每问一行选项按钮（多问时带 Qn 前缀）
        BubbleButtons.Children.Clear();
        _askOptBtns = new List<List<System.Windows.Controls.Button>>();
        for (var i = 0; i < req.Questions.Count; i++)
        {
            var q = req.Questions[i];
            if (q.Options.Count == 0) { _askOptBtns.Add(new List<System.Windows.Controls.Button>()); continue; }
            int qi = i;
            var row = new System.Windows.Controls.StackPanel();
            var btns = new List<System.Windows.Controls.Button>();
            for (var j = 0; j < q.Options.Count; j++)
            {
                int oj = j;
                var b = MakeAskOptionButton((req.Questions.Count > 1 ? "Q" + (qi + 1) + " " : "") + q.Options[oj], false);
                b.Click += (_, _) => ToggleAskOption(qi, oj, q.Multiple);
                btns.Add(b);
                row.Children.Add(b);
            }
            _askOptBtns.Add(btns);
            BubbleButtons.Children.Add(row);
        }

        ConfirmYesBtn.Visibility = Visibility.Collapsed;
        ConfirmTrustBtn.Visibility = Visibility.Collapsed;
        AskInputBox.Clear();
        TextInputRow.Visibility = Visibility.Visible;
        ApplyEmotion("curious"); // 提问表情（缺失时回退 idle）
        System.Windows.Input.Keyboard.Focus(AskInputBox);
        EndDialogChrome();
    }

    private static System.Windows.Controls.Button MakeAskOptionButton(string text, bool selected)
    {
        var b = new System.Windows.Controls.Button
        {
            Content = text,
            Foreground = System.Windows.Media.Brushes.White,
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(0, 2, 6, 2),
            FontSize = 12.5,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        StyleAskOptionPet(b, selected);
        return b;
    }

    private static void StyleAskOptionPet(System.Windows.Controls.Button b, bool selected)
    {
        b.Background = MakeBrush(selected ? "#3D7EBF" : "#26282C");
        b.BorderBrush = MakeBrush(selected ? "#3D7EBF" : "#454B57");
        b.BorderThickness = new Thickness(1);
    }

    /// <summary>点选项：单选替换、多选切换；重绘该问全部按钮的选中态。</summary>
    private void ToggleAskOption(int qi, int oj, bool multiple)
    {
        if (_askReq == null || _askAnswers == null || qi >= _askReq.Questions.Count) return;
        var q = _askReq.Questions[qi];
        if (oj >= q.Options.Count) return;
        var cur = _askAnswers[qi];
        if (multiple)
        {
            var parts = cur.Length == 0 ? new List<string>() : cur.Split('、').ToList();
            if (parts.Contains(q.Options[oj])) parts.Remove(q.Options[oj]);
            else parts.Add(q.Options[oj]);
            _askAnswers[qi] = string.Join("、", parts);
        }
        else
        {
            _askAnswers[qi] = cur == q.Options[oj] ? "" : q.Options[oj];
        }
        if (_askOptBtns != null && qi < _askOptBtns.Count)
        {
            var curSet = _askAnswers[qi].Length == 0 ? new List<string>() : _askAnswers[qi].Split('、').ToList();
            for (var k = 0; k < _askOptBtns[qi].Count; k++)
                StyleAskOptionPet(_askOptBtns[qi][k], curSet.Contains(q.Options[k]));
        }
    }

    private void EnsureDialogTimer()
    {
        if (_confirmTimer == null)
        {
            _confirmTimer = new DispatcherTimer();
            _confirmTimer.Tick += (_, _) => FinishActiveDialog(answered: false, timedOut: true);
        }
    }

    /// <summary>两种气泡共用的显示前置：置顶、关穿透、按窗口宽度收气泡宽。</summary>
    private void BeginDialogChrome()
    {
        StopSpeechTimers(); // 防止旧的语音定时器把气泡收掉
        if (_hwnd != IntPtr.Zero) WindowUtil.BringToTop(_hwnd); // 提到顶层组最前，盖住聊天输入窗等
        _confirmPrevOverride = _clickThroughOverride;
        _clickThroughOverride = false;
        SetPassThrough(false); // 气泡在角色图 alpha 掩码之外，不强制的话鼠标采样会判成穿透、按钮点不到
        Bubble.MaxWidth = Math.Max(240, Math.Min(460, ActualWidth - 12));
    }

    /// <summary>显示收尾：显示按钮行、必要时向上扩窗、定位气泡、启动超时。</summary>
    private void EndDialogChrome()
    {
        ConfirmNoBtn.Visibility = Visibility.Visible;
        BubbleButtons.Visibility = Visibility.Visible;
        GrowWindowForBubble(); // 内容高于头顶空间时临时把窗口向上扩大（对话框关闭时还原）
        LayoutBubble();
        Bubble.Visibility = Visibility.Visible;
        _confirmTimer!.Interval = TimeSpan.FromSeconds(ConfirmTimeoutSec);
        _confirmTimer.Stop();
        _confirmTimer.Start();
    }

    /// <summary>纯文本气泡（提问/回退）：无标题、无徽标、无详情块。</summary>
    private void ShowDialogChrome(string question)
    {
        BeginDialogChrome();
        BubbleTitleRow.Visibility = Visibility.Collapsed;
        BubbleRiskNote.Visibility = Visibility.Collapsed;
        BubbleDetailBox.Visibility = Visibility.Collapsed;
        BubbleText.Visibility = Visibility.Visible;
        BubbleText.Text = CapText(question, 1000);
        EndDialogChrome();
    }

    /// <summary>结构化确认气泡：标题 + 风险徽标（+风险说明）+ 等宽详情块，关键信息不省略。</summary>
    private void ShowConfirmChrome(ConfirmRequest? req)
    {
        BeginDialogChrome();
        if (string.IsNullOrWhiteSpace(req?.Title))
        {
            // 无结构化字段时回退纯文本
            BubbleTitleRow.Visibility = Visibility.Collapsed;
            BubbleRiskNote.Visibility = Visibility.Collapsed;
            BubbleDetailBox.Visibility = Visibility.Collapsed;
            BubbleText.Visibility = Visibility.Visible;
            BubbleText.Text = CapText(req?.Question ?? "", 1000);
        }
        else
        {
            BubbleTitleRow.Visibility = Visibility.Visible;
            BubbleTitle.Text = req.Title!;
            ApplyRiskBadge(req.Risk, req.RiskNote);
            if (string.IsNullOrWhiteSpace(req.Detail))
                BubbleDetailBox.Visibility = Visibility.Collapsed;
            else
            {
                BubbleDetailBox.Visibility = Visibility.Visible;
                BubbleDetail.Text = CapText(req.Detail, 2000);
            }
            BubbleText.Visibility = Visibility.Collapsed;
        }
        EndDialogChrome();
    }

    /// <summary>风险徽标：low=绿 / medium=黄 / high=红；风险说明显示在标题下小字行。</summary>
    private void ApplyRiskBadge(string risk, string note)
    {
        if (!string.IsNullOrWhiteSpace(note))
        {
            BubbleRiskNote.Text = "风险说明：" + CapText(note, 200);
            BubbleRiskNote.Visibility = Visibility.Visible;
        }
        else
        {
            BubbleRiskNote.Visibility = Visibility.Collapsed;
        }

        (System.Windows.Media.Brush? color, string label) badge = risk switch
        {
            "low" => (MakeBrush("#3A7A4A"), "低风险"),
            "medium" => (MakeBrush("#B07D2E"), "中风险"),
            "high" => (MakeBrush("#B03A3A"), "高风险"),
            _ => (null, ""),
        };
        if (badge.color == null) RiskBadge.Visibility = Visibility.Collapsed;
        else
        {
            RiskBadge.Background = badge.color;
            RiskBadgeText.Text = badge.label;
            RiskBadge.Visibility = Visibility.Visible;
        }
    }

    private static System.Windows.Media.Brush MakeBrush(string hex) =>
        new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));

    /// <summary>
    /// 气泡内容高于角色头顶空间时，把窗口临时向上扩大 δ：Top-δ / Height+δ / 预留+δ
    /// （预留同步增加 → 立绘可用高度不变 → 尺寸与屏幕位置保持不动）。对话框关闭时 RestoreDialogWindow 还原。
    /// </summary>
    private void GrowWindowForBubble()
    {
        Bubble.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var bh = Bubble.DesiredSize.Height;
        var headTopY = HeadAnchor().Top;
        const double headGap = 4;        // 尾巴尖到头顶的间距
        const double tailExtent = 12;    // 尾巴在气泡底部之下伸出的长度（与 XAML 中的 Margin 对应）
        const double margin = 4;         // 距窗口顶边最小距离
        var required = bh + headGap + tailExtent + margin;
        var available = headTopY - margin;
        if (required <= available) return;

        var delta = required - available;
        // 不把窗口推出虚拟屏幕顶边；空间不够时允许气泡盖住角色头顶（与旧行为一致）
        if (GetVirtualScreen() is { } vs) delta = Math.Min(delta, Top - vs.Top);
        if (delta <= 0) return;

        Top -= delta;
        Height += delta; // SizeChanged → LayoutImage
        _dialogExtraReserve += delta;
        _dialogGrowDelta += delta;
    }

    /// <summary>对话框结束时还原窗口尺寸。用"加回 δ"而非恢复绝对值，与对话框期间的用户拖动可正确叠加。</summary>
    private void RestoreDialogWindow()
    {
        if (_dialogGrowDelta <= 0) return;
        var d = _dialogGrowDelta;
        _dialogGrowDelta = 0;
        Height -= d;
        Top += d;
        _dialogExtraReserve -= d;
    }

    private static string CapText(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "\n…（已截断）");

    private void OnConfirmYes(object sender, RoutedEventArgs e) => FinishActiveDialog(answered: true);

    private void OnConfirmNo(object sender, RoutedEventArgs e) => FinishActiveDialog(answered: false);

    /// <summary>放行本次操作，并把目标所在目录加入信任列表（对字面路径可验证的 PowerShell 命令同样生效）。</summary>
    private void OnConfirmTrust(object sender, RoutedEventArgs e)
    {
        var dir = _confirmTrustDir;
        if (dir == null) return;
        var list = _config.Chat.Agent.TrustedDirs ??= new ObservableCollection<string>();
        if (!list.Any(d => string.Equals((d ?? "").Trim(), dir.Trim(), StringComparison.OrdinalIgnoreCase)))
            list.Add(dir);
        _config.Save();
        Log.Info("Agent trust dir added: " + dir);
        FinishActiveDialog(answered: true, trustFolder: true);
    }

    private void OnAskSend(object sender, RoutedEventArgs e) => SendAskAnswer();

    private void OnAskInputKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            e.Handled = true;
            SendAskAnswer();
        }
    }

    /// <summary>输入行发送：文本记入第一个未答的问题，然后提交全部。</summary>
    private void SendAskAnswer()
    {
        var text = AskInputBox.Text?.Trim() ?? "";
        if (_askAnswers != null && text.Length > 0)
            for (var i = 0; i < _askAnswers.Count; i++)
                if (_askAnswers[i].Length == 0) { _askAnswers[i] = text; break; }
        FinishActiveDialog(answered: true, text: text);
    }

    /// <summary>当前活动气泡结束（点击/超时），幂等：只有第一个结果生效。确认与提问两种模式共用。</summary>
    private void FinishActiveDialog(bool answered, bool timedOut = false, string? text = null, bool trustFolder = false)
    {
        if (_confirmTcs != null)
        {
            var ok = answered && !timedOut;
            _confirmTcs.TrySetResult(new ConfirmResult { Allowed = ok, TrustFolder = trustFolder && !timedOut });
            Log.Info("Agent confirm: " + (ok ? (trustFolder ? "confirmed + folder trusted" : "confirmed") : timedOut ? "timeout, treated as decline" : "declined"));
        }
        else if (_askTcs != null)
        {
            var okAsk = answered && !timedOut;
            var answers = _askAnswers ?? new List<string>(); // 超时/取消时已填部分仍带回
            _askTcs.TrySetResult(new AskResult { Answered = okAsk, Answers = answers });
            Log.Info("Agent ask: " + (okAsk ? ("answered: " + TruncateLog(string.Join(" | ", answers))) : timedOut ? "timeout, partial answers kept" : "cancelled"));
        }
        else return;

        _confirmTcs = null;
        _askTcs = null;
        _confirmTrustDir = null;
        CleanupAskState();
        _confirmTimer?.Stop();
        _clickThroughOverride = _confirmPrevOverride; // 恢复正常鼠标穿透采样
        RestoreDialogWindow(); // 还原为对话框期间的临时扩窗
        BubbleButtons.Visibility = Visibility.Collapsed;
        TextInputRow.Visibility = Visibility.Collapsed;
        ShowIdle(); // 恢复空闲；最终回答的气泡随后由说话管线重新显示
    }

    private static string TruncateLog(string? s)
    {
        var t = (s ?? "").Replace("\n", " ⏎ ");
        return t.Length > 200 ? t[..200] + "…" : t;
    }

    /// <summary>分段流式播放：仅第一段请求带 stop_prev 并等它被服务端注册后，再并行发出其余段请求，避免误杀自己。</summary>
    private async Task PlaySegmentsStreamingAsync(int seq, IReadOnlyList<SpeechSegmentSpec> segments, ChatTtsConfig tts)
    {
        var cts = new CancellationTokenSource();
        if (seq != Volatile.Read(ref _speechSeq)) return;
        _streamCts = cts;
        try
        {
            var url = tts.Url;
            var buffered = new (IAsyncEnumerable<byte[]> Stream, Task Ready)[segments.Count];
            buffered[0] = TtsClient.StartStreamingBuffered(url, segments[0].Text, tts, segments[0].TtsEmotion, stopPrev: true, cts.Token);
            await buffered[0].Ready.WaitAsync(cts.Token);
            if (seq != Volatile.Read(ref _speechSeq)) return;
            for (var i = 1; i < segments.Count; i++)
            {
                if (seq != Volatile.Read(ref _speechSeq)) return;
                buffered[i] = TtsClient.StartStreamingBuffered(url, segments[i].Text, tts, segments[i].TtsEmotion, stopPrev: false, cts.Token);
            }

            for (var i = 0; i < segments.Count; i++)
            {
                if (seq != Volatile.Read(ref _speechSeq)) return;
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => ApplyEmotion(segments[i].Emotion));
                await foreach (var chunk in buffered[i].Stream.WithCancellation(cts.Token))
                {
                    if (seq != Volatile.Read(ref _speechSeq)) return;
                    if (chunk == null || chunk.Length == 0) continue;
                    PlaySegment(chunk);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("Segmented stream TTS playback failed", ex);
        }
        finally
        {
            if (ReferenceEquals(_streamCts, cts)) _streamCts = null;
        }
    }

    /// <summary>分段非流式播放：各段并行合成后按序播放；某段合成失败则按文本比例停顿兜底。</summary>
    private async Task PlaySegmentsSynthesizedAsync(int seq, IReadOnlyList<SpeechSegmentSpec> segments, ChatTtsConfig tts)
    {
        var cts = new CancellationTokenSource();
        if (seq != Volatile.Read(ref _speechSeq)) return;
        _streamCts = cts;
        try
        {
            var audio = new byte[segments.Count][];
            var tasks = new Task[segments.Count];
            for (var i = 0; i < segments.Count; i++)
            {
                var idx = i;
                var spec = segments[i];
                tasks[i] = Task.Run(async () =>
                {
                    try
                    {
                        (audio[idx], _) = await TtsClient.SynthesizeAsync(tts.Url, spec.Text, tts, spec.TtsEmotion, cts.Token);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        Log.Error("TTS 分段合成失败: " + (spec.Text.Length > 24 ? spec.Text[..24] + "…" : spec.Text), ex);
                    }
                }, cts.Token);
            }
            await Task.WhenAll(tasks);

            var totalChars = segments.Sum(s => s.Text.Length);
            for (var i = 0; i < segments.Count; i++)
            {
                if (seq != Volatile.Read(ref _speechSeq)) return;
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => ApplyEmotion(segments[i].Emotion));
                var wav = audio[i];
                if (wav != null && wav.Length > 0)
                    PlaySegment(wav);
                else
                    await Task.Delay(SegmentDelayMs(_config.Character.BubbleDurationSec, segments[i].Text.Length, totalChars), cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("Segmented TTS playback failed", ex);
        }
        finally
        {
            if (ReferenceEquals(_streamCts, cts)) _streamCts = null;
        }
    }

    /// <summary>无 TTS：按文本比例分配 BubbleDurationSec，定时在段边界切换情绪。</summary>
    private async Task PlaySegmentsProportionalAsync(int seq, IReadOnlyList<SpeechSegmentSpec> segments, double budgetSec)
    {
        var cts = new CancellationTokenSource();
        if (seq != Volatile.Read(ref _speechSeq)) return;
        _streamCts = cts;
        try
        {
            var totalChars = segments.Sum(s => s.Text.Length);
            for (var i = 0; i < segments.Count; i++)
            {
                if (seq != Volatile.Read(ref _speechSeq)) return;
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => ApplyEmotion(segments[i].Emotion));
                await Task.Delay(SegmentDelayMs(budgetSec, segments[i].Text.Length, totalChars), cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_streamCts, cts)) _streamCts = null;
        }
    }

    private static int SegmentDelayMs(double budgetSec, int charCount, int totalChars)
    {
        if (totalChars <= 0 || charCount <= 0) return (int)(budgetSec * 1000);
        return Math.Max(250, (int)(budgetSec * 1000 * charCount / (double)totalChars));
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
