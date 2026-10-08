using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using WpfApp20.Models;

namespace WpfApp20.Modbus;

/// <summary>连接状态机的状态。</summary>
public enum ConnectionState
{
    已断开 = 0,
    连接中,
    已连接,
    重连中,
    重连失败,
}

/// <summary>一次通信操作的结果。</summary>
public enum CommResult
{
    /// <summary>执行成功。</summary>
    Ok = 0,
    /// <summary>连接不可用被跳过（未启动 / 正在重连 / 正在停止）。</summary>
    Skipped,
    /// <summary>连接级错误，连接已被标记为丢失并触发自动重连。</summary>
    ConnectionLost,
    /// <summary>操作级错误（如 Modbus 异常响应），连接本身仍然可用。</summary>
    OperationFailed,
}

/// <summary>
/// Modbus 通信管理器：管理单台设备连接的完整生命周期。
///
/// - <b>自动重连</b>：连接丢失后由监督循环按指数退避（base×2^n，上限 30s）自动重试；
///   可配置最大重连次数（0 = 无限），放弃后进入“重连失败”状态并上报事件。
/// - <b>心跳检测</b>：连接空闲期间周期性读取 1 个线圈，及时发现半开 / 僵死连接
///   （轮询周期长或点表为空时尤其重要），失败立即触发重连。
/// - <b>串行化</b>：轮询 / 写值 / 心跳 / 重连共用一个信号量，避免 Modbus TCP 并发事务冲突。
/// - <b>错误分级</b>：区分连接级错误（Socket/IO/超时 → 重连）与操作级错误
///   （如 Modbus 异常响应 → 仅记录失败，保持连接）。
/// </summary>
public class ModbusConnectionManager : IDisposable
{
    private readonly DeviceConfig _cfg;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _supervisor;
    private IModbusClient _client;
    private DateTime _lastHeartbeatAt = DateTime.Now;
    /// <summary>最近一次「通信成功」的时刻（轮询 / 写值 / 心跳任一成功都会刷新）。</summary>
    private DateTime _lastSuccessAt = DateTime.Now;
    /// <summary>心跳探测点候选（按顺序尝试，失败即自动切换到下一个）。</summary>
    private List<(ModbusArea Area, ushort Address)> _probes = [];
    private int _probeIndex;
    /// <summary>所有候选都不可用时置 true：停止心跳，但<b>不</b>判定为连接故障。</summary>
    private bool _heartbeatStopped;

    public IModbusClient Client => _client;
    public DeviceConfig Config => _cfg;
    public ConnectionState State { get; private set; } = ConnectionState.已断开;
    /// <summary>当前重连尝试次数（连接成功后清零）。</summary>
    public int ReconnectAttempts { get; private set; }
    public DateTime? ConnectedSince { get; private set; }
    public DateTime? LastHeartbeatTime { get; private set; }
    public bool IsConnected => State == ConnectionState.已连接;
    public bool HeartbeatEnabled => _cfg.HeartbeatEnabled;
    /// <summary>心跳已停用（探测地址在从站不可用）。null = 心跳正常工作中。</summary>
    public string? HeartbeatStopReason { get; private set; }
    /// <summary>当前心跳使用的探测点描述（界面排查用）。</summary>
    public string HeartbeatProbeText =>
        _probeIndex < _probes.Count
            ? $"{EnumText.Text(_probes[_probeIndex].Area)} @ {_probes[_probeIndex].Address}"
            : "无可用探测点";

    /// <summary>下次重连前还需等待的秒数（用于界面展示）。</summary>
    public int NextRetryDelaySeconds => State == ConnectionState.重连中
        ? BackoffMs(ReconnectAttempts) / 1000
        : 0;

    // ---------- 事件（可能在后台线程触发） ----------

    public event Action<ConnectionState>? StateChanged;
    /// <summary>连接中断（心跳或轮询/写值检测到连接级错误）。参数为触发原因。</summary>
    public event Action<Exception>? ConnectionLost;
    /// <summary>连接建立成功（含首次连接与重连成功）。</summary>
    public event Action? ConnectionRestored;
    /// <summary>单次重连尝试失败。参数为失败原因。</summary>
    public event Action<Exception>? ReconnectAttemptFailed;
    /// <summary>达到最大重连次数，已停止重试。</summary>
    public event Action? ReconnectGaveUp;
    /// <summary>一次心跳检测成功完成。</summary>
    public event Action? HeartbeatTick;
    /// <summary>
    /// 心跳探测点不可用（从站回了异常响应，例如线圈区不存在）。
    /// 这是「配置问题」而不是「链路故障」，不会触发重连，只用于提示与自动换点。
    /// </summary>
    public event Action<Exception>? HeartbeatProbeFailed;

    public ModbusConnectionManager(DeviceConfig cfg)
    {
        _cfg = cfg;
        _client = CreateClient(cfg);
        _probes = BuildProbes(cfg);
    }

    /// <summary>
    /// 构造心跳探测候选：优先用点表里真实存在的点（能读说明链路没问题），
    /// 再退回到配置的线圈 / 保持寄存器地址。
    /// </summary>
    private static List<(ModbusArea Area, ushort Address)> BuildProbes(DeviceConfig cfg)
    {
        var list = new List<(ModbusArea, ushort)>();
        var first = cfg.Tags.FirstOrDefault();
        if (first is not null) list.Add((first.Area, first.Address));
        list.Add((ModbusArea.线圈, cfg.HeartbeatAddress));
        list.Add((ModbusArea.保持寄存器, cfg.HeartbeatAddress));
        return list.Distinct().ToList();
    }

    private static IModbusClient CreateClient(DeviceConfig cfg) => cfg.Simulated
        ? new SimulatedModbusClient(cfg)
        : new TcpModbusClient(cfg);

    // ---------- 启停 ----------

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        // 每次启动都重新构建并复位心跳探测（点表可能已被编辑）
        _probes = BuildProbes(_cfg);
        _probeIndex = 0;
        _heartbeatStopped = false;
        HeartbeatStopReason = null;
        _lastSuccessAt = DateTime.Now;
        _lastHeartbeatAt = DateTime.Now;
        _supervisor = Task.Run(() => SuperviseAsync(token));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _supervisor?.Wait(3000); } catch { /* ignore */ }
        _cts = null;
        try { _client.Close(); } catch { /* ignore */ }
        _client.Dispose();
        ReconnectAttempts = 0;
        ConnectedSince = null;
        SetState(ConnectionState.已断开);
    }

    // ---------- 对外通信入口 ----------

    /// <summary>
    /// 在独占连接上执行一次通信（轮询 / 写值）。
    /// 连接级错误会自动标记连接丢失并触发重连流程。
    /// </summary>
    public async Task<(CommResult Result, Exception? Error)> ExecuteAsync(
        Func<IModbusClient, CancellationToken, Task> action, int timeoutMs = 10000)
    {
        if (!IsConnected || _cts is null || _cts.IsCancellationRequested)
            return (CommResult.Skipped, null);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        linked.CancelAfter(timeoutMs);
        try
        {
            await _gate.WaitAsync(linked.Token);
            try
            {
                if (!IsConnected || _cts.IsCancellationRequested)
                    return (CommResult.Skipped, null);
                await action(_client, linked.Token);
                _lastSuccessAt = DateTime.Now;   // 链路确实通着，心跳可以不用插手
                return (CommResult.Ok, null);
            }
            finally
            {
                try { _gate.Release(); } catch { /* ignore */ }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            return (CommResult.Skipped, null); // 正在停止
        }
        catch (OperationCanceledException)
        {
            var ex = new TimeoutException($"通信超时（{timeoutMs} ms）");
            NotifyConnectionLost(ex);
            return (CommResult.ConnectionLost, ex);
        }
        catch (Exception ex)
        {
            if (IsConnectionLevel(ex))
            {
                NotifyConnectionLost(ex);
                return (CommResult.ConnectionLost, ex);
            }
            return (CommResult.OperationFailed, ex);
        }
    }

    /// <summary>外部（轮询/写值）检测到连接级错误时调用：关闭连接并进入重连流程。</summary>
    public void NotifyConnectionLost(Exception ex)
    {
        if (State is not (ConnectionState.已连接 or ConnectionState.连接中)) return;
        try { _client.Close(); } catch { /* ignore */ }
        ConnectedSince = null;
        ReconnectAttempts = Math.Max(1, ReconnectAttempts); // 保证重试前先退避
        SetState(ConnectionState.重连中);
        ConnectionLost?.Invoke(ex);
    }

    // ---------- 监督循环：自动重连 + 心跳检测 ----------

    private async Task SuperviseAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                switch (State)
                {
                    case ConnectionState.已断开:
                    case ConnectionState.重连中:
                        await TryConnectAsync(ct);
                        break;

                    case ConnectionState.已连接:
                        // 心跳只在「链路静默」时才有必要：
                        // 若最近刚成功通信过（通常是轮询刚读到数据），说明链路正常，
                        // 不必再额外探测——这样也避免了「探测点配置不当导致假掉线」。
                        if (_cfg.HeartbeatEnabled && !_heartbeatStopped)
                        {
                            var gap = Math.Max(500, _cfg.HeartbeatIntervalMs);
                            if ((DateTime.Now - _lastSuccessAt).TotalMilliseconds >= gap &&
                                (DateTime.Now - _lastHeartbeatAt).TotalMilliseconds >= gap)
                            {
                                await HeartbeatAsync(ct);
                            }
                        }
                        await Delay(500, ct);
                        break;

                    case ConnectionState.重连失败:
                        // 已放弃自动重试，等待用户重启监控或修改配置
                        await Delay(5000, ct);
                        break;

                    default: // 连接中
                        await Delay(200, ct);
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // 监督循环永不退出
            }
        }
    }

    /// <summary>发起一次连接尝试（按退避策略等待后执行）。</summary>
    private async Task TryConnectAsync(CancellationToken ct)
    {
        // 自动重连被关闭且已经失败过一次 → 放弃
        if (!_cfg.AutoReconnect && ReconnectAttempts > 0)
        {
            SetState(ConnectionState.重连失败);
            ReconnectGaveUp?.Invoke();
            return;
        }

        // 第 2 次及以后的重试先等待退避时间（首次连接立即尝试）
        if (ReconnectAttempts > 0)
            await Delay(BackoffMs(ReconnectAttempts), ct);

        if (ct.IsCancellationRequested) return;
        SetState(ConnectionState.连接中);

        Exception? error = null;
        try
        {
            await _gate.WaitAsync(ct);
            try
            {
                if (ct.IsCancellationRequested) return;
                if (!_client.IsConnected) await _client.ConnectAsync(ct);
            }
            finally
            {
                try { _gate.Release(); } catch { /* ignore */ }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            error = ex;
        }

        if (error is null)
        {
            ReconnectAttempts = 0;
            ConnectedSince = DateTime.Now;
            _lastHeartbeatAt = DateTime.Now;
            _lastSuccessAt = DateTime.Now;
            // 重连成功后重新尝试心跳（从站配置可能已修正）
            _probeIndex = 0;
            _heartbeatStopped = false;
            HeartbeatStopReason = null;
            SetState(ConnectionState.已连接);
            ConnectionRestored?.Invoke();
            return;
        }

        ReconnectAttempts++;
        ReconnectAttemptFailed?.Invoke(error);
        if (_cfg.MaxReconnectAttempts > 0 && ReconnectAttempts >= _cfg.MaxReconnectAttempts)
        {
            SetState(ConnectionState.重连失败);
            ReconnectGaveUp?.Invoke();
        }
        else
        {
            SetState(ConnectionState.重连中);
        }
    }

    /// <summary>
    /// 心跳检测：轻量读 1 个点，用于发现半开 / 僵死连接。
    ///
    /// 与旧行为的关键区别：从站回了<b>异常响应</b>（例如线圈区根本不存在）属于
    /// 「配置问题」而不是「链路故障」。旧实现用 catch-all 把它一律当成连接丢失，
    /// 结果就是连接 → 探测失败 → 重连 → 再探测失败，界面上表现为
    /// 「已连接 / 自动重连中」反复横跳。现在改为：
    /// - 异常响应 → 自动换下一个探测点，全部不可用则停用心跳（连接保持）；
    /// - 连接级错误（Socket / IO / 超时）→ 才判定连接丢失并触发重连。
    /// </summary>
    private async Task HeartbeatAsync(CancellationToken ct)
    {
        _lastHeartbeatAt = DateTime.Now;
        if (_gate.CurrentCount == 0) return; // 轮询/写值正在占用连接，本轮跳过

        Exception? lastProbeError = null;

        while (_probeIndex < _probes.Count)
        {
            var (area, addr) = _probes[_probeIndex];
            bool isBit = area is ModbusArea.线圈 or ModbusArea.离散输入;
            try
            {
                await _gate.WaitAsync(ct);
                try
                {
                    if (!IsConnected || ct.IsCancellationRequested) return;
                    if (isBit) await _client.ReadBitsAsync(area, addr, 1, ct);
                    else await _client.ReadRegistersAsync(area, addr, 1, ct);
                    LastHeartbeatTime = DateTime.Now;
                    _lastSuccessAt = DateTime.Now;
                    HeartbeatTick?.Invoke();
                    return;                       // 探测成功，本轮结束
                }
                finally
                {
                    try { _gate.Release(); } catch { /* ignore */ }
                }
            }
            catch (OperationCanceledException)
            {
                return;                           // 停止中
            }
            catch (Exception ex) when (!IsConnectionLevel(ex))
            {
                // 从站不接受这个请求（区域/地址不可用）→ 换下一个探测点
                lastProbeError = ex;
                HeartbeatProbeFailed?.Invoke(ex);
                _probeIndex++;
            }
            catch (Exception ex)
            {
                NotifyConnectionLost(ex);         // 真正的链路问题
                return;
            }
        }

        // 所有候选都不可用：停用心跳，但连接本身保持正常
        _heartbeatStopped = true;
        HeartbeatStopReason = lastProbeError is null
            ? "无可用心跳探测点"
            : "心跳探测点不可用：" + CommError.Describe(lastProbeError);
        HeartbeatProbeFailed?.Invoke(lastProbeError ?? new InvalidOperationException(HeartbeatStopReason));
    }

    // ---------- 辅助 ----------

    private void SetState(ConnectionState s)
    {
        if (State == s) return;
        State = s;
        StateChanged?.Invoke(s);
    }

    /// <summary>指数退避：base × 2^(n-1)，上限 30s。</summary>
    private int BackoffMs(int attempt)
    {
        int b = Math.Max(500, _cfg.ReconnectBaseDelayMs);
        int shift = Math.Clamp(attempt - 1, 0, 4);
        long ms = (long)b << shift;
        return (int)Math.Min(ms, 30_000);
    }

    private static bool IsConnectionLevel(Exception ex) =>
        ex is SocketException or IOException or TimeoutException
            or ObjectDisposedException or InvalidOperationException;

    private static async Task Delay(int ms, CancellationToken ct)
    {
        try { await Task.Delay(ms, ct); }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        Stop();
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }
}
