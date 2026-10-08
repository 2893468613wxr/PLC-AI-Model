using System.IO;
using System.Text;
using WpfApp20.Models;
using WpfApp20.Services;

namespace WpfApp20.Services;

/// <summary>把轮询数据以 CSV 形式追加记录到 logs/data/，报警记录到 logs/alarms/。</summary>
public class DataLogService
{
    private readonly object _sync = new();
    public string BaseDir { get; }

    public DataLogService(string? baseDir = null)
    {
        BaseDir = baseDir ?? Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(Path.Combine(BaseDir, "data"));
        Directory.CreateDirectory(Path.Combine(BaseDir, "alarms"));
    }

    private static string Sanitize(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name)
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        return sb.ToString();
    }

    /// <summary>记录一轮轮询的所有标签值。</summary>
    public void WritePollData(DeviceRuntime device, DateTime time)
    {
        try
        {
            var file = Path.Combine(BaseDir, "data", $"{Sanitize(device.Name)}_{time:yyyyMMdd}.csv");
            var sb = new StringBuilder(256);
            bool newFile = !File.Exists(file);

            if (newFile)
            {
                sb.Append("时间");
                foreach (var t in device.Tags) sb.Append(',').Append(t.Name);
                sb.AppendLine();
            }

            sb.Append(time.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            foreach (var t in device.Tags)
                sb.Append(',').Append(t.QualityGood ? t.Value : "BAD");
            sb.AppendLine();

            lock (_sync) File.AppendAllText(file, sb.ToString(), new UTF8Encoding(true));
        }
        catch
        {
            // 日志失败不应影响采集主流程
        }
    }

    /// <summary>报警事件落盘。</summary>
    public void WriteAlarm(AlarmRecord alarm, string eventName)
    {
        try
        {
            var file = Path.Combine(BaseDir, "alarms", $"alarms_{alarm.TriggeredAt:yyyyMMdd}.csv");
            var sb = new StringBuilder(128);
            if (!File.Exists(file))
                sb.AppendLine("时间,事件,设备,标签,级别,内容,恢复时间,确认时间");

            sb.Append(alarm.TriggeredAt.ToString("yyyy-MM-dd HH:mm:ss")).Append(',')
              .Append(eventName).Append(',')
              .Append(alarm.DeviceName).Append(',')
              .Append(alarm.TagName).Append(',')
              .Append(alarm.LevelText).Append(',')
              .Append(alarm.Message.Replace(',', '，')).Append(',')
              .Append(alarm.RecoveredAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "").Append(',')
              .Append(alarm.AcknowledgedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "")
              .AppendLine();

            lock (_sync) File.AppendAllText(file, sb.ToString(), new UTF8Encoding(true));
        }
        catch
        {
            // ignore
        }
    }

    public string DataDir => Path.Combine(BaseDir, "data");
    public string AlarmDir => Path.Combine(BaseDir, "alarms");

    public List<string> ListDataFiles()
    {
        var dir = Path.Combine(BaseDir, "data");
        return Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.csv").OrderByDescending(f => f).ToList()
            : [];
    }
}
