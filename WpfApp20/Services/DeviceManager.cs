using System.Collections.ObjectModel;
using System.Diagnostics;
using WpfApp20.Core;
using WpfApp20.Models;
using WpfApp20.Modbus;

namespace WpfApp20.Services;

/// <summary>
/// 设备管理器：持有全部设备运行时，负责启动/停止每台设备的轮询任务，
/// 分块读取（01/02/03/04），写值（05/06/0F/10），并把结果分发到 UI / 报警引擎 / 数据记录。
/// 每台设备一个 <see cref="ModbusConnectionManager"/>（自动重连 + 心跳检测），
/// 所有通信共用一个信号量串行化，避免 Modbus TCP 并发事务冲突。
/// </summary>
public class DeviceManager
{
    private readonly Dictionary<Guid, DeviceWorker> _workers = [];
    private readonly object _workersSync = new();

    public ObservableCollection<DeviceRuntime> Devices { get; } = [];
    /// <summary>所有设备所有标签的扁平视图（实时监控页用）。</summary>
    public ObservableCollection<TagValueItem> AllTags { get; } = [];
    public AlarmEngine Alarms { get; }
    public DataLogService Logger { get; }
    public AppConfig Config { get; private set; } = new();
    public ConfigService ConfigService { get; }

    public bool DataLogEnabled
    {
        get => Config.DataLogEnabled;
        set { Config.DataLogEnabled = value; ConfigService.Save(Config); }
    }

    public bool AlarmSoundEnabled
    {
        get => Config.AlarmSoundEnabled;
        set { Config.AlarmSoundEnabled = value; Alarms.SoundEnabled = value; ConfigService.Save(Config); }
    }

    public event Action? DevicesChanged;

    public DeviceManager(ConfigService configService, DataLogService logger, AlarmEngine alarms)
    {
        ConfigService = configService;
        Logger = logger;
        Alarms = alarms;
        Alarms.DataLogged += (rec, evt) => Logger.WriteAlarm(rec, evt);
        Load();
    }

    // ---------- 配置 ----------

    private void Load()
    {
        Config = ConfigService.Load();
        Alarms.SoundEnabled = Config.AlarmSoundEnabled;
        foreach (var cfg in Config.Devices) AddRuntime(cfg);
        RebuildAllTags();
    }

    private void RebuildAllTags()
    {
        AllTags.Clear();
        foreach (var d in Devices)
            foreach (var t in d.Tags)
                AllTags.Add(t);
    }

    private void AddRuntime(DeviceConfig cfg)
    {
        var rt = new DeviceRuntime { Config = cfg };
        rt.RebuildTags();
        Devices.Add(rt);
    }

    public void SaveConfig() => ConfigService.Save(Config);

    // ---------- 设备 CRUD ----------

    public DeviceRuntime AddDevice(DeviceConfig cfg)
    {
        Config.Devices.Add(cfg);
        AddRuntime(cfg);
        RebuildAllTags();
        SaveConfig();
        DevicesChanged?.Invoke();
        return Devices.First(d => d.Config.Id == cfg.Id);
    }

    public void RemoveDevice(DeviceRuntime rt)
    {
        Stop(rt);
        lock (_workersSync) _workers.Remove(rt.Config.Id);
        Config.Devices.Remove(rt.Config);
        Devices.Remove(rt);
        RebuildAllTags();
        SaveConfig();
        DevicesChanged?.Invoke();
    }

    /// <summary>设备配置被编辑后刷新运行时。</summary>
    public void ApplyEdit(DeviceRuntime rt)
    {
        bool wasPolling = rt.IsPolling;
        Stop(rt);
        rt.RebuildTags();
        RebuildAllTags();
        SaveConfig();
        if (wasPolling) Start(rt);
        DevicesChanged?.Invoke();
    }

    // ---------- 监控启停 ----------

    public void Start(DeviceRuntime rt)
    {
        lock (_workersSync)
        {
            if (_workers.TryGetValue(rt.Config.Id, out var existing)) existing.Stop();
            var worker = new DeviceWorker(rt, this);
            _workers[rt.Config.Id] = worker;
            worker.Start();
        }
    }

    public void Stop(DeviceRuntime rt)
    {
        lock (_workersSync)
        {
            if (_workers.Remove(rt.Config.Id, out var worker))
                worker.Stop();
        }
        rt.SetPolling(false);
        rt.State = DeviceState.已断开;
    }

    public void StartAll()
    {
        foreach (var d in Devices.Where(d => d.Config.Enabled).ToList()) Start(d);
    }

    public void StopAll()
    {
        foreach (var d in Devices.ToList()) Stop(d);
    }

    public bool IsAnyPolling => Devices.Any(d => d.IsPolling);

    // ---------- 手动操作 ----------

    /// <summary>立即对指定设备执行一轮读（要求监控已启动）。</summary>
    public Task PollOnceAsync(DeviceRuntime rt)
    {
        DeviceWorker? worker;
        lock (_workersSync) worker = _workers.GetValueOrDefault(rt.Config.Id);
        return worker?.PollOnceAsync() ?? Task.CompletedTask;
    }

    /// <summary>向标签写值（复用该设备的活动连接）。</summary>
    public async Task WriteTagAsync(TagValueItem tag, double engineeringValue)
    {
        DeviceWorker? worker;
        lock (_workersSync) worker = _workers.GetValueOrDefault(tag.Device.Config.Id);
        if (worker is null)
            throw new InvalidOperationException("设备监控未启动，请先在“设备管理”中启动该设备。");

        var def = tag.Tag;
        if (!def.Writable)
            throw new InvalidOperationException("该区域只读（仅线圈 / 保持寄存器可写）。");
        if (!worker.Conn.IsConnected)
            throw new InvalidOperationException($"设备未连接（{worker.Runtime.StateText}），写值已取消。");

        var (result, error) = await worker.Conn.ExecuteAsync(async (client, ct) =>
        {
            if (def.Area == ModbusArea.线圈)
            {
                await client.WriteSingleCoilAsync(def.Address, engineeringValue != 0, ct);
            }
            else
            {
                var words = ValueCodec.Encode(def, engineeringValue);
                if (words.Length == 1)
                    await client.WriteSingleRegisterAsync(def.Address, words[0], ct);
                else
                    await client.WriteMultipleRegistersAsync(def.Address, words, ct);
            }
        }, timeoutMs: 5000);

        if (result != CommResult.Ok)
            throw error ?? new InvalidOperationException("写入失败");
        await worker.PollOnceAsync();
    }

    // ---------- 读取核心 ----------

    /// <summary>在一个已建立的连接上读取一个区域的所有标签（分块）。</summary>
    internal static async Task ReadAreaAsync(IModbusClient client, DeviceRuntime rt,
        ModbusArea area, List<TagDefinition> tags, DateTime now, CancellationToken ct, AlarmEngine alarms)
    {
        bool isBit = area is ModbusArea.线圈 or ModbusArea.离散输入;
        int limit = isBit ? 2000 : 125;
        var items = rt.Tags.ToDictionary(x => x.Tag.Id);
        var ordered = tags.OrderBy(t => t.Address).ToList();

        int i = 0;
        while (i < ordered.Count)
        {
            var start = ordered[i].Address;
            int end = start;
            int j = i;
            while (j < ordered.Count &&
                   ordered[j].Address + ordered[j].WordCount - 1 - start + 1 <= limit)
            {
                end = ordered[j].Address + ordered[j].WordCount - 1;
                j++;
            }
            ushort count = (ushort)(end - start + 1);

            if (isBit)
            {
                var bits = await client.ReadBitsAsync(area, start, count, ct);
                foreach (var t in ordered.Skip(i).Take(j - i))
                {
                    var item = items[t.Id];
                    item.Update(ValueCodec.DecodeBit(bits[t.Address - start]), true, now);
                    alarms.Evaluate(rt, item, now);
                }
            }
            else
            {
                var regs = await client.ReadRegistersAsync(area, start, count, ct);
                foreach (var t in ordered.Skip(i).Take(j - i))
                {
                    var item = items[t.Id];
                    item.Update(ValueCodec.Decode(t, regs, t.Address - start), true, now);
                    alarms.Evaluate(rt, item, now);
                }
            }
            i = j;
        }
    }
}

/// <summary>
/// 单台设备的后台任务：轮询循环 + 通信管理器（自动重连 / 心跳）事件桥接。
/// </summary>
public class DeviceWorker
{
    private readonly DeviceManager _mgr;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public DeviceRuntime Runtime { get; }
    public ModbusConnectionManager Conn { get; }
    public DeviceManager Manager => _mgr;

    public DeviceWorker(DeviceRuntime rt, DeviceManager mgr)
    {
        Runtime = rt;
        _mgr = mgr;
        Conn = new ModbusConnectionManager(rt.Config);

        // ---- 通信管理器事件 → 运行时状态 / 报警引擎 ----
        Conn.StateChanged += s => Runtime.ApplyConnectionState(s, Conn);
        Conn.HeartbeatTick += () => Runtime.RefreshConnectionInfo(Conn);
        Conn.ConnectionLost += ex =>
        {
            var text = CommError.Describe(ex);
            Runtime.LastError = "连接中断：" + text;
            _mgr.Alarms.RaiseCommAlarm(Runtime, DateTime.Now, $"连接中断：{text}");
        };
        Conn.ReconnectAttemptFailed += ex =>
            _mgr.Alarms.RaiseCommAlarm(Runtime, DateTime.Now, $"连接失败：{CommError.Describe(ex)}");
        Conn.HeartbeatProbeFailed += ex =>
        {
            // 心跳探测点不可用属于配置问题，不是通讯故障：只提示，不重连
            Runtime.LastError = "心跳提示：" + CommError.Describe(ex);
            Runtime.RefreshConnectionInfo(Conn);
        };
        Conn.ConnectionRestored += () =>
        {
            Runtime.LastError = "";
            _mgr.Alarms.ClearCommAlarm(Runtime, DateTime.Now);
            _ = PollOnceAsync(); // 连接恢复后立即刷新一轮数据
        };
        Conn.ReconnectGaveUp += () =>
        {
            var msg = Conn.Config.AutoReconnect
                ? $"自动重连失败：已尝试 {Conn.ReconnectAttempts} 次，停止重试"
                : "连接中断且自动重连已关闭";
            Runtime.LastError = msg;
            _mgr.Alarms.RaiseCommAlarm(Runtime, DateTime.Now, msg);
        };
        Runtime.ConnectionInfoRefresher = () => Runtime.RefreshConnectionInfo(Conn);
    }

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        Runtime.SetPolling(true);
        Conn.Start();                 // 启动通信管理器（连接监督 + 心跳）
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _loop?.Wait(3000); } catch { /* ignore */ }
        _cts = null;
        Conn.Stop();
        Runtime.ConnectionInfoRefresher = null;
    }

    /// <summary>立即轮询一轮（连接不可用时直接跳过，不计失败）。</summary>
    public async Task PollOnceAsync()
    {
        if (_cts is null) return;
        var sw = Stopwatch.StartNew();
        var areaErrors = new List<string>();

        var (result, error) = await Conn.ExecuteAsync(async (client, ct) =>
        {
            var now = DateTime.Now;
            foreach (var group in Runtime.Tags.GroupBy(t => t.Tag.Area))
            {
                try
                {
                    await DeviceManager.ReadAreaAsync(client, Runtime, group.Key,
                        group.Select(t => t.Tag).ToList(), now, ct, _mgr.Alarms);
                }
                catch (Exception ex)
                {
                    // 单个区域读失败（典型：线圈区在从站里根本没定义）不再拖垮整轮。
                    // 旧实现里一次异常会让后续区域全部跳过，表现为「明明连上了却没有数据」。
                    areaErrors.Add($"{EnumText.Text(group.Key)}：{CommError.Describe(ex)}");
                    foreach (var item in group) item.Update(item.Value, false, now);
                }
            }
        });

        var ts = DateTime.Now;

        if (areaErrors.Count > 0)
        {
            // 连接可能仍然正常，只是部分区域读不到：把原因明确显示到「最近错误」
            var text = string.Join(" | ", areaErrors);
            Runtime.OnPollResult(ok: false, sw.Elapsed.TotalMilliseconds, ts, text);
            _mgr.Alarms.RaiseCommAlarm(Runtime, ts, "读取失败：" + text);
            return;
        }

        switch (result)
        {
            case CommResult.Ok:
                Runtime.State = DeviceState.已连接;
                Runtime.LastError = "";
                Runtime.OnPollResult(ok: true, sw.Elapsed.TotalMilliseconds, ts, null);
                _mgr.Alarms.ClearCommAlarm(Runtime, ts);
                if (_mgr.DataLogEnabled && Runtime.Config.Enabled)
                    _mgr.Logger.WritePollData(Runtime, ts);
                break;

            case CommResult.ConnectionLost:
                // 状态与报警由通信管理器事件统一上报
                Runtime.OnPollResult(ok: false, sw.Elapsed.TotalMilliseconds, ts, error?.Message);
                break;

            case CommResult.OperationFailed:
                // Modbus 层错误（如非法地址），连接仍在：只计失败并提示
                var msg = error is null ? "读取失败" : CommError.Describe(error);
                Runtime.OnPollResult(ok: false, sw.Elapsed.TotalMilliseconds, ts, msg);
                _mgr.Alarms.RaiseCommAlarm(Runtime, ts, $"读取失败：{msg}");
                break;
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!Runtime.Config.Enabled)
            {
                Runtime.State = DeviceState.已断开;
                await SafeDelay(500, ct);
                continue;
            }
            if (Conn.IsConnected)
                await PollOnceAsync();
            await SafeDelay(Runtime.PollIntervalMs, ct);
        }
    }

    private static async Task SafeDelay(int ms, CancellationToken ct)
    {
        try { await Task.Delay(ms, ct); }
        catch (OperationCanceledException) { }
    }
}
