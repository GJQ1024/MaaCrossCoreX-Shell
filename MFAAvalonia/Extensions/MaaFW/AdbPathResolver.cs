using System;
using System.Collections.Generic;
using System.IO;

namespace MFAAvalonia.Extensions.MaaFW;

/// <summary>
/// adb.exe 路径解析。
/// 创建控制器前必须拿到一个真实存在的绝对路径；解析失败时返回 false，
/// 调用方必须报错，绝不能把裸 "adb" 传给 MaaAdbController（PATH 里没有 adb 时连接会在毫秒级失败）。
/// 解析顺序：配置值 → 设备名称中的 adb 路径 → MuMu 各安装布局 → PATH。
/// </summary>
internal static class AdbPathResolver
{
    private const string AdbExeName = "adb.exe";

    private static readonly string[] MumuRelativeLayouts =
    [
        Path.Combine("nx_main", "adb.exe"),
        Path.Combine("shell", "adb.exe"),
        Path.Combine("nx_device", "12.0", "shell", "adb.exe"),
        "adb.exe",
    ];

    private static readonly string[] MumuDefaultInstallRoots =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Netease", "MuMu Player 12"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Netease", "MuMuPlayer-12.0"),
    ];

    /// <summary>
    /// 解析 adb.exe 绝对路径。
    /// </summary>
    /// <param name="configured">用户配置的 adb 路径（可能是裸 "adb" 或空）。</param>
    /// <param name="deviceName">MaaToolkit 返回的设备名称。</param>
    /// <param name="softwarePath">设置里的模拟器程序路径（通常是 MuMuManager.exe）。</param>
    /// <param name="adbPath">解析成功时的绝对路径。</param>
    /// <param name="source">命中来源，用于日志。</param>
    public static bool TryResolve(string? configured, string? deviceName, string? softwarePath,
        out string adbPath, out string source)
    {
        adbPath = string.Empty;
        source = string.Empty;

        // 1. 配置值：只接受真实存在的文件，裸 "adb" 视为未配置
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var configuredPath = configured.Trim().Trim('"');
            if (Accept(configuredPath, "配置值", out adbPath, out source))
                return true;
        }

        // 2. 设备名称里带的 adb 路径（用户只选了设备、没手填 adb 时的主要来源）
        foreach (var candidate in EnumerateDeviceNameCandidates(deviceName))
        {
            if (Accept(candidate, "设备名称", out adbPath, out source))
                return true;
        }

        // 3. MuMu 各安装布局
        foreach (var root in EnumerateMumuRoots(softwarePath))
        {
            foreach (var layout in MumuRelativeLayouts)
            {
                var candidate = Path.Combine(root, layout);
                if (Accept(candidate, candidate, out adbPath, out source))
                    return true;
            }
        }

        // 4. PATH
        var fromPath = FindOnPath();
        if (fromPath != null)
        {
            adbPath = fromPath;
            source = "PATH";
            return true;
        }

        return false;
    }

    /// <summary>解析结果是否是可用的绝对路径。</summary>
    public static bool IsUsable(string? adbPath) =>
        !string.IsNullOrWhiteSpace(adbPath) && Path.IsPathFullyQualified(adbPath) && File.Exists(adbPath);

    private static bool Accept(string candidate, string sourceName, out string adbPath, out string source)
    {
        adbPath = string.Empty;
        source = string.Empty;
        if (string.IsNullOrWhiteSpace(candidate))
            return false;

        var trimmed = candidate.Trim().Trim('"');
        if (!File.Exists(trimmed))
            return false;

        adbPath = Path.GetFullPath(trimmed);
        source = sourceName;
        return true;
    }

    private static IEnumerable<string> EnumerateDeviceNameCandidates(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            yield break;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // MaaToolkit 把 adb 路径拼在序列号后面："127.0.0.1:16384-C:/.../nx_main/adb.exe"。
        // 按 "adb.exe" 反查盘符起点，避免路径中间恰好含 "adb" 的目录名被截断。
        var searchIndex = 0;
        while (searchIndex < deviceName.Length)
        {
            var adbIndex = deviceName.IndexOf(AdbExeName, searchIndex, StringComparison.OrdinalIgnoreCase);
            if (adbIndex < 0)
                break;

            var end = adbIndex + AdbExeName.Length;
            var start = FindDriveLetterStart(deviceName, adbIndex);
            if (start >= 0)
            {
                var candidate = deviceName[start..end];
                if (seen.Add(candidate))
                    yield return candidate;
            }

            searchIndex = end;
        }

        // 兜底：设备名称用 "-" 拼接，取最后一段
        var dashIndex = deviceName.LastIndexOf('-');
        if (dashIndex >= 0 && dashIndex + 1 < deviceName.Length)
        {
            var tail = deviceName[(dashIndex + 1)..].Trim();
            if (tail.Length > 0 && seen.Add(tail))
                yield return tail;
        }
    }

    private static int FindDriveLetterStart(string text, int endExclusive)
    {
        for (var index = endExclusive - 1; index >= 1; index--)
        {
            // "X:" 且冒号前是字母；序列号里的冒号（127.0.0.1:16384）前面是数字，不会误判
            if (text[index] == ':' && char.IsLetter(text[index - 1]))
                return index - 1;
        }

        return -1;
    }

    private static IEnumerable<string> EnumerateMumuRoots(string? softwarePath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(softwarePath))
        {
            var path = softwarePath.Trim().Trim('"');

            // SoftwarePath 指向 <MuMu 安装目录>\nx_main\MuMuManager.exe（旧版为 \shell\MuMuManager.exe）
            if (File.Exists(path))
            {
                var directory = Path.GetDirectoryName(path);
                var root = directory == null ? null : Path.GetDirectoryName(directory);
                if (root != null && seen.Add(root))
                    yield return root;
            }
            else if (Directory.Exists(path) && seen.Add(path))
            {
                yield return path;
            }
        }

        foreach (var root in MumuDefaultInstallRoots)
        {
            if (Directory.Exists(root) && seen.Add(root))
                yield return root;
        }
    }

    private static string? FindOnPath()
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathVariable))
            return null;

        foreach (var rawDirectory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = rawDirectory.Trim().Trim('"');
            if (directory.Length == 0)
                continue;

            foreach (var name in new[] { "adb.exe", "adb" })
            {
                try
                {
                    var candidate = Path.Combine(directory, name);
                    if (File.Exists(candidate))
                        return Path.GetFullPath(candidate);
                }
                catch (ArgumentException)
                {
                    // PATH 里的非法片段直接跳过
                }
            }
        }

        return null;
    }
}
