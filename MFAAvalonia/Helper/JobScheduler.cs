using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Threading;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper.ValueType;
using MFAAvalonia.ViewModels.Pages;

namespace MFAAvalonia.Helper;

/// <summary>
/// 任务级调度器：每个任务按自己的 NextRunAt 到点触发。
/// 同一分钟多条到期 → 合并成一轮，按队列顺序串行跑（不并发）。
/// </summary>
public static class JobScheduler
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, PendingRound> Pending = new();
    private static DispatcherTimer? _timer;
    private static bool _started;

    public static void Start()
    {
        if (_started)
            return;

        _started = true;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _timer.Tick += (_, _) => Check(DateTime.Now);
        _timer.Start();
        LoggerHelper.Info($"[任务调度] 已启动，状态文件={JobScheduleStore.FilePath}");
    }

    public static void Check(DateTime now)
    {
        lock (Gate)
        {
            if (Pending.Count > 0)
                return;
        }

        if (IsAnyInstanceRunning())
            return;

        var due = JobScheduleStore.Snapshot()
            .Where(item => IsDue(item, now))
            .OrderBy(item => item.NextRunAt)
            .ToList();
        if (due.Count == 0)
            return;

        // 合并成轮：同一实例、同一 tick 到期的任务按队列顺序一次派发。
        var group = due
            .GroupBy(item => item.InstanceId)
            .OrderBy(g => g.Min(item => item.NextRunAt))
            .First()
            .OrderBy(item => item.NextRunAt)
            .ToList();

        DispatchRound(group.First().InstanceId, group, now);
    }

    private static bool IsDue(JobScheduleEntry item, DateTime now)
    {
        if (!item.Enabled || item.NextRunAt is not { } next || next > now)
            return false;

        // 同一调度点只触发一次：派发时刻已经跨过该 NextRunAt 就不再重复。
        return item.LastDispatchAt == null || item.LastDispatchAt < next;
    }

    private static void DispatchRound(string instanceId, List<JobScheduleEntry> jobs, DateTime now)
    {
        var manager = MaaProcessorManager.Instance;
        var vm = manager.GetViewModel(instanceId);
        if (vm == null)
        {
            manager.EnsureInstanceLoaded(instanceId);
            vm = manager.GetViewModel(instanceId);
        }

        if (vm == null)
        {
            MarkSkipped(jobs, now, "skipped_missing", $"target={instanceId}");
            return;
        }

        var items = jobs
            .Select(job => vm.TaskItemViewModels.FirstOrDefault(task => Matches(task, job)))
            .Where(task => task != null)
            .Select(task => task!)
            .ToList();

        if (items.Count == 0)
        {
            MarkSkipped(jobs, now, "skipped_missing", $"target={instanceId} 队列中没有对应任务");
            return;
        }

        var round = new PendingRound
        {
            InstanceId = instanceId,
            Jobs = jobs,
            Items = items,
            ViewModel = vm,
            DispatchedAt = now
        };
        round.Handler = (_, args) => OnRoundFinished(round, args);

        foreach (var job in jobs)
        {
            job.LastDispatchAt = now;
            job.LastResult = "running";
            JobScheduleStore.Update(job);
        }

        lock (Gate)
        {
            Pending[instanceId] = round;
        }

        vm.Processor.TaskRunFinished += round.Handler;
        LoggerHelper.Info(
            $"[任务调度] 派发 instance={instanceId} jobs={string.Join("、", jobs.Select(item => item.Name))} " +
            $"next={string.Join(",", jobs.Select(item => item.NextRunAt?.ToString("MM-dd HH:mm")))}");

        try
        {
            vm.Processor.Start(items, ignoreCheckedState: true);
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"[任务调度] 派发失败：{ex.Message}", ex);
            FinishRound(round, MFATask.MFATaskStatus.FAILED, DateTime.Now);
        }

        // Start 被互斥闸门拒绝时不会再有完成回调，直接按失败间隔回写，避免永久挂住。
        if (!vm.Processor.IsTaskRunActive && !vm.IsRunning)
            FinishRound(round, MFATask.MFATaskStatus.FAILED, DateTime.Now);
    }

    private static void OnRoundFinished(PendingRound round, TaskRunFinishedEventArgs args)
    {
        FinishRound(round, args.Status, DateTime.Now);
    }

    private static void FinishRound(PendingRound round, MFATask.MFATaskStatus status, DateTime now)
    {
        var handler = round.Handler;
        if (handler != null)
        {
            round.Handler = null;
            round.ViewModel.Processor.TaskRunFinished -= handler;
        }

        lock (Gate)
        {
            if (Pending.TryGetValue(round.InstanceId, out var current) && ReferenceEquals(current, round))
                Pending.Remove(round.InstanceId);
        }

        foreach (var job in round.Jobs)
        {
            var item = round.ViewModel.TaskItemViewModels.FirstOrDefault(task => Matches(task, job))
                       ?? round.Items.FirstOrDefault(task => Matches(task, job));
            var state = item?.RunState ?? TaskRunState.None;

            var success = state == TaskRunState.Succeeded
                          || (state == TaskRunState.None && status == MFATask.MFATaskStatus.SUCCEEDED);
            var (next, source, hintNote) = success
                ? ComputeSuccessNext(job, now)
                : (now.AddMinutes(Math.Max(1, job.FailMinutes)), "fail", string.Empty);

            job.NextRunAt = next;
            job.Source = source;
            job.LastRunAt = now;
            job.LastResult = success ? "success" : state == TaskRunState.Failed ? "failed" : "stopped";
            JobScheduleStore.Update(job);

            LoggerHelper.Info(
                $"[任务调度] 完成 job={job.Name} result={job.LastResult} source={source} " +
                $"next={next:yyyy-MM-dd HH:mm:ss}{hintNote}");
        }

        DispatcherHelper.PostOnMainThread(() => JobScheduleStore.NotifyChanged());
    }

    /// <summary>
    /// 手动运行结束后的回写：已配置任务级定时的条目按与调度轮相同的规则推进 NextRun，
    /// 避免手跑完 30s 内又被调度器补跑一遍。调度轮由 FinishRound 回写，这里在 Pending 存在时跳过。
    /// </summary>
    public static void OnRunFinished(string instanceId, IReadOnlyList<DragItemViewModel> items,
        MFATask.MFATaskStatus status, DateTime now)
    {
        if (string.IsNullOrEmpty(instanceId) || items.Count == 0)
            return;

        lock (Gate)
        {
            if (Pending.ContainsKey(instanceId))
                return;
        }

        var updated = false;
        foreach (var item in items)
        {
            if (item.IsResourceOptionItem)
                continue;

            var name = item.InterfaceItem?.Name ?? item.Name;
            var entry = item.InterfaceItem?.Entry;
            var job = JobScheduleStore.Find(instanceId, name, entry);
            if (job == null)
                continue; // 没配任务级定时的任务不落盘

            var state = item.RunState;
            var success = state == TaskRunState.Succeeded
                          || (state == TaskRunState.None && status == MFATask.MFATaskStatus.SUCCEEDED);
            var (next, source, hintNote) = success
                ? ComputeSuccessNext(job, now)
                : (now.AddMinutes(Math.Max(1, job.FailMinutes)), "fail", string.Empty);

            job.NextRunAt = next;
            job.Source = source;
            job.LastRunAt = now;
            job.LastResult = success ? "success" : state == TaskRunState.Failed ? "failed" : "stopped";
            job.LastDispatchAt = now;
            JobScheduleStore.Update(job);
            updated = true;

            LoggerHelper.Info(
                $"[任务调度] 手动运行回写 job={job.Name} result={job.LastResult} source={source} " +
                $"next={next:yyyy-MM-dd HH:mm:ss}{hintNote}");
        }

        if (updated)
            DispatcherHelper.PostOnMainThread(() => JobScheduleStore.NotifyChanged());
    }

    private static (DateTime next, string source, string note) ComputeSuccessNext(
        JobScheduleEntry job, DateTime now)
    {
        var hint = TakeHint(job.InstanceId, job.HintId);
        var note = hint == null ? " hint=none" : $" hint={hint.Source}";

        // 一级：任务自报的游戏剩余时间（agent 在「开始改造」成功后读取面板写入 notBefore）。
        if (hint?.Source == "game" && hint.NotBefore is { } notBefore && notBefore > now)
            return (notBefore, "game", note);

        switch (job.HintId)
        {
            case "chip_build":
                RecordBuildCycle(job, hint);
                if (job.ObservedMinutes.Count >= 3)
                {
                    var minutes = job.ObservedMinutes.Min();
                    if (minutes > 0)
                        return (now.AddMinutes(minutes + 2), "learned",
                            note + $" observed={minutes:F1}min samples={job.ObservedMinutes.Count}");
                }

                return (now.AddMinutes(Math.Max(1, job.SuccessMinutes)), "static", note);

            case "infra_shift":
                RecordShiftCycle(job, hint, now);
                if (job.ObservedSwapToRedMinutes.Count >= 3)
                {
                    var minutes = job.ObservedSwapToRedMinutes.Min() * 0.85;
                    return (now.AddMinutes(Math.Max(60, minutes)), "learned",
                        note + $" earliestRed={job.ObservedSwapToRedMinutes.Min():F1}min samples={job.ObservedSwapToRedMinutes.Count}");
                }

                return (now.AddMinutes(Math.Max(1, job.SuccessMinutes)), "static", note);

            default:
                return (now.AddMinutes(Math.Max(1, job.SuccessMinutes)), "static", note);
        }
    }

    private static void RecordBuildCycle(JobScheduleEntry job, JobHint? hint)
    {
        if (hint?.StartedAt is not { } startedAt)
            return;

        if (job.LastBuildStartAt is { } previous && startedAt > previous)
        {
            var minutes = (startedAt - previous).TotalMinutes;
            if (minutes is >= 60 and <= 720)
            {
                job.ObservedMinutes.Add(Math.Round(minutes, 1));
                JobScheduleStore.TrimSamples(job.ObservedMinutes);
            }
        }

        job.LastBuildStartAt = startedAt;
    }

    private static void RecordShiftCycle(JobScheduleEntry job, JobHint? hint, DateTime now)
    {
        if (hint?.Swapped != true)
            return;

        if (job.LastSwapAt is { } lastSwap && now > lastSwap)
        {
            var minutes = (now - lastSwap).TotalMinutes;
            if (minutes is >= 120 and <= 7 * 24 * 60)
            {
                job.ObservedSwapToRedMinutes.Add(Math.Round(minutes, 1));
                JobScheduleStore.TrimSamples(job.ObservedSwapToRedMinutes);
            }
        }

        job.LastSwapAt = now;
    }

    private static void MarkSkipped(List<JobScheduleEntry> jobs, DateTime now, string result, string reason)
    {
        foreach (var job in jobs)
        {
            job.LastResult = result;
            job.NextRunAt = now.AddMinutes(Math.Max(1, job.FailMinutes));
            JobScheduleStore.Update(job);
        }

        LoggerHelper.Warning($"[任务调度] 跳过 {result}：{reason}");
    }

    private static bool Matches(DragItemViewModel task, JobScheduleEntry job)
    {
        if (task.IsResourceOptionItem)
            return false;

        var name = task.InterfaceItem?.Name ?? task.Name;
        var entry = task.InterfaceItem?.Entry;
        return string.Equals(name, job.Name, StringComparison.Ordinal)
               && string.Equals(entry, job.Entry, StringComparison.Ordinal);
    }

    private static bool IsAnyInstanceRunning()
    {
        var manager = MaaProcessorManager.Instance;
        foreach (var (id, _) in manager.GetAllInstanceIdsAndNames())
        {
            var vm = manager.GetViewModel(id);
            if (vm == null)
                continue;

            if (vm.IsRunning || vm.Processor.IsTaskRunActive || vm.Processor.TaskQueue.Count > 0)
                return true;
        }

        return false;
    }

    private static JobHint? TakeHint(string instanceId, string hintId)
    {
        if (string.IsNullOrEmpty(hintId))
            return null;

        var path = Path.Combine(JobScheduleStore.HintDirectory, $"{instanceId}.json");
        if (!File.Exists(path))
            return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("jobs", out var jobs)
                || !jobs.TryGetProperty(hintId, out var node))
                return null;

            var hint = new JobHint
            {
                Source = node.TryGetProperty("source", out var source) ? source.GetString() ?? "" : "",
                Swapped = node.TryGetProperty("swapped", out var swapped)
                          && swapped.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? swapped.GetBoolean()
                    : null,
                NotBefore = ReadTime(node, "notBefore"),
                StartedAt = ReadTime(node, "startedAt")
            };

            ConsumeHint(path, hintId);
            return hint;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            LoggerHelper.Warning($"[任务调度] hint 读取失败：{ex.Message}");
            return null;
        }
    }

    private static DateTime? ReadTime(JsonElement node, string property)
    {
        if (!node.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
            return null;

        return DateTimeOffset.TryParse(value.GetString(), out var parsed)
            ? parsed.LocalDateTime
            : null;
    }

    private static void ConsumeHint(string path, string hintId)
    {
        try
        {
            var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path));
            if (root?["jobs"] is not System.Text.Json.Nodes.JsonObject jobs)
                return;

            jobs.Remove(hintId);
            var temp = path + ".tmp";
            File.WriteAllText(temp, root.ToJsonString());
            File.Move(temp, path, true);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            LoggerHelper.Warning($"[任务调度] hint 清理失败：{ex.Message}");
        }
    }

    private sealed class JobHint
    {
        public string Source { get; init; } = string.Empty;
        public DateTime? NotBefore { get; init; }
        public DateTime? StartedAt { get; init; }
        public bool? Swapped { get; init; }
    }

    private sealed class PendingRound
    {
        public string InstanceId { get; init; } = string.Empty;
        public List<JobScheduleEntry> Jobs { get; init; } = new();
        public List<DragItemViewModel> Items { get; init; } = new();
        public TaskQueueViewModel ViewModel { get; init; } = null!;
        public DateTime DispatchedAt { get; init; }
        public EventHandler<TaskRunFinishedEventArgs>? Handler { get; set; }
    }
}
