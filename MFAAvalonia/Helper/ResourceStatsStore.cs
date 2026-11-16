using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace MFAAvalonia.Helper;

/// <summary>
/// 资源统计只读数据层：读取 config/resource_stats/*.jsonl，按 run_id 配对 before/after。
/// 统计页只读：不写 jsonl、不触发任何任务、不消耗资源。
/// </summary>
public static class ResourceStatsStore
{
    public static string DirectoryPath => Path.Combine(AppPaths.ConfigDirectory, "resource_stats");

    /// <summary>统计页固定展示的五条任务（用户指定的采集范围）。</summary>
    public static readonly string[] TargetTaskKeys =
    {
        "MailAttachments", "DailyExplore", "InfraShift", "ChipCraft", "ShopPurchase"
    };

    /// <summary>商店购买分组的多个 pipeline 任务统一归到一行。</summary>
    private static readonly HashSet<string> ShopKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "FreeGift", "ShopPurchase", "ShopTrade", "ShopVoid", "ShopStardust", "ShopArena", "ShopMirror"
    };

    private static readonly Dictionary<string, string> DisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MailAttachments"] = "邮件附件领取",
        ["DailyExplore"] = "每日探索扫荡",
        ["InfraShift"] = "基建换班",
        ["ChipCraft"] = "芯片制造",
        ["ShopPurchase"] = "商店购买",
        ["StartUp"] = "启动游戏",
        ["DailyRewards"] = "领取奖励",
        ["FreeGift"] = "商店购买"
    };

    public static string NormalizeKey(string? task)
    {
        var key = (task ?? string.Empty).Trim();
        if (key.Length == 0)
            return "unknown";
        return ShopKeys.Contains(key) ? "ShopPurchase" : key;
    }

    public static string DisplayName(string key) =>
        DisplayNames.TryGetValue(key, out var name) ? name : key;

    /// <summary>已有数据文件对应的账号（文件名去扩展名）。</summary>
    public static IReadOnlyList<string> ListAccounts()
    {
        try
        {
            if (!Directory.Exists(DirectoryPath))
                return Array.Empty<string>();

            return Directory.EnumerateFiles(DirectoryPath, "*.jsonl")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList()!;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoggerHelper.Warning($"[资源统计] 读取数据目录失败：{ex.Message}");
            return Array.Empty<string>();
        }
    }

    /// <summary>按账号与时间范围聚合。from/to 为 null 表示不限。</summary>
    public static ResourceStatsSnapshot Load(string account, DateTime? from, DateTime? to)
    {
        var snapshot = new ResourceStatsSnapshot();
        var tasks = new Dictionary<string, TaskStats>(StringComparer.OrdinalIgnoreCase);
        var beforeByRun = new Dictionary<string, RawRecord>(StringComparer.Ordinal);
        var afterByRun = new Dictionary<string, RawRecord>(StringComparer.Ordinal);

        foreach (var record in ReadRecords(account))
        {
            if (string.IsNullOrEmpty(record.RunId))
                continue;

            if (record.Phase == "before")
                beforeByRun[record.RunId] = record;
            else if (record.Phase == "after" && !afterByRun.ContainsKey(record.RunId))
                afterByRun[record.RunId] = record;
        }

        foreach (var after in afterByRun.Values)
        {
            if (!InRange(after.Ts, from, to))
                continue;

            var stat = GetTask(tasks, after.Task);
            stat.Runs++;
            var failed = after.DeltaAp is null || after.DeltaCoin is null || after.DeltaGem is null;
            if (failed)
                stat.Failures++;
            else
                stat.Successes++;
            Accumulate(stat, after);
        }

        foreach (var before in beforeByRun.Values)
        {
            if (afterByRun.ContainsKey(before.RunId))
                continue;
            if (!InRange(before.Ts, from, to))
                continue;

            GetTask(tasks, before.Task).Interrupted++;
        }

        foreach (var key in TargetTaskKeys)
            GetTask(tasks, key);

        var ordered = ResourceStatsSnapshot.SortTasks(tasks.Values);
        snapshot.Tasks.AddRange(ordered);

        foreach (var stat in ordered)
        {
            snapshot.TotalRuns += stat.Runs;
            snapshot.TotalSuccess += stat.Successes;
            snapshot.TotalFailures += stat.Failures;
            snapshot.TotalInterrupted += stat.Interrupted;
        }

        snapshot.ProducedAp = SumNonNull(ordered.Select(item => item.ProducedAp));
        snapshot.ConsumedAp = SumNonNull(ordered.Select(item => item.ConsumedAp));
        snapshot.ProducedCoin = SumNonNull(ordered.Select(item => item.ProducedCoin));
        snapshot.ConsumedCoin = SumNonNull(ordered.Select(item => item.ConsumedCoin));
        snapshot.ProducedGem = SumNonNull(ordered.Select(item => item.ProducedGem));
        snapshot.ConsumedGem = SumNonNull(ordered.Select(item => item.ConsumedGem));

        return snapshot;
    }

    /// <summary>导出与 tools/export_resource_csv.py 同列的 CSV（UTF-8 BOM）。返回文件路径与行数。</summary>
    public static (string Path, int Rows) ExportCsv(string account, DateTime? from, DateTime? to, string tag)
    {
        var beforeByRun = new Dictionary<string, RawRecord>(StringComparer.Ordinal);
        var afterRecords = new List<RawRecord>();

        foreach (var record in ReadRecords(account))
        {
            if (string.IsNullOrEmpty(record.RunId))
                continue;

            if (record.Phase == "before")
                beforeByRun[record.RunId] = record;
            else if (record.Phase == "after" && InRange(record.Ts, from, to))
                afterRecords.Add(record);
        }

        afterRecords.Sort((left, right) => left.Ts.CompareTo(right.Ts));

        var directory = DirectoryPath;
        Directory.CreateDirectory(directory);
        var safeTag = string.IsNullOrWhiteSpace(tag) ? "all" : tag;
        var path = Path.Combine(directory, $"{account}_{safeTag}.csv");

        var builder = new StringBuilder();
        builder.Append('\uFEFF');
        builder.AppendLine("ts,account,task,run_id,phase,ap_before,ap_after,ap_delta,coin_before,coin_after,coin_delta,gem_before,gem_after,gem_delta");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = 0;
        foreach (var after in afterRecords)
        {
            if (!seen.Add(after.RunId))
                continue;

            beforeByRun.TryGetValue(after.RunId, out var before);
            builder
                .Append(Csv(after.TsText)).Append(',')
                .Append(Csv(after.Account)).Append(',')
                .Append(Csv(after.Task)).Append(',')
                .Append(Csv(after.RunId)).Append(',')
                .Append("after,")
                .Append(Csv(before?.Ap)).Append(',')
                .Append(Csv(after.Ap)).Append(',')
                .Append(Csv(after.DeltaAp)).Append(',')
                .Append(Csv(before?.Coin)).Append(',')
                .Append(Csv(after.Coin)).Append(',')
                .Append(Csv(after.DeltaCoin)).Append(',')
                .Append(Csv(before?.Gem)).Append(',')
                .Append(Csv(after.Gem)).Append(',')
                .Append(Csv(after.DeltaGem))
                .AppendLine();
            rows++;
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
        return (path, rows);
    }

    private static string Csv(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Csv(string? value)
    {
        var text = value ?? string.Empty;
        return text.Contains(',') || text.Contains('"')
            ? "\"" + text.Replace("\"", "\"\"") + "\""
            : text;
    }

    private static int? SumNonNull(IEnumerable<int?> values)
    {
        int? sum = null;
        foreach (var value in values)
        {
            if (value is null)
                continue;
            sum = (sum ?? 0) + value.Value;
        }
        return sum;
    }

    private static TaskStats GetTask(IDictionary<string, TaskStats> tasks, string? task)
    {
        var key = NormalizeKey(task);
        if (!tasks.TryGetValue(key, out var stat))
        {
            stat = new TaskStats(key, DisplayName(key));
            tasks[key] = stat;
        }
        return stat;
    }

    private static void Accumulate(TaskStats stat, RawRecord record)
    {
        stat.ProducedAp = Accumulate(stat.ProducedAp, record.DeltaAp, true);
        stat.ConsumedAp = Accumulate(stat.ConsumedAp, record.DeltaAp, false);
        stat.ProducedCoin = Accumulate(stat.ProducedCoin, record.DeltaCoin, true);
        stat.ConsumedCoin = Accumulate(stat.ConsumedCoin, record.DeltaCoin, false);
        stat.ProducedGem = Accumulate(stat.ProducedGem, record.DeltaGem, true);
        stat.ConsumedGem = Accumulate(stat.ConsumedGem, record.DeltaGem, false);
    }

    private static int? Accumulate(int? current, int? delta, bool positive)
    {
        if (delta is null)
            return current;

        var value = positive ? Math.Max(delta.Value, 0) : Math.Min(delta.Value, 0);
        if (value == 0)
            return current;

        return (current ?? 0) + value;
    }

    private static bool InRange(DateTime ts, DateTime? from, DateTime? to)
    {
        if (from is not null && ts < from.Value)
            return false;
        if (to is not null && ts > to.Value)
            return false;
        return true;
    }

    private static IEnumerable<RawRecord> ReadRecords(string account)
    {
        var records = new List<RawRecord>();
        try
        {
            if (!Directory.Exists(DirectoryPath))
                return records;

            foreach (var file in Directory.EnumerateFiles(DirectoryPath, "*.jsonl"))
            {
                var fileAccount = Path.GetFileNameWithoutExtension(file);
                string[] lines;
                try
                {
                    lines = File.ReadAllLines(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LoggerHelper.Warning($"[资源统计] 读取 {file} 失败：{ex.Message}");
                    continue;
                }

                foreach (var line in lines)
                {
                    var record = ParseRecord(line, fileAccount);
                    if (record is null)
                        continue;
                    if (!string.IsNullOrEmpty(account) &&
                        !string.Equals(record.Account, account, StringComparison.OrdinalIgnoreCase))
                        continue;
                    records.Add(record);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoggerHelper.Warning($"[资源统计] 读取数据目录失败：{ex.Message}");
        }

        return records;
    }

    private static RawRecord? ParseRecord(string line, string fileAccount)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var record = new RawRecord
            {
                Account = ReadString(root, "account") ?? fileAccount,
                Task = ReadString(root, "task") ?? "unknown",
                RunId = ReadString(root, "run_id") ?? string.Empty,
                Phase = ReadString(root, "phase") ?? string.Empty,
                TsText = ReadString(root, "ts") ?? string.Empty
            };

            if (DateTime.TryParse(record.TsText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts))
                record.Ts = ts;

            if (root.TryGetProperty("resources", out var resources) && resources.ValueKind == JsonValueKind.Object)
            {
                record.Ap = ReadResourceValue(resources, "ap");
                record.Coin = ReadResourceValue(resources, "coin");
                record.Gem = ReadResourceValue(resources, "gem");
            }

            if (root.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
            {
                record.DeltaAp = ReadDeltaValue(delta, "ap");
                record.DeltaCoin = ReadDeltaValue(delta, "coin");
                record.DeltaGem = ReadDeltaValue(delta, "gem");
            }

            return record;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadResourceValue(JsonElement resources, string key)
    {
        if (!resources.TryGetProperty(key, out var resource) || resource.ValueKind != JsonValueKind.Object)
            return null;
        if (!resource.TryGetProperty("value", out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Object)
            return value.TryGetProperty("current", out var current) && current.TryGetInt32(out var parsed)
                ? parsed
                : null;

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
    }

    private static int? ReadDeltaValue(JsonElement delta, string key)
    {
        if (!delta.TryGetProperty(key, out var value))
            return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
    }

    private sealed class RawRecord
    {
        public string Account { get; init; } = string.Empty;
        public string Task { get; init; } = "unknown";
        public string RunId { get; init; } = string.Empty;
        public string Phase { get; init; } = string.Empty;
        public string TsText { get; init; } = string.Empty;
        public DateTime Ts { get; set; } = DateTime.MinValue;
        public int? Ap { get; set; }
        public int? Coin { get; set; }
        public int? Gem { get; set; }
        public int? DeltaAp { get; set; }
        public int? DeltaCoin { get; set; }
        public int? DeltaGem { get; set; }
    }
}

public sealed class TaskStats(string key, string name)
{
    public string Key { get; } = key;
    public string Name { get; } = name;

    public int Runs { get; set; }
    public int Successes { get; set; }
    public int Failures { get; set; }
    public int Interrupted { get; set; }

    public int? ProducedAp { get; set; }
    public int? ConsumedAp { get; set; }
    public int? ProducedCoin { get; set; }
    public int? ConsumedCoin { get; set; }
    public int? ProducedGem { get; set; }
    public int? ConsumedGem { get; set; }

    /// <summary>是否真的产生过可用的资源差值（用于区分「识别失败」与「无运行记录」）。</summary>
    public bool HasDelta =>
        ProducedAp is not null || ConsumedAp is not null ||
        ProducedCoin is not null || ConsumedCoin is not null ||
        ProducedGem is not null || ConsumedGem is not null;
}

public sealed class ResourceStatsSnapshot
{
    public List<TaskStats> Tasks { get; } = new();

    public int TotalRuns { get; set; }
    public int TotalSuccess { get; set; }
    public int TotalFailures { get; set; }
    public int TotalInterrupted { get; set; }

    public int? ProducedAp { get; set; }
    public int? ConsumedAp { get; set; }
    public int? ProducedCoin { get; set; }
    public int? ConsumedCoin { get; set; }
    public int? ProducedGem { get; set; }
    public int? ConsumedGem { get; set; }

    internal static List<TaskStats> SortTasks(IEnumerable<TaskStats> source)
    {
        var order = ResourceStatsStore.TargetTaskKeys
            .Select((key, index) => (key, index))
            .ToDictionary(pair => pair.key, pair => pair.index, StringComparer.OrdinalIgnoreCase);

        return source
            .OrderBy(item => order.TryGetValue(item.Key, out var index) ? index : int.MaxValue)
            .ThenByDescending(item => item.Runs)
            .ThenBy(item => item.Name, StringComparer.CurrentCulture)
            .ToList();
    }
}
