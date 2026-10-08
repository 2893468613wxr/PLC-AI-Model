using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;

namespace WpfApp20.Services;

/// <summary>可供大模型调用的工具声明（函数名 + 说明 + JSON Schema）。</summary>
public sealed record ToolSpec(string Name, string Description, string ParametersJson);

/// <summary>数据点的实时快照（纯数据，可跨线程安全使用）。</summary>
public sealed record TagSnapshot(
    string Device, string Name, string Address, string DataType,
    double Value, string Display, bool Quality, DateTime Timestamp,
    bool Alarming, string AlarmText, bool Writable);

/// <summary>设备的运行时快照。</summary>
public sealed record DeviceSnapshot(
    int Index, string Name, string StateText, string EndPoint, bool Simulated, bool Enabled,
    long Success, long Fail, double RttMs, string LastError, DateTime? LastPoll,
    IReadOnlyList<TagSnapshot> Tags);

/// <summary>
/// 把上位机的实时数据暴露成一组可被大模型调用的工具。
/// 所有取值都在 UI 线程上做一次快照，避免跨线程直接操作 ObservableCollection。
/// </summary>
public static class PlcTools
{
    /// <summary>是否允许 AI 向 PLC 写入数据（默认关闭，需在界面显式开启）。</summary>
    public static bool AllowWrites { get; set; }

    public static IReadOnlyList<ToolSpec> Specs { get; } =
    [
        new("list_devices",
            "列出所有 PLC 设备的编号、名称、连接状态、点位数量与通讯统计。当询问有哪些设备、设备是否在线、通讯是否正常时使用。",
            """{"type":"object","properties":{},"required":[]}"""),

        new("get_device_data",
            "读取指定设备的全部数据点实时值。当询问某台设备的数据、运行状况、详细参数时使用。",
            """{"type":"object","properties":{"device":{"type":"string","description":"设备名或编号，例如 1号线 / 2#线 / 仿真 PLC 1#线。用户问全部/所有设备时**必须留空**，否则会漏查设备。"}},"required":[]}"""),

        new("query_tags",
            "按关键词在全部数据中检索数据点的实时值，可按设备限定。例如查 温度 会返回所有名称含温度的点。",
            """{"type":"object","properties":{"keyword":{"type":"string","description":"数据点名称关键词，如 温度、压力、液位、计数、电机"},"device":{"type":"string","description":"限定设备名或编号；用户问全部/所有设备时必须留空，否则会漏查设备"}},"required":["keyword"]}"""),

        new("get_active_alarms",
            "获取当前处于触发状态的报警（数据点越限、设备通讯中断等）。当询问有无故障、异常、报警时使用。",
            """{"type":"object","properties":{"device":{"type":"string","description":"限定设备名或编号；用户问全部/所有设备时必须留空，否则会漏查设备"}},"required":[]}"""),

        new("get_alarm_history",
            "获取最近的报警历史记录，包含已恢复和已确认的条目。",
            """{"type":"object","properties":{"device":{"type":"string","description":"限定设备名或编号；用户问全部/所有设备时必须留空"},"limit":{"type":"integer","description":"返回条数，默认 10"}},"required":[]}"""),

        new("get_device_stats",
            "获取设备的通信质量统计：轮询成功与失败次数、最近一轮耗时、最后错误信息。用于判断通讯是否稳定。",
            """{"type":"object","properties":{"device":{"type":"string","description":"设备名或编号；用户问全部/所有设备时必须留空"}},"required":[]}"""),

        new("get_tag_history",
            "读取数据点最近的采集历史（当日 CSV 记录），返回最近若干条采样，用于判断变化趋势。",
            """{"type":"object","properties":{"device":{"type":"string","description":"设备名或编号"},"tag":{"type":"string","description":"数据点名称关键词"},"count":{"type":"integer","description":"返回采样条数，默认 10，最大 60"}},"required":[]}"""),

        new("write_tag_value",
            "向可写的数据点写入工程量数值（如设定值、启停命令）。仅在用户明确提出写入、下发、设定要求时才调用。",
            """{"type":"object","properties":{"device":{"type":"string","description":"设备名或编号"},"tag":{"type":"string","description":"数据点名称关键词"},"value":{"type":"number","description":"要写入的工程量数值"}},"required":["tag","value"]}"""),
    ];

    /// <summary>执行一次工具调用，返回给模型的纯文本结果。</summary>
    public static async Task<string> ExecuteAsync(string name, JsonElement args)
    {
        try
        {
            return name switch
            {
                "list_devices" => ListDevices(),
                "get_device_data" => GetDeviceData(Str(args, "device")),
                "query_tags" => QueryTags(Str(args, "keyword") ?? "", Str(args, "device")),
                "get_active_alarms" => GetAlarms(false, Str(args, "device"), 10),
                "get_alarm_history" => GetAlarms(true, Str(args, "device"), Int(args, "limit", 10)),
                "get_device_stats" => GetDeviceStats(Str(args, "device")),
                "get_tag_history" => GetTagHistory(Str(args, "device"), Str(args, "tag"), Int(args, "count", 10)),
                "write_tag_value" => await WriteTagAsync(Str(args, "device"), Str(args, "tag") ?? "", Dbl(args, "value", double.NaN)),
                _ => $"未知工具 {name}。可用工具：{string.Join("、", Specs.Select(s => s.Name))}",
            };
        }
        catch (Exception ex)
        {
            return $"工具 {name} 执行失败：{ex.Message}";
        }
    }

    // ────────────────────────────── 快照 ──────────────────────────────

    /// <summary>在 UI 线程上抓取全部设备与数据点的快照。</summary>
    public static IReadOnlyList<DeviceSnapshot> SnapshotAll() => Ui(() =>
        App.Manager.Devices.Select((d, i) => new DeviceSnapshot(
            i + 1, d.Name, d.StateText, d.EndPoint, d.Simulated, d.Config.Enabled,
            d.SuccessCount, d.FailCount, d.LastRoundTripMs, d.LastError, d.LastPollTime,
            d.Tags.Select(Snap).ToList())).ToList());

    private static TagSnapshot Snap(TagValueItem t) => new(
        t.DeviceName, t.Name, t.AddressText, t.DataTypeText,
        t.Value, t.DisplayValue, t.QualityGood, t.Timestamp,
        t.IsAlarming, t.ActiveAlarmText, t.Writable);

    private static T Ui<T>(Func<T> f)
    {
        var app = Application.Current;
        return app is null || app.Dispatcher.CheckAccess() ? f() : app.Dispatcher.Invoke(f);
    }

    // ──────────────────────────── 设备定位 ────────────────────────────

    private static readonly Regex NumRe = new(@"\d+", RegexOptions.Compiled);

    /// <summary>把"1号线""2#线""车间 PLC"这类口语提示解析到具体设备。快照版。</summary>
    private static DeviceSnapshot? ResolveSnapshot(IReadOnlyList<DeviceSnapshot> devices, string? hint)
        => ResolveNamed(devices.Select(d => (d.Name, (DeviceSnapshot?)d)).ToList(), hint);

    /// <summary>同上，但返回 UI 线程上的实时 DeviceRuntime 对象（写值、报警过滤需要原对象）。</summary>
    private static DeviceRuntime? ResolveLive(string? hint)
        => Ui(() => ResolveNamed(App.Manager.Devices.Select(d => (d.Name, (DeviceRuntime?)d)).ToList(), hint));

    /// <summary>
    /// 设备名模糊定位：精确匹配 → 相互包含 → 名称里的 "N#" → 按序号 → 去停用词后再包含匹配。
    /// 提示为空且只有一台设备时直接返回它，方便单设备场景。
    /// </summary>
    private static T? ResolveNamed<T>(IReadOnlyList<(string Name, T? Item)> entries, string? hint) where T : class
    {
        if (entries.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(hint)) return entries.Count == 1 ? entries[0].Item : null;

        var h = hint.Trim();
        var names = entries.Select(e => e.Name ?? string.Empty).ToList();

        // 1) 名称精确 / 互相包含
        var hit = PickByName(names, h);
        if (hit is not null) return entries.First(e => e.Name == hit).Item;

        // 2) 抽取数字：先匹配名称里的 "N#"，再退化为按序号取第 N 台
        var numbers = NumRe.Matches(h)
            .Select(m => int.Parse(m.ValueSpan, CultureInfo.InvariantCulture))
            .ToList();

        foreach (var n in numbers)
        {
            var byHash = names.FirstOrDefault(x => Regex.IsMatch(x, $@"(?<![0-9]){n}\s*#"));
            if (byHash is not null) return entries.First(e => e.Name == byHash).Item;
        }

        foreach (var n in numbers)
            if (n >= 1 && n <= entries.Count)
                return entries[n - 1].Item;

        // 3) 去掉"设备/号/线/PLC"等停用词后再做包含匹配
        var core = h.Replace("设备", "").Replace("号线", "").Replace("线", "")
                    .Replace("号", "").Replace("PLC", "").Replace("plc", "").Trim();
        if (core.Length > 0)
        {
            hit = PickByName(names, core, containsOnly: true);
            if (hit is not null) return entries.First(e => e.Name == hit).Item;
        }

        return null;
    }

    /// <summary>名称列表内的匹配：先相等，再双向包含。</summary>
    private static string? PickByName(List<string> names, string h, bool containsOnly = false)
    {
        if (!containsOnly)
        {
            var exact = names.FirstOrDefault(x => string.Equals(x, h, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
        }
        return names.FirstOrDefault(x => x.Contains(h, StringComparison.OrdinalIgnoreCase));
    }

    private static string DeviceOptions(IReadOnlyList<DeviceSnapshot> devices) =>
        "未匹配到该设备。现有设备：" + string.Join("；", devices.Select(d => $"#{d.Index} {d.Name}"));

    /// <summary>数据点名称模糊匹配：点名含关键词，或关键词含点名。</summary>
    private static bool TagHit(string tagName, string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return true;
        return tagName.Contains(keyword.Trim(), StringComparison.OrdinalIgnoreCase)
            || keyword.Trim().Contains(tagName, StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────────────── 工具实现 ────────────────────────────

    private static string Stamp(DateTime ts) =>
        ts == default ? "--" : ts.ToString("HH:mm:ss");

    /// <summary>设备是否真的连着。离线的设备没有有效读数，绝不能把残留值当实时值用。</summary>
    private static bool IsOnline(DeviceSnapshot d) =>
        d.StateText.StartsWith("已连接", StringComparison.Ordinal);

    /// <summary>多设备场景下，若本次查询被设备名收窄，提醒模型可能漏掉了其它设备。</summary>
    private static string ScopeNote(IReadOnlyList<DeviceSnapshot> devices, string? hint) =>
        devices.Count > 1 && !string.IsNullOrWhiteSpace(hint)
            ? $"（提醒：系统共有 {devices.Count} 台设备，本次只查了其中一部分。" +
              "如果用户问的是全部/所有设备，请留空 device 参数重新调用。）"
            : "";

    /// <summary>设备一行表头；离线时明确标注数据不可信。</summary>
    private static string Header(DeviceSnapshot d, bool online)
    {
        var head = $"【{d.Name}】状态：{d.StateText}（{(d.Simulated ? "仿真" : "真实 Modbus TCP")}，{d.EndPoint}）";
        return online
            ? head
            : head + $" ⚠ 该设备当前未建立连接，下面的数值均为上一次的残留值，不是实时数据。" +
              "请勿用它们回答具体读数，应当如实告诉用户该设备离线、读不到数据。" +
              (string.IsNullOrEmpty(d.LastError) ? "" : $" 最近错误：{d.LastError}");
    }

    /// <summary>单个数据点的一行描述；离线时不输出任何数值。</summary>
    private static string Render(TagSnapshot t, bool online)
    {
        var line = $"{t.Name} = {(online ? t.Display : "（离线，无有效读数）")}";

        if (online)
        {
            line += $"（原始值 {t.Value:0.###}，地址 {t.Address}，{t.DataType}）" +
                    $" | 质量：{(t.Quality ? "良好" : "坏值")} | 更新于 {Stamp(t.Timestamp)}" +
                    (t.Alarming ? $" | ⚠ 报警：{t.AlarmText}" : "");
        }
        else
        {
            line += $"（地址 {t.Address}，{t.DataType}） | 设备离线，本次未采集到有效数据";
        }

        return line + (t.Writable ? " | 可写" : "");
    }

    private static string CommLine(DeviceSnapshot d)
    {
        var total = d.Success + d.Fail;
        return $"  通讯：成功 {d.Success} / 失败 {d.Fail}" +
               (total > 0 ? $"，成功率 {d.Success * 100d / total:0.##}%" : "") +
               $"，最近耗时 {d.RttMs:0} ms" +
               (string.IsNullOrEmpty(d.LastError) ? "" : $" | 最近错误：{d.LastError}");
    }

    private static string ListDevices()
    {
        var devices = SnapshotAll();
        if (devices.Count == 0) return "当前没有配置任何设备。请先在「设备管理」中添加设备与数据点。";

        var sb = new StringBuilder();
        sb.AppendLine($"共 {devices.Count} 台设备：");
        foreach (var d in devices)
        {
            sb.AppendLine(
                $"#{d.Index} {d.Name} | 状态：{d.StateText} | 地址：{d.EndPoint}" +
                $" | {(d.Simulated ? "仿真模式" : "真实 Modbus TCP")}" +
                $" | 已启用：{(d.Enabled ? "是" : "否")}" +
                $" | 点位 {d.Tags.Count} 个" +
                $" | 通讯：成功 {d.Success} / 失败 {d.Fail}" +
                (string.IsNullOrEmpty(d.LastError) ? "" : $" | 最近错误：{d.LastError}"));
            if (d.Tags.Count > 0)
                sb.AppendLine("   数据点：" + string.Join("、", d.Tags.Select(t => t.Name)));
        }
        return sb.ToString().TrimEnd();
    }

    private static string GetDeviceData(string? deviceHint)
    {
        var devices = SnapshotAll();
        if (devices.Count == 0) return "当前没有配置任何设备。";

        IEnumerable<DeviceSnapshot> targets;
        if (string.IsNullOrWhiteSpace(deviceHint))
        {
            targets = devices;
        }
        else
        {
            var d = ResolveSnapshot(devices, deviceHint);
            if (d is null) return DeviceOptions(devices);
            targets = [d];
        }

        var sb = new StringBuilder();
        foreach (var d in targets)
        {
            var online = IsOnline(d);
            sb.AppendLine(Header(d, online));

            if (d.Tags.Count == 0)
            {
                sb.AppendLine("  该设备尚未配置数据点。");
                continue;
            }
            foreach (var t in d.Tags)
                sb.AppendLine("  " + Render(t, online));
        }

        var note = ScopeNote(devices, deviceHint);
        return note.Length > 0 ? sb.ToString().TrimEnd() + "\n" + note : sb.ToString().TrimEnd();
    }

    private static string QueryTags(string keyword, string? deviceHint)
    {
        var devices = SnapshotAll();
        if (devices.Count == 0) return "当前没有配置任何设备。";

        IEnumerable<DeviceSnapshot> targets;
        if (string.IsNullOrWhiteSpace(deviceHint))
        {
            targets = devices;
        }
        else
        {
            var d = ResolveSnapshot(devices, deviceHint);
            if (d is null) return DeviceOptions(devices);
            targets = [d];
        }

        var hits = targets.SelectMany(d => d.Tags.Where(t => TagHit(t.Name, keyword)).Select(t => (d, t))).ToList();
        if (hits.Count == 0)
        {
            var all = targets.SelectMany(d => d.Tags.Select(t => t.Name)).Distinct().ToList();
            return $"没有找到名称匹配“{keyword}”的数据点。可用数据点：{string.Join("、", all)}";
        }

        var sb = new StringBuilder($"匹配“{keyword}”的数据点 {hits.Count} 个：");
        sb.AppendLine();
        foreach (var (d, t) in hits)
            sb.AppendLine($"  {d.Name} → {Render(t, IsOnline(d))}");

        var note = ScopeNote(devices, deviceHint);
        return note.Length > 0 ? sb.ToString().TrimEnd() + "\n" + note : sb.ToString().TrimEnd();
    }

    private static string GetAlarms(bool history, string? deviceHint, int limit)
    {
        limit = Math.Clamp(limit, 1, 50);

        return Ui(() =>
        {
            DeviceRuntime? dev = null;
            if (!string.IsNullOrWhiteSpace(deviceHint))
                dev = ResolveLive(deviceHint);

            var source = history
                ? App.Manager.Alarms.History.AsEnumerable().Reverse()
                : App.Manager.Alarms.ActiveAlarms.AsEnumerable();

            if (dev is not null)
                source = source.Where(a => string.Equals(a.DeviceName, dev.Name, StringComparison.OrdinalIgnoreCase));

            var list = source.Take(limit).ToList();
            var note = AlarmScopeNote(deviceHint);

            if (list.Count == 0)
                return WithNote(history
                    ? (dev is null ? "暂无报警历史记录。" : $"{dev.Name} 暂无报警历史记录。")
                    : (dev is null ? "当前没有任何活动报警，所有设备运行正常。" : $"{dev.Name} 当前没有活动报警。"));

            string Tip(AlarmRecord a) =>
                $"[{a.FullTimeText}] {a.DeviceName} / {a.TagName} | {a.LevelText} | {a.StatusText} | {a.Message}";

            return WithNote((history ? $"最近 {list.Count} 条报警历史：" : $"当前 {list.Count} 条活动报警：") + "\n" +
                            string.Join("\n", list.Select(a => "  " + Tip(a))));

            string WithNote(string s) => note.Length == 0 ? s : s + "\n" + note;
        });
    }

    /// <summary>告警可能出现不少通讯类报警，若被设备名收窄则提示模型。</summary>
    private static string AlarmScopeNote(string? deviceHint) =>
        !string.IsNullOrWhiteSpace(deviceHint) && SnapshotAll().Count > 1
            ? "（提醒：本次只查了部分设备。若用户问的是全部设备，请留空 device 参数重新调用。）"
            : "";

    /// <summary>
    /// 启发式检索：直接从自然语言中猜它想问哪台设备、哪些数据点，
    /// 返回对应的真实数据文本。用于模型没有主动调用工具时的兜底。
    /// </summary>
    public static string RetrieveFor(string question)
    {
        var devices = SnapshotAll();
        if (devices.Count == 0) return "当前没有配置任何设备，无法读取任何数据。";

        var sb = new StringBuilder();

        var target = ResolveSnapshot(devices, question);

        if (target is not null)
        {
            var online = IsOnline(target);

            // 句子里点到了具体的点名就只给这些点，否则给整台设备
            var matched = target.Tags.Where(t => SharesToken(t.Name, question)).ToList();
            var tags = matched.Count > 0 ? matched : target.Tags.ToList();

            sb.AppendLine(Header(target, online));
            foreach (var t in tags) sb.AppendLine("  " + Render(t, online));
            sb.AppendLine("  " + CommLine(target).TrimStart());

            if (question.Contains("报警") || question.Contains("故障")
             || question.Contains("异常") || question.Contains("警告"))
                sb.AppendLine().AppendLine(GetAlarms(false, target.Name, 10));
        }
        else
        {
            foreach (var d in devices)
            {
                var online = IsOnline(d);
                sb.AppendLine(Header(d, online));
                foreach (var t in d.Tags) sb.AppendLine("  " + Render(t, online));
                sb.AppendLine("  " + CommLine(d).TrimStart());
            }

            if (question.Contains("报警") || question.Contains("故障")
             || question.Contains("异常") || question.Contains("警告"))
                sb.AppendLine().AppendLine(GetAlarms(false, null, 10));
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>判断一句话是否像是在问设备数据（用于决定是否触发兜底校验）。</summary>
    public static bool IsDataQuestion(string question)
    {
        if (string.IsNullOrWhiteSpace(question)) return false;

        var devices = SnapshotAll();
        if (devices.Count > 0)
        {
            // 提到了某台设备的名字
            if (devices.Any(d => question.Contains(d.Name, StringComparison.OrdinalIgnoreCase)
                              || d.Name.Contains(question.Trim(), StringComparison.OrdinalIgnoreCase)))
                return true;

            // 提到了某个数据点的名字
            if (devices.SelectMany(d => d.Tags).Any(t => SharesToken(t.Name, question)))
                return true;
        }

        string[] cues =
        [
            "多少", "数值", "读数", "实时", "当前", "现在", "最新", "报警", "故障", "异常", "警告",
            "状况", "状态", "运行", "温度", "压力", "液位", "流量", "计数", "通信", "通讯", "在线",
        ];

        return cues.Any(question.Contains);
    }

    /// <summary>数据点名与问句是否共享关键词（按 2 字滑窗匹配，适配中文命名）。</summary>
    private static bool SharesToken(string tagName, string question)
    {
        if (string.IsNullOrWhiteSpace(tagName)) return false;
        if (question.Contains(tagName, StringComparison.OrdinalIgnoreCase)) return true;

        // ASCII 名称（如 Reactor_Temp）按整词判断，避免误配单字母
        if (tagName.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
            return false;

        for (var i = 0; i + 2 <= tagName.Length; i++)
            if (question.Contains(tagName.Substring(i, 2), StringComparison.Ordinal))
                return true;

        return false;
    }

    private static string GetDeviceStats(string? deviceHint)
    {
        var devices = SnapshotAll();
        if (devices.Count == 0) return "当前没有配置任何设备。";

        var targets = string.IsNullOrWhiteSpace(deviceHint)
            ? devices
            : (ResolveSnapshot(devices, deviceHint) is { } d ? new[] { d } : Array.Empty<DeviceSnapshot>());

        if (targets.Count == 0) return DeviceOptions(devices);

        var body = string.Join("\n", targets.Select(d =>
        {
            var lastPoll = d.LastPoll is null ? "从未成功采集" : Stamp(d.LastPoll.Value);
            return OnlineFlag(d) +
                   $"【{d.Name}】状态：{d.StateText} | 轮询成功 {d.Success} 次 / 失败 {d.Fail} 次，" +
                   $"成功率 {rate(d):0.##}% | 最近一轮耗时 {d.RttMs:0} ms | 最后采集 {lastPoll}" +
                   (string.IsNullOrEmpty(d.LastError) ? "" : $" | 最近错误：{d.LastError}");
        }));

        var note = ScopeNote(devices, deviceHint);
        return note.Length > 0 ? body + "\n" + note : body;
    }

    private static double rate(DeviceSnapshot d)
    {
        var total = d.Success + d.Fail;
        return total == 0 ? 0d : d.Success * 100d / total;
    }

    /// <summary>离线设备的显著提示，避免把"从未采集"的设备说成通讯正常。</summary>
    private static string OnlineFlag(DeviceSnapshot d) =>
        IsOnline(d) ? "" : "⚠ 离线设备：";

    private static string GetTagHistory(string? deviceHint, string? tagKeyword, int count)
    {
        count = Math.Clamp(count, 1, 60);

        var devices = SnapshotAll();
        if (devices.Count == 0) return "当前没有配置任何设备。";

        var dev = ResolveSnapshot(devices, deviceHint);
        if (dev is null) return DeviceOptions(devices);

        var file = Path.Combine(App.Manager.Logger.DataDir, $"{Sanitize(dev.Name)}_{DateTime.Now:yyyyMMdd}.csv");
        if (!File.Exists(file))
            return $"{dev.Name} 今日尚无采集历史（未找到 {Path.GetFileName(file)}）。请确认已启用数据记录且设备正在轮询。";

        string[] lines;
        try
        {
            lines = File.ReadAllLines(file);
        }
        catch (IOException ex)
        {
            return $"读取历史文件失败：{ex.Message}";
        }

        if (lines.Length < 2) return $"{dev.Name} 今日的采集文件为空。";

        var header = SplitCsv(lines[0]);
        var col = -1;
        if (!string.IsNullOrWhiteSpace(tagKeyword))
        {
            col = header
                .Select((h, i) => (h, i))
                .Where(x => x.i > 0 && TagHit(x.h, tagKeyword))
                .Select(x => x.i)
                .FirstOrDefault(-1);

            if (col < 1) return $"采集文件里没有匹配“{tagKeyword}”的列。可用列：{string.Join("、", header.Skip(1))}";
        }

        var rows = lines.Skip(1).Where(l => l.Length > 0).ToList();
        if (rows.Count == 0) return $"{dev.Name} 今日的采集文件尚无数据行。";

        var picked = rows.TakeLast(Math.Min(count, rows.Count)).ToList();

        var sb = new StringBuilder();
        var columns = col > 0 ? new[] { 0, col } : Enumerable.Range(0, header.Length).ToArray();
        sb.AppendLine($"{dev.Name} 最近 {picked.Count} 条采集记录（{DateTime.Now:yyyy-MM-dd}）：");
        foreach (var row in picked)
        {
            var cells = SplitCsv(row);
            var parts = columns
                .Where(c => c < cells.Length)
                .Select(c => c == 0 ? cells[c][^8..] : $"{header[c]}={TrimNum(cells[c])}");
            sb.AppendLine("  " + string.Join("  ", parts));
        }
        return sb.ToString().TrimEnd();
    }

    private static string TrimNum(string s)
    {
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        return s;
    }

    private static string[] SplitCsv(string line)
    {
        var res = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        foreach (var c in line)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (c == ',' && !quoted) { res.Add(sb.ToString()); sb.Clear(); continue; }
            sb.Append(c);
        }
        res.Add(sb.ToString());
        return res.ToArray();
    }

    private static string Sanitize(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name)
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        return sb.ToString();
    }

    private static async Task<string> WriteTagAsync(string? deviceHint, string tagKeyword, double value)
    {
        if (double.IsNaN(value))
            return "缺少要写入的数值 value。";
        if (!AllowWrites)
            return "当前未开启写入权限。请先在界面勾选「允许 AI 写入 PLC」，确认后我才能下发数据。";

        var item = Ui<TagValueItem?>(() =>
        {
            if (string.IsNullOrWhiteSpace(deviceHint))
                return App.Manager.AllTags.FirstOrDefault(t => TagHit(t.Name, tagKeyword));

            var dev = ResolveLive(deviceHint);
            return dev is null
                ? null
                : dev.Tags.FirstOrDefault(t => TagHit(t.Name, tagKeyword));
        });

        if (item is null)
        {
            var names = Ui(() => App.Manager.AllTags.Select(t => $"{t.DeviceName}/{t.Name}").ToList());
            return $"没有找到可写的数据点“{tagKeyword}”。现有数据点：{string.Join("、", names)}";
        }
        if (!item.Writable)
            return $"数据点 {item.DeviceName}/{item.Name}（{item.AddressText}）位于只读区域，不支持写入。";

        try
        {
            await App.Manager.WriteTagAsync(item, value);
            return $"已向 {item.DeviceName}/{item.Name} 写入 {value:0.###}，等待下一轮轮询后该值将刷新。";
        }
        catch (Exception ex)
        {
            return $"写入失败：{ex.Message}";
        }
    }

    // ────────────────────────────── 参数读取 ──────────────────────────────

    private static string? Str(JsonElement args, string key) =>
        args.ValueKind == JsonValueKind.Object
        && args.TryGetProperty(key, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static int Int(JsonElement args, string key, int def)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(key, out var v)) return def;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt32(out var i) => i,
            JsonValueKind.String when int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) => s,
            _ => def,
        };
    }

    private static double Dbl(JsonElement args, string key, double def)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(key, out var v)) return def;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetDouble(out var d) => d,
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) => s,
            _ => def,
        };
    }
}
