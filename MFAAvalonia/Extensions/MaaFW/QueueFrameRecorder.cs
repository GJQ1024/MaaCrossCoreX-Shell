using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using MaaFramework.Binding;
using MaaFramework.Binding.Buffers;
using MFAAvalonia.Helper;
using SkiaSharp;

namespace MFAAvalonia.Extensions.MaaFW;

/// <summary>
/// 操作期帧记录器。
/// 只在任务队列运行期间，以约 1 fps 把 MaaFramework 截图任务执行器缓存的帧落盘到
/// &lt;root&gt;\&lt;yyyy-MM-dd&gt;\&lt;任务名&gt;\&lt;HHmmss-fff&gt;.png，并在当天目录维护 index.csv。
/// 帧直接复用框架已有的截图流水线（独立截图任务执行器的缓存），不额外开启 adb 截图通道；
/// 任何截图/写盘失败都只记日志，绝不影响主任务。
/// </summary>
internal sealed class QueueFrameRecorder : IDisposable
{
    private const string DefaultRoot = @"E:\MAA-screenshots";
    private const int DefaultIntervalMs = 1000;
    private const string StartupTaskName = "启动准备";
    private const int MaxConsecutiveDiskFailures = 5;

    private readonly MaaProcessor _processor;
    private readonly string _root;
    private readonly int _intervalMs;
    private readonly bool _enabled;

    private readonly object _sync = new();
    private readonly HashSet<string> _touchedIndexFiles = [];
    private readonly StringBuilder _lineBuilder = new();

    private Timer? _timer;
    private int _tickBusy;
    private int _active;
    private int _diskBlocked;
    private int _sessionId;
    private int _frameCount;
    private int _consecutiveCaptureFailures;
    private int _consecutiveDiskFailures;
    private string _currentTask = StartupTaskName;
    private string _currentDate = string.Empty;
    private StreamWriter? _indexWriter;
    private string _indexPath = string.Empty;
    private SKBitmap? _bitmapCache;

    public QueueFrameRecorder(MaaProcessor processor)
    {
        _processor = processor;
        _root = Environment.GetEnvironmentVariable("MAA_FRAME_RECORDER_ROOT") is { Length: > 0 } root
            ? root
            : DefaultRoot;
        _enabled = !string.Equals(Environment.GetEnvironmentVariable("MAA_FRAME_RECORDER"), "0",
            StringComparison.OrdinalIgnoreCase);
        _intervalMs = int.TryParse(Environment.GetEnvironmentVariable("MAA_FRAME_RECORDER_INTERVAL_MS"),
            out var interval) && interval >= 200
            ? interval
            : DefaultIntervalMs;
        // session 编号按进程号起算：多个壳进程共用同一份 index.csv 时，
        // 避免收尾回填把「同号」的历史行一起改掉。
        _sessionId = Environment.ProcessId * 1000;
    }

    /// <summary>队列开始运行：启动定时落帧。</summary>
    public void Begin()
    {
        if (!_enabled)
            return;

        lock (_sync)
        {
            _timer ??= new Timer(OnTick, null, Timeout.Infinite, Timeout.Infinite);
            _sessionId++;
            _currentTask = StartupTaskName;
            _touchedIndexFiles.Clear();
            _consecutiveCaptureFailures = 0;
            _consecutiveDiskFailures = 0;
            _frameCount = 0;
            Volatile.Write(ref _diskBlocked, 0);
            Volatile.Write(ref _active, 1);
            LoggerHelper.Info($"[帧记录] 开始：session={_sessionId}，根目录={_root}，间隔={_intervalMs}ms");
            _timer.Change(0, _intervalMs);
        }
    }

    /// <summary>队列结束（成功/失败/停止）：停止落帧并回填任务结果。</summary>
    public void End(string result)
    {
        if (!_enabled)
            return;

        lock (_sync)
        {
            if (Volatile.Read(ref _active) == 0)
                return;

            Volatile.Write(ref _active, 0);
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        }

        // 等可能在途的一次落盘结束，确保 End 返回后不再有任何写盘。
        var deadline = Environment.TickCount64 + 1500;
        while (Volatile.Read(ref _tickBusy) != 0 && Environment.TickCount64 < deadline)
            Thread.Sleep(10);

        lock (_sync)
        {
            var session = _sessionId;
            CloseIndexWriter();
            FinalizeSession(session, result);
            LoggerHelper.Info($"[帧记录] 结束：session={session}，结果={result}，帧数={_frameCount}");
        }
    }

    /// <summary>任务切换：后续帧落到新任务目录。空名称（例如停止时的清理）忽略。</summary>
    public void SetTask(string? taskName)
    {
        if (!_enabled || string.IsNullOrWhiteSpace(taskName))
            return;

        Volatile.Write(ref _currentTask, SanitizeTaskName(taskName));
    }

    private void OnTick(object? state)
    {
        if (Volatile.Read(ref _active) == 0 || Volatile.Read(ref _diskBlocked) != 0)
            return;

        if (Interlocked.Exchange(ref _tickBusy, 1) == 1)
            return;

        try
        {
            // 非阻塞提交截图任务；在途帧会被复用，不会堆积。
            var status = _processor.PostScreencapPipelined();
            if (status is MaaJobStatus.Failed or MaaJobStatus.Invalid)
            {
                if (++_consecutiveCaptureFailures == 1 || _consecutiveCaptureFailures % 30 == 0)
                    LoggerHelper.Warning($"[帧记录] 截图不可用（{status}），已跳过第 {_consecutiveCaptureFailures} 帧。");
                return;
            }

            using var buffer = _processor.GetLiveViewBuffer(false);
            if (buffer == null || buffer.IsEmpty)
                return;

            _consecutiveCaptureFailures = 0;

            var task = Volatile.Read(ref _currentTask) ?? StartupTaskName;
            var now = DateTime.Now;
            var dateFolder = now.ToString("yyyy-MM-dd");
            var taskFolder = Path.Combine(_root, dateFolder, task);
            var timeName = now.ToString("HHmmss-fff");
            var framePath = Path.Combine(taskFolder, timeName + ".png");

            // PNG 落盘与索引写入必须同处一个锁区间：End 会在同一把锁内停表并收尾，
            // 若分开加锁，收尾可能插在「已写 PNG、未写索引」之间，产生无索引的孤立帧。
            lock (_sync)
            {
                if (Volatile.Read(ref _active) == 0)
                    return;

                try
                {
                    Directory.CreateDirectory(taskFolder);
                    if (!TryEncodePng(buffer, framePath))
                        return;
                }
                catch (Exception ex)
                {
                    OnDiskFailureLocked(ex);
                    return;
                }

                _consecutiveDiskFailures = 0;
                AppendIndexLocked(now, task, framePath, dateFolder);
                _frameCount++;
            }
        }
        catch (Exception ex)
        {
            if (_consecutiveCaptureFailures++ == 0)
                LoggerHelper.Warning($"[帧记录] 本轮落帧异常（不影响任务）：{ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _tickBusy, 0);
        }
    }

    /// <summary>写入索引行。调用方必须已持有 _sync。</summary>
    private void AppendIndexLocked(DateTime now, string task, string framePath, string dateFolder)
    {
        try
        {
            EnsureIndexWriter(dateFolder);
            if (_indexWriter == null)
                return;

            _lineBuilder.Clear();
            _lineBuilder.Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(',')
                .Append(EscapeCsv(task)).Append(',')
                .Append(EscapeCsv(framePath)).Append(',')
                .Append("running").Append(',')
                .Append(_sessionId);
            _indexWriter.WriteLine(_lineBuilder.ToString());
            _indexWriter.Flush();
        }
        catch (Exception ex)
        {
            OnDiskFailureLocked(ex);
        }
    }

    private void OnDiskFailureLocked(Exception ex)
    {
        if (++_consecutiveDiskFailures < MaxConsecutiveDiskFailures)
            return;

        if (Interlocked.Exchange(ref _diskBlocked, 1) == 0)
        {
            CloseIndexWriter();
            LoggerHelper.Warning($"[帧记录] 连续写索引失败，已停止本次录制（不影响任务）：{ex.Message}");
        }
    }

    private void EnsureIndexWriter(string dateFolder)
    {
        if (_indexWriter != null && string.Equals(_currentDate, dateFolder, StringComparison.Ordinal))
            return;

        CloseIndexWriter();

        var folder = Path.Combine(_root, dateFolder);
        Directory.CreateDirectory(folder);
        _indexPath = Path.Combine(folder, "index.csv");
        _currentDate = dateFolder;

        var isNew = !File.Exists(_indexPath);
        _indexWriter = new StreamWriter(_indexPath, append: true, new UTF8Encoding(false));
        if (isNew)
        {
            _indexWriter.WriteLine("time,task,frame,result,session");
            _indexWriter.Flush();
        }

        _touchedIndexFiles.Add(_indexPath);
    }

    private void CloseIndexWriter()
    {
        try
        {
            _indexWriter?.Flush();
            _indexWriter?.Dispose();
        }
        catch (Exception ex)
        {
            LoggerHelper.Debug($"[帧记录] 关闭索引文件失败：{ex.Message}");
        }
        finally
        {
            _indexWriter = null;
            _indexPath = string.Empty;
            _currentDate = string.Empty;
        }
    }

    /// <summary>把本次会话写入的所有行从 running 回填为最终结果。</summary>
    private void FinalizeSession(int session, string result)
    {
        var sessionText = session.ToString();
        foreach (var path in _touchedIndexFiles)
        {
            try
            {
                if (!File.Exists(path))
                    continue;

                var lines = File.ReadAllLines(path, Encoding.UTF8);
                var changed = false;
                for (var i = 0; i < lines.Length; i++)
                {
                    if (i == 0 && lines[i].StartsWith("time,", StringComparison.Ordinal))
                        continue;

                    var fields = ParseCsvLine(lines[i]);
                    if (fields.Count < 5 || !string.Equals(fields[4], sessionText, StringComparison.Ordinal))
                        continue;

                    if (string.Equals(fields[3], result, StringComparison.Ordinal))
                        continue;

                    fields[3] = result;
                    lines[i] = string.Join(',', fields.Select(EscapeCsv));
                    changed = true;
                }

                if (changed)
                    File.WriteAllLines(path, lines, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                LoggerHelper.Debug($"[帧记录] 回填索引失败（{path}）：{ex.Message}");
            }
        }
    }

    private bool TryEncodePng(MaaImageBuffer buffer, string path)
    {
        if (!buffer.TryGetRawData(out var rawData, out var width, out var height, out _))
            return false;
        if (rawData == IntPtr.Zero || width <= 0 || height <= 0)
            return false;

        var channels = buffer.Channels;
        if (channels is not (3 or 4))
            return false;

        if (_bitmapCache == null
            || _bitmapCache.Width != width
            || _bitmapCache.Height != height)
        {
            _bitmapCache?.Dispose();
            _bitmapCache = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        }

        var dstPtr = _bitmapCache.GetPixels();
        if (dstPtr == IntPtr.Zero)
            return false;

        var dstStride = _bitmapCache.RowBytes;
        var srcStride = width * channels;
        unsafe
        {
            var src = (byte*)rawData;
            var dst = (byte*)dstPtr;
            if (channels == 4)
            {
                var rowBytes = Math.Min(srcStride, dstStride);
                for (var y = 0; y < height; y++)
                    Buffer.MemoryCopy(src + y * srcStride, dst + y * dstStride, dstStride, rowBytes);
            }
            else
            {
                for (var y = 0; y < height; y++)
                {
                    var srcRow = src + y * srcStride;
                    var dstRow = dst + y * dstStride;
                    for (var x = 0; x < width; x++)
                    {
                        var si = x * 3;
                        var di = x * 4;
                        dstRow[di] = srcRow[si];
                        dstRow[di + 1] = srcRow[si + 1];
                        dstRow[di + 2] = srcRow[si + 2];
                        dstRow[di + 3] = 255;
                    }
                }
            }
        }

        using var image = SKImage.FromBitmap(_bitmapCache);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        if (data == null)
            return false;

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        data.SaveTo(stream);
        return true;
    }

    private static string SanitizeTaskName(string taskName)
    {
        var trimmed = taskName.Trim();
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(trimmed.Length);
        foreach (var c in trimmed)
        {
            builder.Append(Array.IndexOf(invalid, c) >= 0 || c is '/' or '\\' or ':' ? '_' : c);
        }

        var name = builder.ToString().Trim();
        if (name.Length == 0)
            return StartupTaskName;
        return name.Length > 60 ? name[..60] : name;
    }

    private static string EscapeCsv(string value)
    {
        if (value.IndexOfAny([',', '"', '\r', '\n']) < 0)
            return value;
        return '"' + value.Replace("\"", "\"\"") + '"';
    }

    private static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        var builder = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        builder.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    builder.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                result.Add(builder.ToString());
                builder.Clear();
            }
            else
            {
                builder.Append(c);
            }
        }

        result.Add(builder.ToString());
        return result;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            Volatile.Write(ref _active, 0);
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
            _timer?.Dispose();
            _timer = null;
            CloseIndexWriter();
            _bitmapCache?.Dispose();
            _bitmapCache = null;
        }
    }
}
