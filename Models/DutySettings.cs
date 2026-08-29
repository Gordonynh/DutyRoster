using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassIsland.DutyRoster.Models;

/// <summary>弹窗出现在屏幕的什么位置。</summary>
public enum PopupPlacement
{
    /// <summary>屏幕正中。最难忽略。</summary>
    Center,

    /// <summary>顶部居中，压在 ClassIsland 主界面下方。</summary>
    Top,

    /// <summary>右下角。最不打扰，但也最容易被无视。</summary>
    BottomRight
}

/// <summary>弹窗尺寸档位。</summary>
public enum PopupScale
{
    Compact,
    Normal,
    Large
}

/// <summary>值日提醒用哪种形式呈现。</summary>
public enum DutyStyle
{
    /// <summary>屏幕中央的卡片浮窗，背景是 UltraCode 像素场。</summary>
    Card,

    /// <summary>整屏 UltraCode 像素场从右往左铺满，配一行大标题。拦得住人，但也最打扰。</summary>
    Fullscreen,

    /// <summary>只发一条 ClassIsland 主界面提醒，不弹窗。</summary>
    NotificationOnly,

    /// <summary>这一类不提醒。</summary>
    Off
}

/// <summary>
/// 值日提醒的设置。
/// </summary>
public partial class DutySettings : ObservableObject
{
    public static DutySettings Current { get; private set; } = new();

    private static string? _configPath;

    /// <summary>总开关。</summary>
    [ObservableProperty] private bool _isEnabled = true;

    /// <summary>提前几分钟提醒。0 = 到点提醒。</summary>
    [ObservableProperty] private int _leadMinutes;

    /// <summary>弹窗停留秒数。到点自动消失；点屏幕任意位置也会立刻关掉。</summary>
    [ObservableProperty] private int _holdSeconds = 17;

    /// <summary>
    /// 在时间点之后再延迟几秒才弹。
    /// </summary>
    /// <remarks>
    /// 默认 26 秒。表里写的是整分钟的下课点，铃响完、人站起来了再提醒，
    /// 比掐着整点弹更容易被真的看见。
    /// </remarks>
    [ObservableProperty] private int _fireDelaySeconds = 26;

    /// <summary>弹窗位置。</summary>
    [ObservableProperty] private PopupPlacement _placement = PopupPlacement.Center;

    /// <summary>弹窗大小。</summary>
    [ObservableProperty] private PopupScale _scale = PopupScale.Large;

    /// <summary>
    /// 日常时间点（擦黑板 / 倒垃圾 / 清理讲台）用哪种提醒形式。
    /// </summary>
    [ObservableProperty] private DutyStyle _frequentStyle = DutyStyle.Card;

    /// <summary>
    /// 大扫除时间点用哪种提醒形式。
    /// </summary>
    /// <remarks>
    /// 默认整屏。一天三次的大扫除是全班的事，卡片浮窗那点面积压不住教室里的动静。
    /// </remarks>
    [ObservableProperty] private DutyStyle _sweepStyle = DutyStyle.Fullscreen;

    /// <summary>
    /// 命中这些项目名的时间点算「大扫除」，走 <see cref="SweepStyle"/>。
    /// </summary>
    /// <remarks>
    /// 按项目名而不是按时刻判断：值日表改了时间也不用回来改设置。
    /// </remarks>
    [ObservableProperty] private System.Collections.Generic.List<string> _sweepProjects = ["扫地", "拖地"];

    /// <summary>整屏提醒的大标题。</summary>
    [ObservableProperty] private string _sweepTitle = "请值日生打扫";

    /// <summary>
    /// 整屏提醒上像素场的格子边长。
    /// </summary>
    /// <remarks>
    /// 只影响铺满整屏的大扫除提醒，卡片浮窗那个不受影响（那个面积小，无所谓）。
    /// 每帧开销和格子数成正比，格子数是 <c>宽 × 高 ÷ 边长²</c>——<b>边长翻倍，开销降到四分之一</b>。
    /// 默认 10；教室那种老机器可以再往上调。
    /// </remarks>
    [ObservableProperty] private double _sweepCellSize = 10;

    /// <summary>
    /// 是否在弹窗之外<b>额外</b>发一条 ClassIsland 提醒。
    /// </summary>
    /// <remarks>
    /// 主界面那条太窄，塞不下「谁做什么」这种多行内容，而且一晃就过去了，
    /// 所以主力始终是弹窗，这条只是补充。
    /// （<see cref="DutyStyle.NotificationOnly"/> 是另一回事：那是「只发提醒、不弹窗」。）
    /// </remarks>
    [ObservableProperty] private bool _alsoSendClassIslandNotification = true;

    /// <summary>弹窗时是否播一声提示音。</summary>
    [ObservableProperty] private bool _playSound = true;

    #region 读写

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static void Initialize(string pluginConfigFolder)
    {
        _configPath = Path.Combine(pluginConfigFolder, "settings.json");
        try
        {
            if (File.Exists(_configPath) &&
                JsonSerializer.Deserialize<DutySettings>(File.ReadAllText(_configPath), JsonOptions) is { } loaded)
            {
                Current = loaded;
            }
        }
        catch (Exception)
        {
            // 配置坏了就用默认值，不要因为一个 json 拦住整个插件。
        }

        Current.PropertyChanged += (_, _) => Current.Save();
    }

    public void Save()
    {
        if (_configPath is null)
        {
            return;
        }

        try
        {
            var dir = Path.GetDirectoryName(_configPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(_configPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception)
        {
            // 存不上就算了，下次再存。
        }
    }

    #endregion

    /// <summary>弹窗里项目名的字号。</summary>
    [JsonIgnore]
    public double ProjectFontSize => Scale switch
    {
        PopupScale.Compact => 17,
        PopupScale.Large => 30,
        _ => 23
    };

    /// <summary>弹窗里人名的字号。人名是重点，比项目名更大。</summary>
    [JsonIgnore]
    public double PersonFontSize => Scale switch
    {
        PopupScale.Compact => 22,
        PopupScale.Large => 40,
        _ => 30
    };

    /// <summary>弹窗宽度。</summary>
    [JsonIgnore]
    public double PopupWidth => Scale switch
    {
        PopupScale.Compact => 380,
        PopupScale.Large => 680,
        _ => 520
    };

    /// <summary>
    /// 这个时间点算不算大扫除。
    /// </summary>
    /// <remarks>
    /// 按<b>项目名</b>判断而不是按时刻：值日表里改了时间也不用回来改设置。
    /// 用户配的那张表里，一天三次大扫除（11:30 / 16:55 / 22:00）正好是
    /// 唯一带「扫地」「拖地」的三个时间点。
    /// </remarks>
    public bool IsSweep(DutySlot slot) =>
        slot.Items.Any(item => SweepProjects.Any(keyword =>
            !string.IsNullOrWhiteSpace(keyword) &&
            item.Project.Contains(keyword.Trim(), StringComparison.OrdinalIgnoreCase)));

    /// <summary>这个时间点该用哪种提醒形式。</summary>
    public DutyStyle StyleFor(DutySlot slot) => IsSweep(slot) ? SweepStyle : FrequentStyle;
}
