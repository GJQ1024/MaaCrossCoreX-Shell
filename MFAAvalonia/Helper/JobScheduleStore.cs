using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MFAAvalonia.Helper;

/// <summary>
/// 任务级调度记录：每个任务自己保存「下次运行时间」。
/// 与 Timer.* 槽位配置完全独立，存放在 config/job-schedules.json（原子写）。
/// </summary>
public sealed class JobScheduleEntry
{
    public string InstanceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Entry { get; set; } = string.Empty;

    /// <summary>任务自报 hint 的键（chip_build / infra_shift），空表示该任务没有自报。</summary>
    public string HintId { get; set; } = string.Empty;

    public bool Enabled { get; set; }
    public DateTime? NextRunAt { get; set; }

    /// <summary>self（任务自报）/ game（游戏内读数）/ learned（观测学习）/ static（静态间隔）</summary>
    public string Source { get; set; } = "static";

    public int SuccessMinutes { get; set; } = 240;
    public int FailMinutes { get; set; } = 30;

    public DateTime? LastRunAt { get; set; }

    /// <summary>success / failed / stopped / running / interrupted / skipped_missing</summary>
    public string LastResult { get; set; } = string.Empty;

    public DateTime? LastDispatchAt { get; set; }

    /// <summary>芯片制造：相邻两次「开始改造」之间的实测分钟数。</summary>
    public List<double> ObservedMinutes { get; set; } = new();
    public DateTime? LastBuildStartAt { get; set; }

    /// <summary>基建换班：上次换班到本次检测到红脸之间的实测分钟数。</summary>
    public List<double> ObservedSwapToRedMinutes { get; set; } = new();
    public DateTime? LastSwapAt { get; set; }

    [JsonIgnore]
    public string Key => BuildKey(InstanceId, Name, Entry);

    public static string BuildKey(string instanceId, string name, string entry) =>
        $"{instanceId}\u001f{name}\u001f{entry}";
}

public static class JobScheduleStore
{
    private const int SchemaVersion = 1;
    private const int MaxSamples = 20;

    private static readonly object Gate = new();
    private static List<JobScheduleEntry>? _jobs;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    /// <summary>任务级调度状态变化（启用开关、回写下次时间）后触发。</summary>
    public static event Action? Changed;

    public static string DirectoryPath => Path.Combine(AppPaths.DataRoot, "config");
    public static string FilePath => Path.Combine(DirectoryPath, "job-schedules.json");
    public static string HintDirectory => Path.Combine(DirectoryPath, "job_next");

    /// <summary>任务入口 → 自报 hint 键。没有自报的任务返回空串。</summary>
    public static string ResolveHintId(string? entry) => entry switch
    {
        "芯片制作任务开始" => "chip_build",
        "基建_换班入口" => "infra_shift",
        _ => string.Empty
    };

    public static JobScheduleEntry? Find(string instanceId, string name, string? entry)
    {
        if (string.IsNullOrEmpty(instanceId) || string.IsNullOrEmpty(entry))
            return null;

        lock (Gate)
        {
            return LoadLocked().FirstOrDefault(item =>
                item.InstanceId == instanceId && item.Name == name && item.Entry == entry);
        }
    }

    public static JobScheduleEntry GetOrCreate(string instanceId, string name, string entry)
    {
        lock (Gate)
        {
            var jobs = LoadLocked();
            var key = JobScheduleEntry.BuildKey(instanceId, name, entry);
            var existing = jobs.FirstOrDefault(item => item.Key == key);
            if (existing != null)
                return existing;

            var hintId = ResolveHintId(entry);
            var entryItem = new JobScheduleEntry
            {
                InstanceId = instanceId,
                Name = name,
                Entry = entry,
                HintId = hintId,
                Enabled = false,
                SuccessMinutes = hintId == "infra_shift" ? 480 : 240,
                FailMinutes = 30
            };
            jobs.Add(entryItem);
            return entryItem;
        }
    }

    public static IReadOnlyList<JobScheduleEntry> Snapshot()
    {
        lock (Gate)
        {
            return LoadLocked()
                .Select(Clone)
                .ToList();
        }
    }

    /// <summary>UI 开关：首次启用时把下次时间推到「现在 + 成功间隔」，不立即抢跑。</summary>
    public static void SetEnabled(string instanceId, string name, string entry, bool enabled)
    {
        if (string.IsNullOrEmpty(instanceId) || string.IsNullOrEmpty(entry))
            return;

        lock (Gate)
        {
            var item = GetOrCreate(instanceId, name, entry);
            item.Enabled = enabled;
            if (enabled)
            {
                item.NextRunAt ??= DateTime.Now.AddMinutes(Math.Max(1, item.SuccessMinutes));
                if (string.IsNullOrEmpty(item.LastResult))
                    item.Source = "static";
            }

            SaveLocked();
        }

        Changed?.Invoke();
    }

    public static void ResetNextRun(string instanceId, string name, string entry, DateTime next)
    {
        lock (Gate)
        {
            var item = GetOrCreate(instanceId, name, entry);
            item.NextRunAt = next;
            SaveLocked();
        }

        Changed?.Invoke();
    }

    /// <summary>调度器回写：由调用方保证 item 来自 Snapshot 的克隆，需按 Key 找回归档对象。</summary>
    public static void Update(JobScheduleEntry updated)
    {
        lock (Gate)
        {
            var jobs = LoadLocked();
            var index = jobs.FindIndex(item => item.Key == updated.Key);
            if (index < 0)
                jobs.Add(updated);
            else
                jobs[index] = updated;
            SaveLocked();
        }

        Changed?.Invoke();
    }

    public static void NotifyChanged() => Changed?.Invoke();

    public static void TrimSamples(List<double> samples)
    {
        while (samples.Count > MaxSamples)
            samples.RemoveAt(0);
    }

    private static List<JobScheduleEntry> LoadLocked()
    {
        if (_jobs != null)
            return _jobs;

        var jobs = new List<JobScheduleEntry>();
        try
        {
            if (File.Exists(FilePath))
            {
                var model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(FilePath), JsonOptions);
                if (model?.Jobs != null)
                    jobs = model.Jobs.Where(item => !string.IsNullOrEmpty(item.Entry)).ToList();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            LoggerHelper.Error($"[任务调度] 状态文件读取失败，按空记录继续：{ex.Message}", ex);
        }

        // 上次进程在任务运行中被杀：不当作已完成，给一次失败间隔后补跑。
        var now = DateTime.Now;
        foreach (var item in jobs.Where(item => item.LastResult == "running"))
        {
            item.LastResult = "interrupted";
            item.NextRunAt = (item.LastDispatchAt ?? now).AddMinutes(Math.Max(1, item.FailMinutes));
            item.Source = "static";
        }

        _jobs = jobs;
        if (jobs.Count > 0)
            SaveLocked();
        return _jobs;
    }

    private static void SaveLocked()
    {
        if (_jobs == null)
            return;

        try
        {
            System.IO.Directory.CreateDirectory(DirectoryPath);
            var model = new FileModel { Version = SchemaVersion, Jobs = _jobs };
            var json = JsonSerializer.Serialize(model, JsonOptions);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, FilePath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoggerHelper.Error($"[任务调度] 状态文件写入失败：{ex.Message}", ex);
        }
    }

    private static JobScheduleEntry Clone(JobScheduleEntry source) => new()
    {
        InstanceId = source.InstanceId,
        Name = source.Name,
        Entry = source.Entry,
        HintId = source.HintId,
        Enabled = source.Enabled,
        NextRunAt = source.NextRunAt,
        Source = source.Source,
        SuccessMinutes = source.SuccessMinutes,
        FailMinutes = source.FailMinutes,
        LastRunAt = source.LastRunAt,
        LastResult = source.LastResult,
        LastDispatchAt = source.LastDispatchAt,
        ObservedMinutes = new List<double>(source.ObservedMinutes),
        LastBuildStartAt = source.LastBuildStartAt,
        ObservedSwapToRedMinutes = new List<double>(source.ObservedSwapToRedMinutes),
        LastSwapAt = source.LastSwapAt
    };

    private sealed class FileModel
    {
        public int Version { get; set; } = SchemaVersion;
        public List<JobScheduleEntry> Jobs { get; set; } = new();
    }
}
