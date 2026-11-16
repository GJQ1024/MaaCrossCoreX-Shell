using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace MFAAvalonia.Helper.Converters;

public class TitleConverter : MarkupExtension, IMultiValueConverter
{
    public override object ProvideValue(IServiceProvider serviceProvider) => this;

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        // 安全解包参数（处理 UnsetValue 和 null）
        var customTitle = SafeGetValue<string>(values, 0);
        var isCustomVisible = SafeGetValue<bool>(values, 1);
        var appName = SafeGetValue<object>(values, 2);
        var appVersion = SafeGetValue<string>(values, 3);
        var resourceName = SafeGetValue<string>(values, 4);
        var resourceVersion = SafeGetValue<string>(values, 5);
        var isResourceVisible = SafeGetValue<bool>(values, 6);

        // 主逻辑
        if (isCustomVisible && !string.IsNullOrEmpty(customTitle))
            return customTitle;

        // 逐段拼接，空段（含零宽占位符）直接跳过，避免出现多余的分隔符
        var parts = new List<string>(2);
        var mainPart = Normalize($"{appName} {appVersion}");
        if (mainPart.Length > 0)
            parts.Add(mainPart);

        if (isResourceVisible)
        {
            var resourcePart = Normalize($"{resourceName} {resourceVersion}");
            if (resourcePart.Length > 0)
                parts.Add(resourcePart);
        }

        return string.Join(" | ", parts);
    }

    /// <summary>
    /// 去掉零宽字符并裁剪首尾空白，用于判断标题片段是否为空
    /// </summary>
    private static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch is '\u200B' or '\u200C' or '\u200D' or '\uFEFF')
                continue;
            builder.Append(ch);
        }
        return builder.ToString().Trim();
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }

    /// <summary>
    /// 安全获取绑定值（处理 UnsetValue 和类型转换）
    /// </summary>
    private T SafeGetValue<T>(IList<object?> values, int index, T defaultValue = default)
    {
        if (index >= values.Count) return defaultValue;

        var value = values[index];

        // 处理 Avalonia 的 UnsetValue
        if (value is UnsetValueType || value == null) return defaultValue;

        try
        {
            return (T)System.Convert.ChangeType(value, typeof(T));
        }
        catch
        {
            return defaultValue;
        }
    }
}
