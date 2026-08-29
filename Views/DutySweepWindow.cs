using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
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
/// 老师想关掉它就只能等，反而更烦人。现在点屏幕任何位置、按任何键都立刻收场。
/// </remarks>
internal class DutySweepWindow : Window
{
    private const int RevealMs = 900;
    private const int CollapseMs = 620;

    /// <summary>文字比像素场晚多少（相对于揭示时长）开始浮现。</summary>
    private const double TextDelayFactor = 0.62;

    private static DutySweepWindow? _instance;

    private readonly UltraCodeField _field;
    private readonly StackPanel _body;
    private readonly DispatcherTimer _autoClose;
    private bool _closing;

    /// <summary>独占标记。开着的时候别的插件的置顶窗口会让位。</summary>
    private IDisposable? _exclusive;

    /// <summary>当前是否有整屏提醒开着。</summary>
    public static bool IsOpen => _instance is { _closing: false };

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
            Text = slot.TimeText,
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

        Content = new Panel { Children = { _field, _body } };

        _autoClose = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(Math.Clamp(settings.HoldSeconds, 3, 300))
        };
        _autoClose.Tick += (_, _) => Dismiss();

        // 点哪儿都能收，按键也能收。
        PointerPressed += (_, _) => Dismiss();
        KeyDown += (_, _) => Dismiss();
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
        new TopmostEnforcer(this, TimeSpan.FromMilliseconds(400), preventActivation: false,
            exclusive: true).Attach();

        _field.Open();

        var delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(RevealMs * TextDelayFactor) };
        delay.Tick += (_, _) =>
        {
            delay.Stop();
            _body.Opacity = 1;
        };
        delay.Start();

        _autoClose.Start();
    }

    private void Dismiss()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        _autoClose.Stop();
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
        _exclusive?.Dispose();
        _exclusive = null;
        if (ReferenceEquals(_instance, this))
        {
            _instance = null;
        }

        Close();
    }
}
