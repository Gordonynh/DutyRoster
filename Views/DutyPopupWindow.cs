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
using ClassIsland.Core.Controls;
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
/// <item>停留十几秒后自己消失；关掉它的方式按设置走（点任意位置 / 右上角按钮）；</item>
/// <item>可以拖走，但拖不出屏幕。</item>
/// </list>
/// <para/>
/// 背景是 UltraCode 像素场：整块背景从右往左浮现，文字跟在扫过的波前后面淡入。
/// 尺寸和老版本完全一致，换的只是背景和入场方式。
/// 关闭时先让像素场往回流走再收窗，不是直接淡出——收场和入场对称，看着才像一件事。
/// <para/>
/// <b>它拿不到键盘焦点</b>（<c>ShowActivated = false</c> + <c>WS_EX_NOACTIVATE</c>），
/// 所以 Esc 兜底不能挂窗口的 <c>KeyDown</c>——那永远不会触发，只能走全局键盘钩子。
/// </remarks>
internal class DutyPopupWindow : Window
{
    /// <summary>文字比像素场晚多少（相对于揭示时长）开始浮现。</summary>
    private const double TextDelayFactor = 0.55;

    private const int RevealMs = 620;
    private const int CollapseMs = 380;

    private static DutyPopupWindow? _instance;

    /// <summary>这个窗口是为哪个时间点的开始时间开的。用来判断「该不该轮到我来关」。</summary>
    private readonly TimeSpan _slotStart;

    private readonly Border _card;
    private readonly StackPanel _body;
    private readonly UltraCodeField _field;
    private readonly DispatcherTimer? _autoClose;

    /// <summary>点别处也能关掉它。仅在设置允许时安装，否则一个系统钩子都不留。</summary>
    private readonly bool _closeOnClick;

    /// <summary>有没有右上角关闭按钮。记下来是为了决定要不要装 Esc 钩子。</summary>
    private readonly bool _hasCloseButton;

    private TopmostEnforcer? _topmost;
    private GlobalClickWatcher? _dismissHook;
    private GlobalKeyWatcher? _escHook;
    private bool _closing;

    private bool _pointerDown;
    private bool _dragging;
    private Point _pressOrigin;
    private PixelPoint _grabOffset;

    /// <summary>当前是否有提醒浮窗开着。</summary>
    public static bool IsOpen => _instance is { _closing: false };

    /// <summary>
    /// 当前开着的窗口是不是为这个开始时间开的。
    /// </summary>
    /// <remarks>
    /// 时间段到 End 时要靠它判断「该关的是不是我这个窗」：用户可能中途已经手动关掉、
    /// 又弹了别的提醒，这时候顺手一关就显得插件很蠢。
    /// </remarks>
    public static bool IsShowing(TimeSpan start) => _instance is { _closing: false } open && open._slotStart == start;

    /// <summary>
    /// 弹出（或刷新）值日提醒。
    /// </summary>
    /// <remarks>
    /// 替换旧窗口时走 <see cref="Dismiss"/> 而不是 <c>Close()</c>：直接 <c>Close()</c> 会绕过
    /// <see cref="FinishClose"/>，全局鼠标钩子和置顶重申定时器就永远留在系统里——
    /// 每替换一次泄漏一个，提醒越勤漏得越多。
    /// </remarks>
    public static void Popup(string title, DutySlot slot, DutySettings settings, Color accent)
    {
        if (_instance is { _closing: false } existing)
        {
            existing.Dismiss();
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
        _slotStart = slot.Start;
        _closeOnClick = settings.CanCloseByClick;
        _hasCloseButton = settings.CanCloseByButton;

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
                        Text = slot.RangeText,
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
            Child = BuildCardFace(settings, ink),
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

        // 单时刻才靠「停留多久」自动消失；时间段由 Service 到点关窗，这里不起定时器。
        // 注意：自动消失**不依赖**有没有手动关闭手段——用户把「点击关闭」「按钮关闭」都
        // 取消勾选，语义是「别被我误触关掉、别被人随手点掉」，而不是「一直挂到有人按 Esc」。
        // 尤其是教室大屏没有键盘，Esc 那条退路根本不存在，所以到点必须自己收掉。
        if (!slot.IsRange)
        {
            _autoClose = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(Math.Clamp(settings.HoldSeconds, 3, 600))
            };
            _autoClose.Tick += (_, _) => Dismiss();
        }

        _placement = settings.Placement;
    }

    /// <summary>
    /// 卡片的内容层：像素场打底、正文压在上面，关闭按钮只在设置勾了「按钮关闭」时才加。
    /// </summary>
    private Control BuildCardFace(DutySettings settings, Color ink)
    {
        var face = new Panel();
        face.Children.Add(_field);
        face.Children.Add(new Border
        {
            Padding = new Thickness(settings.ProjectFontSize, settings.ProjectFontSize * 0.85),
            Child = _body
        });

        if (BuildCloseButton(settings, ink) is { } closeButton)
        {
            face.Children.Add(closeButton);
        }

        return face;
    }

    /// <summary>
    /// 右上角的关闭按钮。只在设置勾了「按钮关闭」时才放上去。
    /// </summary>
    /// <remarks>
    /// 用 <c>FluentIcon</c>（<c>&#xE711;</c>，取消图标）而不是画个叉：字形跟 ClassIsland 自己的图标
    /// 一套，且 <c>FluentIcon</c> 自带 Fluent Icons 字体族，不用手动指定字体。
    /// <para/>
    /// 样式取向：像素场是流动、雾化的背景，按钮不能是一个突兀的实心色块。
    /// 默认状态是几乎透明的雾色底 + 一圈极淡的边；悬停时底色和边框一起提亮，
    /// 让「可点」这件事在鼠标经过时浮现出来，不抢正文又保留点击暗示。
    /// </remarks>
    private Button? BuildCloseButton(DutySettings settings, Color ink)
    {
        if (!settings.CanCloseByButton)
        {
            return null;
        }

        var button = new Button
        {
            Content = new FluentIcon("\uE711", 13)
            {
                Foreground = new SolidColorBrush(ink, 0.82)
            },
            Width = 30,
            Height = 30,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(15),
            Background = new SolidColorBrush(ink, 0.08),
            BorderBrush = new SolidColorBrush(ink, 0.22),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 10, 10, 0),
            Cursor = new Cursor(StandardCursorType.Hand)
        };

        // 悬停 / 按压反馈直接改背景色，不依赖样式选择器，简单可靠。
        var baseBg = new SolidColorBrush(ink, 0.08);
        var hoverBg = new SolidColorBrush(ink, 0.22);
        var pressedBg = new SolidColorBrush(ink, 0.34);
        button.PointerEntered += (_, _) => button.Background = hoverBg;
        button.PointerExited += (_, _) => button.Background = baseBg;
        button.PointerPressed += (_, _) => button.Background = pressedBg;
        button.PointerReleased += (_, _) => button.Background = hoverBg;

        button.Click += (_, _) => Dismiss();
        return button;
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
        // 用户没勾这一项时一个钩子都不装，别在系统里留常驻的东西。
        if (_closeOnClick)
        {
            _dismissHook = new GlobalClickWatcher(() => Dispatcher.UIThread.Post(Dismiss));
            _dismissHook.Start();
        }

        // 什么关闭手段都没勾时，这个窗口拿不到键盘焦点（见类注释），
        // 只能靠全局键盘钩子把 Esc 接住——否则用户会被锁在弹窗里。
        if (!_closeOnClick && !_hasCloseButton)
        {
            _escHook = new GlobalKeyWatcher(() => Dispatcher.UIThread.Post(Dismiss));
            _escHook.Start();
        }

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
        // 没勾「点击关闭」时，点在卡片上也不该关——那属于用户主动操作，统一由设置说了算。
        if (!wasDragging && _closeOnClick)
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
        ReleaseHooks();
        if (ReferenceEquals(_instance, this))
        {
            _instance = null;
        }

        Close();
    }

    /// <summary>放掉所有系统级资源。幂等，重复调用无害。</summary>
    private void ReleaseHooks()
    {
        _dismissHook?.Dispose();
        _dismissHook = null;
        _escHook?.Dispose();
        _escHook = null;
        _topmost?.Dispose();
        _topmost = null;
    }

    /// <summary>
    /// 兜底清理。
    /// </summary>
    /// <remarks>
    /// 正常路径走 <see cref="FinishClose"/>，但窗口也可能被宿主直接关掉
    /// （应用退出、插件卸载）。这一层保证全局钩子和置顶定时器在任何情况下都会被释放——
    /// 漏一个就是一个常驻的低级鼠标/键盘钩子。
    /// </remarks>
    protected override void OnClosed(EventArgs e)
    {
        ReleaseHooks();
        if (ReferenceEquals(_instance, this))
        {
            _instance = null;
        }

        base.OnClosed(e);
    }
}
