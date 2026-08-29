using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using ClassIsland.DutyRoster.Interop;
using ClassIsland.DutyRoster.Models;
using ClassIsland.UltraCodeShared;

namespace ClassIsland.DutyRoster.Views;

/// <summary>
/// 值日安排提醒浮窗。
/// </summary>
/// <remarks>
/// 这个插件的存在理由是「原来的提醒不显眼，没人会去值日」，所以窗口的设计目标
/// 是<b>让人一眼看到</b>，但不拦路：
/// <list type="bullet">
/// <item>人名用最大的字号——真正需要被看到的是「谁」，不是「擦黑板」；</item>
/// <item>置顶且反复重申，全屏应用也盖不住；</item>
/// <item>停留十几秒后自己消失，点屏幕任意位置也能立刻关掉，不需要专门去找按钮；</item>
/// <item>可以拖走，但拖不出屏幕。</item>
/// </list>
/// <para/>
/// 背景是 UltraCode 像素场：整块背景从右往左浮现，文字跟在扫过的波前后面淡入。
/// 尺寸和老版本完全一致，换的只是背景和入场方式。
/// 关闭时先让像素场往回流走再收窗，不是直接淡出——收场和入场对称，看着才像一件事。
/// </remarks>
internal class DutyPopupWindow : Window
{
    /// <summary>文字比像素场晚多少（相对于揭示时长）开始浮现。</summary>
    private const double TextDelayFactor = 0.55;

    private const int RevealMs = 620;
    private const int CollapseMs = 380;

    private static DutyPopupWindow? _instance;

    private readonly Border _card;
    private readonly StackPanel _body;
    private readonly UltraCodeField _field;
    private readonly DispatcherTimer? _autoClose;
    private TopmostEnforcer? _topmost;
    private GlobalClickWatcher? _dismissHook;
    private bool _closing;

    private bool _pointerDown;
    private bool _dragging;
    private Point _pressOrigin;
    private PixelPoint _grabOffset;

    /// <summary>当前是否有提醒浮窗开着。</summary>
    public static bool IsOpen => _instance is { _closing: false };

    /// <summary>
    /// 弹出（或刷新）值日提醒。
    /// </summary>
    public static void Popup(string title, DutySlot slot, DutySettings settings, Color accent)
    {
        if (_instance is { _closing: false } existing)
        {
            existing.Close();
            _instance = null;
        }

        _instance = new DutyPopupWindow(title, slot, settings, accent);
        _instance.Show();
    }

    /// <summary>关掉当前浮窗（不算「已确认」）。</summary>
    public static void CloseCurrent()
    {
        var open = _instance;
        _instance = null;
        open?.Dismiss();
    }

    private DutyPopupWindow(string title, DutySlot slot, DutySettings settings, Color accent)
    {
        SystemDecorations = SystemDecorations.None;
        Background = null;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        ShowInTaskbar = false;
        Topmost = true;
        CanResize = false;
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;

        // 像素场先刷新一次调色板，下面的文字色才拿得到与背景匹配的那一档。
        UltraCodePalette.Refresh();
        var ink = UltraCodePalette.ForegroundColor;

        _body = new StackPanel
        {
            Spacing = 0,
            // 文字从透明开始，等像素场扫过一多半再浮现。
            Opacity = 0,
            Transitions =
            [
                new DoubleTransition
                {
                    Property = OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(RevealMs * (1 - TextDelayFactor)),
                    Easing = new CubicEaseOut()
                }
            ]
        };

        // 标题行：时间段 + 「值日」二字。
        _body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                new Border
                {
                    Background = new SolidColorBrush(ink, 0.14),
                    BorderBrush = new SolidColorBrush(ink, 0.42),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(9, 3),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = slot.TimeText,
                        FontSize = settings.ProjectFontSize * 0.72,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = new SolidColorBrush(ink)
                    }
                },
                new TextBlock
                {
                    Text = title,
                    FontSize = settings.ProjectFontSize * 0.72,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(ink, 0.72)
                }
            }
        });

        // 每一项：项目名在上（小、淡），人名在下（大、实）。
        // 顺序刻意反过来——需要被记住的是人。
        foreach (var item in slot.Items)
        {
            _body.Children.Add(new StackPanel
            {
                Margin = new Thickness(0, settings.ProjectFontSize * 0.62, 0, 0),
                Spacing = 2,
                Children =
                {
                    new TextBlock
                    {
                        Text = item.Project,
                        FontSize = settings.ProjectFontSize * 0.68,
                        Foreground = new SolidColorBrush(ink, 0.66)
                    },
                    new TextBlock
                    {
                        Text = item.PeopleText,
                        FontSize = settings.PersonFontSize,
                        FontWeight = FontWeight.Bold,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = new SolidColorBrush(ink)
                    }
                }
            });
        }

        _field = new UltraCodeField
        {
            CellSize = 6,
            PixelOpacity = 0.62,
            RevealDurationMs = RevealMs,
            CollapseDurationMs = CollapseMs,
            ClipToBounds = true
        };
        _field.Collapsed += (_, _) => Dispatcher.UIThread.Post(FinishClose);

        _card = new Border
        {
            BorderBrush = new SolidColorBrush(accent, 0.75),
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(14),
            Width = settings.PopupWidth,
            // 圆角要真的把像素场裁掉，否则方角会从圆角外面露出来。
            ClipToBounds = true,
            Child = new Panel
            {
                Children =
                {
                    _field,
                    new Border
                    {
                        Padding = new Thickness(settings.ProjectFontSize, settings.ProjectFontSize * 0.85),
                        Child = _body
                    }
                }
            },
            Effect = new DropShadowEffect
            {
                BlurRadius = 30,
                OffsetX = 0,
                OffsetY = 6,
                Color = Colors.Black,
                Opacity = 0.55
            },
            Opacity = 0,
            RenderTransform = TransformOperations.Parse("scale(0.96)"),
            RenderTransformOrigin = RelativePoint.Center,
            Transitions =
            [
                new DoubleTransition
                {
                    Property = OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(140),
                    Easing = new CubicEaseOut()
                },
                new TransformOperationsTransition
                {
                    Property = RenderTransformProperty,
                    Duration = TimeSpan.FromMilliseconds(220),
                    Easing = new CubicEaseOut()
                }
            ]
        };

        Content = new Panel { Children = { _card } };

        // 拖动：整张卡片都能拖，但拖不出屏幕。
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;

        _autoClose = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(Math.Clamp(settings.HoldSeconds, 3, 300))
        };
        _autoClose.Tick += (_, _) => Dismiss();

        _placement = settings.Placement;
    }

    private readonly PopupPlacement _placement;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        Place();
        SizeChanged += (_, _) => Place();

        _topmost = new TopmostEnforcer(this, TimeSpan.FromMilliseconds(600));
        _topmost.Attach();

        // 点屏幕上任何地方都能关掉它。窗口本身只覆盖中间一小块，
        // 所以「别处」的点击收不到——只能靠一个全局鼠标钩子来听。
        _dismissHook = new GlobalClickWatcher(() => Dispatcher.UIThread.Post(Dismiss));
        _dismissHook.Start();

        _card.Opacity = 1;
        _card.RenderTransform = TransformOperations.Parse("scale(1)");
        _field.Open();

        // 文字等像素场扫过一多半再开始浮现。
        var delay = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(RevealMs * TextDelayFactor)
        };
        delay.Tick += (_, _) =>
        {
            delay.Stop();
            _body.Opacity = 1;
        };
        delay.Start();

        _autoClose?.Start();
    }

    /// <summary>按设置把窗口摆好，并夹在屏幕内。</summary>
    private void Place()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null)
        {
            return;
        }

        var scaling = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
        var width = (int)Math.Ceiling(Bounds.Width * scaling);
        var height = (int)Math.Ceiling(Bounds.Height * scaling);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var b = screen.Bounds;
        var margin = (int)(56 * scaling);
        var (x, y) = _placement switch
        {
            PopupPlacement.Top => (b.X + (b.Width - width) / 2, b.Y + margin),
            PopupPlacement.BottomRight => (b.X + b.Width - width - margin, b.Y + b.Height - height - margin),
            _ => (b.X + (b.Width - width) / 2, b.Y + (b.Height - height) / 2)
        };

        Position = new PixelPoint(
            Math.Clamp(x, b.X, Math.Max(b.X, b.X + b.Width - width)),
            Math.Clamp(y, b.Y, Math.Max(b.Y, b.Y + b.Height - height)));
    }

    #region 拖动

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _pointerDown = true;
        _dragging = false;
        _pressOrigin = point.Position;
        var onScreen = this.PointToScreen(point.Position);
        _grabOffset = new PixelPoint(onScreen.X - Position.X, onScreen.Y - Position.Y);
        e.Pointer.Capture(this);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_pointerDown)
        {
            return;
        }

        var current = e.GetPosition(this);
        var dx = current.X - _pressOrigin.X;
        var dy = current.Y - _pressOrigin.Y;
        if (!_dragging && Math.Sqrt(dx * dx + dy * dy) < 4.0)
        {
            return;
        }

        _dragging = true;
        var onScreen = this.PointToScreen(current);
        Position = new PixelPoint(onScreen.X - _grabOffset.X, onScreen.Y - _grabOffset.Y);
        ClampToScreen();
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var wasDragging = _dragging;
        _pointerDown = false;
        _dragging = false;
        e.Pointer.Capture(null);

        // 拖完不算「点了一下」，否则挪个位置就把它关掉了。
        if (!wasDragging)
        {
            Dismiss();
        }
    }

    private void ClampToScreen()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.ScreenFromPoint(Position) ?? Screens.Primary;
        if (screen is null)
        {
            return;
        }

        var scaling = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
        var width = (int)Math.Ceiling(Bounds.Width * scaling);
        var height = (int)Math.Ceiling(Bounds.Height * scaling);
        var b = screen.Bounds;
        Position = new PixelPoint(
            Math.Clamp(Position.X, b.X, Math.Max(b.X, b.X + b.Width - width)),
            Math.Clamp(Position.Y, b.Y, Math.Max(b.Y, b.Y + b.Height - height)));
    }

    #endregion

    /// <summary>开始收场：文字先退，像素场往回流，流完了再关窗。</summary>
    private void Dismiss()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        _autoClose?.Stop();
        _body.Opacity = 0;
        _field.Collapse();

        // 兜底：像素场万一没在跑（比如窗口刚开就被点掉），Collapsed 不会来，
        // 这里用一个略长于收场时长的定时器把窗口收掉，免得留在屏幕上。
        var safety = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(CollapseMs + 220) };
        safety.Tick += (_, _) =>
        {
            safety.Stop();
            FinishClose();
        };
        safety.Start();
    }

    private void FinishClose()
    {
        _dismissHook?.Dispose();
        _dismissHook = null;
        _topmost?.Dispose();
        _topmost = null;
        if (ReferenceEquals(_instance, this))
        {
            _instance = null;
        }

        Close();
    }
}
