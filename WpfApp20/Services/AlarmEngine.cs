using System.Collections.ObjectModel;
using System.Media;
using WpfApp20.Core;
using WpfApp20.Models;

namespace WpfApp20.Services;

/// <summary>
/// 报警引擎：评估标签报警规则 + 设备通讯报警，维护活动报警列表与确认操作。
/// 线程安全：所有集合操作都通过 DeviceManager 指定的同步上下文回调进行。
/// </summary>
public class AlarmEngine
{
    // key: deviceId:tagId:ruleIndex
    private readonly Dictionary<string, AlarmRecord> _active = [];
    private readonly object _sync = new();

    public ObservableCollection<AlarmRecord> ActiveAlarms { get; } = [];
    /// <summary>本次会话的报警历史（含已恢复）。</summary>
    public ObservableCollection<AlarmRecord> History { get; } = [];

    public bool SoundEnabled { get; set; } = true;
    public event Action<int>? ActiveCountChanged;

    private void RaiseCount() => ActiveCountChanged?.Invoke(ActiveCountLocked());

    private int ActiveCountLocked() { lock (_sync) return _active.Count; }

    /// <summary>评估一个标签的所有报警规则。</summary>
    public void Evaluate(DeviceRuntime device, TagValueItem tag, DateTime time)
    {
        for (int i = 0; i < tag.Tag.AlarmRules.Count; i++)
        {
            var rule = tag.Tag.AlarmRules[i];
            var key = $"{device.Config.Id}:{tag.Tag.Id}:{i}";
            bool breached = rule.Enabled && tag.QualityGood && Match(rule, tag.Value);

            lock (_sync)
            {
                if (breached && !_active.TryGetValue(key, out _))
                {
                    var rec = new AlarmRecord
                    {
                        TriggeredAt = time,
                        DeviceName = device.Name,
                        TagName = tag.Name,
                        Level = rule.Level,
                        Message = string.IsNullOrWhiteSpace(rule.Message)
                            ? $"{tag.Name} = {tag.DisplayValue} {EnumText.Text(rule.Operator)} {rule.Threshold:G}"
                            : rule.Message,
                    };
                    _active[key] = rec;
                    AddToCollections(rec);
                    LogAndNotify(rec, "触发");
                }
                else if (!breached && _active.TryGetValue(key, out var rec))
                {
                    if (rec.RecoveredAt is null)
                    {
                        rec.RecoveredAt = time;
                        if (rec.AcknowledgedAt is not null) _active.Remove(key);
                        LogAndNotify(rec, "恢复");
                    }
                    if (rec.RecoveredAt is not null && rec.AcknowledgedAt is not null)
                        _active.Remove(key);
                }
            }
        }

        // 标签行红显
        tag.IsAlarming = tag.Tag.AlarmRules.Count > 0 &&
                         tag.Tag.AlarmRules.Any(r => r.Enabled && tag.QualityGood && Match(r, tag.Value));
        var worst = tag.Tag.AlarmRules
            .Where(r => r.Enabled && tag.QualityGood && Match(r, tag.Value))
            .OrderByDescending(r => r.Level)
            .FirstOrDefault();
        tag.ActiveAlarmText = worst != null
            ? $"{EnumText.Text(worst.Operator)} {worst.Threshold:G} {(string.IsNullOrWhiteSpace(worst.Message) ? "" : "· " + worst.Message)}"
            : string.Empty;
    }

    /// <summary>设备通讯异常报警。</summary>
    public void RaiseCommAlarm(DeviceRuntime device, DateTime time, string error)
    {
        var key = $"comm:{device.Config.Id}";
        lock (_sync)
        {
            if (_active.ContainsKey(key)) return;
            var rec = new AlarmRecord
            {
                TriggeredAt = time,
                DeviceName = device.Name,
                TagName = "(通讯)",
                Level = AlarmLevel.严重,
                Message = $"设备通讯异常：{error}",
            };
            _active[key] = rec;
            AddToCollections(rec);
            LogAndNotify(rec, "触发");
        }
    }

    /// <summary>设备通讯恢复正常 → 解除通讯报警。</summary>
    public void ClearCommAlarm(DeviceRuntime device, DateTime time)
    {
        var key = $"comm:{device.Config.Id}";
        lock (_sync)
        {
            if (_active.TryGetValue(key, out var rec) && rec.RecoveredAt is null)
            {
                rec.RecoveredAt = time;
                if (rec.AcknowledgedAt is not null) _active.Remove(key);
                LogAndNotify(rec, "恢复");
            }
        }
    }

    /// <summary>确认报警（可确认活动或已恢复的报警）。</summary>
    public void Acknowledge(AlarmRecord rec, DataLogService logger, DateTime time)
    {
        lock (_sync)
        {
            rec.AcknowledgedAt = time;
            var key = _active.FirstOrDefault(kv => kv.Value == rec).Key;
            if (rec.RecoveredAt is not null && key != null) _active.Remove(key);
        }
        logger.WriteAlarm(rec, "确认");
        RaiseCount();
    }

    public void AcknowledgeAll(DataLogService logger)
    {
        List<AlarmRecord> toAck;
        lock (_sync) toAck = _active.Values.Where(r => r.AcknowledgedAt is null).ToList();
        foreach (var rec in toAck) Acknowledge(rec, logger, DateTime.Now);
    }

    private void AddToCollections(AlarmRecord rec)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            ActiveAlarms.Insert(0, rec);
            History.Insert(0, rec);
            if (History.Count > 5000) History.RemoveAt(History.Count - 1);
        });
    }

    private void LogAndNotify(AlarmRecord rec, string evt)
    {
        DataLogged?.Invoke(rec, evt);
        if (evt == "触发" && SoundEnabled)
        {
            try { SystemSounds.Exclamation.Play(); } catch { /* ignore */ }
        }
        RaiseCount();
    }

    /// <summary>由 DeviceManager 订阅以落盘。</summary>
    public event Action<AlarmRecord, string>? DataLogged;

    private static bool Match(AlarmRule r, double v) => r.Operator switch
    {
        AlarmOperator.大于 => v > r.Threshold,
        AlarmOperator.小于 => v < r.Threshold,
        AlarmOperator.等于 => Math.Abs(v - r.Threshold) < 1e-9,
        AlarmOperator.不等于 => Math.Abs(v - r.Threshold) >= 1e-9,
        AlarmOperator.大于等于 => v >= r.Threshold,
        _ => v <= r.Threshold,
    };
}
