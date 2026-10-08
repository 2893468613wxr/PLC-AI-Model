using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using WpfApp20.Services;

namespace WpfApp20;

public class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object? value, Type t, object? p, CultureInfo c)
        => ((value is bool b) && b != Invert) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>设备状态 → 状态灯颜色。</summary>
public class DeviceStateBrushConverter : IValueConverter
{
    private static readonly Brush Ok = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x6F));
    private static readonly Brush Off = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x8C));
    private static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xE8, 0xA2, 0x2E));
    private static readonly Brush Danger = new SolidColorBrush(Color.FromRgb(0xE5, 0x53, 0x4B));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x4F, 0x8C, 0xFF));

    public object Convert(object? value, Type t, object? p, CultureInfo c) => value is DeviceState s ? s switch
    {
        DeviceState.已连接 => Ok,
        DeviceState.连接中 => Accent,
        DeviceState.重连中 => Warn,
        DeviceState.异常 => Danger,
        DeviceState.重连失败 => Danger,
        _ => Off,
    } : Off;

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public class BoolToBrushConverter : IValueConverter
{
    private static readonly Brush Danger = new SolidColorBrush(Color.FromRgb(0xE5, 0x53, 0x4B));
    private static readonly Brush Ok = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF2));

    public object Convert(object? value, Type t, object? p, CultureInfo c)
        => value is bool b && b ? Danger : Ok;

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
        => value == null ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>数值非 0 → True。可直接以 {local:NotZeroConverter} 使用。</summary>
public class NotZeroConverter : MarkupExtension, IValueConverter
{
    public override object ProvideValue(IServiceProvider serviceProvider) => this;

    public object Convert(object? value, Type t, object? p, CultureInfo c)
        => value is int i ? i != 0 : value is long l && l != 0;

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>非 null → Visible。可直接以 {local:NotNullToVisibilityConverter} 使用。</summary>
public class NotNullToVisibilityConverter : MarkupExtension, IValueConverter
{
    public override object ProvideValue(IServiceProvider serviceProvider) => this;

    public object Convert(object? value, Type t, object? p, CultureInfo c)
        => value == null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>
/// 数值输入框的容错转换器。
///
/// 背景：端口 / 单元号 / 地址 / 阈值等输入框绑定的是 ushort、int、double，
/// WPF 默认转换器在用户<b>清空输入框</b>或输入非法内容时执行 ConvertBack 会抛
/// FormatException（"ConvertBack cannot convert value ''"），在输出窗口刷一堆绑定错误。
///
/// 行为：Convert 正常转字符串；ConvertBack 遇到空串或无法解析的内容时返回
/// <see cref="Binding.DoNothing"/>，即<b>保持原值不写入</b>，不抛异常、不产生绑定错误，
/// 用户继续输入合法数字后照常生效。
///
/// 用法：Text="{Binding Address, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged,
///              Converter={app:NumericTextConverter}}"
/// </summary>
public class NumericTextConverter : MarkupExtension, IValueConverter
{
    public override object ProvideValue(IServiceProvider serviceProvider) => this;

    public object Convert(object? value, Type t, object? p, CultureInfo c)
        => value is null ? "" : (System.Convert.ToString(value, c) ?? "");

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c)
    {
        var s = (value as string ?? "").Trim();
        if (s.Length == 0) return Binding.DoNothing;

        var target = Nullable.GetUnderlyingType(t) ?? t;
        try
        {
            if (target == typeof(double)) return double.Parse(s, NumberStyles.Float, c);
            if (target == typeof(float)) return float.Parse(s, NumberStyles.Float, c);
            if (target == typeof(decimal)) return decimal.Parse(s, NumberStyles.Float, c);
            if (target == typeof(int)) return int.Parse(s, NumberStyles.Integer, c);
            if (target == typeof(long)) return long.Parse(s, NumberStyles.Integer, c);
            if (target == typeof(ushort)) return ushort.Parse(s, NumberStyles.Integer, c);
            if (target == typeof(short)) return short.Parse(s, NumberStyles.Integer, c);
            if (target == typeof(byte)) return byte.Parse(s, NumberStyles.Integer, c);
            if (target == typeof(uint)) return uint.Parse(s, NumberStyles.Integer, c);
        }
        catch
        {
            return Binding.DoNothing; // 非法输入：忽略本次回写，保留原值
        }
        return Binding.DoNothing;
    }
}
