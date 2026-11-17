using MaaFramework.Binding;

namespace MFAAvalonia.Extensions.MaaFW;

/// <summary>
/// MaaFW 配置类
/// </summary>
public class MaaFWConfiguration
{
    public AdbDeviceCoreConfig AdbDevice { get; set; } = new();
    public DesktopWindowCoreConfig DesktopWindow { get; set; } = new();
    public MacOSWindowCoreConfig MacOSWindow { get; set; } = new();
    public WlRootsCoreConfig WlRoots { get; set; } = new();
    public GamepadCoreConfig Gamepad { get; set; } = new();

    public PlayCoverCoreConfig PlayCover { get; set; } = new();
}

/// <summary>
/// 桌面窗口核心配置
/// </summary>
public class DesktopWindowCoreConfig
{
    public string Name { get; set; } = string.Empty;
    public nint HWnd { get; set; }
    public Win32InputMethod Mouse { get; set; } = Win32InputMethod.SendMessage;
    public Win32InputMethod KeyBoard { get; set; } = Win32InputMethod.SendMessage;
    public Win32ScreencapMethods ScreenCap { get; set; } = Win32ScreencapMethods.FramePool;
    public LinkOption Link { get; set; } = LinkOption.Start;
    public CheckStatusOption Check { get; set; } = CheckStatusOption.ThrowIfNotSucceeded;
}

/// <summary>
/// ADB 设备核心配置
/// </summary>
public class AdbDeviceCoreConfig
{
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// adb 路径。默认留空，由 <see cref="AdbPathResolver"/> 在连接前解析成绝对路径；
    /// 不要把裸 "adb" 当作默认值（PATH 里通常没有 adb，会导致连接直接失败）。
    /// </summary>
    public string AdbPath { get; set; } = string.Empty;
    public string AdbSerial { get; set; } = "";
    public string Config { get; set; } = "{}";
    public AdbInputMethods Input { get; set; } = AdbInputMethods.Default;
    public AdbScreencapMethods ScreenCap { get; set; } = AdbScreencapMethods.Default;
    public AdbDeviceInfo? Info { get; set; } = null;
}

// Keep controller creation parameters independent of subsequent device selection changes.
internal sealed record AdbControllerSnapshot(
    string AdbPath,
    string AdbSerial,
    AdbScreencapMethods ScreenCap,
    AdbInputMethods Input,
    string Config)
{
    public static AdbControllerSnapshot Capture(AdbDeviceCoreConfig device)
    {
        lock (device)
        {
            return new(device.AdbPath, device.AdbSerial, device.ScreenCap, device.Input,
                string.IsNullOrWhiteSpace(device.Config) ? "{}" : device.Config);
        }
    }

    /// <summary>
    /// 与 <see cref="Capture"/> 相同，但会把 AdbPath 解析成真实存在的绝对路径。
    /// 控制器创建与“当前选择是否仍然匹配”的比较必须同时使用本方法，否则两边不一致会反复重连。
    /// 解析失败时保留原始值，由调用方报错。
    /// </summary>
    public static AdbControllerSnapshot CaptureResolved(AdbDeviceCoreConfig device, string? softwarePath)
    {
        lock (device)
        {
            var snapshot = Capture(device);
            return AdbPathResolver.TryResolve(device.AdbPath, device.Name, softwarePath, out var resolved, out _)
                ? snapshot with { AdbPath = resolved }
                : snapshot;
        }
    }
}

/// <summary>
/// PlayCover 设备核心配置
/// </summary>
public class PlayCoverCoreConfig
{
    public string Name { get; set; } = string.Empty;
    public string PlayCoverAddress { get; set; } = "";
    public string UUID { get; set; } = "maa.playcover";
}

/// <summary>
/// macOS 原生窗口核心配置
/// </summary>
public class MacOSWindowCoreConfig
{
    public uint WindowId { get; set; }
    public string Name { get; set; } = string.Empty;
    public MacOSScreencapMethod ScreenCap { get; set; } = MacOSScreencapMethod.ScreenCaptureKit;
    public MacOSInputMethod Input { get; set; } = MacOSInputMethod.GlobalEvent;
    public LinkOption Link { get; set; } = LinkOption.Start;
    public CheckStatusOption Check { get; set; } = CheckStatusOption.ThrowIfNotSucceeded;
}

public class GamepadCoreConfig
{
    public GamepadType Type { get; set; } = GamepadType.Xbox360;
    public Win32ScreencapMethods ScreenCap { get; set; } = Win32ScreencapMethods.FramePool;
}

public class WlRootsCoreConfig
{
    public string SocketPath { get; set; } = string.Empty;
    public bool UseWin32VirtualKeyCodes { get; set; }
    public LinkOption Link { get; set; } = LinkOption.Start;
    public CheckStatusOption Check { get; set; } = CheckStatusOption.ThrowIfNotSucceeded;
}
