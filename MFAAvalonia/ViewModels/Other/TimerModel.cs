using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MaaFramework.Binding;
using CommunityToolkit.Mvvm.Input;
using MFAAvalonia.Configuration;
using MFAAvalonia.Extensions;
using System.Collections.Generic;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.Helper.ValueType;
using MFAAvalonia.ViewModels.Pages;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace MFAAvalonia.ViewModels.Other;

/// <summary>
/// 全局定时器模型，所有多开实例共享同一个定时器。
/// 切换配置实际效果为切换多开实例，选用配置实际效果为选用多开实例。
/// </summary>
public partial class TimerModel : ViewModelBase
{
    private static readonly Lazy<TimerModel> LazyInstance = new(() => new TimerModel());
    public static TimerModel Instance => LazyInstance.Value;

    private readonly DispatcherTimer _dispatcherTimer;
    private readonly Dictionary<int, PendingTimerTrigger> _pendingTriggers = new();

    public ObservableCollection<TimerProperties> Timers { get; } = new();

    [ObservableProperty] private bool _customConfig;
    [ObservableProperty] private bool _forceScheduledStart;

    /// <summary>
    /// 补触发窗口（分钟）：错过计划点后，允许在该窗口内补跑一次。
    /// </summary>
    [ObservableProperty] private int _catchUpWindowMinutes;

    public decimal CatchUpWindowValue
    {
        get => CatchUpWindowMinutes;
        set => CatchUpWindowMinutes = (int)Math.Clamp(value, 0, 720);
    }

    partial void OnCatchUpWindowMinutesChanged(int value)
    {
        GlobalConfiguration.SetTimerCatchUpWindowMinutes(value);
        OnPropertyChanged(nameof(CatchUpWindowValue));
    }

    /// <summary>
    /// 实例列表，供 UI ComboBox 绑定（UI 上仍显示为"配置"）
    /// </summary>
    public ObservableCollection<InstanceEntry> InstanceList { get; } = new();

    partial void OnCustomConfigChanged(bool value)
    {
        GlobalConfiguration.SetValue(ConfigurationKeys.CustomConfig, value.ToString());
    }

    partial void OnForceScheduledStartChanged(bool value)
    {
        GlobalConfiguration.SetValue(ConfigurationKeys.ForceScheduledStart, value.ToString());
    }

    private TimerModel()
    {
        CustomConfig = GlobalConfiguration.GetValue(ConfigurationKeys.CustomConfig, bool.FalseString) == bool.TrueString;
        ForceScheduledStart = GlobalConfiguration.GetValue(ConfigurationKeys.ForceScheduledStart, bool.FalseString) == bool.TrueString;
        _catchUpWindowMinutes = GlobalConfiguration.GetTimerCatchUpWindowMinutes();

        var count = GlobalConfiguration.GetTimerCount(8);
        for (var i = 0; i < count; i++)
        {
            Timers.Add(new TimerProperties(i, this));
        }

        _dispatcherTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(1)
        };
        _dispatcherTimer.Tick += CheckTimerElapsed;
        _dispatcherTimer.Start();
        PlatformTimerScheduler.RequestReschedule();
    }

    [RelayCommand]
    public void AddTimer()
    {
        var newId = Timers.Count;
        var timer = new TimerProperties(newId, this);
        if (string.IsNullOrEmpty(timer.TimerConfig) && InstanceList.Count > 0)
        {
            timer.TimerConfig = MaaProcessorManager.Instance.Current.InstanceId;
        }
        Timers.Add(timer);
        GlobalConfiguration.SetTimerCount(Timers.Count);
        PlatformTimerScheduler.RequestReschedule();
    }

    public void RemoveTimer(TimerProperties timer)
    {
        if (Timers.Count <= 1) return;
        var index = Timers.IndexOf(timer);
        if (index < 0) return;

        Timers.RemoveAt(index);
        GlobalConfiguration.RemoveTimerConfig(Timers.Count); // 清除末尾配置

        // 重新编号并保存后续定时器的配置
        for (var i = index; i < Timers.Count; i++)
        {
            var t = Timers[i];
            t.TimerId = i;
            t.SaveAll();
        }

        GlobalConfiguration.SetTimerCount(Timers.Count);
        PlatformTimerScheduler.RequestReschedule();
    }

    /// <summary>
    /// 刷新实例列表（当多开实例增删时调用）
    /// 包含所有实例（含尚未懒加载的），供定时器 ComboBox 使用
    /// </summary>
    public void RefreshInstanceList()
    {
        var manager = MaaProcessorManager.Instance;
        var allInstances = manager.GetAllInstanceIdsAndNames();

        var currentIds = InstanceList.Select(e => e.InstanceId).ToHashSet();
        var newIds = allInstances.Select(x => x.Id).ToHashSet();

        // 移除已不存在的
        var toRemove = InstanceList.Where(e => !newIds.Contains(e.InstanceId)).ToList();
        foreach (var item in toRemove)
            InstanceList.Remove(item);

        // 添加新的
        foreach (var (id, name) in allInstances)
        {
            if (!currentIds.Contains(id))
            {
                InstanceList.Add(new InstanceEntry(id, name));
            }
        }

        // 更新名称
        foreach (var entry in InstanceList)
        {
            var match = allInstances.FirstOrDefault(x => x.Id == entry.InstanceId);
            if (match.Id != null)
                entry.Name = match.Name;
        }

        // 为未选择实例的定时器设置默认值（当前激活实例）
        if (InstanceList.Count > 0)
        {
            var currentInstanceId = manager.Current.InstanceId;
            foreach (var timer in Timers)
            {
                if (string.IsNullOrEmpty(timer.TimerConfig)
                    || InstanceList.All(e => e.InstanceId != timer.TimerConfig))
                {
                    timer.TimerConfig = currentInstanceId;
                }
            }
        }
    }

    /// <summary>
    /// 检查当天是否有定时任务需要触发
    /// </summary>
    public bool HasScheduledTasksToday()
    {
        var now = DateTime.Now;
        return Timers.Any(t => t.IsOn && t.ScheduleConfig.ShouldTrigger(now));
    }

    private void CheckTimerElapsed(object? sender, EventArgs e) => CheckSchedules(DateTime.Now);

    private void CheckSchedules(DateTime now)
    {
        CleanupStalePending(now);

        var due = new List<TimerDue>();
        foreach (var timer in Timers)
        {
            if (!timer.IsOn)
                continue;

            var plan = timer.ScheduleConfig.GetLatestPlannedTime(timer.Time, now);
            if (plan == null)
                continue;

            CollectDue(timer, plan.Value, now, due);
        }

        DispatchDue(due, now);
    }

    /// <summary>
    /// 判断计划点是否仍需处理：已处理则忽略；超出补触发窗口记为错过；否则进入待派发。
    /// </summary>
    private void CollectDue(TimerProperties timer, DateTime plan, DateTime now, List<TimerDue> due)
    {
        var planKey = TimerScheduleConfig.ToPlanKey(plan);
        if (string.Equals(timer.LastPlanKey, planKey, StringComparison.Ordinal))
            return;

        var age = now - plan;
        if (age < TimeSpan.Zero)
            return;

        if (age > TimeSpan.FromMinutes(CatchUpWindowMinutes))
        {
            // 跨天的历史计划点（首次启动、长时间停机后）不属于本次错过，也不消耗，避免启动即显示「错过」
            if (plan.Date != now.Date)
                return;

            timer.SetOutcome(planKey, TimerOutcomes.Missed, plan, now);
            LogTimer(timer, plan, now, TimerOutcomes.Missed, $"ageMinutes={(int)age.TotalMinutes}");
            return;
        }

        if (age > TimeSpan.FromMinutes(1))
            LogTimer(timer, plan, now, "CatchUp", $"ageMinutes={(int)age.TotalMinutes}");

        due.Add(new TimerDue(timer, plan, planKey));
    }

    /// <summary>
    /// 同一 tick 只允许一个槽位真正启动，其余记为跳过（不排队、不抢占）。
    /// </summary>
    private void DispatchDue(List<TimerDue> due, DateTime now)
    {
        if (due.Count == 0)
            return;

        var started = false;
        foreach (var item in due.OrderBy(d => d.Plan).ThenBy(d => d.Timer.TimerId))
        {
            if (started)
            {
                item.Timer.SetOutcome(item.PlanKey, TimerOutcomes.SkippedSerial, item.Plan, now);
                LogTimer(item.Timer, item.Plan, now, TimerOutcomes.SkippedSerial, "sameTick");
                continue;
            }

            if (TryDispatch(item.Timer, item.Plan, item.PlanKey, now))
                started = true;
        }
    }

    /// <summary>
    /// Delivers an AlarmManager trigger. The planned time is used for validation and
    /// de-duplication so a delayed alarm still runs once without racing the minute timer.
    /// </summary>
    public void TriggerFromPlatform(int timerId, long scheduledAtUnixMilliseconds)
    {
        DispatcherHelper.RunOnMainThread(() =>
        {
            var timer = Timers.FirstOrDefault(candidate => candidate.TimerId == timerId);
            if (timer == null || !timer.IsOn)
                return;

            var scheduledTime = DateTimeOffset.FromUnixTimeMilliseconds(scheduledAtUnixMilliseconds)
                .LocalDateTime;
            if (scheduledTime.Second > 0)
                scheduledTime = scheduledTime.AddSeconds(-scheduledTime.Second);
            if (scheduledTime.Hour != timer.Time.Hours
                || scheduledTime.Minute != timer.Time.Minutes
                || !timer.ScheduleConfig.ShouldTrigger(scheduledTime))
                return;

            var due = new List<TimerDue>();
            CollectDue(timer, scheduledTime, DateTime.Now, due);
            DispatchDue(due, DateTime.Now);
        });
    }

    private bool TryDispatch(TimerProperties timer, DateTime plan, string planKey, DateTime now)
    {
        var manager = MaaProcessorManager.Instance;
        var targetInstanceId = CustomConfig && !string.IsNullOrEmpty(timer.TimerConfig)
            ? timer.TimerConfig!
            : manager.Current.InstanceId;

        var vm = manager.GetViewModel(targetInstanceId);
        if (vm == null)
        {
            manager.EnsureInstanceLoaded(targetInstanceId);
            vm = manager.GetViewModel(targetInstanceId);
        }

        if (vm == null)
        {
            timer.SetOutcome(planKey, TimerOutcomes.SkippedMissing, plan, now);
            LogTimer(timer, plan, now, TimerOutcomes.SkippedMissing, $"target={targetInstanceId}");
            return false;
        }

        if (timer.TimerAction == TimerActionType.StopTask)
        {
            timer.SetOutcome(planKey, TimerOutcomes.Dispatched, plan, now);
            LogTimer(timer, plan, now, TimerOutcomes.Dispatched, "stop");
            ExecuteAction(timer, vm);
            return true;
        }

        // 手动/其他实例正在运行时让路；ForceScheduledStart 只允许目标实例自身被抢占。
        if (IsAnyInstanceRunning(ForceScheduledStart ? targetInstanceId : null))
        {
            timer.SetOutcome(planKey, TimerOutcomes.SkippedBusy, plan, now);
            LogTimer(timer, plan, now, TimerOutcomes.SkippedBusy, "deviceBusy");
            return false;
        }

        if (!HasExecutableTasks(vm))
        {
            timer.SetOutcome(planKey, TimerOutcomes.SkippedEmpty, plan, now);
            LogTimer(timer, plan, now, TimerOutcomes.SkippedEmpty, $"target={targetInstanceId}");
            return false;
        }

        // 先消费计划点，保证重启/多 tick 不重复触发
        timer.SetOutcome(planKey, TimerOutcomes.Running, plan, now);
        LogTimer(timer, plan, now, TimerOutcomes.Running, $"target={targetInstanceId}");

        var globalStartEnabled = GlobalConfiguration.GetValue(ConfigurationKeys.GlobalStartEnabled, bool.FalseString) == bool.TrueString;
        if (globalStartEnabled)
        {
            timer.SetOutcome(planKey, TimerOutcomes.Dispatched, plan, now);
            _ = StartAllEmulatorsAndRunTasks();
            return true;
        }

        if (CustomConfig && !string.IsNullOrEmpty(timer.TimerConfig))
        {
            _ = SwitchAndRunAsync(timer, targetInstanceId, vm, planKey, plan);
        }
        else
        {
            BeginPending(timer, vm.Processor, planKey, plan);
            ExecuteAction(timer, vm);
        }

        return true;
    }

    private static bool IsAnyInstanceRunning(string? allowedInstanceId)
    {
        var manager = MaaProcessorManager.Instance;
        foreach (var (id, _) in manager.GetAllInstanceIdsAndNames())
        {
            if (allowedInstanceId != null && id == allowedInstanceId)
                continue;

            var vm = manager.GetViewModel(id);
            if (vm == null)
                continue;

            if (vm.IsRunning || vm.Processor.IsTaskRunActive || vm.Processor.TaskQueue.Count > 0)
                return true;
        }

        return false;
    }

    private static bool HasExecutableTasks(TaskQueueViewModel vm)
    {
        return vm.TaskItemViewModels.Any(task =>
            !task.IsResourceOptionItem && (task.IsChecked || task.IsCheckedWithNull == null));
    }

    private async Task SwitchAndRunAsync(TimerProperties timer, string targetInstanceId,
        TaskQueueViewModel vm, string planKey, DateTime plan)
    {
        try
        {
            await DispatcherHelper.RunOnMainThreadAsync(() =>
            {
                Instances.InstanceTabBarViewModel.SwitchToInstanceById(targetInstanceId);
            });

            await Task.Delay(5000);

            BeginPending(timer, vm.Processor, planKey, plan);
            DispatcherHelper.RunOnMainThread(() => ExecuteAction(timer, vm));
        }
        catch (Exception ex)
        {
            timer.SetOutcome(planKey, TimerOutcomes.Failed, plan, DateTime.Now);
            LoggerHelper.Error($"[定时] slot={timer.TimerId + 1} 切换实例失败：{ex.Message}", ex);
        }
    }

    private void BeginPending(TimerProperties timer, MaaProcessor processor, string planKey, DateTime plan)
    {
        EndPending(timer.TimerId);

        var handler = new EventHandler<TaskRunFinishedEventArgs>((_, args) =>
            OnTaskRunFinished(timer.TimerId, planKey, args));
        var pending = new PendingTimerTrigger
        {
            TimerId = timer.TimerId,
            PlanKey = planKey,
            PlanTime = plan,
            Processor = processor,
            Handler = handler,
            DispatchedAt = DateTime.Now
        };

        _pendingTriggers[timer.TimerId] = pending;
        processor.TaskRunFinished += handler;
    }

    private void OnTaskRunFinished(int timerId, string planKey, TaskRunFinishedEventArgs args)
    {
        if (!_pendingTriggers.TryGetValue(timerId, out var pending)
            || !string.Equals(pending.PlanKey, planKey, StringComparison.Ordinal))
            return;

        EndPending(timerId);

        var timer = Timers.FirstOrDefault(candidate => candidate.TimerId == timerId);
        if (timer == null)
            return;

        var outcome = args.Status switch
        {
            MFATask.MFATaskStatus.SUCCEEDED => TimerOutcomes.Success,
            MFATask.MFATaskStatus.STOPPED => TimerOutcomes.Stopped,
            _ => TimerOutcomes.Failed
        };
        timer.SetOutcome(planKey, outcome, pending.PlanTime, DateTime.Now);
        LogTimer(timer, pending.PlanTime, DateTime.Now, outcome, "finished");
    }

    private void EndPending(int timerId)
    {
        if (_pendingTriggers.Remove(timerId, out var pending))
            pending.Processor.TaskRunFinished -= pending.Handler;
    }

    private void CleanupStalePending(DateTime now)
    {
        foreach (var pending in _pendingTriggers.Values.ToList())
        {
            if (pending.Processor.IsTaskRunActive || now - pending.DispatchedAt < TimeSpan.FromHours(6))
                continue;

            var timer = Timers.FirstOrDefault(candidate => candidate.TimerId == pending.TimerId);
            EndPending(pending.TimerId);
            timer?.SetOutcome(pending.PlanKey, TimerOutcomes.Failed, pending.PlanTime, now);
            LoggerHelper.Error($"[定时] slot={pending.TimerId + 1} 结果回调超时，记为失败。");
        }
    }

    private static void LogTimer(TimerProperties timer, DateTime plan, DateTime now, string outcome, string reason)
    {
        LoggerHelper.Info($"[定时] slot={timer.TimerId + 1} plan={plan:yyyy-MM-dd HH:mm} now={now:yyyy-MM-dd HH:mm:ss} outcome={outcome} reason={reason}");
    }

    /// <summary>
    /// 执行全局启动（若启用）：
    /// 一次拉起全部模拟器并依次执行所有实例任务。
    /// </summary>
    private static async Task StartAllEmulatorsAndRunTasks()
    {
        await GlobalStartManager.StartAllAndRunTasks();
    }

    private void ExecuteAction(TimerProperties timer, TaskQueueViewModel vm)
    {
        if (timer.TimerAction == TimerActionType.StopTask)
        {
            if (!vm.IsRunning) return;

            if (timer.StopConnectedProcess && timer.StopMFA)
            {
                // 先关闭连接的进程，再关闭 MFA
                vm.StopTask(() => MaaProcessor.CloseSoftware(vm.Processor, Instances.ShutdownApplication));
            }
            else if (timer.StopConnectedProcess)
            {
                vm.StopTask(() => MaaProcessor.CloseSoftware(vm.Processor));
            }
            else if (timer.StopMFA)
            {
                vm.StopTask(Instances.ShutdownApplication);
            }
            else
            {
                vm.StopTask();
            }
        }
        else
        {
            if (ForceScheduledStart && vm.IsRunning)
                vm.StopTask(vm.StartTask);
            else
                vm.StartTask();
        }
    }

    /// <summary>
    /// 定时触发结果/跳过原因
    /// </summary>
    public static class TimerOutcomes
    {
        public const string Running = "Running";
        public const string Success = "Success";
        public const string Failed = "Failed";
        public const string Stopped = "Stopped";
        public const string Missed = "Missed";
        public const string SkippedBusy = "SkippedBusy";
        public const string SkippedEmpty = "SkippedEmpty";
        public const string SkippedMissing = "SkippedMissing";
        public const string SkippedSerial = "SkippedSerial";
        public const string Dispatched = "Dispatched";
    }

    private readonly record struct TimerDue(TimerProperties Timer, DateTime Plan, string PlanKey);

    private sealed class PendingTimerTrigger
    {
        public int TimerId { get; init; }
        public string PlanKey { get; init; } = string.Empty;
        public DateTime PlanTime { get; init; }
        public MaaProcessor Processor { get; init; } = null!;
        public EventHandler<TaskRunFinishedEventArgs> Handler { get; init; } = null!;
        public DateTime DispatchedAt { get; init; }
    }

    /// <summary>
    /// 实例条目，用于 UI ComboBox 绑定
    /// </summary>
    public partial class InstanceEntry : ObservableObject
    {
        public string InstanceId { get; }

        [ObservableProperty] private string _name;

        public InstanceEntry(string instanceId, string name)
        {
            InstanceId = instanceId;
            _name = name;
        }
    }

    public partial class TimerProperties : ViewModelBase
    {
        private readonly TimerModel _parent;
        public ObservableCollection<InstanceEntry> InstanceList => _parent.InstanceList;

        public TimerProperties(int timeId, TimerModel parent)
        {
            TimerId = timeId;
            _parent = parent;

            _isOn = GlobalConfiguration.GetTimer(timeId, bool.FalseString) == bool.TrueString;
            var timeStr = GlobalConfiguration.GetTimerTime(timeId, $"{(timeId * 3) % 24}:0");
            _time = TimeSpan.TryParse(timeStr, out var parsed) ? parsed : TimeSpan.Zero;
            _timerConfig = GlobalConfiguration.GetTimerConfig(timeId, string.Empty);
            var actionStr = GlobalConfiguration.GetTimerAction(timeId, "0");
            _timerAction = int.TryParse(actionStr, out var actionVal) ? (TimerActionType)actionVal : TimerActionType.StartTask;
            _stopConnectedProcess = GlobalConfiguration.GetTimerStopConnectedProcess(timeId, bool.FalseString) == bool.TrueString;
            _stopMFA = GlobalConfiguration.GetTimerStopMFA(timeId, bool.FalseString) == bool.TrueString;

            _scheduleConfig = new TimerScheduleConfig(GlobalConfiguration.GetTimerSchedule(timeId, string.Empty));
            LoadLastResult();

            TimerName = $"{LangKeys.Timer.ToLocalization()} {TimerId + 1}";
            LanguageHelper.LanguageChanged += OnLanguageChanged;
        }

        public int TimerId { get; set; }

        private string _lastPlanKey = string.Empty;
        /// <summary>
        /// 最近一次已处理的计划点（yyyyMMddHHmm），用于幂等去重。
        /// </summary>
        public string LastPlanKey => _lastPlanKey;

        private string _lastOutcome = string.Empty;
        public string LastOutcome => _lastOutcome;

        private DateTime? _lastResultAt;
        public DateTime? LastResultAt => _lastResultAt;

        public string LastRunText => _lastResultAt.HasValue
            ? _lastResultAt.Value.ToString("MM-dd HH:mm")
            : LangKeys.ScheduleNeverRun.ToLocalization();

        public string LastResultText => _lastOutcome switch
        {
            TimerOutcomes.Running => LangKeys.ScheduleRunning.ToLocalization(),
            TimerOutcomes.Success => LangKeys.TaskCompleted.ToLocalization(),
            TimerOutcomes.Failed => LangKeys.TaskFailed.ToLocalization(),
            TimerOutcomes.Stopped => LangKeys.TaskStopped.ToLocalization(),
            TimerOutcomes.Missed => LangKeys.ScheduleSkippedMissed.ToLocalization(),
            TimerOutcomes.SkippedBusy => LangKeys.ScheduleSkippedBusy.ToLocalization(),
            TimerOutcomes.SkippedEmpty => LangKeys.ScheduleSkippedEmpty.ToLocalization(),
            TimerOutcomes.SkippedMissing => LangKeys.ScheduleSkippedMissing.ToLocalization(),
            TimerOutcomes.SkippedSerial => LangKeys.ScheduleSkippedSerial.ToLocalization(),
            TimerOutcomes.Dispatched => LangKeys.ScheduleDispatched.ToLocalization(),
            _ => string.Empty
        };

        private void LoadLastResult()
        {
            _lastPlanKey = GlobalConfiguration.GetTimerLastPlan(TimerId, string.Empty);
            var raw = GlobalConfiguration.GetTimerLastResult(TimerId, string.Empty);
            var parts = raw.Split('|');
            if (parts.Length >= 2 && DateTime.TryParse(parts[1], out var at))
            {
                _lastOutcome = parts[0];
                _lastResultAt = at;
            }
        }

        /// <summary>
        /// 记录一次计划点的处理结果，并立刻持久化（保证重启后不重复触发）。
        /// </summary>
        public void SetOutcome(string planKey, string outcome, DateTime planTime, DateTime at)
        {
            _lastPlanKey = planKey;
            _lastOutcome = outcome;
            _lastResultAt = at;

            GlobalConfiguration.SetTimerLastPlan(TimerId, planKey);
            GlobalConfiguration.SetTimerLastResult(TimerId,
                $"{outcome}|{at:yyyy-MM-dd HH:mm:ss}|{planTime:yyyyMMddHHmm}");

            OnPropertyChanged(nameof(LastPlanKey));
            OnPropertyChanged(nameof(LastOutcome));
            OnPropertyChanged(nameof(LastResultAt));
            OnPropertyChanged(nameof(LastRunText));
            OnPropertyChanged(nameof(LastResultText));
        }

        [ObservableProperty] private string _timerName;

        private void OnLanguageChanged(object sender, EventArgs e)
        {
            TimerName = $"{LangKeys.Timer.ToLocalization()} {TimerId + 1}";
            OnPropertyChanged(nameof(LastRunText));
            OnPropertyChanged(nameof(LastResultText));
        }

        private bool _isOn;
        public bool IsOn
        {
            get => _isOn;
            set
            {
                if (!SetProperty(ref _isOn, value)) return;
                GlobalConfiguration.SetTimer(TimerId, value.ToString());
                PlatformTimerScheduler.RequestReschedule();
            }
        }

        private TimeSpan _time;
        public TimeSpan Time
        {
            get => _time;
            set
            {
                if (!SetProperty(ref _time, value)) return;
                GlobalConfiguration.SetTimerTime(TimerId, value.ToString(@"h\:mm"));
                PlatformTimerScheduler.RequestReschedule();
            }
        }

        /// <summary>
        /// 存储的是实例ID（原来存配置名，现在存多开实例ID）
        /// </summary>
        private string? _timerConfig;
        public string? TimerConfig
        {
            get => _timerConfig;
            set
            {
                SetProperty(ref _timerConfig, value ?? string.Empty);
                GlobalConfiguration.SetTimerConfig(TimerId, _timerConfig ?? string.Empty);
            }
        }

        private TimerScheduleConfig _scheduleConfig;
        public TimerScheduleConfig ScheduleConfig
        {
            get => _scheduleConfig;
            set
            {
                SetNewProperty(ref _scheduleConfig, value);
                GlobalConfiguration.SetTimerSchedule(TimerId, _scheduleConfig?.Serialize() ?? string.Empty);
                OnPropertyChanged(nameof(ScheduleDisplayText));
                PlatformTimerScheduler.RequestReschedule();
            }
        }

        private TimerActionType _timerAction;
        public TimerActionType TimerAction
        {
            get => _timerAction;
            set
            {
                SetProperty(ref _timerAction, value);
                GlobalConfiguration.SetTimerAction(TimerId, ((int)value).ToString());
            }
        }

        public int TimerActionIndex
        {
            get => (int)_timerAction;
            set => TimerAction = (TimerActionType)value;
        }

        private bool _stopConnectedProcess;
        public bool StopConnectedProcess
        {
            get => _stopConnectedProcess;
            set
            {
                SetProperty(ref _stopConnectedProcess, value);
                GlobalConfiguration.SetTimerStopConnectedProcess(TimerId, value.ToString());
            }
        }

        private bool _stopMFA;
        public bool StopMFA
        {
            get => _stopMFA;
            set
            {
                SetProperty(ref _stopMFA, value);
                GlobalConfiguration.SetTimerStopMFA(TimerId, value.ToString());
            }
        }

        [RelayCommand]
        private void Remove() => _parent.RemoveTimer(this);

        public string ScheduleDisplayText => _scheduleConfig.GetDisplayText();

        public void UpdateScheduleConfig()
        {
            GlobalConfiguration.SetTimerSchedule(TimerId, _scheduleConfig.Serialize());
            OnPropertyChanged(nameof(ScheduleDisplayText));
            PlatformTimerScheduler.RequestReschedule();
        }

        /// <summary>
        /// 重新编号后保存所有配置到新ID
        /// </summary>
        public void SaveAll()
        {
            TimerName = $"{LangKeys.Timer.ToLocalization()} {TimerId + 1}";
            GlobalConfiguration.SetTimer(TimerId, _isOn.ToString());
            GlobalConfiguration.SetTimerTime(TimerId, _time.ToString(@"h\:mm"));
            GlobalConfiguration.SetTimerConfig(TimerId, _timerConfig ?? string.Empty);
            GlobalConfiguration.SetTimerAction(TimerId, ((int)_timerAction).ToString());
            GlobalConfiguration.SetTimerSchedule(TimerId, _scheduleConfig?.Serialize() ?? string.Empty);
            GlobalConfiguration.SetTimerStopConnectedProcess(TimerId, _stopConnectedProcess.ToString());
            GlobalConfiguration.SetTimerStopMFA(TimerId, _stopMFA.ToString());
            GlobalConfiguration.SetTimerLastPlan(TimerId, _lastPlanKey);
            if (_lastResultAt.HasValue)
                GlobalConfiguration.SetTimerLastResult(TimerId,
                    $"{_lastOutcome}|{_lastResultAt.Value:yyyy-MM-dd HH:mm:ss}|{_lastPlanKey}");
        }
    }
}
