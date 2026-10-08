using System.Collections.ObjectModel;
using System.Windows.Media;
using WpfApp20.Core;
using WpfApp20.Models;
using WpfApp20.Modbus;

namespace WpfApp20.Services;

public enum DeviceState { 已断开 = 0, 连接中, 已连接, 异常, 重连中, 重连失败 }

/// <summary>标签实时值（监控表格行）。</summary>
public class TagValueItem : ObservableObject
{
    public required DeviceRuntime Device { get; init; }
    public required TagDefinition Tag { get; init; }

    private double _value;
    private bool _qualityGood = true;
    private DateTime _timestamp;
    private bool _isAlarming;

    public string DeviceName => Device.Config.Name;
    public string Name => Tag.Name;
    public string AddressText => Tag.AddressText;
    public string AreaText => EnumText.Text(Tag.Area);
    public string DataTypeText => EnumText.Text(Tag.DataType);
    public bool Writable => Tag.Writable;

    public double Value
    {
        get => _value;
        private set => SetProperty(ref _value, value);
    }

    public string DisplayValue => ValueCodec.FormatValue(_value);
    public string QualityText => QualityGood ? "良好" : "坏值";

    public bool QualityGood
    {
        get => _qualityGood;
        private set => SetProperty(ref _qualityGood, value);
    }

    public DateTime Timestamp
    {
        get => _timestamp;
        private set => SetProperty(ref _timestamp, value);
    }

    public bool IsAlarming
    {
        get => _isAlarming;
        set => SetProperty(ref _isAlarming, value);
    }

    /// <summary>当前处于触发状态的报警描述（空 = 无）。</summary>
    public string ActiveAlarmText { get; set; } = string.Empty;

    public void Update(double value, bool good, DateTime ts)
    {
        Value = value;
        QualityGood = good;
        Timestamp = ts;
        OnPropertyChanged(nameof(DisplayValue));
    }
}

/// <summary>设备运行时状态（设备管理表格行 / 轮询引擎宿主）。</summary>
public class DeviceRuntime : ObservableObject
{
    private DeviceState _state = DeviceState.已断开;
    private string _lastError = "";
    private DateTime? _lastPollTime;
    private long _successCount, _failCount;
    private double _lastRoundTripMs;
    private bool _isPolling;
    private string _connectionInfo = "未启动";

    public required DeviceConfig Config { get; init; }
    public ObservableCollection<TagValueItem> Tags { get; } = [];

    public string Name => Config.Name;
    public string EndPoint => Config.EndPoint;
    public byte UnitId => Config.UnitId;
    public int PollIntervalMs => Math.Max(100, Config.PollIntervalMs);
    public int TagCount => Tags.Count;
    public bool Simulated => Config.Simulated;

    public DeviceState State
    {
        get => _state;
        set { SetProperty(ref _state, value); OnPropertyChanged(nameof(StateText)); }
    }

    public string StateText => State switch
    {
        DeviceState.已连接 => Simulated ? "已连接(仿真)" : "已连接",
        DeviceState.连接中 => "连接中…",
        DeviceState.重连中 => "自动重连中",
        DeviceState.重连失败 => "重连失败",
        DeviceState.异常 => "通讯异常",
        _ => "已断开",
    };

    /// <summary>连接详情（已连接时长 / 心跳 / 重连进度），随连接状态与心跳刷新。</summary>
    public string ConnectionInfo
    {
        get => _connectionInfo;
        private set => SetProperty(ref _connectionInfo, value);
    }

    public string LastError
    {
        get => _lastError;
        set => SetProperty(ref _lastError, value);
    }

    public DateTime? LastPollTime
    {
        get => _lastPollTime;
        set => SetProperty(ref _lastPollTime, value);
    }

    public long SuccessCount { get => _successCount; private set => SetProperty(ref _successCount, value); }
    public long FailCount { get => _failCount; private set => SetProperty(ref _failCount, value); }
    public double LastRoundTripMs { get => _lastRoundTripMs; private set => SetProperty(ref _lastRoundTripMs, value); }
    public bool IsPolling { get => _isPolling; private set => SetProperty(ref _isPolling, value); }

    public string StatsText => $"成功 {SuccessCount} / 失败 {FailCount} · {LastRoundTripMs:0} ms";

    public void OnPollResult(bool ok, double rttMs, DateTime time, string? error)
    {
        if (ok) SuccessCount++; else FailCount++;
        LastRoundTripMs = rttMs;
        LastPollTime = time;
        if (!ok && error != null) LastError = error;
        OnPropertyChanged(nameof(StatsText));
    }

    public void SetPolling(bool polling) => IsPolling = polling;

    /// <summary>由 DeviceWorker 注入的连接信息刷新回调（界面定时器调用以更新连接时长）。</summary>
    public Action? ConnectionInfoRefresher { get; set; }

    /// <summary>把通信管理器的连接状态同步到 UI 模型。</summary>
    public void ApplyConnectionState(Modbus.ConnectionState state, ModbusConnectionManager conn)
    {
        State = state switch
        {
            Modbus.ConnectionState.已连接 => DeviceState.已连接,
            Modbus.ConnectionState.连接中 => DeviceState.连接中,
            Modbus.ConnectionState.重连中 => DeviceState.重连中,
            Modbus.ConnectionState.重连失败 => DeviceState.重连失败,
            _ => DeviceState.已断开,
        };
        RefreshConnectionInfo(conn);
    }

    /// <summary>刷新连接详情文本（连接时长 / 心跳 / 重连进度）。</summary>
    public void RefreshConnectionInfo(ModbusConnectionManager conn)
    {
        ConnectionInfo = conn.State switch
        {
            Modbus.ConnectionState.已连接 => BuildConnectedInfo(conn),
            Modbus.ConnectionState.连接中 => "正在建立连接…",
            Modbus.ConnectionState.重连中 =>
                $"第 {conn.ReconnectAttempts} 次重连 · {conn.NextRetryDelaySeconds}s 后重试",
            Modbus.ConnectionState.重连失败 =>
                conn.Config.AutoReconnect
                    ? $"已重试 {conn.ReconnectAttempts} 次，停止重试"
                    : "自动重连已关闭",
            _ => "未启动",
        };
    }

    private string BuildConnectedInfo(ModbusConnectionManager conn)
    {
        var since = conn.ConnectedSince;
        string duration = since is null ? "--"
            : FormatDuration(DateTime.Now - since.Value);
        if (!conn.HeartbeatEnabled)
            return $"已连接 {duration} · 心跳关";
        if (conn.HeartbeatStopReason is not null)
            return $"已连接 {duration} · 心跳停用（探测点不可用）";
        string hb = conn.LastHeartbeatTime is { } h
            ? $"{Math.Max(0, (int)(DateTime.Now - h).TotalSeconds)}s 前"
            : "--";
        return $"已连接 {duration} · 心跳 {hb}";
    }

    private static string FormatDuration(TimeSpan d)
    {
        if (d.TotalMinutes < 1) return $"{(int)d.TotalSeconds}s";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes}m{(int)d.Seconds:D2}s";
        return $"{(int)d.TotalHours}h{(int)d.Minutes:D2}m";
    }

    public void RebuildTags()
    {
        Tags.Clear();
        foreach (var t in Config.Tags)
            Tags.Add(new TagValueItem { Device = this, Tag = t });
        OnPropertyChanged(nameof(TagCount));
    }
}

/// <summary>一条报警记录。</summary>
public class AlarmRecord : ObservableObject
{
    public Guid Id { get; } = Guid.NewGuid();
    public DateTime TriggeredAt { get; init; }
    public string DeviceName { get; init; } = "";
    public string TagName { get; init; } = "";
    public string Message { get; init; } = "";
    public AlarmLevel Level { get; init; } = AlarmLevel.警告;
    public AlarmLevel DisplayLevel => Level;

    private DateTime? _recoveredAt;
    public DateTime? RecoveredAt { get => _recoveredAt; set => SetProperty(ref _recoveredAt, value); }

    private DateTime? _acknowledgedAt;
    public DateTime? AcknowledgedAt { get => _acknowledgedAt; set => SetProperty(ref _acknowledgedAt, value); }

    public string StatusText =>
        RecoveredAt is null ? "活动中" : AcknowledgedAt is null ? "待确认" : "已恢复";

    public string LevelText => Level.ToString();
    public string TimeText => TriggeredAt.ToString("HH:mm:ss");
    public string FullTimeText => TriggeredAt.ToString("yyyy-MM-dd HH:mm:ss");
    public Brush LevelBrush => Level switch
    {
        AlarmLevel.严重 => new SolidColorBrush(Color.FromRgb(0xE5, 0x3E, 0x3E)),
        AlarmLevel.警告 => new SolidColorBrush(Color.FromRgb(0xE8, 0x9B, 0x2E)),
        _ => new SolidColorBrush(Color.FromRgb(0x4A, 0x9E, 0xE8)),
    };
}
