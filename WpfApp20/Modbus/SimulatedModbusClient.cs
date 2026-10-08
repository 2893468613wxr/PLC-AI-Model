using System.Diagnostics;
using WpfApp20.Models;
using WpfApp20.Modbus;

namespace WpfApp20.Modbus;

/// <summary>
/// 内置仿真 PLC：无需真实设备即可演示。
/// 按设备点表语义生成协调变化的值（Float 的两个字一致、位状态有占空比、32 位计数器递增）；
/// 用户写入的值会一直保持，其余值持续按波形刷新。
/// </summary>
public class SimulatedModbusClient : IModbusClient
{
    private readonly DeviceConfig? _cfg;
    private readonly Dictionary<ushort, bool> _coils = [];
    private readonly HashSet<ushort> _coilOverridden = [];
    private readonly Dictionary<ushort, ushort> _registers = [];
    private readonly HashSet<ushort> _regOverridden = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public SimulatedModbusClient(DeviceConfig? cfg = null) => _cfg = cfg;

    public bool IsConnected { get; private set; }

    public Task ConnectAsync(CancellationToken ct)
    {
        IsConnected = true;
        return Task.CompletedTask;
    }

    public void Close() => IsConnected = false;

    private double T => _clock.Elapsed.TotalSeconds;

    /// <summary>按地址生成一个随时间平滑变化的 16 位原始值。</summary>
    private ushort Generate(ushort address)
    {
        int seed = (int)((address * 2654435761u) >> 8);
        double baseVal = 50 + (seed % 300);
        double amp = 10 + (seed >> 3) % 40;
        double period = 15 + ((seed >> 5) % 30);
        double phase = (address % 7) * 0.9;
        double v = baseVal + amp * Math.Sin(2 * Math.PI * T / period + phase);
        return (ushort)Math.Clamp((int)v, 0, ushort.MaxValue);
    }

    /// <summary>标签地址是否落在读取块内。</summary>
    private static bool InRange(ushort addr, ushort start, ushort count)
        => addr >= start && addr < start + (int)count;

    private bool AnyWordOverridden(TagDefinition tag)
    {
        if (tag.Area == ModbusArea.线圈) return _coilOverridden.Contains(tag.Address);
        for (int i = 0; i < tag.WordCount; i++)
            if (_regOverridden.Contains((ushort)(tag.Address + i))) return true;
        return false;
    }

    /// <summary>按标签语义刷新仿真值（跳过被用户写入覆盖的标签）。</summary>
    private void RefreshTags(ModbusArea area, ushort start, ushort count)
    {
        if (_cfg is null) return;
        foreach (var tag in _cfg.Tags)
        {
            if (tag.Area != area || !InRange(tag.Address, start, count)) continue;
            if (AnyWordOverridden(tag)) continue;

            int seed = (int)((tag.Address * 48271u) % 1000);
            double period = 25 + seed % 40;
            double phase = seed % 7;
            switch (tag.DataType)
            {
                case TagDataType.浮点32位:
                    // 工程量 25±24，正弦周期随地址变化；报警阈值可周期性穿越
                    double eng = 25 + 24 * Math.Sin(2 * Math.PI * T / period + phase);
                    foreach (var (a, w) in Enumerable.Zip(
                        Enumerable.Range(tag.Address, 2),
                        ValueCodec.Encode(tag, eng)))
                        _registers[(ushort)a] = w;
                    break;

                case TagDataType.无符号32位:
                    // 递增计数器
                    var ctr = (uint)((T * (3 + seed % 10)) % 1_000_000);
                    foreach (var (a, w) in Enumerable.Zip(
                        Enumerable.Range(tag.Address, 2),
                        ValueCodec.Encode(tag, ctr * Math.Max(1e-9, tag.Scale))))
                        _registers[(ushort)a] = w;
                    break;

                case TagDataType.位 when area == ModbusArea.线圈:
                    // ~85% 占空比的运行信号
                    _coils[tag.Address] = Math.Sin(2 * Math.PI * T / 30 + phase) > -0.6;
                    break;

                default:
                    // 16 位类型沿用地址波形（工程量换算由上位机侧完成）
                    if (area != ModbusArea.线圈)
                        _registers[tag.Address] = Generate(tag.Address);
                    else
                        _coils[tag.Address] = Generate(tag.Address) > 200;
                    break;
            }
        }
    }

    public Task<bool[]> ReadBitsAsync(ModbusArea area, ushort start, ushort count, CancellationToken ct)
    {
        if (!IsConnected) throw new InvalidOperationException("仿真设备未连接");
        RefreshTags(area, start, count);
        var result = new bool[count];
        for (int i = 0; i < count; i++)
        {
            ushort addr = (ushort)(start + i);
            result[i] = area == ModbusArea.线圈
                ? _coils.TryGetValue(addr, out bool v) && v
                : Generate(addr) > 200;
        }
        return Task.FromResult(result);
    }

    public Task<ushort[]> ReadRegistersAsync(ModbusArea area, ushort start, ushort count, CancellationToken ct)
    {
        if (!IsConnected) throw new InvalidOperationException("仿真设备未连接");
        RefreshTags(area, start, count);
        var result = new ushort[count];
        for (int i = 0; i < count; i++)
        {
            ushort addr = (ushort)(start + i);
            result[i] = _registers.TryGetValue(addr, out ushort v) ? v : Generate(addr);
        }
        return Task.FromResult(result);
    }

    public Task WriteSingleCoilAsync(ushort address, bool value, CancellationToken ct)
    {
        _coilOverridden.Add(address);
        _coils[address] = value;
        return Task.CompletedTask;
    }

    public Task WriteSingleRegisterAsync(ushort address, ushort value, CancellationToken ct)
    {
        _regOverridden.Add(address);
        _registers[address] = value;
        return Task.CompletedTask;
    }

    public Task WriteMultipleRegistersAsync(ushort start, ushort[] values, CancellationToken ct)
    {
        for (int i = 0; i < values.Length; i++)
        {
            _regOverridden.Add((ushort)(start + i));
            _registers[(ushort)(start + i)] = values[i];
        }
        return Task.CompletedTask;
    }

    public Task WriteMultipleCoilsAsync(ushort start, bool[] values, CancellationToken ct)
    {
        for (int i = 0; i < values.Length; i++)
        {
            _coilOverridden.Add((ushort)(start + i));
            _coils[(ushort)(start + i)] = values[i];
        }
        return Task.CompletedTask;
    }

    public void Dispose() => Close();
}
