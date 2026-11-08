using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ClassIsland.Core.Controls;
using ClassIsland.DutyRoster.Interop;
using ClassIsland.DutyRoster.Models;
using ClassIsland.PluginShared;
using ClassIsland.UltraCodeShared;

namespace ClassIsland.DutyRoster.Views;

/// <summary>
/// 大扫除的整屏提醒。
/// </summary>
/// <remarks>
/// 一天三次的大扫除是全班的事，卡片浮窗那点面积压不住教室里的动静，所以铺满整屏：
/// UltraCode 像素场从右往左覆盖，随后大标题和名单浮现，停留几秒再从右往左流走。
/// <para/>
/// <b>它会吃掉点击</b>——和值日卡片浮窗刚好相反。这是刻意的：整屏窗口如果点击穿透，
/// 老师想关掉它就只能等，反而更烦人。关掉它的方式按设置走：
/// 点了「点击空白处或按任意键关闭」就点哪都关、按哪个键都关，右上角按钮单独一个开关；
/// 两项都没勾时不响应任何用户操作，<b>但 Esc 永远有效</b>——应用卡死时总得有条路退出来。
/// </remarks>
internal class DutySweepWindow : Window
{
    private const int RevealMs = 900;
    private const int CollapseMs = 620;

    /// <summary>文字比像素场晚多少（相对于揭示时长）开始浮现。</summary>
    private const double TextDelayFactor = 0.62;

    private static DutySweepWindow? _instance;

    /// <summary>这个窗口是为哪个时间点的开始时间开的。用来判断「该不该轮到我来关」。</summary>
    private readonly TimeSpan _slotStart;

    private readonly UltraCodeField _field;
    private readonly StackPanel _body;
    private readonly DispatcherTimer? _autoClose;
    private bool _closing;

    /// <summary>点任意位置 / 按任意键就收场。没勾的话这些路径一条都不接。</summary>
    private readonly bool _closeOnInput;

    private TopmostEnforcer? _topmost;

    /// <summary>独占标记。开着的时候别的插件的置顶窗口会让位。</summary>
    private IDisposable? _exclusive;

    /// <summary>当前是否有整屏提醒开着。</summary>
    public static bool IsOpen => _instance is { _closing: false };

    /// <summary>
    /// 当前开着的窗口是不是为这个开始时间开的。
    /// </summary>
    /// <remarks>
    /// 时间段到 End 时要靠它判断「该关的是不是我这个窗」，避免顺手关掉用户的另一条提醒。
    /// </remarks>
    public static bool IsShowing(TimeSpan start) => _instance is { _closing: false } open && open._slotStart == start;

    public static void Popup(string title, DutySlot slot, DutySettings settings)
    {
        CloseCurrent();
        _instance = new DutySweepWindow(title, slot, settings);
        _instance.Show();
        _instance.Activate();
    }

    public static void CloseCurrent()
    {
        var open = _instance;
        _instance = null;
        open?.Dismiss();
    }

    private DutySweepWindow(string title, DutySlot slot, DutySettings settings)
    {
        _slotStart = slot.Start;
        _closeOnInput = settings.CanCloseByClick;

        SystemDecorations = SystemDecorations.None;
        Background = null;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        ShowInTaskbar = false;
        Topmost = true;
        CanResize = false;
        ShowActivated = true;

        UltraCodePalette.Refresh();
        var ink = UltraCodePalette.ForegroundColor;

        _field = new UltraCodeField
        {
            CellSize = 6,
            // 整屏的格子数是卡片浮窗的几十倍，边长要能单独调粗来压计算量。
            CellSizeOverride = Math.Clamp(settings.SweepCellSize, 3, 40),
            // 整屏的面积大得多，像素层压低一点，否则大标题会被闪烁盖过去。
            PixelOpacity = 0.5,
            RevealDurationMs = RevealMs,
            CollapseDurationMs = CollapseMs,
            ClipToBounds = true
        };
        _field.Collapsed += (_, _) => Dispatcher.UIThread.Post(FinishClose);

        _body = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 0,
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

        _body.Children.Add(new TextBlock
        {
            Text = slot.RangeText,
            FontSize = 30,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(ink, 0.68),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 10)
        });

        _body.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 96,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(ink),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center
        });

        // 名单铺成两列，人多的时候也不会拉成很长一条。
        var list = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = 1180,
            Margin = new Thickness(0, 44, 0, 0)
        };

        foreach (var item in slot.Items)
        {
            list.Children.Add(new Border
            {
                Margin = new Thickness(12, 8),
                Padding = new Thickness(20, 12),
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(ink, 0.10),
                BorderBrush = new SolidColorBrush(ink, 0.28),
                BorderThickness = new Thickness(1),
                Child = new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = item.Project,
                            FontSize = 20,
                            Foreground = new SolidColorBrush(ink, 0.66),
                            HorizontalAlignment = HorizontalAlignment.Center
                        },
                        new TextBlock
                        {
                            Text = item.PeopleText,
                            FontSize = 38,
                            FontWeight = FontWeight.Bold,
                            Foreground = new SolidColorBrush(ink),
                            HorizontalAlignment = HorizontalAlignment.Center
                        }
                    }
                }
            });
        }

        _body.Children.Add(list);

        var root = new Panel();
        root.Children.Add(_field);
        root.Children.Add(_body);

        if (BuildCloseButton(settings, ink) is { } closeButton)
        {
            root.Children.Add(closeButton);
        }

        Content = root;

        // 单时刻才靠「停留多久」自动消失；时间段由 Service 到点关窗，这里不起定时器。
        // 自动消失不依赖有没有手动关闭手段：两个关闭方式都不勾，只是「别被误触/随手点掉」，
        // 不是「一直挂着」。教室大屏往往没有键盘，Esc 那条兜底根本用不上，到点必须自己收。
        if (!slot.IsRange)
        {
            _autoClose = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(Math.Clamp(settings.HoldSeconds, 3, 600))
            };
            _autoClose.Tick += (_, _) => Dismiss();
        }

        // 点哪儿都能收、按键也能收——但只在用户勾了「点击空白处或按任意键关闭」时。
        // Esc 不并进来：它是不看设置的那条救急退路，见 OnKeyDown。
        if (_closeOnInput)
        {
            PointerPressed += (_, _) => Dismiss();
            KeyDown += (_, _) => Dismiss();
        }
    }

    /// <summary>
    /// 右上角的关闭按钮。压在像素场上，用前景色上色。
    /// </summary>
    /// <remarks>
    /// 48×48 是因为整屏提醒通常投在教室大屏上，坐着看距离远，按钮小了根本点不准。
    /// 样式和卡片窗口保持一致：默认是淡雾底 + 细边，悬停提亮，别让一个实心色块
    /// 破坏整屏像素场的观感。
    /// </remarks>
    private Button? BuildCloseButton(DutySettings settings, Color ink)
    {
        if (!settings.CanCloseByButton)
        {
            return null;
        }

        var button = new Button
        {
            Content = new FluentIcon("\uE711", 22)
            {
                Foreground = new SolidColorBrush(ink, 0.86)
            },
            Width = 48,
            Height = 48,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(24),
            Background = new SolidColorBrush(ink, 0.08),
            BorderBrush = new SolidColorBrush(ink, 0.22),
            BorderThickness = new Thickness(1.5),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 44, 44, 0),
            Cursor = new Cursor(StandardCursorType.Hand)
        };

        var baseBg = new SolidColorBrush(ink, 0.08);
        var hoverBg = new SolidColorBrush(ink, 0.20);
        var pressedBg = new SolidColorBrush(ink, 0.30);
        button.PointerEntered += (_, _) => button.Background = hoverBg;
        button.PointerExited += (_, _) => button.Background = baseBg;
        button.PointerPressed += (_, _) => button.Background = pressedBg;
        button.PointerReleased += (_, _) => button.Background = hoverBg;

        button.Click += (_, _) => Dismiss();
        return button;
    }

    /// <summary>
    /// Esc 永远能关，不受「关闭方式」设置影响。
    /// </summary>
    /// <remarks>
    /// 用户把两个关闭方式都取消勾选，换来的语义是「别自己消失、别被误触关掉」，
    /// 不是「连卡死时都没法退出」。这一条是那种取舍里唯一不能动的退路。
    /// </remarks>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Dismiss();
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is not null)
        {
            Position = screen.Bounds.Position;
            var scaling = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
            Width = screen.Bounds.Width / scaling;
            Height = screen.Bounds.Height / scaling;
        }

        // 要盖住一切，包括别的置顶窗口；但它是可激活的——需要接键盘。
        // exclusive: 抽人悬浮钮那些会主动让位，不跟这个整屏提醒抢置顶。
        _exclusive = ExclusiveOverlay.Acquire();
        _topmost = new TopmostEnforcer(this, TimeSpan.FromMilliseconds(400), preventActivation: false,
            exclusive: true);
        _topmost.Attach();

        _field.Open();

        var delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(RevealMs * TextDelayFactor) };
        delay.Tick += (_, _) =>
        {
            delay.Stop();
            _body.Opacity = 1;
        };
        delay.Start();

        _autoClose?.Start();
    }

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

        // 兜底：像素场没在跑时 Collapsed 不会来，别把整屏窗口留在屏幕上。
        var safety = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(CollapseMs + 260) };
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
        // 不 Dispose 的话这个 1Hz 的置顶重申定时器会一直跑下去，每次提醒漏一个。
        _topmost?.Dispose();
        _topmost = null;
        _exclusive?.Dispose();
        _exclusive = null;
    }

    /// <summary>兜底清理：窗口被宿主直接关掉时也要把系统和独占资源放掉。</summary>
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
