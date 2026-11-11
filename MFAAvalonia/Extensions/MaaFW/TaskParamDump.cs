using MFAAvalonia.Helper;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Text;

namespace MFAAvalonia.Extensions.MaaFW;

/// <summary>
/// 离线导出当前实例配置下每条可执行任务的最终参数（dry-run）。
/// 由隐藏启动参数 <c>--dump-task-params &lt;输出文件&gt;</c> 触发；
/// 可选 <c>--data-root &lt;目录&gt;</c> 指向一份可写的数据目录副本，避免读取生产目录时写回配置。
/// 不连接设备、不启动任务、不执行识别。
/// </summary>
public static class TaskParamDump
{
    public const string ArgumentName = "dump-task-params";
    public const string DataRootArgumentName = "data-root";

    public static bool IsRequested { get; private set; }

    public static string? OutputPath { get; private set; }

    /// <summary>
    /// 在 Avalonia / AppPaths 初始化之前解析隐藏参数。
    /// </summary>
    public static void TryParseArguments(string[] args)
    {
        var parsed = AppRuntime.ParseArguments(args);
        if (!parsed.TryGetValue(ArgumentName, out var outputPath) || string.IsNullOrWhiteSpace(outputPath))
            return;

        IsRequested = true;
        OutputPath = Path.GetFullPath(outputPath);
        if (parsed.TryGetValue(DataRootArgumentName, out var dataRoot) && !string.IsNullOrWhiteSpace(dataRoot))
            AppPaths.SetDataRootOverride(dataRoot);
    }

    /// <summary>
    /// 导出当前实例的任务参数，返回进程退出码。
    /// </summary>
    public static int Write()
    {
        var path = OutputPath;
        if (string.IsNullOrWhiteSpace(path))
            return 2;

        var processor = MaaProcessorManager.Instance.Current;
        var snapshots = processor.DumpTaskParams();

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        using (var writer = new StreamWriter(path, false, new UTF8Encoding(false)) { NewLine = "\n" })
        {
            foreach (var snapshot in snapshots)
            {
                var param = string.IsNullOrWhiteSpace(snapshot.Param)
                    ? new JObject()
                    : JToken.Parse(snapshot.Param);
                var row = new JObject
                {
                    ["task"] = snapshot.Task,
                    ["entry"] = snapshot.Entry,
                    ["count"] = snapshot.Count,
                    ["param"] = param
                };
                writer.WriteLine(row.ToString(Formatting.None));
            }
        }

        LoggerHelper.Info($"[参数导出] 实例={processor.InstanceId} 任务数={snapshots.Count} 输出={path}");
        try
        {
            Console.WriteLine($"[参数导出] 实例={processor.InstanceId} 任务数={snapshots.Count} 输出={path}");
        }
        catch
        {
            // WinExe 没有控制台时忽略。
        }
        return 0;
    }
}
