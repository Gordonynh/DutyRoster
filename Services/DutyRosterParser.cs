using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using ClassIsland.DutyRoster.Models;

namespace ClassIsland.DutyRoster.Services;

/// <summary>
/// 值日表纯文本格式的解析器。
/// </summary>
/// <remarks>
/// 格式长这样：
/// <code>
/// [设置]
/// 轮换起始 = 2026-08-24
/// 轮换周期 = 7
///
/// [第1批]
/// 跳过 = 周六
///
/// 周一
/// 08:40-08:50  擦黑板：张三；倒垃圾：李四
/// 11:30-11:40  擦黑板：张三；扫地：王五 赵六
///
/// 周二~周五 周日
/// 08:40  擦黑板：李四
/// </code>
/// 设计上刻意迁就手改：<c>#</c> 开头是注释、空行随便加、
/// 中英文标点都认（<c>：:</c> 和 <c>；;</c>）、人名之间空格顿号逗号都行。
/// 解析不了的行不会让整张表失败，只记一条警告，方便在设置页里指出来。
/// </remarks>
public static class DutyRosterParser
{
    private static readonly Regex SectionPattern = new(@"^\[(?<name>.+?)\]$", RegexOptions.Compiled);
    private static readonly Regex KeyValuePattern = new(@"^(?<key>[^=]+?)\s*=\s*(?<value>.*)$", RegexOptions.Compiled);
    private static readonly Regex SlotPattern = new(
        @"^(?<start>\d{1,2}[:：]\d{2})\s*(?:[-–—~]\s*(?<end>\d{1,2}[:：]\d{2}))?\s+(?<body>.+)$",
        RegexOptions.Compiled);

    private static readonly Dictionary<string, DayOfWeek> WeekdayNames = new(StringComparer.Ordinal)
    {
        ["周一"] = DayOfWeek.Monday, ["星期一"] = DayOfWeek.Monday, ["礼拜一"] = DayOfWeek.Monday,
        ["周二"] = DayOfWeek.Tuesday, ["星期二"] = DayOfWeek.Tuesday, ["礼拜二"] = DayOfWeek.Tuesday,
        ["周三"] = DayOfWeek.Wednesday, ["星期三"] = DayOfWeek.Wednesday, ["礼拜三"] = DayOfWeek.Wednesday,
        ["周四"] = DayOfWeek.Thursday, ["星期四"] = DayOfWeek.Thursday, ["礼拜四"] = DayOfWeek.Thursday,
        ["周五"] = DayOfWeek.Friday, ["星期五"] = DayOfWeek.Friday, ["礼拜五"] = DayOfWeek.Friday,
        ["周六"] = DayOfWeek.Saturday, ["星期六"] = DayOfWeek.Saturday, ["礼拜六"] = DayOfWeek.Saturday,
        ["周日"] = DayOfWeek.Sunday, ["周天"] = DayOfWeek.Sunday,
        ["星期日"] = DayOfWeek.Sunday, ["星期天"] = DayOfWeek.Sunday, ["礼拜日"] = DayOfWeek.Sunday
    };

    /// <summary>星期在「周一~周五」这种范围写法里的顺序。中文习惯周一打头。</summary>
    private static readonly DayOfWeek[] WeekOrder =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday
    ];

    public static DutySchedule Parse(string text)
    {
        var schedule = new DutySchedule();
        DutyGroup? group = null;
        List<DayOfWeek> currentDays = [];
        var inSettings = false;
        var lineNumber = 0;

        foreach (var rawLine in text.Split('\n'))
        {
            lineNumber++;
            var line = StripComment(rawLine);
            if (line.Length == 0)
            {
                continue;
            }

            // [设置] 或 [批次名]
            var section = SectionPattern.Match(line);
            if (section.Success)
            {
                var name = section.Groups["name"].Value.Trim();
                if (name is "设置" or "配置" or "settings" or "Settings")
                {
                    inSettings = true;
                    group = null;
                }
                else
                {
                    inSettings = false;
                    group = new DutyGroup { Name = name };
                    schedule.Groups.Add(group);
                }

                currentDays = [];
                continue;
            }

            // key = value
            var kv = KeyValuePattern.Match(line);
            if (kv.Success && !SlotPattern.IsMatch(line))
            {
                ApplyKeyValue(schedule, group, inSettings, kv.Groups["key"].Value.Trim(),
                    kv.Groups["value"].Value.Trim(), lineNumber);
                continue;
            }

            // 星期行
            var days = TryParseWeekdays(line);
            if (days is { Count: > 0 })
            {
                currentDays = days;
                continue;
            }

            // 时段行
            var slotMatch = SlotPattern.Match(line);
            if (!slotMatch.Success)
            {
                schedule.Warnings.Add($"第 {lineNumber} 行看不懂，已跳过：{line}");
                continue;
            }

            if (group is null)
            {
                // 没写 [批次] 就直接排班也允许，自动建一个默认批次。
                group = new DutyGroup { Name = "值日" };
                schedule.Groups.Add(group);
            }

            if (currentDays.Count == 0)
            {
                schedule.Warnings.Add($"第 {lineNumber} 行的时段前面没有星期行，已跳过：{line}");
                continue;
            }

            if (!TryParseTime(slotMatch.Groups["start"].Value, out var start))
            {
                schedule.Warnings.Add($"第 {lineNumber} 行的开始时间无效：{slotMatch.Groups["start"].Value}");
                continue;
            }

            // 结束时间解析得到但**故意不用**：提醒只发生在开始那一刻。
            // 之所以还认这个写法，是为了让从旧数据转过来的 08:40-08:50 直接能用。
            var items = ParseItems(slotMatch.Groups["body"].Value, lineNumber, schedule.Warnings);
            if (items.Count == 0)
            {
                continue;
            }

            var slot = new DutySlot(start, items);
            foreach (var day in currentDays)
            {
                if (!group.Days.TryGetValue(day, out var list))
                {
                    group.Days[day] = list = [];
                }

                list.Add(slot);
            }
        }

        // 每天按时间排好序，后面找「下一个时段」就不用再排。
        foreach (var slots in schedule.Groups.SelectMany(g => g.Days.Values))
        {
            slots.Sort((a, b) => a.Start.CompareTo(b.Start));
        }

        return schedule;
    }

    private static void ApplyKeyValue(DutySchedule schedule, DutyGroup? group, bool inSettings,
        string key, string value, int lineNumber)
    {
        switch (key)
        {
            case "轮换起始" or "轮换开始" or "起始日期":
                if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    schedule.RotationStart = date.Date;
                }
                else
                {
                    schedule.Warnings.Add($"第 {lineNumber} 行的日期看不懂：{value}");
                }

                break;

            case "轮换周期" or "周期":
                if (int.TryParse(value, out var days) && days > 0)
                {
                    schedule.RotationPeriodDays = days;
                }
                else
                {
                    schedule.Warnings.Add($"第 {lineNumber} 行的周期要是正整数：{value}");
                }

                break;

            case "跳过" or "跳过星期" or "不值日":
                var skipped = TryParseWeekdays(value);
                if (group is not null && skipped is { Count: > 0 })
                {
                    foreach (var day in skipped)
                    {
                        group.SkipDays.Add(day);
                    }
                }
                else if (skipped is null or { Count: 0 })
                {
                    schedule.Warnings.Add($"第 {lineNumber} 行的「跳过」没认出星期：{value}");
                }

                break;

            default:
                if (!inSettings)
                {
                    schedule.Warnings.Add($"第 {lineNumber} 行有个不认识的设置项「{key}」，已忽略。");
                }

                break;
        }
    }

    /// <summary>
    /// 解析星期行。支持「周一」「周一 周三」「周一、周三」「周一~周五」「周一~周五 周日」。
    /// </summary>
    /// <returns>认不出任何星期时返回 <c>null</c>，表示这行不是星期行。</returns>
    private static List<DayOfWeek>? TryParseWeekdays(string line)
    {
        var tokens = line.Split([' ', '\t', '、', ',', '，'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return null;
        }

        var result = new List<DayOfWeek>();
        foreach (var token in tokens)
        {
            var parts = token.Split(['~', '～', '-', '–', '—', '至'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 &&
                WeekdayNames.TryGetValue(parts[0].Trim(), out var from) &&
                WeekdayNames.TryGetValue(parts[1].Trim(), out var to))
            {
                var i = Array.IndexOf(WeekOrder, from);
                var j = Array.IndexOf(WeekOrder, to);
                if (i < 0 || j < 0)
                {
                    return null;
                }

                // 支持跨周末回绕，比如「周五~周一」。
                for (var step = 0; step <= ((j - i) + 7) % 7; step++)
                {
                    result.Add(WeekOrder[(i + step) % 7]);
                }

                continue;
            }

            if (!WeekdayNames.TryGetValue(token.Trim(), out var single))
            {
                return null;
            }

            result.Add(single);
        }

        return result.Distinct().ToList();
    }

    /// <summary>
    /// 解析时段正文，比如 <c>擦黑板：张三；倒垃圾：李四</c>。
    /// </summary>
    private static List<DutyItem> ParseItems(string body, int lineNumber, List<string> warnings)
    {
        var items = new List<DutyItem>();
        foreach (var chunk in body.Split([';', '；'], StringSplitOptions.RemoveEmptyEntries))
        {
            var piece = chunk.Trim();
            if (piece.Length == 0)
            {
                continue;
            }

            var sep = piece.IndexOfAny([':', '：']);
            if (sep <= 0)
            {
                warnings.Add($"第 {lineNumber} 行的「{piece}」缺少冒号，应该写成「项目：人名」。");
                continue;
            }

            var project = piece[..sep].Trim();
            var people = piece[(sep + 1)..]
                .Split([' ', '\t', '、', ',', '，', '/'], StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .ToList();

            if (project.Length == 0 || people.Count == 0)
            {
                warnings.Add($"第 {lineNumber} 行的「{piece}」缺项目名或人名。");
                continue;
            }

            items.Add(new DutyItem(project, people));
        }

        return items;
    }

    private static bool TryParseTime(string text, out TimeSpan time)
    {
        time = default;
        var parts = text.Replace('：', ':').Split(':');
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], out var hour) ||
            !int.TryParse(parts[1], out var minute) ||
            hour is < 0 or > 23 || minute is < 0 or > 59)
        {
            return false;
        }

        time = new TimeSpan(hour, minute, 0);
        return true;
    }

    private static string StripComment(string line)
    {
        var hash = line.IndexOf('#');
        return (hash >= 0 ? line[..hash] : line).Trim();
    }
}
