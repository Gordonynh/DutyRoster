using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ClassIsland.DutyRoster.Models;

namespace ClassIsland.DutyRoster.Services;

/// <summary>写回值日表失败。</summary>
/// <remarks>
/// 只在「用户点了保存、但没能安全落盘」时抛出。消息直接给用户看，
/// 所以要么说清哪一步失败，要么说清备份还在哪儿。
/// </remarks>
public sealed class DutyRosterWriteException : Exception
{
    public DutyRosterWriteException(string message) : base(message)
    {
    }

    public DutyRosterWriteException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>
/// 把 <see cref="DutySchedule"/> 写回纯文本值日表。
/// </summary>
/// <remarks>
/// 写回这件事本身是有损的：解析器不保留注释、空行和用户的书写形式
/// （见 <see cref="DutyRosterParser"/>）。所以这里只承诺三件事：
/// <list type="number">
/// <item>写出的一定能被 <see cref="DutyRosterParser"/> 读回成同一张表（写前写后各校验一次）；</item>
/// <item>连续内容相同的星期会合并成 <c>周一~周五</c> 一行，不会把文件撑大；</item>
/// <item>落盘前先备份成 <c>.bak</c>，落盘后读回来再核一遍，对不上就把备份还原回去。</item>
/// </list>
/// <para/>
/// <b>绝不自动调用。</b>只允许在用户显式点「保存到文件」时写——否则会和
/// <see cref="DutyRosterService"/> 的文件监视形成写回循环。
/// </remarks>
public static class DutyRosterWriter
{
    /// <summary>星期的固定顺序。中文习惯周一打头，写出来的星期行也按这个顺序。</summary>
    private static readonly DayOfWeek[] WeekOrder =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday
    ];

    private static readonly string[] WeekNames = ["周一", "周二", "周三", "周四", "周五", "周六", "周日"];

    /// <summary>备份文件的扩展名。不匹配文件监视的过滤器，所以不会触发重新载入。</summary>
    public const string BackupExtension = ".bak";

    /// <summary>
    /// 星期名字，比如 <c>周一</c>。
    /// </summary>
    public static string DayName(DayOfWeek day) => WeekNames[Array.IndexOf(WeekOrder, day)];

    /// <summary>
    /// 把一组星期写成一行文字：<c>周一~周五</c>、<c>周一 周三</c>、<c>周日</c>。
    /// </summary>
    /// <remarks>
    /// 连续 3 天及以上收成范围写法，2 天还是分开写——<c>周一~周二</c> 比
    /// <c>周一 周二</c> 并不更短，反而更难扫读。跨周末不回收（<c>周五~周一</c>），
    /// 那属于更少见的写法，保持可预测更重要。
    /// </remarks>
    public static string DescribeDays(IEnumerable<DayOfWeek> days)
    {
        var ordered = days
            .Distinct()
            .OrderBy(d => Array.IndexOf(WeekOrder, d))
            .ToList();
        if (ordered.Count == 0)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        var i = 0;
        while (i < ordered.Count)
        {
            var j = i;
            while (j + 1 < ordered.Count &&
                   Array.IndexOf(WeekOrder, ordered[j + 1]) == Array.IndexOf(WeekOrder, ordered[j]) + 1)
            {
                j++;
            }

            if (j - i + 1 >= 3)
            {
                parts.Add($"{DayName(ordered[i])}~{DayName(ordered[j])}");
            }
            else
            {
                for (var k = i; k <= j; k++)
                {
                    parts.Add(DayName(ordered[k]));
                }
            }

            i = j + 1;
        }

        return string.Join(" ", parts);
    }

    /// <summary>
    /// 把整张值日表渲染成文本。
    /// </summary>
    /// <remarks>
    /// 文件头固定输出模板注释——解析器读不回用户原来的注释，那就至少给一份能看懂的说明，
    /// 而不是让用户打开文件看到一堆光秃秃的键值对。
    /// </remarks>
    public static string Render(DutySchedule schedule)
    {
        var sb = new StringBuilder();

        // 固定用 \n 而不是 AppendLine 的 \r\n：插件首次生成值日表时写的就是 \n，
        // 两种换行混在一个文件里，用户用老编辑器打开会看到「有的行有方块」。
        void Line(string text = "") => sb.Append(text).Append('\n');

        Line("# 值日表 —— 用记事本就能改，保存后自动生效");
        Line("#");
        Line("#   星期行：  周一        或  周一~周五 周日      （下面的时间点都属于这些天）");
        Line("#   时间行：  08:40  擦黑板：张三；倒垃圾：李四");
        Line("#             08:40-08:50  擦黑板：张三           （带结束时间就显示到这个时刻）");
        Line("#");
        Line("#   · 以 # 开头的行和空行都会忽略");
        Line("#   · 中英文冒号分号都认，人名之间空格、顿号、逗号、斜杠都行");
        Line("#   · 时间只写到分钟。铃响后再等几秒才弹，那个偏移在设置页里调");
        Line("#   · 带「扫地 / 拖地」的时间点算大扫除，默认用整屏提醒；其余用卡片提醒");
        Line();
        Line("[设置]");
        Line($"轮换起始 = {schedule.RotationStart.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
        Line($"轮换周期 = {schedule.RotationPeriodDays}");
        Line();

        foreach (var group in schedule.Groups)
        {
            Line($"[{SanitizeSection(group.Name)}]");

            if (group.SkipDays.Count > 0)
            {
                Line($"跳过 = {DescribeDays(group.SkipDays)}");
            }

            Line();

            foreach (var bucket in BucketByDaySet(group))
            {
                Line(DescribeDays(bucket.Days));

                foreach (var slot in bucket.Slots)
                {
                    var time = slot.End is { } end && end > slot.Start
                        ? $"{Format(slot.Start)}-{Format(end)}"
                        : Format(slot.Start);
                    var items = string.Join("；", slot.Items.Select(x =>
                        $"{x.Project}：{string.Join("、", x.People)}"));
                    // 时间和正文之间必须是空白，解析器的 SlotPattern 靠它切分。
                    Line($"{time}  {items}");
                }

                Line();
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// 把值日表写回文件。
    /// </summary>
    /// <remarks>
    /// 顺序是刻意的：<b>先校验文本、再备份、最后替换</b>。
    /// 校验不通过时原文件根本没被碰过，不需要「回滚」这种更危险的操作；
    /// 写完再读回来核一遍，是为了挡住编码/换行上的意外。
    /// </remarks>
    /// <exception cref="DutyRosterWriteException">任一步失败。消息可以直接展示给用户。</exception>
    public static void Save(string path, DutySchedule schedule)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new DutyRosterWriteException("值日表路径为空，没法保存。");
        }

        var text = Render(schedule);

        // 一、写之前先确认这段文本能被解析回同一张表。
        var problem = Compare(schedule, DutyRosterParser.Parse(text));
        if (problem is not null)
        {
            throw new DutyRosterWriteException(
                $"生成的文本没能通过自校验，为避免写坏原文件，这次没有保存。\n{problem}");
        }

        // 二、备份。失败就直接放弃——没有备份的情况下覆盖用户的文件不可接受。
        var backupPath = path + BackupExtension;
        var hadOriginal = File.Exists(path);
        if (hadOriginal)
        {
            try
            {
                File.Copy(path, backupPath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new DutyRosterWriteException($"备份原文件失败，这次没有写入。\n{ex.Message}", ex);
            }
        }

        // 三、写到临时文件再整体替换。临时文件叫 .tmp，不匹配文件监视的过滤器。
        var tempPath = path + ".tmp";
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(tempPath, text, new UTF8Encoding(false));
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(tempPath);
            throw new DutyRosterWriteException($"写入值日表失败。\n{ex.Message}", ex);
        }

        // 四、读回来再核一遍。对不上说明落盘环节出了意外，把备份还原回去。
        string? onDiskText;
        try
        {
            onDiskText = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 文件已经写下去了，只是读不回来核对。这不是致命错误，但要用户自己确认一眼。
            throw new DutyRosterWriteException(
                $"文件已写入，但回读校验时读不到它，请打开值日表确认内容。\n{ex.Message}", ex);
        }

        var mismatch = Compare(schedule, DutyRosterParser.Parse(onDiskText));
        if (mismatch is null)
        {
            return;
        }

        // 走到这里说明落盘后的内容和编辑结果不一致，必须回滚，不能让用户带着错数据继续用。
        try
        {
            if (hadOriginal)
            {
                File.Copy(backupPath, path, overwrite: true);
                throw new DutyRosterWriteException(
                    $"写入后的文件与编辑结果不一致，已还原成保存前的内容（备份在 {backupPath}）。\n{mismatch}");
            }

            File.Delete(path);
            throw new DutyRosterWriteException(
                $"写入后的文件与编辑结果不一致，已删除这个写坏的文件。\n{mismatch}");
        }
        catch (DutyRosterWriteException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DutyRosterWriteException(
                $"写入后的文件与编辑结果不一致，而且回滚也失败了——请手动用 {backupPath} 还原值日表。\n{mismatch}\n{ex.Message}",
                ex);
        }
    }

    /// <summary>
    /// 按「这个批次的哪几天内容一样」分组，供 <see cref="Render"/> 合并成星期行。
    /// </summary>
    /// <remarks>
    /// 解析器会把 <c>周一~周五</c> 展开成 5 份各自独立的列表，直接按天输出会让文件膨胀。
    /// 这里按内容分组，内容一样的几天重新收成一行。
    /// </remarks>
    private static List<DayBucket> BucketByDaySet(DutyGroup group)
    {
        var buckets = new List<DayBucket>();
        var bySignature = new Dictionary<string, DayBucket>(StringComparer.Ordinal);

        foreach (var day in WeekOrder)
        {
            if (!group.Days.TryGetValue(day, out var slots) || slots.Count == 0)
            {
                continue;
            }

            var ordered = slots
                .OrderBy(x => x.Start)
                .ThenBy(x => ItemsSignature(x), StringComparer.Ordinal)
                .ToList();
            // 排序后拼接全部时段作为这一天的「内容指纹」：内容一样的几天可以并回一行。
            var fingerprint = string.Join("\n", ordered.Select(SlotSignature));

            if (!bySignature.TryGetValue(fingerprint, out var bucket))
            {
                bucket = new DayBucket { Slots = ordered };
                bySignature[fingerprint] = bucket;
                buckets.Add(bucket);
            }

            bucket.Days.Add(day);
        }

        return buckets;
    }

    private sealed class DayBucket
    {
        public List<DayOfWeek> Days { get; } = [];

        public List<DutySlot> Slots { get; init; } = [];
    }

    private static string SlotSignature(DutySlot slot) =>
        $"{Format(slot.Start)}|{(slot.End is { } e ? Format(e) : "-")}|{ItemsSignature(slot)}";

    private static string ItemsSignature(DutySlot slot) =>
        string.Join("；", slot.Items.Select(x => $"{x.Project}：{string.Join("、", x.People)}"));

    /// <summary>
    /// 比较两张表是不是同一张，不同就返回一句能指出差异的话。
    /// </summary>
    /// <remarks>
    /// 用「规范化签名串」对比而不是逐字段比对：<see cref="DutySlot"/> 是 record，
    /// 但它的 <c>Items</c> 是集合，两个内容完全相同的实例按引用比较并不相等
    /// （实测 <c>Equals == false</c>），不能拿 record 的相等性做校验。
    /// </remarks>
    private static string? Compare(DutySchedule expected, DutySchedule actual)
    {
        var a = Signature(expected);
        var b = Signature(actual);
        if (a == b)
        {
            return null;
        }

        var la = a.Split('\n');
        var lb = b.Split('\n');
        var max = Math.Min(la.Length, lb.Length);
        for (var i = 0; i < max; i++)
        {
            if (la[i] != lb[i])
            {
                return $"第 {i + 1} 处不一致：期望「{la[i]}」，实际「{lb[i]}」。";
            }
        }

        return $"行数不一致：期望 {la.Length} 行，实际 {lb.Length} 行。";
    }

    private static string Signature(DutySchedule schedule)
    {
        var sb = new StringBuilder();
        sb.Append("rotation:")
          .Append(schedule.RotationStart.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
          .Append(";period:")
          .Append(schedule.RotationPeriodDays)
          .Append('\n');

        foreach (var group in schedule.Groups)
        {
            sb.Append("group:").Append(group.Name).Append(";skip:")
              .Append(string.Join(",", group.SkipDays.OrderBy(d => Array.IndexOf(WeekOrder, d)).Select(d => (int)d)))
              .Append('\n');

            foreach (var day in WeekOrder)
            {
                if (!group.Days.TryGetValue(day, out var slots) || slots.Count == 0)
                {
                    continue;
                }

                sb.Append("day:").Append((int)day).Append('\n');
                foreach (var slot in slots
                             .OrderBy(x => x.Start)
                             .ThenBy(x => ItemsSignature(x), StringComparer.Ordinal))
                {
                    sb.Append("  ").Append(SlotSignature(slot)).Append('\n');
                }
            }
        }

        return sb.ToString();
    }

    private static string Format(TimeSpan time) => time.ToString(@"hh\:mm", CultureInfo.InvariantCulture);

    private static string SanitizeSection(string name)
    {
        var cleaned = name.Replace('[', ' ').Replace(']', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
        return cleaned.Length == 0 ? "值日" : cleaned;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // 清理临时文件失败不影响结论，下次写入会覆盖它。
        }
    }
}
