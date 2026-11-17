using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MFAAvalonia.Helper;

/// <summary>
/// 资源包热更新：只替换 resource / agent / scripts 三个根目录，不写 interface.json，
/// 不碰 exe、dll、libs、python 运行时，也不重启壳。新配置在下次任务启动时生效。
/// 远端描述文件挂在 Release（tag=<see cref="PackReleaseTag"/>）的 resource-pack.json 资产上；
/// 描述文件不可用时静默跳过，行为与未接入热更新一致。
/// </summary>
public static class ResourcePackUpdater
{
    public const string PackReleaseTag = "resource-pack-latest";
    private const string PackOwner = "GJQ1024";
    private const string PackRepo = "MaaCrossCoreX";
    private const string DescriptorAssetName = "resource-pack.json";
    private const string LocalRecordFileName = "resource_pack.json";
    private const string ManifestFileName = "manifest.json";
    private const string BackupStateFileName = "state.json";
    private const int SchemaVersion = 1;
    private const int KeepBackupCount = 2;

    // 仅用于本地验证：指向包含 resource-pack.json 与 zip 的目录；模拟替换中断/失败时填入根目录名。
    private const string DebugPackSourceEnv = "MFA_DEBUG_PACK_SOURCE";
    private const string DebugPackCrashEnv = "MFA_DEBUG_PACK_CRASH";
    private const string DebugPackFailEnv = "MFA_DEBUG_PACK_FAIL";

    private static readonly string[] PackRoots = ["resource", "agent", "scripts"];
    private static readonly string[] RequiredRoots = ["resource", "agent"];

    private const string StatusPrepared = "prepared";
    private const string StatusSwapping = "swapping";
    private const string StatusCommitted = "committed";
    private const string StatusRolledBack = "rolledback";

    #region 对外类型

    public enum PackCheckStatus
    {
        Unavailable,
        Invalid,
        InterfaceMismatch,
        ShellTooOld,
        UpToDate,
        UpdateAvailable
    }

    public sealed class PackDescriptor
    {
        public int Schema { get; set; } = SchemaVersion;
        public string PackVersion { get; set; } = string.Empty;
        public string InterfaceVersion { get; set; } = string.Empty;
        public string InterfaceSha256 { get; set; } = string.Empty;
        public string MinShellVersion { get; set; } = string.Empty;
        public string ZipName { get; set; } = string.Empty;
        public string ZipSha256 { get; set; } = string.Empty;
        public long ZipSize { get; set; }
        public string PublishedAt { get; set; } = string.Empty;
    }

    public sealed class RemotePack
    {
        public PackDescriptor Descriptor { get; init; } = new();
        public string ZipUrl { get; init; } = string.Empty;
        public string ZipDigest { get; init; } = string.Empty;
    }

    public sealed class PackCheckResult
    {
        public PackCheckStatus Status { get; init; }
        public RemotePack? Remote { get; init; }
        public string Message { get; init; } = string.Empty;
    }

    public sealed class PackApplyResult
    {
        public bool Success { get; init; }
        public bool RolledBack { get; init; }
        public string PackVersion { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
    }

    public sealed record PackProgress(double Percent, string Status);

    public sealed class PackManifest
    {
        public int Schema { get; set; }
        public string PackVersion { get; set; } = string.Empty;
        public string InterfaceVersion { get; set; } = string.Empty;
        public string InterfaceSha256 { get; set; } = string.Empty;
        public string MinShellVersion { get; set; } = string.Empty;
        public List<PackFileEntry> Files { get; set; } = [];
    }

    public sealed class PackFileEntry
    {
        public string Path { get; set; } = string.Empty;
        public long Size { get; set; }
        public string Sha256 { get; set; } = string.Empty;
    }

    private sealed class InstalledPackRecord
    {
        public int Schema { get; set; } = SchemaVersion;
        public string PackVersion { get; set; } = string.Empty;
        public string InterfaceVersion { get; set; } = string.Empty;
        public string InterfaceSha256 { get; set; } = string.Empty;
        public string InstalledAt { get; set; } = string.Empty;
        public int FileCount { get; set; }
        public long TotalBytes { get; set; }
        public string BackupDirectory { get; set; } = string.Empty;
    }

    private sealed class PackSwapState
    {
        public int Schema { get; set; } = SchemaVersion;
        public string PackVersion { get; set; } = string.Empty;
        public string Status { get; set; } = StatusPrepared;
        public string CreatedAt { get; set; } = string.Empty;
        public List<PackSwapRoot> Roots { get; set; } = [];
    }

    private sealed class PackSwapRoot
    {
        public string Root { get; set; } = string.Empty;
        public bool ExistedBefore { get; set; }
        public List<PackFileEntry> Files { get; set; } = [];
    }

    #endregion

    #region 路径与本地记录

    private static string RecordPath => Path.Combine(AppPaths.ConfigDirectory, LocalRecordFileName);
    private static string TempRoot => Path.Combine(AppPaths.TempDirectory, "resource_pack");
    private static string BackupRoot => Path.Combine(AppPaths.BackupDirectory, "resource_pack");

    public static string GetInstalledPackVersion() => ReadRecord()?.PackVersion ?? string.Empty;

    /// <summary>
    /// 本地资源包基线版本：没有记录、或记录与当前 interface.json 不匹配时，退回 interface.json 的 version。
    /// </summary>
    public static string GetLocalPackVersion(string interfaceVersion, string interfaceFingerprint)
    {
        var record = ReadRecord();
        if (record == null || string.IsNullOrWhiteSpace(record.PackVersion))
            return interfaceVersion;
        if (!string.IsNullOrWhiteSpace(record.InterfaceVersion)
            && !record.InterfaceVersion.Equals(interfaceVersion, StringComparison.OrdinalIgnoreCase))
            return interfaceVersion;
        if (!string.IsNullOrWhiteSpace(record.InterfaceSha256)
            && !string.IsNullOrWhiteSpace(interfaceFingerprint)
            && !record.InterfaceSha256.Equals(interfaceFingerprint, StringComparison.OrdinalIgnoreCase))
            return interfaceVersion;
        return record.PackVersion;
    }

    /// <summary>设置页显示：1.0.3（资源包 1.0.4）。没有可用记录时原样返回。</summary>
    public static string FormatDisplayVersion(string interfaceVersion)
    {
        if (string.IsNullOrWhiteSpace(interfaceVersion))
            return interfaceVersion;
        var record = ReadRecord();
        if (record == null || string.IsNullOrWhiteSpace(record.PackVersion))
            return interfaceVersion;
        if (!string.IsNullOrWhiteSpace(record.InterfaceVersion)
            && !record.InterfaceVersion.Equals(interfaceVersion, StringComparison.OrdinalIgnoreCase))
            return interfaceVersion;
        return $"{interfaceVersion}（资源包 {record.PackVersion}）";
    }

    private static InstalledPackRecord? ReadRecord()
    {
        try
        {
            if (!File.Exists(RecordPath))
                return null;
            return JsonConvert.DeserializeObject<InstalledPackRecord>(File.ReadAllText(RecordPath));
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"读取资源包安装记录失败：{ex.Message}");
            return null;
        }
    }

    private static void WriteRecord(InstalledPackRecord record)
    {
        Directory.CreateDirectory(AppPaths.ConfigDirectory);
        var tempPath = RecordPath + ".tmp";
        File.WriteAllText(tempPath, JsonConvert.SerializeObject(record, Formatting.Indented), new UTF8Encoding(false));
        File.Move(tempPath, RecordPath, overwrite: true);
    }

    #endregion

    #region interface.json 版本与指纹

    public static string ReadInterfaceVersion()
    {
        try
        {
            if (!File.Exists(AppPaths.InterfaceJsonPath))
                return string.Empty;
            var json = JObject.Parse(File.ReadAllText(AppPaths.InterfaceJsonPath), new JsonLoadSettings
            {
                CommentHandling = CommentHandling.Ignore
            });
            return json["version"]?.ToString() ?? string.Empty;
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"读取 interface.json 版本失败：{ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>
    /// interface.json 指纹，口径必须与 build-resource-pack.ps1 一致：去 UTF-8 BOM，CRLF 归一为 LF，再取 SHA256。
    /// </summary>
    public static string ComputeInterfaceFingerprint()
    {
        try
        {
            if (!File.Exists(AppPaths.InterfaceJsonPath))
                return string.Empty;
            return ComputeFingerprint(File.ReadAllBytes(AppPaths.InterfaceJsonPath));
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"计算 interface.json 指纹失败：{ex.Message}");
            return string.Empty;
        }
    }

    private static string ComputeFingerprint(byte[] bytes)
    {
        var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        using var normalized = new MemoryStream(bytes.Length - start);
        for (var i = start; i < bytes.Length; i++)
        {
            // CRLF -> LF；单独的 CR 保持不变。
            if (bytes[i] == (byte)'\r' && i + 1 < bytes.Length && bytes[i + 1] == (byte)'\n')
                continue;
            normalized.WriteByte(bytes[i]);
        }
        return Convert.ToHexString(SHA256.HashData(normalized.ToArray()));
    }

    #endregion

    #region 检查

    public static async Task<PackCheckResult> CheckAsync(
        string localPackVersion,
        string interfaceVersion,
        string interfaceFingerprint,
        string shellVersion)
    {
        var remote = await FetchRemoteAsync().ConfigureAwait(false);
        if (remote == null)
            return new PackCheckResult
            {
                Status = PackCheckStatus.Unavailable,
                Message = "未找到资源包发布信息"
            };

        var descriptor = remote.Descriptor;
        if (!TryComparePackVersion(descriptor.PackVersion, localPackVersion, out var versionComparison))
            return new PackCheckResult
            {
                Status = PackCheckStatus.Invalid,
                Remote = remote,
                Message = $"资源包版本号无法识别：{descriptor.PackVersion} / 本地 {localPackVersion}"
            };

        var compatibility = CheckCompatibility(descriptor, interfaceVersion, interfaceFingerprint, shellVersion);
        if (compatibility.Status != PackCheckStatus.UpdateAvailable)
            return new PackCheckResult
            {
                Status = compatibility.Status,
                Remote = remote,
                Message = compatibility.Message
            };

        return new PackCheckResult
        {
            Status = versionComparison > 0 ? PackCheckStatus.UpdateAvailable : PackCheckStatus.UpToDate,
            Remote = remote,
            Message = versionComparison > 0 ? $"发现资源包 {descriptor.PackVersion}" : $"资源包已是最新（{localPackVersion}）"
        };
    }

    private static PackCheckResult CheckCompatibility(
        PackDescriptor descriptor,
        string interfaceVersion,
        string interfaceFingerprint,
        string shellVersion)
    {
        if (!string.IsNullOrWhiteSpace(descriptor.InterfaceVersion)
            && !descriptor.InterfaceVersion.Equals(interfaceVersion, StringComparison.OrdinalIgnoreCase))
            return new PackCheckResult
            {
                Status = PackCheckStatus.InterfaceMismatch,
                Message = $"该资源包需要配置版本 {descriptor.InterfaceVersion}，本机为 {interfaceVersion}，请使用整包更新"
            };

        if (!string.IsNullOrWhiteSpace(descriptor.InterfaceSha256)
            && !string.IsNullOrWhiteSpace(interfaceFingerprint)
            && !descriptor.InterfaceSha256.Equals(interfaceFingerprint, StringComparison.OrdinalIgnoreCase))
            return new PackCheckResult
            {
                Status = PackCheckStatus.InterfaceMismatch,
                Message = "本机 interface.json 与资源包不匹配，请使用整包更新"
            };

        if (!string.IsNullOrWhiteSpace(descriptor.MinShellVersion)
            && TryComparePackVersion(descriptor.MinShellVersion, shellVersion, out var shellComparison)
            && shellComparison > 0)
            return new PackCheckResult
            {
                Status = PackCheckStatus.ShellTooOld,
                Message = $"该资源包需要壳版本 {descriptor.MinShellVersion} 及以上，当前为 {shellVersion}，请先更新完整包"
            };

        return new PackCheckResult { Status = PackCheckStatus.UpdateAvailable };
    }

    private static async Task<RemotePack?> FetchRemoteAsync()
    {
        var debugSource = Environment.GetEnvironmentVariable(DebugPackSourceEnv);
        if (!string.IsNullOrWhiteSpace(debugSource))
            return ReadLocalRemote(debugSource);

        var url = $"https://api.github.com/repos/{PackOwner}/{PackRepo}/releases/tags/{PackReleaseTag}";
        using var http = CreateApiClient();
        using var response = await http.GetAsync(url).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            LoggerHelper.Info($"未找到资源包发布信息（tag={PackReleaseTag}），已跳过资源热更新检查。");
            return null;
        }
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"请求资源包发布信息失败：{(int)response.StatusCode} {response.ReasonPhrase}");

        var release = JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        if (release["assets"] is not JArray assets)
            return null;

        var descriptorAsset = assets.FirstOrDefault(asset =>
            DescriptorAssetName.Equals(asset["name"]?.ToString(), StringComparison.OrdinalIgnoreCase));
        if (descriptorAsset == null)
        {
            LoggerHelper.Info($"资源包发布缺少描述文件 {DescriptorAssetName}，已跳过资源热更新检查。");
            return null;
        }

        var descriptorBytes = await DownloadSmallAssetAsync(http, descriptorAsset).ConfigureAwait(false);
        if (descriptorBytes == null)
            return null;

        PackDescriptor? descriptor;
        try
        {
            descriptor = JsonConvert.DeserializeObject<PackDescriptor>(Encoding.UTF8.GetString(descriptorBytes));
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"资源包描述文件无法解析：{ex.Message}", ex);
        }
        if (descriptor == null || string.IsNullOrWhiteSpace(descriptor.PackVersion) || string.IsNullOrWhiteSpace(descriptor.ZipName))
            throw new InvalidDataException("资源包描述文件缺少 packVersion 或 zipName。");
        if (string.IsNullOrWhiteSpace(descriptor.ZipSha256))
            throw new InvalidDataException("资源包描述文件缺少 zipSha256。");

        var zipAsset = assets.FirstOrDefault(asset =>
            descriptor.ZipName.Equals(asset["name"]?.ToString(), StringComparison.OrdinalIgnoreCase));
        if (zipAsset == null)
            throw new InvalidDataException($"资源包发布缺少资产：{descriptor.ZipName}");

        return new RemotePack
        {
            Descriptor = descriptor,
            ZipUrl = GetAssetUrl(zipAsset),
            ZipDigest = ExtractDigest(zipAsset["digest"]?.ToString())
        };
    }

    /// <summary>
    /// 本地调试源：目录（或直接指向 resource-pack.json），zip 与描述文件同目录。
    /// 只有设置了 MFA_DEBUG_PACK_SOURCE 才会走到这里。
    /// </summary>
    private static RemotePack ReadLocalRemote(string source)
    {
        var descriptorPath = Directory.Exists(source) ? Path.Combine(source, DescriptorAssetName) : source;
        if (!File.Exists(descriptorPath))
            throw new FileNotFoundException($"调试资源包描述文件不存在：{descriptorPath}");

        var descriptor = JsonConvert.DeserializeObject<PackDescriptor>(File.ReadAllText(descriptorPath))
                         ?? throw new InvalidDataException($"调试资源包描述文件无法解析：{descriptorPath}");
        if (string.IsNullOrWhiteSpace(descriptor.PackVersion) || string.IsNullOrWhiteSpace(descriptor.ZipName))
            throw new InvalidDataException("调试资源包描述文件缺少 packVersion 或 zipName。");

        var zipPath = Path.Combine(Path.GetDirectoryName(descriptorPath)!, descriptor.ZipName);
        if (!File.Exists(zipPath))
            throw new FileNotFoundException($"调试资源包 zip 不存在：{zipPath}");

        LoggerHelper.Warning($"使用本地调试资源包源：{descriptorPath}");
        return new RemotePack { Descriptor = descriptor, ZipUrl = zipPath, ZipDigest = string.Empty };
    }

    private static HttpClient CreateApiClient()
    {
        var http = VersionChecker.CreateHttpClientWithProxy(TimeSpan.FromSeconds(30));
        http.DefaultRequestHeaders.UserAgent.TryParseAdd("MaaCrossCoreX-ResourcePack/1.0");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        var token = Instances.VersionUpdateSettingsUserControlModel.GitHubToken;
        if (!string.IsNullOrWhiteSpace(token))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    private static async Task<byte[]?> DownloadSmallAssetAsync(HttpClient http, JToken asset)
    {
        var url = GetAssetUrl(asset);
        if (string.IsNullOrWhiteSpace(url))
            return null;

        using var request = CreateAssetRequest(url);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            LoggerHelper.Warning($"下载资源包描述文件失败：{(int)response.StatusCode} {response.ReasonPhrase}");
            return null;
        }
        if (response.Content.Headers.ContentLength > 1024 * 1024)
        {
            LoggerHelper.Warning("资源包描述文件超过 1MB，已拒绝解析。");
            return null;
        }
        return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
    }

    private static string GetAssetUrl(JToken asset) =>
        asset["url"]?.ToString() ?? asset["browser_download_url"]?.ToString() ?? string.Empty;

    private static string ExtractDigest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
            return string.Empty;
        return digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : digest;
    }

    #endregion

    #region 安装

    public static async Task<PackApplyResult> ApplyAsync(
        RemotePack remote,
        string interfaceVersion,
        string interfaceFingerprint,
        string shellVersion,
        IProgress<PackProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var descriptor = remote.Descriptor;
        var workDir = Path.Combine(TempRoot, descriptor.PackVersion);
        var stagingDir = Path.Combine(workDir, "staging");
        var zipPath = Path.Combine(workDir, "pack.zip");
        string? backupDir = null;
        PackSwapState? state = null;

        try
        {
            var compatibility = CheckCompatibility(descriptor, interfaceVersion, interfaceFingerprint, shellVersion);
            if (compatibility.Status != PackCheckStatus.UpdateAvailable)
                throw new InvalidOperationException(compatibility.Message);

            var freeSpace = GetAvailableFreeSpace(AppPaths.DataRoot);
            var requiredSpace = Math.Max(descriptor.ZipSize, 0) * 3 + 64L * 1024 * 1024;
            if (freeSpace < requiredSpace)
                throw new IOException($"磁盘空间不足：需要约 {requiredSpace / 1024 / 1024} MB，当前可用 {freeSpace / 1024 / 1024} MB");

            TryDeleteDirectory(workDir);
            Directory.CreateDirectory(workDir);

            progress?.Report(new PackProgress(0, "正在下载资源包…"));
            if (Path.IsPathRooted(remote.ZipUrl) && File.Exists(remote.ZipUrl))
            {
                // 本地调试源：直接复制，仍走同一套校验。
                File.Copy(remote.ZipUrl, zipPath, overwrite: true);
                progress?.Report(new PackProgress(69, "已读取本地资源包"));
            }
            else
            {
                await DownloadAsync(remote.ZipUrl, zipPath, descriptor.ZipSize, progress, cancellationToken).ConfigureAwait(false);
            }

            progress?.Report(new PackProgress(70, "正在校验资源包…"));
            var actualZipSha = ComputeFileSha256(zipPath);
            if (!actualZipSha.Equals(descriptor.ZipSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"资源包 SHA256 不符：期望 {descriptor.ZipSha256}，实际 {actualZipSha}");
            if (!string.IsNullOrWhiteSpace(remote.ZipDigest)
                && !remote.ZipDigest.Equals(actualZipSha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"资源包与 Release 资产摘要不一致：资产 {remote.ZipDigest}，实际 {actualZipSha}");

            progress?.Report(new PackProgress(75, "正在解压资源包…"));
            if (!UniversalExtractor.Extract(zipPath, stagingDir))
                throw new InvalidDataException("资源包解压失败。");

            progress?.Report(new PackProgress(82, "正在校验资源包内文件…"));
            var manifest = ValidateStaging(stagingDir, descriptor, interfaceVersion, interfaceFingerprint);

            progress?.Report(new PackProgress(88, "正在备份当前资源…"));
            backupDir = Path.Combine(BackupRoot, $"{descriptor.PackVersion}-{DateTime.Now:yyyyMMddHHmmss}");
            Directory.CreateDirectory(backupDir);
            state = BuildSwapState(descriptor.PackVersion);

            progress?.Report(new PackProgress(92, "正在替换资源文件…"));
            CreateBackup(backupDir, state);
            ApplyPackage(stagingDir, manifest);

            progress?.Report(new PackProgress(97, "正在写入安装记录…"));
            WriteRecord(new InstalledPackRecord
            {
                PackVersion = descriptor.PackVersion,
                InterfaceVersion = interfaceVersion,
                InterfaceSha256 = interfaceFingerprint,
                InstalledAt = DateTime.Now.ToString("O"),
                FileCount = manifest.Files.Count,
                TotalBytes = manifest.Files.Sum(file => file.Size),
                BackupDirectory = backupDir
            });

            state.Status = StatusCommitted;
            WriteState(backupDir, state);
            LoggerHelper.Info($"资源包已安装：版本={descriptor.PackVersion}，文件数={manifest.Files.Count}，备份={backupDir}");

            progress?.Report(new PackProgress(100, "更新完成"));
            return new PackApplyResult
            {
                Success = true,
                PackVersion = descriptor.PackVersion,
                Message = $"资源包 {descriptor.PackVersion} 已安装"
            };
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"资源包更新失败：{ex.Message}", ex);
            var rolledBack = false;
            if (state != null && backupDir != null)
                rolledBack = TryRollback(backupDir, state);

            var message = rolledBack
                ? $"{ex.Message}（已整份回退到更新前状态）"
                : (state != null && backupDir != null
                    ? $"{ex.Message}（回退失败，备份保留在 {backupDir}，请手工恢复）"
                    : ex.Message);
            return new PackApplyResult
            {
                Success = false,
                RolledBack = rolledBack,
                PackVersion = descriptor.PackVersion,
                Message = message
            };
        }
        finally
        {
            TryDeleteDirectory(workDir);
            CleanupOldBackups();
        }
    }

    private static PackSwapState BuildSwapState(string packVersion)
    {
        var state = new PackSwapState
        {
            PackVersion = packVersion,
            Status = StatusPrepared,
            CreatedAt = DateTime.Now.ToString("O")
        };
        foreach (var root in PackRoots)
        {
            var target = Path.Combine(AppPaths.DataRoot, root);
            var existed = Directory.Exists(target);
            state.Roots.Add(new PackSwapRoot
            {
                Root = root,
                ExistedBefore = existed,
                Files = existed ? BuildFileList(AppPaths.DataRoot, root) : []
            });
        }
        return state;
    }

    /// <summary>
    /// 文件级备份：把三个根目录的现有文件复制到备份目录。不改名、不移动目录，
    /// 避免与壳已打开的资源目录句柄冲突（整目录 rename 在 Windows 上会被拒绝）。
    /// </summary>
    private static void CreateBackup(string backupDir, PackSwapState state)
    {
        WriteState(backupDir, state);

        foreach (var entry in state.Roots)
        {
            if (!entry.ExistedBefore)
                continue;

            var source = Path.Combine(AppPaths.DataRoot, entry.Root);
            var destination = Path.Combine(backupDir, entry.Root);
            foreach (var file in entry.Files)
            {
                var relative = file.Path[(entry.Root.Length + 1)..].Replace('/', Path.DirectorySeparatorChar);
                CopyFileWithRetry(Path.Combine(source, relative), Path.Combine(destination, relative));
            }
            LoggerHelper.Info($"资源包热更新已备份目录：{entry.Root}（文件数={entry.Files.Count}）");
        }

        // 备份完整落盘后才标记为替换中：此后安装目录随时可能被改动，中断必须整份回退。
        state.Status = StatusSwapping;
        WriteState(backupDir, state);
    }

    /// <summary>
    /// 文件级替换：按清单覆盖/新增文件，删掉包内不存在的旧文件，最后整体校验一遍。
    /// 任一文件失败都抛异常，由上层整份回退，不会出现半新半旧。
    /// 包内缺少某个根目录时（例如旧包没有 scripts）保留本地该目录不动。
    /// </summary>
    private static void ApplyPackage(string stagingDir, PackManifest manifest)
    {
        var crashRoot = Environment.GetEnvironmentVariable(DebugPackCrashEnv);
        var failRoot = Environment.GetEnvironmentVariable(DebugPackFailEnv);

        foreach (var root in PackRoots)
        {
            var stagedRoot = Path.Combine(stagingDir, root);
            if (!Directory.Exists(stagedRoot))
            {
                LoggerHelper.Info($"资源包未包含 {root} 目录，保留本地现有内容。");
                continue;
            }

            var expected = manifest.Files
                .Where(file => file.Path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var targetRoot = Path.Combine(AppPaths.DataRoot, root);
            Directory.CreateDirectory(targetRoot);

            foreach (var file in expected)
            {
                var relative = file.Path[(root.Length + 1)..].Replace('/', Path.DirectorySeparatorChar);
                CopyFileWithRetry(Path.Combine(stagedRoot, relative), Path.Combine(targetRoot, relative));
            }

            // 包内没有的旧文件要删掉，否则旧配置会继续参与加载。
            var known = expected.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(targetRoot, "*", SearchOption.AllDirectories).ToList())
            {
                var relative = Path.GetRelativePath(AppPaths.DataRoot, file).Replace('\\', '/');
                if (known.Contains(relative))
                    continue;
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
                LoggerHelper.Info($"资源包热更新删除旧文件：{relative}");
            }
            RemoveEmptyDirectories(targetRoot);
            LoggerHelper.Info($"资源包热更新已替换目录：{root}（文件数={expected.Count}）");

            // 仅本地验证：在某个根目录替换完成后模拟进程被杀 / 替换失败。
            if (!string.IsNullOrWhiteSpace(crashRoot) && crashRoot.Equals(root, StringComparison.OrdinalIgnoreCase))
            {
                LoggerHelper.Warning($"调试：模拟资源包替换中断（根目录={root}），进程立即退出。");
                Environment.Exit(1);
            }
            if (!string.IsNullOrWhiteSpace(failRoot) && failRoot.Equals(root, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"调试：模拟替换失败（根目录={root}）。");
        }

        if (!VerifyAppliedMatchesManifest(stagingDir, manifest))
            throw new IOException("替换后校验不一致：安装目录中的资源文件与资源包清单不符。");
    }

    /// <summary>替换完成后按清单逐文件校验（存在、大小、SHA256），并确认没有清单外残留。</summary>
    private static bool VerifyAppliedMatchesManifest(string stagingDir, PackManifest manifest)
    {
        foreach (var file in manifest.Files)
        {
            var fullPath = Path.Combine(AppPaths.DataRoot, file.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
            {
                LoggerHelper.Error($"替换后缺少文件：{file.Path}");
                return false;
            }
            var length = new FileInfo(fullPath).Length;
            if (length != file.Size)
            {
                LoggerHelper.Error($"替换后文件大小不符：{file.Path}（期望 {file.Size}，实际 {length}）");
                return false;
            }
            if (!ComputeFileSha256(fullPath).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                LoggerHelper.Error($"替换后文件 SHA256 不符：{file.Path}");
                return false;
            }
        }

        var known = manifest.Files.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var root in PackRoots)
        {
            if (!Directory.Exists(Path.Combine(stagingDir, root)))
                continue;
            var rootDir = Path.Combine(AppPaths.DataRoot, root);
            if (!Directory.Exists(rootDir))
                continue;
            foreach (var file in Directory.EnumerateFiles(rootDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(AppPaths.DataRoot, file).Replace('\\', '/');
                if (!known.Contains(relative))
                {
                    LoggerHelper.Error($"替换后残留清单外文件：{relative}");
                    return false;
                }
            }
        }
        return true;
    }

    private static void CopyFileWithRetry(string source, string destination, int attempts = 3)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var directory = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                if (File.Exists(destination))
                    File.SetAttributes(destination, FileAttributes.Normal);
                File.Copy(source, destination, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < attempts)
            {
                LoggerHelper.Warning($"复制资源文件失败（第 {attempt}/{attempts} 次）：{destination}，原因={ex.Message}");
                Thread.Sleep(300 * attempt);
            }
        }
    }

    private static void RemoveEmptyDirectories(string root)
    {
        try
        {
            foreach (var directory in Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
                         .OrderByDescending(path => path.Length))
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory);
            }
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"清理空目录失败：目录={root}，原因={ex.Message}");
        }
    }

    private static bool TryRollback(string backupDir, PackSwapState state)
    {
        LoggerHelper.Warning($"资源包更新未完成，开始整份回退：备份={backupDir}");
        var success = RestoreFromState(backupDir, state);
        if (success)
        {
            LoggerHelper.Info("资源包已回退到更新前状态。");
            state.Status = StatusRolledBack;
            WriteState(backupDir, state);
        }
        else
        {
            LoggerHelper.Error($"资源包回退失败，备份保留供手工恢复：{backupDir}");
        }
        return success;
    }

    /// <summary>
    /// 按 state.json 逐文件还原：删掉替换新增的文件，把备份里的旧文件复制回去，最后逐文件校验。
    /// 幂等，可在启动时重复执行。
    /// </summary>
    private static bool RestoreFromState(string backupDir, PackSwapState state)
    {
        var success = true;
        foreach (var entry in state.Roots)
        {
            var target = Path.Combine(AppPaths.DataRoot, entry.Root);
            var backup = Path.Combine(backupDir, entry.Root);
            try
            {
                if (!entry.ExistedBefore)
                {
                    // 替换前不存在、替换后新增的目录，回退时删除。
                    if (Directory.Exists(target) && !TryDeleteDirectory(target))
                        throw new IOException($"无法删除新增目录：{target}");
                    continue;
                }

                Directory.CreateDirectory(target);
                var known = entry.Files.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

                // 先清掉替换过程中新增出来的文件，再逐份写回旧文件。
                foreach (var file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).ToList())
                {
                    var relative = Path.GetRelativePath(AppPaths.DataRoot, file).Replace('\\', '/');
                    if (known.Contains(relative))
                        continue;
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }

                foreach (var file in entry.Files)
                {
                    var relative = file.Path[(entry.Root.Length + 1)..].Replace('/', Path.DirectorySeparatorChar);
                    var backupPath = Path.Combine(backup, relative);
                    if (!File.Exists(backupPath))
                    {
                        // 备份没写完就中断时，该文件还没被动过；确认目标仍是旧内容即可。
                        var targetPath = Path.Combine(target, relative);
                        if (File.Exists(targetPath)
                            && new FileInfo(targetPath).Length == file.Size
                            && ComputeFileSha256(targetPath).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                            continue;
                        throw new IOException($"备份文件缺失且目标文件已被改动：{file.Path}");
                    }
                    CopyFileWithRetry(backupPath, Path.Combine(target, relative));
                }
                RemoveEmptyDirectories(target);

                if (!VerifyRootMatches(AppPaths.DataRoot, entry))
                    throw new IOException($"回退后校验不一致：{entry.Root}");
            }
            catch (Exception ex)
            {
                success = false;
                LoggerHelper.Error($"回退资源目录失败：目录={entry.Root}，原因={ex.Message}", ex);
            }
        }

        if (success)
            LoggerHelper.Info("资源目录已恢复到替换前状态并通过校验。");
        return success;
    }

    private static bool VerifyRootMatches(string dataRoot, PackSwapRoot entry)
    {
        var prefix = entry.Root + "/";
        var expected = entry.Files
            .Where(file => file.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(file => file.Path, file => file, StringComparer.OrdinalIgnoreCase);

        var target = Path.Combine(dataRoot, entry.Root);
        if (!Directory.Exists(target))
            return expected.Count == 0;

        var actualFiles = Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).ToList();
        if (actualFiles.Count != expected.Count)
            return false;

        foreach (var file in actualFiles)
        {
            var relative = Path.GetRelativePath(dataRoot, file).Replace('\\', '/');
            if (!expected.TryGetValue(relative, out var expectedFile))
                return false;
            var info = new FileInfo(file);
            if (info.Length != expectedFile.Size)
                return false;
            if (!ComputeFileSha256(file).Equals(expectedFile.Sha256, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    #endregion

    #region 校验

    private static PackManifest ValidateStaging(
        string stagingDir,
        PackDescriptor descriptor,
        string interfaceVersion,
        string interfaceFingerprint)
    {
        var manifestPath = Path.Combine(stagingDir, ManifestFileName);
        if (!File.Exists(manifestPath))
            throw new InvalidDataException($"资源包缺少 {ManifestFileName}");

        PackManifest? manifest;
        try
        {
            manifest = JsonConvert.DeserializeObject<PackManifest>(File.ReadAllText(manifestPath));
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"资源包内 {ManifestFileName} 无法解析：{ex.Message}", ex);
        }
        if (manifest == null)
            throw new InvalidDataException($"资源包内 {ManifestFileName} 为空");
        if (manifest.Schema != SchemaVersion)
            throw new InvalidDataException($"资源包清单版本不支持：{manifest.Schema}");
        if (!manifest.PackVersion.Equals(descriptor.PackVersion, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"资源包清单版本不符：清单 {manifest.PackVersion}，描述 {descriptor.PackVersion}");
        if (!string.IsNullOrWhiteSpace(descriptor.InterfaceVersion)
            && !manifest.InterfaceVersion.Equals(descriptor.InterfaceVersion, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("资源包清单与描述文件的配置版本不一致");
        if (!string.IsNullOrWhiteSpace(descriptor.InterfaceSha256)
            && !manifest.InterfaceSha256.Equals(descriptor.InterfaceSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("资源包清单与描述文件的配置指纹不一致");
        if (!string.IsNullOrWhiteSpace(manifest.InterfaceVersion)
            && !manifest.InterfaceVersion.Equals(interfaceVersion, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"资源包需要配置版本 {manifest.InterfaceVersion}，本机为 {interfaceVersion}，请使用整包更新");
        if (!string.IsNullOrWhiteSpace(manifest.InterfaceSha256)
            && !string.IsNullOrWhiteSpace(interfaceFingerprint)
            && !manifest.InterfaceSha256.Equals(interfaceFingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("本机 interface.json 与资源包不匹配，请使用整包更新");
        if (manifest.Files.Count == 0)
            throw new InvalidDataException("资源包清单为空");

        // 顶层只允许 manifest.json 与三个白名单目录。
        foreach (var entry in Directory.EnumerateFileSystemEntries(stagingDir))
        {
            var name = Path.GetFileName(entry);
            if (name.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!PackRoots.Contains(name, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException($"资源包包含白名单外的顶层项：{name}");
            if (!Directory.Exists(entry))
                throw new InvalidDataException($"资源包顶层项不是目录：{name}");
        }

        foreach (var root in RequiredRoots)
        {
            if (!Directory.Exists(Path.Combine(stagingDir, root)))
                throw new InvalidDataException($"资源包缺少 {root} 目录");
        }

        // 逐文件校验：路径白名单、大小、SHA256、重复项。
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            var relative = NormalizeRelativePath(file.Path);
            if (!seen.Add(relative))
                throw new InvalidDataException($"资源包清单存在重复路径：{relative}");

            var fullPath = Path.Combine(stagingDir, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
                throw new InvalidDataException($"资源包缺少清单中的文件：{relative}");

            var info = new FileInfo(fullPath);
            if (info.Length != file.Size)
                throw new InvalidDataException($"资源包文件大小不符：{relative}（期望 {file.Size}，实际 {info.Length}）");

            var actualSha = ComputeFileSha256(fullPath);
            if (!actualSha.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"资源包文件 SHA256 不符：{relative}（期望 {file.Sha256}，实际 {actualSha}）");
        }

        // 不允许清单外文件混入。
        foreach (var root in PackRoots)
        {
            var rootDir = Path.Combine(stagingDir, root);
            if (!Directory.Exists(rootDir))
                continue;
            foreach (var file in Directory.EnumerateFiles(rootDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(stagingDir, file).Replace('\\', '/');
                if (!seen.Contains(relative))
                    throw new InvalidDataException($"资源包包含清单外文件：{relative}");
            }
        }

        return manifest;
    }

    private static string NormalizeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidDataException("资源包清单包含空路径。");

        var normalized = path.Replace('\\', '/').TrimStart('/');
        if (normalized.Contains(':'))
            throw new InvalidDataException($"资源包清单包含非法路径：{path}");
        if (normalized.Contains("..", StringComparison.Ordinal))
            throw new InvalidDataException($"资源包清单包含越界路径：{path}");

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
            throw new InvalidDataException($"资源包清单包含非法路径：{path}");
        if (!PackRoots.Contains(segments[0], StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException($"资源包清单包含白名单外的路径：{path}");

        return string.Join('/', segments);
    }

    private static List<PackFileEntry> BuildFileList(string packageRoot, string root)
    {
        var files = new List<PackFileEntry>();
        var directory = Path.Combine(packageRoot, root);
        if (!Directory.Exists(directory))
            return files;

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            files.Add(new PackFileEntry
            {
                Path = Path.GetRelativePath(packageRoot, file).Replace('\\', '/'),
                Size = new FileInfo(file).Length,
                Sha256 = ComputeFileSha256(file)
            });
        }
        return files;
    }

    #endregion

    #region 下载

    private static async Task DownloadAsync(
        string url,
        string destinationPath,
        long expectedSize,
        IProgress<PackProgress>? progress,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await DownloadOnceAsync(url, destinationPath, progress, cancellationToken).ConfigureAwait(false);
                if (expectedSize > 0)
                {
                    var actualSize = new FileInfo(destinationPath).Length;
                    if (actualSize != expectedSize)
                        throw new InvalidDataException($"下载大小不符：期望 {expectedSize}，实际 {actualSize}");
                }
                return;
            }
            catch (OperationCanceledException)
            {
                TryDeleteFile(destinationPath);
                throw;
            }
            catch (Exception ex)
            {
                TryDeleteFile(destinationPath);
                LoggerHelper.Warning($"资源包下载失败（第 {attempt}/{maxAttempts} 次）：{ex.Message}");
                if (attempt == maxAttempts)
                    throw;
                await Task.Delay(2000 * attempt, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task DownloadOnceAsync(
        string url,
        string destinationPath,
        IProgress<PackProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var http = VersionChecker.CreateHttpClientWithProxy(TimeSpan.FromMinutes(10));
        http.DefaultRequestHeaders.UserAgent.TryParseAdd("MaaCrossCoreX-ResourcePack/1.0");
        var token = Instances.VersionUpdateSettingsUserControlModel.GitHubToken;
        if (!string.IsNullOrWhiteSpace(token))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var request = CreateAssetRequest(url);
        using var response = await http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new FileStream(
            destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long received = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;
            if (total > 0)
                progress?.Report(new PackProgress(received * 70.0 / total, $"正在下载资源包… {received * 100 / total}%"));
        }
    }

    private static HttpRequestMessage CreateAssetRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Clear();
        // GitHub Release 资产用 API 地址下载时，必须声明二进制流，私有仓库也能通过重定向拿到签名地址。
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        return request;
    }

    #endregion

    #region 启动恢复与清理

    /// <summary>
    /// 启动时调用：把上次未完成的替换整份回退，并清理临时目录与过期备份。内部吞掉异常，不阻断启动。
    /// </summary>
    public static void RecoverPendingRollback()
    {
        try
        {
            if (Directory.Exists(BackupRoot))
            {
                foreach (var backupDir in Directory.GetDirectories(BackupRoot).OrderBy(Directory.GetLastWriteTimeUtc))
                {
                    var state = ReadState(backupDir);
                    if (state == null || state.Status is StatusCommitted or StatusRolledBack)
                        continue;

                    LoggerHelper.Warning($"检测到上次未完成的资源包替换：备份={backupDir}，状态={state.Status}，开始回退。");
                    if (RestoreFromState(backupDir, state))
                    {
                        state.Status = StatusRolledBack;
                        WriteState(backupDir, state);
                        LoggerHelper.Warning("上次未完成的资源包替换已回退完成。");
                    }
                    else
                    {
                        LoggerHelper.Error($"上次未完成的资源包替换回退失败，备份保留：{backupDir}");
                    }
                }
            }

            TryDeleteDirectory(TempRoot);
            CleanupOldBackups();
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"资源包回退检查失败：{ex.Message}", ex);
        }
    }

    public static void CleanupOldBackups(int keep = KeepBackupCount)
    {
        try
        {
            if (!Directory.Exists(BackupRoot))
                return;

            var backups = Directory.GetDirectories(BackupRoot)
                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                .ToList();
            foreach (var backupDir in backups.Skip(keep))
            {
                var state = ReadState(backupDir);
                // 未完成回退的备份不能删，避免丢掉唯一可恢复的一份。
                if (state != null && state.Status is StatusPrepared or StatusSwapping)
                    continue;
                TryDeleteDirectory(backupDir);
            }
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"清理资源包旧备份失败：{ex.Message}");
        }
    }

    private static PackSwapState? ReadState(string backupDir)
    {
        try
        {
            var path = Path.Combine(backupDir, BackupStateFileName);
            if (!File.Exists(path))
                return null;
            return JsonConvert.DeserializeObject<PackSwapState>(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"读取资源包备份状态失败：备份={backupDir}，原因={ex.Message}");
            return null;
        }
    }

    private static void WriteState(string backupDir, PackSwapState state)
    {
        Directory.CreateDirectory(backupDir);
        var path = Path.Combine(backupDir, BackupStateFileName);
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, JsonConvert.SerializeObject(state, Formatting.Indented), new UTF8Encoding(false));
        File.Move(tempPath, path, overwrite: true);
    }

    #endregion

    #region 通用工具

    private static bool TryComparePackVersion(string? left, string? right, out int comparison)
    {
        comparison = 0;
        if (!TryParsePackVersion(left, out var leftParts) || !TryParsePackVersion(right, out var rightParts))
            return false;

        var length = Math.Max(leftParts.Length, rightParts.Length);
        for (var i = 0; i < length; i++)
        {
            var leftValue = i < leftParts.Length ? leftParts[i] : 0;
            var rightValue = i < rightParts.Length ? rightParts[i] : 0;
            if (leftValue == rightValue)
                continue;
            comparison = leftValue > rightValue ? 1 : -1;
            return true;
        }
        return true;
    }

    private static bool TryParsePackVersion(string? version, out int[] parts)
    {
        parts = [];
        if (string.IsNullOrWhiteSpace(version))
            return false;
        var segments = version.Trim().TrimStart('v', 'V').Split('.');
        if (segments.Length is < 1 or > 3)
            return false;

        var parsed = new int[segments.Length];
        for (var i = 0; i < segments.Length; i++)
        {
            if (!int.TryParse(segments[i], out parsed[i]) || parsed[i] < 0)
                return false;
        }
        parts = parsed;
        return true;
    }

    private static string ComputeFileSha256(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static long GetAvailableFreeSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrWhiteSpace(root) ? long.MaxValue : new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            return long.MaxValue;
        }
    }

    private static bool TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
            return true;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(path, recursive: true);
                return true;
            }
            catch (Exception ex)
            {
                if (attempt == 3)
                {
                    LoggerHelper.Warning($"删除目录失败：目录={path}，原因={ex.Message}");
                    return false;
                }
                System.Threading.Thread.Sleep(300 * attempt);
            }
        }
        return false;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"删除文件失败：文件={path}，原因={ex.Message}");
        }
    }

    #endregion
}
