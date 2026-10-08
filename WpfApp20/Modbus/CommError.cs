using System.IO;
using System.Net.Sockets;

namespace WpfApp20.Modbus;

/// <summary>
/// 把通信异常整理成一句人能看懂的短消息。
///
/// 背景：NModbus 抛出的从站异常响应（SlaveException）的 Message 里塞了一整段
/// Modbus 规范原文（好几百字），直接显示到界面会淹没「最近错误」列，也看不出重点。
/// 这里统一转成「区域/地址 + 异常码含义」的结构化短句。
/// </summary>
public static class CommError
{
    /// <summary>生成简短可读的错误描述（单行，长度可控）。</summary>
    public static string Describe(Exception ex)
    {
        if (ex is null) return "未知错误";

        var type = ex.GetType();

        // NModbus 的 SlaveException：从站回了异常响应帧（连接是好的，只是请求不被接受）
        if (type.Name == "SlaveException")
        {
            // 反射读取，避免与具体版本的属性签名强耦合
            byte fc = ReadByte(ex, "FunctionCode");
            byte code = ReadByte(ex, "SlaveExceptionCode");
            string fcText = fc == 0 ? "??" : (fc & 0x7F).ToString("X2");
            return $"从站返回异常：功能码 0x{fcText} — {SlaveCodeText(code)}";
        }

        if (ex is TimeoutException) return "通信超时（从站无应答）";

        if (ex is SocketException se)
            return $"网络错误（{se.SocketErrorCode}）：{Short(se.Message)}";

        if (ex is IOException) return $"通信中断：{Short(ex.Message)}";

        if (ex is InvalidOperationException) return Short(ex.Message);

        return Short(ex.Message);
    }

    /// <summary>Modbus 异常码 → 中文含义。</summary>
    public static string SlaveCodeText(byte code) => code switch
    {
        1 => "非法功能（从站不支持该功能码，检查区域是否开放）",
        2 => "非法数据地址（地址/数量超出从站定义范围，检查地址基址与寄存器数量）",
        3 => "非法数据值",
        4 => "从站设备故障",
        5 => "从站已接受但处理较慢（请稍后重试）",
        6 => "从站忙",
        10 => "网关路径不可用",
        11 => "网关无响应",
        _ => $"异常码 {code}",
    };

    private static byte ReadByte(Exception ex, string propName)
    {
        try
        {
            var v = ex.GetType().GetProperty(propName)?.GetValue(ex);
            return v is null ? (byte)0 : Convert.ToByte(v);
        }
        catch
        {
            return 0;
        }
    }

    private static string Short(string s)
    {
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length <= 120 ? s : s[..120] + "…";
    }
}
