using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using Xunit;

namespace MFAAvalonia.Core.Tests;

public class AdbPathResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mfa-adb-resolve-" + Guid.NewGuid().ToString("N"));

    public AdbPathResolverTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响断言结果
        }
    }

    private string CreateAdbFile(params string[] relativeParts)
    {
        var directory = Path.Combine(new[] { _root }.Concat(relativeParts).ToArray());
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "adb.exe");
        File.WriteAllText(file, "fake adb");
        return file;
    }

    [Fact]
    public void UsesConfiguredPathWhenFileExists()
    {
        var adb = CreateAdbFile("custom");
        var ok = AdbPathResolver.TryResolve(adb, null, null, out var resolved, out var source);

        Assert.True(ok);
        Assert.Equal(Path.GetFullPath(adb), resolved);
        Assert.Equal("配置值", source);
    }

    [Fact]
    public void IgnoresBareAdbAndResolvesFromDeviceName()
    {
        var adb = CreateAdbFile("nx_main");
        var deviceName = "127.0.0.1:16384-" + adb.Replace('\\', '/');

        var ok = AdbPathResolver.TryResolve("adb", deviceName, null, out var resolved, out var source);

        Assert.True(ok);
        Assert.Equal(Path.GetFullPath(adb), resolved);
        Assert.Equal("设备名称", source);
    }

    [Fact]
    public void ResolvesFromMuMuLayoutUnderSoftwarePath()
    {
        var adb = CreateAdbFile("nx_device", "12.0", "shell");
        var manager = Path.Combine(_root, "nx_main", "MuMuManager.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(manager)!);
        File.WriteAllText(manager, string.Empty);

        var ok = AdbPathResolver.TryResolve(null, null, manager, out var resolved, out _);

        Assert.True(ok);
        Assert.Equal(Path.GetFullPath(adb), resolved);
    }

    [Fact]
    public void PrefersNxMainLayoutWhenPresent()
    {
        var nxMain = CreateAdbFile("nx_main");
        CreateAdbFile("shell");
        var manager = Path.Combine(_root, "nx_main", "MuMuManager.exe");
        File.WriteAllText(manager, string.Empty);

        var ok = AdbPathResolver.TryResolve(string.Empty, string.Empty, manager, out var resolved, out _);

        Assert.True(ok);
        Assert.Equal(Path.GetFullPath(nxMain), resolved);
    }

    [Fact]
    public void FallsBackToPathLookup()
    {
        var adb = CreateAdbFile("onpath");
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", Path.GetDirectoryName(adb));
            var ok = AdbPathResolver.TryResolve("adb", null, null, out var resolved, out var source);

            Assert.True(ok);
            Assert.Equal(Path.GetFullPath(adb), resolved);
            Assert.Equal("PATH", source);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }

    [Fact]
    public void ReturnsFalseWhenNothingCanBeFound()
    {
        var programFilesRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Netease", "MuMu Player 12");
        if (Directory.Exists(programFilesRoot))
            Assert.Skip("本机安装了 MuMu 12，默认布局一定会命中，无法验证“找不到”分支");

        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", string.Empty);
            var ok = AdbPathResolver.TryResolve("adb", "127.0.0.1:16384", null, out var resolved, out var source);

            Assert.False(ok);
            Assert.Equal(string.Empty, resolved);
            Assert.Equal(string.Empty, source);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }

    [Fact]
    public void BareAdbIsNotUsableForController()
    {
        Assert.False(AdbPathResolver.IsUsable("adb"));
        Assert.False(AdbPathResolver.IsUsable(string.Empty));
        Assert.False(AdbPathResolver.IsUsable(Path.Combine(_root, "missing", "adb.exe")));
    }
}

public class AtomicFileWriterTests
{
    [Fact]
    public void ConcurrentWritesNeverCorruptOrLoseFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mfa-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "default.json");
        try
        {
            var payloads = Enumerable.Range(0, 16)
                .Select(index => "{\"key\":\"value-" + index + "\",\"filler\":\"" + new string('x', 4096) + "\"}")
                .ToArray();

            var errors = new List<Exception>();
            Parallel.For(0, 128, new ParallelOptions { MaxDegreeOfParallelism = 16 }, index =>
            {
                try
                {
                    AtomicFileWriter.WriteAllText(file, payloads[index % payloads.Length]);
                }
                catch (Exception ex)
                {
                    lock (errors)
                        errors.Add(ex);
                }
            });

            Assert.Empty(errors);
            Assert.True(File.Exists(file));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
            var final = File.ReadAllText(file);
            Assert.Contains(final, payloads);

            // 反复覆盖也不会把文件写成半截
            for (var i = 0; i < 50; i++)
                AtomicFileWriter.WriteAllText(file, payloads[i % payloads.Length]);
            Assert.Contains(File.ReadAllText(file), payloads);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // ignore
            }
        }
    }
}
