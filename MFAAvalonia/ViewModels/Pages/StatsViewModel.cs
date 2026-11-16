using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MFAAvalonia.Helper;

namespace MFAAvalonia.ViewModels.Pages;

public partial class StatsViewModel : ViewModelBase
{
    private bool _initializing;

    public ObservableCollection<string> Accounts { get; } = new();
    public ObservableCollection<string> Ranges { get; } = new() { "今天", "近 7 天", "全部" };
    public ObservableCollection<StatsRowItem> Rows { get; } = new();

    [ObservableProperty]
    private string? _selectedAccount;

    [ObservableProperty]
    private int _selectedRangeIndex = 1;

    [ObservableProperty]
    private string _outputApText = "—";

    [ObservableProperty]
    private string _outputCoinText = "—";

    [ObservableProperty]
    private string _outputGemText = "—";

    [ObservableProperty]
    private string _consumeApText = "—";

    [ObservableProperty]
    private string _consumeCoinText = "—";

    [ObservableProperty]
    private string _consumeGemText = "—";

    [ObservableProperty]
    private string _successText = "—";

    [ObservableProperty]
    private string _failureText = "—";

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isEmpty = true;

    public string EmptyHint => "暂无数据：所选范围内没有任务记录（统计只在任务前后打点，没有任务就没有数据）";

    public string DataDirectory => ResourceStatsStore.DirectoryPath;

    public StatsViewModel()
    {
        _initializing = true;
        ReloadAccounts(null);
        _initializing = false;
        Refresh();
    }

    partial void OnSelectedAccountChanged(string? value)
    {
        if (!_initializing)
            Refresh();
    }

    partial void OnSelectedRangeIndexChanged(int value)
    {
        if (!_initializing)
            Refresh();
    }

    [RelayCommand]
    private void Refresh()
    {
        var (from, to, _) = ResolveRange();
        var snapshot = ResourceStatsStore.Load(SelectedAccount ?? string.Empty, from, to);

        Rows.Clear();
        foreach (var task in snapshot.Tasks)
            Rows.Add(StatsRowItem.From(task));

        IsEmpty = snapshot.TotalRuns == 0 && snapshot.TotalInterrupted == 0;

        OutputApText = FormatProduced("体力", snapshot.ProducedAp);
        OutputCoinText = FormatProduced("金币", snapshot.ProducedCoin);
        OutputGemText = FormatProduced("粲晶", snapshot.ProducedGem);
        ConsumeApText = FormatConsumed("体力", snapshot.ConsumedAp);
        ConsumeCoinText = FormatConsumed("金币", snapshot.ConsumedCoin);
        ConsumeGemText = FormatConsumed("粲晶", snapshot.ConsumedGem);

        var hasRecords = snapshot.TotalRuns > 0 || snapshot.TotalInterrupted > 0;
        SuccessText = hasRecords ? $"{snapshot.TotalSuccess:N0} 次" : "—";
        FailureText = hasRecords ? $"{snapshot.TotalFailures:N0} 次" : "—";

        StatusText = $"范围：{Ranges[Math.Clamp(SelectedRangeIndex, 0, Ranges.Count - 1)]} · " +
                     $"运行 {snapshot.TotalRuns:N0} · 成功 {snapshot.TotalSuccess:N0} · " +
                     $"识别失败 {snapshot.TotalFailures:N0} · 中断 {snapshot.TotalInterrupted:N0}";
    }

    [RelayCommand]
    private void ExportCsv()
    {
        try
        {
            var (from, to, tag) = ResolveRange();
            var account = string.IsNullOrWhiteSpace(SelectedAccount) ? "default" : SelectedAccount!;
            var (path, rows) = ResourceStatsStore.ExportCsv(account, from, to, tag);
            StatusText = $"已导出 {rows:N0} 条记录 → {path}";
            ToastHelper.Info("CSV 已导出", path);
        }
        catch (Exception ex)
        {
            StatusText = $"导出失败：{ex.Message}";
            LoggerHelper.Error($"[资源统计] 导出 CSV 失败：{ex.Message}");
        }
    }

    private void ReloadAccounts(string? preferred)
    {
        var accounts = ResourceStatsStore.ListAccounts();
        Accounts.Clear();
        foreach (var account in accounts)
            Accounts.Add(account);

        var target = preferred;
        if (string.IsNullOrWhiteSpace(target) || !Accounts.Contains(target))
            target = Accounts.Contains("default") ? "default" : Accounts.FirstOrDefault();

        SelectedAccount = target;
    }

    private (DateTime? From, DateTime? To, string Tag) ResolveRange()
    {
        var today = DateTime.Today;
        return SelectedRangeIndex switch
        {
            0 => (today, today.AddDays(1).AddTicks(-1), today.ToString("yyyy-MM-dd")),
            1 => (today.AddDays(-6), today.AddDays(1).AddTicks(-1), "7d"),
            _ => (null, null, "all")
        };
    }

    private static string FormatProduced(string label, int? value) =>
        value is null ? "—" : $"{label} +{value.Value:N0}";

    private static string FormatConsumed(string label, int? value) =>
        value is null ? "—" : $"{label} -{Math.Abs(value.Value):N0}";
}

public sealed class StatsRowItem
{
    public string Name { get; init; } = string.Empty;
    public string ProduceText { get; init; } = "—";
    public string ConsumeText { get; init; } = "—";
    public string RunsText { get; init; } = "—";
    public string SuccessText { get; init; } = "—";
    public string FailureText { get; init; } = "—";
    public string InterruptedText { get; init; } = "—";
    public string? Note { get; init; }
    public bool IsEmptyRow { get; init; }
    public double Opacity => IsEmptyRow ? 0.55 : 1.0;

    public static StatsRowItem From(TaskStats task)
    {
        var empty = task.Runs == 0 && task.Interrupted == 0 && !task.HasDelta;
        return new StatsRowItem
        {
            Name = task.Name,
            IsEmptyRow = empty,
            ProduceText = empty ? "无运行记录" : JoinResources(task.ProducedAp, task.ProducedCoin, task.ProducedGem, "+"),
            ConsumeText = empty ? "—" : JoinResources(task.ConsumedAp, task.ConsumedCoin, task.ConsumedGem, "-"),
            RunsText = task.Runs > 0 ? task.Runs.ToString("N0", CultureInfo.CurrentCulture) : "—",
            SuccessText = task.Runs > 0 ? task.Successes.ToString("N0", CultureInfo.CurrentCulture) : "—",
            FailureText = task.Runs > 0 ? task.Failures.ToString("N0", CultureInfo.CurrentCulture) : "—",
            InterruptedText = task.Interrupted > 0 ? task.Interrupted.ToString("N0", CultureInfo.CurrentCulture) : "—",
            Note = task.Runs > 0 && task.Failures == task.Runs ? "识别失败" :
                task.Interrupted > 0 && task.Runs == 0 ? "中断记录" : null
        };
    }

    private static string JoinResources(int? ap, int? coin, int? gem, string sign)
    {
        var parts = new[]
        {
            ap is null ? null : $"体力 {sign}{Math.Abs(ap.Value):N0}",
            coin is null ? null : $"金币 {sign}{Math.Abs(coin.Value):N0}",
            gem is null ? null : $"粲晶 {sign}{Math.Abs(gem.Value):N0}"
        }.Where(part => part is not null);

        var text = string.Join(" / ", parts);
        return text.Length == 0 ? "—" : text;
    }
}
