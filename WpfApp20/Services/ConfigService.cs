using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using WpfApp20.Models;

namespace WpfApp20.Services;

/// <summary>配置持久化：config/appconfig.json（带默认演示配置）。</summary>
public class ConfigService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ConfigPath { get; }

    public ConfigService(string? path = null)
    {
        var dir = path is null ? Path.Combine(AppContext.BaseDirectory, "config") : Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        ConfigPath = path ?? Path.Combine(dir, "appconfig.json");
    }

    public AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), Options);
                if (cfg != null) return cfg;
            }
        }
        catch
        {
            // 配置损坏时回退到默认
        }
        var fresh = CreateDefault();
        Save(fresh);
        return fresh;
    }

    public void Save(AppConfig cfg)
    {
        try
        {
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg, Options));
        }
        catch
        {
            // 只读目录等场景下忽略保存失败
        }
    }

    /// <summary>首次运行时生成一个开箱即用的仿真演示配置。</summary>
    private static AppConfig CreateDefault() => new()
    {
        DataLogEnabled = true,
        AlarmSoundEnabled = true,
        AutoStartMonitoring = true,
        Devices =
        [
            new DeviceConfig
            {
                Name = "仿真 PLC 1#线",
                Host = "127.0.0.1",
                Port = 502,
                UnitId = 1,
                PollIntervalMs = 1000,
                Enabled = true,
                Simulated = true,
                Tags =
                [
                    new TagDefinition
                    {
                        Name = "反应釜温度", Address = 0, Area = ModbusArea.保持寄存器,
                        DataType = TagDataType.浮点32位, Scale = 0.1, Offset = 0, WordSwap = true,
                        AlarmRules =
                        [
                            new AlarmRule { Operator = AlarmOperator.大于, Threshold = 45, Level = AlarmLevel.严重, Message = "温度超上限，请检查冷却" },
                            new AlarmRule { Operator = AlarmOperator.小于, Threshold = 5, Level = AlarmLevel.警告, Message = "温度偏低" },
                        ],
                    },
                    new TagDefinition
                    {
                        Name = "管道压力", Address = 2, Area = ModbusArea.保持寄存器,
                        DataType = TagDataType.无符号16位, Scale = 0.01,
                        AlarmRules = [new AlarmRule { Operator = AlarmOperator.大于, Threshold = 3.0, Level = AlarmLevel.警告, Message = "压力偏高" }],
                    },
                    new TagDefinition
                    {
                        Name = "电机运行", Address = 0, Area = ModbusArea.线圈, DataType = TagDataType.位,
                    },
                    new TagDefinition
                    {
                        Name = "产品计数", Address = 4, Area = ModbusArea.保持寄存器, DataType = TagDataType.无符号32位,
                    },
                    new TagDefinition
                    {
                        Name = "目标温度设定", Address = 10, Area = ModbusArea.保持寄存器,
                        DataType = TagDataType.无符号16位, Scale = 0.1,
                    },
                ],
            },
            new DeviceConfig
            {
                Name = "车间 PLC 2#线（示例，未连接）",
                Host = "192.168.1.10",
                Port = 502,
                UnitId = 1,
                PollIntervalMs = 2000,
                Enabled = true,
                Simulated = false,
                Tags =
                [
                    new TagDefinition { Name = "液位", Address = 0, Area = ModbusArea.保持寄存器, DataType = TagDataType.浮点32位, WordSwap = true },
                    new TagDefinition { Name = "泵启动", Address = 1, Area = ModbusArea.线圈, DataType = TagDataType.位 },
                ],
            },
        ],
    };
}
