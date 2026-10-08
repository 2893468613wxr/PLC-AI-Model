using System.Net.Sockets;
using System.Net;
using NModbus;
using WpfApp20.Models;

namespace WpfApp20.Modbus;

/// <summary>统一的 Modbus 主站客户端抽象（真实 TCP / 仿真实现共用）。</summary>
public interface IModbusClient : IDisposable
{
    bool IsConnected { get; }
    Task ConnectAsync(CancellationToken ct);
    void Close();
    Task<bool[]> ReadBitsAsync(ModbusArea area, ushort start, ushort count, CancellationToken ct);
    Task<ushort[]> ReadRegistersAsync(ModbusArea area, ushort start, ushort count, CancellationToken ct);
    Task WriteSingleCoilAsync(ushort address, bool value, CancellationToken ct);
    Task WriteSingleRegisterAsync(ushort address, ushort value, CancellationToken ct);
    Task WriteMultipleRegistersAsync(ushort start, ushort[] values, CancellationToken ct);
    Task WriteMultipleCoilsAsync(ushort start, bool[] values, CancellationToken ct);
}

/// <summary>基于 NModbus 的真实 Modbus TCP 主站。</summary>
public class TcpModbusClient : IModbusClient
{
    private readonly DeviceConfig _cfg;
    private TcpClient? _tcp;
    private IModbusMaster? _master;
    private static readonly ModbusFactory Factory = new();

    public TcpModbusClient(DeviceConfig cfg) => _cfg = cfg;

    public bool IsConnected => _tcp?.Connected == true && _master != null;

    public async Task ConnectAsync(CancellationToken ct)
    {
        Close();
        _tcp = new TcpClient();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(3000);
        try
        {
            await _tcp.ConnectAsync(_cfg.Host, _cfg.Port, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"连接 {_cfg.EndPoint} 超时（3s）");
        }
        _tcp.NoDelay = true;
        _master = Factory.CreateMaster(_tcp);
        ApplyResponseTimeout(_master);
    }

    /// <summary>
    /// 设置 NModbus 应答超时（默认 1s），确保从站无响应时请求能及时失败，
    /// 心跳/轮询的超时检测依赖该设置。不同 NModbus 版本属性可能不同，反射兼容处理。
    /// </summary>
    private static void ApplyResponseTimeout(IModbusMaster master)
    {
        try
        {
            var prop = master.GetType().GetProperty("Timeout");
            if (prop?.CanWrite == true && prop.PropertyType == typeof(TimeSpan))
                prop.SetValue(master, TimeSpan.FromSeconds(3));
        }
        catch
        {
            // 属性不存在时使用 NModbus 默认超时
        }
    }

    public void Close()
    {
        try { _master?.Dispose(); } catch { /* ignore */ }
        try { _tcp?.Close(); } catch { /* ignore */ }
        _master = null;
        _tcp = null;
    }

    private IModbusMaster Master => _master ?? throw new InvalidOperationException("Modbus 主站未连接");
    private byte Unit => _cfg.UnitId;

    public Task<bool[]> ReadBitsAsync(ModbusArea area, ushort start, ushort count, CancellationToken ct) => area switch
    {
        ModbusArea.线圈 => Master.ReadCoilsAsync(Unit, start, count),
        ModbusArea.离散输入 => Master.ReadInputsAsync(Unit, start, count),
        _ => throw new ArgumentException("区域不是位区域", nameof(area)),
    };

    public Task<ushort[]> ReadRegistersAsync(ModbusArea area, ushort start, ushort count, CancellationToken ct) => area switch
    {
        ModbusArea.输入寄存器 => Master.ReadInputRegistersAsync(Unit, start, count),
        ModbusArea.保持寄存器 => Master.ReadHoldingRegistersAsync(Unit, start, count),
        _ => throw new ArgumentException("区域不是寄存器区域", nameof(area)),
    };

    public Task WriteSingleCoilAsync(ushort address, bool value, CancellationToken ct)
        => Master.WriteSingleCoilAsync(Unit, address, value);

    public Task WriteSingleRegisterAsync(ushort address, ushort value, CancellationToken ct)
        => Master.WriteSingleRegisterAsync(Unit, address, value);

    public Task WriteMultipleRegistersAsync(ushort start, ushort[] values, CancellationToken ct)
        => Master.WriteMultipleRegistersAsync(Unit, start, values);

    public Task WriteMultipleCoilsAsync(ushort start, bool[] values, CancellationToken ct)
        => Master.WriteMultipleCoilsAsync(Unit, start, values);

    public void Dispose()
    {
        Close();
        GC.SuppressFinalize(this);
    }
}
