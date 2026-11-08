using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using ClassIsland.Core.Models.Notification;
using ClassIsland.DutyRoster.Models;
using ClassIsland.DutyRoster.Views;
using ClassIsland.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClassIsland.DutyRoster.Services;

/// <summary>
/// 插件主体：盯着时间，到点把值日安排弹出来。
/// </summary>
public class DutyRosterService : IHostedService
{
    /// <summary>
    /// 到点判定的容差。定时器是 5 秒一跳，容差取 90 秒，
    /// 这样即使某一跳被卡住（比如系统休眠刚醒），也不会整个错过一个时间点。
    /// </summary>
    private static readonly TimeSpan FireTolerance = TimeSpan.FromSeconds(90);

    private readonly string _rosterPath;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };

    private DutySchedule _schedule = new();
    private FileSystemWatcher? _watcher;
    private NotificationRequest? _lastRequest;

    /// <summary>已经提醒过的时间点，避免同一个点重复弹。跨天时清空。</summary>
    /// <remarks>
    /// 键是开始时间。同一个开始时间只弹一次就够——同一分钟的多个项目在表里本来就该写成同一个时间点，
    /// 真写成两个时间点（不同项目）时，弹窗也只有一块地方，后一个替换前一个是正常的。
    /// </remarks>
    private readonly HashSet<TimeSpan> _firedToday = [];
    private DateTime _firedDate = DateTime.MinValue;

    /// <summary>
    /// 正在显示的时间段：（开始时间，结束时间）。
    /// </summary>
    /// <remarks>
    /// 用列表而不是 <c>Dictionary&lt;TimeSpan, TimeSpan&gt;</c>：值日表里同一分钟写两条不同项目是合法的，
    /// 用字典做键的话后一条会把前一条覆盖掉，那个时间段的关窗就丢了。
    /// <para/>
    /// 到 End 时只关「为这个时间段开的」那个窗——靠 <c>IsShowing(start)</c> 比对，
    /// 免得用户手动关掉以后又被顺手收掉一条无关的提醒。
    /// </remarks>
    private readonly List<(TimeSpan Start, TimeSpan End)> _activeRanges = [];

    public DutyRosterService(string pluginConfigFolder)
    {
        _rosterPath = Path.Combine(pluginConfigFolder, "值日表.txt");
    }

    /// <summary>当前解析出来的值日表。设置页要用。</summary>
    public DutySchedule Schedule => _schedule;

    /// <summary>值日表文件路径。</summary>
    public string RosterPath => _rosterPath;

    /// <summary>值日表里出现过的全部项目名。设置页拿来当「大扫除项目」的候选。</summary>
    public List<string> AllProjectNames =>
        _schedule.Groups
            .SelectMany(g => g.Days.Values)
            .SelectMany(day => day)
            .SelectMany(slot => slot.Items)
            .Select(item => item.Project)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>值日表重新载入后触发。</summary>
    public event EventHandler? ScheduleChanged;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        EnsureRosterFile();
        Reload();
        StartWatching();

        // 防抖定时器要在 UI 线程上建，才能挂在 UI 的 Dispatcher 上。
        Dispatcher.UIThread.Post(EnsureReloadDebounce, DispatcherPriority.Background);

        _timer.Tick += (_, _) => Tick();
        Dispatcher.UIThread.Post(() => _timer.Start(), DispatcherPriority.Background);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer.Stop();
        Dispatcher.UIThread.Post(() =>
        {
            _reloadDebounce?.Stop();
            DutyPopupWindow.CloseCurrent();
            DutySweepWindow.CloseCurrent();
        });
        DisposeWatcher();
        return Task.CompletedTask;
    }

    #region 值日表文件

    private void EnsureRosterFile()
    {
        if (File.Exists(_rosterPath))
        {
            return;
        }

        var dir = Path.GetDirectoryName(_rosterPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(_rosterPath, DefaultRoster, new UTF8Encoding(false));
    }

    /// <summary>重新读取并解析值日表。</summary>
    public void Reload()
    {
        try
        {
            _schedule = File.Exists(_rosterPath)
                ? DutyRosterParser.Parse(File.ReadAllText(_rosterPath, Encoding.UTF8))
                : new DutySchedule();
        }
        catch (IOException)
        {
            // 多半是保存的瞬间被占用了，保留上一次解析结果，下次变更再读。
            return;
        }

        ScheduleChanged?.Invoke(this, EventArgs.Empty);
    }

    private void StartWatching()
    {
        var dir = Path.GetDirectoryName(_rosterPath);
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        try
        {
            _watcher = new FileSystemWatcher(dir, Path.GetFileName(_rosterPath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            _watcher.Changed += OnFileTouched;
            _watcher.Created += OnFileTouched;
            _watcher.Renamed += OnFileTouched;
        }
        catch (Exception)
        {
            // 监视不了就算了，设置页里还有「重新载入」。
        }
    }

    private void OnFileTouched(object sender, FileSystemEventArgs e)
    {
        // 记事本保存会连着触发好几次，还会在文件真正写完之前就来通知。
        // 这里不能用 Thread.Sleep：这是 watcher 的线程池回调，堵住它只是白占一个线程，
        // 阻塞期间后续事件还会排队甚至被丢掉。改成重置一个防抖定时器，安静下来才读。
        Dispatcher.UIThread.Post(() =>
        {
            _reloadDebounce?.Stop();
            _reloadDebounce?.Start();
        });
    }

    /// <summary>
    /// 值日表文件变动后的防抖定时器：每次事件都重置，静默 300ms 才真正重读。
    /// </summary>
    /// <remarks>
    /// 必须在 UI 线程上用——<see cref="DispatcherTimer"/> 绑定创建时的那个 Dispatcher。
    /// </remarks>
    private DispatcherTimer? _reloadDebounce;

    private void EnsureReloadDebounce()
    {
        if (_reloadDebounce is not null)
        {
            return;
        }

        _reloadDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _reloadDebounce.Tick += (_, _) =>
        {
            _reloadDebounce?.Stop();
            Reload();
        };
    }

    private void DisposeWatcher()
    {
        if (_watcher is null)
        {
            return;
        }

        _watcher.EnableRaisingEvents = false;
        _watcher.Changed -= OnFileTouched;
        _watcher.Created -= OnFileTouched;
        _watcher.Renamed -= OnFileTouched;
        _watcher.Dispose();
        _watcher = null;
    }

    #endregion

    #region 定时判断

    private void Tick()
    {
        var settings = DutySettings.Current;
        if (!settings.IsEnabled || _schedule.IsEmpty)
        {
            return;
        }

        var now = DateTime.Now;
        if (now.Date != _firedDate)
        {
            _firedDate = now.Date;
            _firedToday.Clear();
            _activeRanges.Clear();
        }

        var slots = _schedule.SlotsOn(now.Date);
        if (slots.Count == 0)
        {
            return;
        }

        // 提前分钟数（往前）和触发延迟秒数（往后）一起折算到「现在等于表上的几点」。
        // 延迟默认 26 秒：表里写的是整分钟的下课点，铃响完、人站起来了再提醒，
        // 比掐着整点弹更容易被真的看见。
        var lead = TimeSpan.FromMinutes(Math.Max(0, settings.LeadMinutes));
        var delay = TimeSpan.FromSeconds(Math.Max(0, settings.FireDelaySeconds));
        var target = now.TimeOfDay + lead - delay;

        LeaveFinishedRanges(target);

        foreach (var slot in slots)
        {
            if (_firedToday.Contains(slot.Start))
            {
                continue;
            }

            var delta = target - slot.Start;
            // 只在「刚过点」的一小段窗口内触发。晚太多就当错过了，不补弹——
            // 中午打开电脑不该把上午所有时间点一次性全弹出来。
            if (delta < TimeSpan.Zero || delta > FireTolerance)
            {
                continue;
            }

            _firedToday.Add(slot.Start);

            // 这里**不能 return**：同一分钟有好几个时间点（不同项目分两行写）时，
            // 一 return 就只弹第一个，后面的永远轮不到。
            Enter(slot, settings);
        }
    }

    /// <summary>进入一个时间点：按设置的形式提醒，时间段还要记下来等 End 到了关窗。</summary>
    private void Enter(DutySlot slot, DutySettings settings)
    {
        if (slot.IsRange)
        {
            _activeRanges.Add((slot.Start, slot.End!.Value));
        }

        Fire(slot, settings);
    }

    /// <summary>
    /// 到点的段落关窗。
    /// </summary>
    /// <remarks>
    /// <b>这条不看关闭方式设置。</b><c>End</c> 属于值日表内容（「这个时间段有人值日」），
    /// 不是用户主动关提醒的动作——用户两项都不勾表示「别自己消失、别被误触关掉」，
    /// 不是「把提醒永远钉在屏幕上」。时段本来就给出了明确的结束时间。
    /// </remarks>
    private void LeaveFinishedRanges(TimeSpan target)
    {
        if (_activeRanges.Count == 0)
        {
            return;
        }

        List<(TimeSpan Start, TimeSpan End)>? finished = null;
        foreach (var range in _activeRanges)
        {
            // 过了 End 就算结束。这里不设上限，哪怕中途电脑睡了一觉，
            // 醒来后也该把「早就该关的」那个时段收掉，而不是让它一直挂着。
            if (target >= range.End)
            {
                (finished ??= []).Add(range);
            }
        }

        if (finished is null)
        {
            return;
        }

        foreach (var range in finished)
        {
            _activeRanges.Remove(range);
        }

        // 只关「为这个时间段开的」那个窗。用户可能中途手动关掉、又弹了另一条提醒，
        // 这时候把无关的窗一起收掉，就显得插件根本没记住自己开过什么。
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var range in finished)
            {
                if (DutyPopupWindow.IsShowing(range.Start))
                {
                    DutyPopupWindow.CloseCurrent();
                }

                if (DutySweepWindow.IsShowing(range.Start))
                {
                    DutySweepWindow.CloseCurrent();
                }
            }
        });
    }

    /// <summary>
    /// 按这个时间点该用的形式提醒。
    /// </summary>
    /// <remarks>
    /// 大扫除（带「扫地 / 拖地」的时间点）和日常时间点各有各的形式设置，
    /// 见 <see cref="DutySettings.StyleFor"/>。
    /// </remarks>
    private void Fire(DutySlot slot, DutySettings settings)
    {
        var style = settings.StyleFor(slot);
        if (style == DutyStyle.Off)
        {
            return;
        }

        if (style == DutyStyle.NotificationOnly)
        {
            SendNotification(slot);
            return;
        }

        ShowPopup(slot, settings, style == DutyStyle.Fullscreen);

        if (settings.AlsoSendClassIslandNotification)
        {
            SendNotification(slot);
        }
    }

    /// <summary>弹窗。<paramref name="fullscreen"/> 为 true 时走整屏那套。</summary>
    public void ShowPopup(DutySlot slot, DutySettings settings, bool fullscreen, string? title = null)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (fullscreen)
            {
                // 整屏和卡片是互斥的两个窗口，先把另一个收掉，免得叠在一起。
                DutyPopupWindow.CloseCurrent();
                DutySweepWindow.Popup(title ?? settings.SweepTitle, slot, settings);
            }
            else
            {
                DutySweepWindow.CloseCurrent();
                DutyPopupWindow.Popup(title ?? "该值日了", slot, settings, ResolveAccent());
            }

            if (settings.PlaySound)
            {
                PlayChime();
            }
        });
    }

    /// <summary>
    /// 播一声系统提示音。
    /// </summary>
    /// <remarks>
    /// <see cref="Console.Beep()"/> 在非 Windows 平台上是空实现或直接抛
    /// <see cref="PlatformNotSupportedException"/>，所以先判平台再叫——
    /// 提醒本身不该因为一声提示音没响出来就整个失败。
    /// </remarks>
    private static void PlayChime()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            Console.Beep();
        }
        catch (Exception)
        {
            // 有些环境没有蜂鸣器，忽略。
        }
    }

    #endregion

    /// <summary>
    /// 立刻弹一次，用于设置页里的「试一下」。
    /// </summary>
    /// <param name="fullscreen">
    /// true 时强制走整屏那套并挑一个大扫除时间点，false 时强制走卡片并挑一个日常时间点。
    /// 传 null 就按当前时间该用的形式来。
    /// </param>
    public void PreviewNow(bool? fullscreen = null)
    {
        var settings = DutySettings.Current;
        var slots = _schedule.SlotsOn(DateTime.Now.Date);
        if (slots.Count == 0)
        {
            return;
        }

        // 预览要能看清目标形式，所以挑一个真的属于那一类的时间点，
        // 否则「试弹整屏」挑到一个只有擦黑板的时间点，看到的就不是实际效果。
        var pool = fullscreen switch
        {
            true => slots.Where(settings.IsSweep).ToList(),
            false => slots.Where(x => !settings.IsSweep(x)).ToList(),
            null => slots.ToList()
        };

        if (pool.Count == 0)
        {
            pool = slots.ToList();
        }

        // 优先挑一个正在生效的时间段来预览——这样能看到「一直显示到结束」的实际效果。
        // 没有正在生效的，再退回「下一个 / 最后一个」的老挑法。
        var now = DateTime.Now.TimeOfDay;
        var slot = pool.FirstOrDefault(x => x.IsRange && x.Start <= now && now < x.End!.Value)
                   ?? pool.FirstOrDefault(x => x.Start >= now)
                   ?? pool[^1];

        var useFullscreen = fullscreen ?? settings.StyleFor(slot) == DutyStyle.Fullscreen;
        ShowPopup(slot, settings, useFullscreen,
            useFullscreen ? settings.SweepTitle : "值日安排（预览）");
    }

    private void SendNotification(DutySlot slot)
    {
        var provider = IAppHost.Host?.Services
            .GetServices<IHostedService>()
            .OfType<DutyNotificationProvider>()
            .FirstOrDefault();
        if (provider is null)
        {
            return;
        }

        _lastRequest?.Cancel();

        var text = string.Join("    ", slot.Items.Select(x => $"{x.Project} {x.PeopleText}"));
        var request = new NotificationRequest
        {
            MaskContent = NotificationContent.CreateTwoIconsMask("该值日了", hasRightIcon: false, factory: x =>
            {
                x.Duration = TimeSpan.FromSeconds(1.5);
                x.IsSpeechEnabled = false;
            }),
            OverlayContent = NotificationContent.CreateSimpleTextContent(text, factory: x =>
            {
                x.Duration = TimeSpan.FromSeconds(12);
                x.IsSpeechEnabled = false;
            })
        };

        _lastRequest = request;
        Dispatcher.UIThread.Post(() => provider.ShowNotification(request));
    }

    /// <summary>取应用当前主题色。</summary>
    private static Color ResolveAccent()
    {
        if (Application.Current is { } app &&
            app.TryFindResource("SystemAccentColor", app.ActualThemeVariant, out var value))
        {
            return value switch
            {
                Color c => c,
                SolidColorBrush b => b.Color,
                _ => DefaultAccent
            };
        }

        return DefaultAccent;
    }

    private static readonly Color DefaultAccent = Color.FromRgb(0x5B, 0x8D, 0xEF);

    private const string DefaultRoster = """
        # 值日表 —— 用记事本就能改，保存后自动生效
        #
        #   星期行：  周一        或  周一~周五 周日      （下面的时间点都属于这些天）
        #   时间行：  08:40  擦黑板：张三；倒垃圾：李四
        #
        #   · 以 # 开头的行和空行都会忽略
        #   · 中英文冒号分号都认，人名之间空格、顿号、逗号、斜杠都行
        #   · 时间只写到分钟。铃响后再等几秒才弹，那个偏移在设置页里调
        #   · 带「扫地 / 拖地」的时间点算大扫除，默认用整屏提醒；其余用卡片提醒

        [设置]
        轮换起始 = 2026-01-05
        轮换周期 = 7          # 天。有多个 [批次] 时按这个周期依次轮换

        [第1批]
        跳过 = 周六 周日

        周一~周五
        08:40  擦黑板：张三；倒垃圾：李四；清理讲台：王五
        11:30  擦黑板：张三；倒垃圾：李四；清理讲台：王五；扫地：赵六 钱七；拖地：孙八
        """;
}
