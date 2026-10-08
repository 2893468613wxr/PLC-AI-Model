using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace WpfApp20.Models;

/// <summary>Modbus 区域（决定功能码）。序列化按数值存储。</summary>
public enum ModbusArea
{
    /// <summary>线圈（读 0x01 / 写 0x05、0x0F）</summary>
    线圈 = 0,
    /// <summary>离散输入（只读 0x02）</summary>
    离散输入 = 1,
    /// <summary>输入寄存器（只读 0x04）</summary>
    输入寄存器 = 2,
    /// <summary>保持寄存器（读 0x03 / 写 0x06、0x10）</summary>
    保持寄存器 = 3,
}

public enum TagDataType
{
    位 = 0,
    无符号16位 = 1,
    有符号16位 = 2,
    无符号32位 = 3,
    有符号32位 = 4,
    浮点32位 = 5,
}

public enum AlarmOperator { 大于 = 0, 小于, 等于, 不等于, 大于等于, 小于等于 }

public enum AlarmLevel { 提示 = 0, 警告, 严重 }

public static class EnumText
{
    public static string Text(ModbusArea a) => a switch
    {
        ModbusArea.线圈 => "线圈 0x01",
        ModbusArea.离散输入 => "离散输入 0x02",
        ModbusArea.输入寄存器 => "输入寄存器 0x04",
        _ => "保持寄存器 0x03",
    };

    public static string Text(TagDataType t) => t switch
    {
        TagDataType.位 => "Bool(位)",
        TagDataType.无符号16位 => "UShort(16位无符号)",
        TagDataType.有符号16位 => "Short(16位有符号)",
        TagDataType.无符号32位 => "UInt(32位无符号)",
        TagDataType.有符号32位 => "Int(32位有符号)",
        _ => "Float(32位浮点)",
    };

    public static string Text(AlarmOperator o) => o.ToString();
}

/// <summary>报警规则（挂在标签上）。</summary>
public class AlarmRule
{
    public AlarmOperator Operator { get; set; } = AlarmOperator.大于;
    public double Threshold { get; set; } = 100;
    public AlarmLevel Level { get; set; } = AlarmLevel.警告;
    public string? Message { get; set; }
    public bool Enabled { get; set; } = true;

    [JsonIgnore]
    public string ExpressionText =>
        $"{EnumText.Text(Operator)} {Threshold:G}" + (string.IsNullOrWhiteSpace(Message) ? "" : $"  ({Message})");
}

/// <summary>一个被监控的点（标签）。</summary>
public class TagDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "新标签";
    /// <summary>0 基地址。</summary>
    public ushort Address { get; set; }
    public ModbusArea Area { get; set; } = ModbusArea.保持寄存器;
    public TagDataType DataType { get; set; } = TagDataType.无符号16位;
    /// <summary>工程量缩放：显示值 = 原始值 × Scale + Offset。</summary>
    public double Scale { get; set; } = 1.0;
    public double Offset { get; set; } = 0.0;
    /// <summary>32 位类型的高/低字交换（大小端字序）。</summary>
    public bool WordSwap { get; set; } = false;
    public ObservableCollection<AlarmRule> AlarmRules { get; set; } = [];

    [JsonIgnore]
    public int WordCount => DataType switch
    {
        TagDataType.位 or TagDataType.无符号16位 or TagDataType.有符号16位 => 1,
        _ => 2,
    };

    [JsonIgnore]
    public bool Writable => Area is ModbusArea.线圈 or ModbusArea.保持寄存器;

    [JsonIgnore]
    public string AddressText => Area is ModbusArea.线圈 or ModbusArea.离散输入
        ? Address.ToString()
        : $"4x{Address:0000}";

    [JsonIgnore]
    public string AreaDisplay => EnumText.Text(Area);

    [JsonIgnore]
    public string DataTypeDisplay => EnumText.Text(DataType);

    [JsonIgnore]
    public string ScaleText => Scale == 1 && Offset == 0 ? "—" : $"×{Scale:0.###} +{Offset:0.###}";
}

/// <summary>一台 PLC 设备的配置。</summary>
public class DeviceConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "新设备";
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 502;
    public byte UnitId { get; set; } = 1;
    public int PollIntervalMs { get; set; } = 1000;
    public bool Enabled { get; set; } = true;
    /// <summary>仿真设备：不建立真实 TCP 连接，使用内置仿真器（便于演示）。</summary>
    public bool Simulated { get; set; } = false;

    // ---------- 通信管理（自动重连 / 心跳检测） ----------

    /// <summary>连接丢失后是否自动重连。</summary>
    public bool AutoReconnect { get; set; } = true;
    /// <summary>最大自动重连次数，0 = 无限重试。</summary>
    public int MaxReconnectAttempts { get; set; } = 0;
    /// <summary>重连基础退避延时（ms），之后按 2 的指数增长，上限 30s。</summary>
    public int ReconnectBaseDelayMs { get; set; } = 3000;
    /// <summary>是否启用心跳检测（周期性轻量读，及时发现半开/僵死连接）。</summary>
    public bool HeartbeatEnabled { get; set; } = true;
    /// <summary>心跳检测间隔（ms）。</summary>
    public int HeartbeatIntervalMs { get; set; } = 5000;
    /// <summary>心跳探测读取的线圈地址。</summary>
    public ushort HeartbeatAddress { get; set; } = 0;

    public ObservableCollection<TagDefinition> Tags { get; set; } = [];

    [JsonIgnore]
    public string EndPoint => $"{Host}:{Port}";
}

/// <summary>应用配置（持久化到 config/appconfig.json）。</summary>
public class AppConfig
{
    public ObservableCollection<DeviceConfig> Devices { get; set; } = [];
    public bool DataLogEnabled { get; set; } = true;
    public bool AlarmSoundEnabled { get; set; } = true;
    public bool AutoStartMonitoring { get; set; } = true;
}
