using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace MFAAvalonia.Helper;

/// <summary>
/// 配置文件写盘工具：同路径串行化 + 临时文件原子替换。
/// 直接 File.WriteAllText 在“切换设备 / 启动任务 / 界面保存”并发时会撞上
/// "The process cannot access the file because it is being used by another process"，
/// 结果是用户的配置改动被静默丢弃。
/// </summary>
public static class AtomicFileWriter
{
    private const int MaxAttempts = 6;

    private static readonly ConcurrentDictionary<string, object> Gates =
        new(StringComparer.OrdinalIgnoreCase);

    public static void WriteAllText(string filePath, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var fullPath = Path.GetFullPath(filePath);
        var gate = Gates.GetOrAdd(fullPath, static _ => new object());

        lock (gate)
        {
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            var tempPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        File.WriteAllText(tempPath, content);

                        if (File.Exists(fullPath))
                        {
                            // 同目录同卷，NTFS 上是原子替换；读取方要么看到旧文件，要么看到新文件。
                            File.Replace(tempPath, fullPath, null, ignoreMetadataErrors: true);
                        }
                        else
                        {
                            File.Move(tempPath, fullPath);
                        }

                        return;
                    }
                    catch (IOException) when (attempt < MaxAttempts)
                    {
                        Thread.Sleep(20 * attempt);
                    }
                    catch (UnauthorizedAccessException) when (attempt < MaxAttempts)
                    {
                        Thread.Sleep(20 * attempt);
                    }
                }
            }
            finally
            {
                TryDelete(tempPath);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 清理失败不影响主流程，残留的 .tmp 不会覆盖任何配置
        }
    }
}
