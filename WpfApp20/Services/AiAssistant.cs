using System.Text;
using System.Text.Json;

namespace WpfApp20.Services;

/// <summary>UI 侧观察助手执行过程的回调。</summary>
public sealed class AssistantCallbacks
{
    /// <summary>状态提示（如"正在读取实时数据…"）。</summary>
    public Func<string, Task>? Status { get; set; }

    /// <summary>一次工具调用及其结果的摘要。</summary>
    public Func<string, Task>? ToolCall { get; set; }

    /// <summary>流式输出的文本片段。</summary>
    public Func<string, Task>? Token { get; set; }
}

/// <summary>
/// 把本地大模型与上位机实时数据连起来的问答助手。
/// 采用工具调用（function calling）方式：模型决定读哪些数据，本类负责执行并把结果回传，
/// 直到模型给出最终的自然语言回答。
/// </summary>
public sealed class AiAssistant
{
    private const int MaxHistory = 16;

    private readonly List<LlmMessage> _history = [];

    /// <summary>使用的本地模型名。</summary>
    public string Model { get; set; } = LlmClient.DefaultModel;

    /// <summary>单次提问最多允许的连续工具调用轮数。</summary>
    public int MaxToolRounds { get; set; } = 4;

    public void ClearHistory() => _history.Clear();

    public async Task<string> AskAsync(string question, AssistantCallbacks cb, CancellationToken ct)
    {
        _history.Add(new LlmMessage { Role = "user", Content = question });

        var usedTool = false;
        var tools = PlcTools.Specs;

        for (var round = 0; round < MaxToolRounds; round++)
        {
            ct.ThrowIfCancellationRequested();

            if (cb.Status is not null && round == 0)
                await cb.Status("正在分析你的问题…");

            var reply = await LlmClient.ChatAsync(Model, BuildMessages(), tools, cb.Token, ct);

            if (reply.ToolCalls.Count == 0)
            {
                // 关键护栏：数据类问题却一次工具都没调用 —— 回答很可能是编的，
                // 此时由程序自己取真实数据，让模型重新作答。
                if (!usedTool && round == 0 && PlcTools.IsDataQuestion(question))
                    return await GuardAndRedoAsync(question, cb, ct);

                // 既没调工具也没有内容：兜底重试一次（不带工具、直接注入全量数据）
                if (reply.Content.Length == 0 && round == 0)
                {
                    var fallback = await LlmClient.ChatAsync(Model, BuildFallbackMessages(question), null, cb.Token, ct);
                    if (fallback.Content.Length > 0)
                    {
                        _history.Add(new LlmMessage { Role = "assistant", Content = fallback.Content });
                        TrimHistory();
                        return fallback.Content;
                    }
                }

                _history.Add(new LlmMessage { Role = "assistant", Content = reply.Content });
                TrimHistory();
                return reply.Content.Length == 0
                    ? "模型没有返回内容。可尝试换个说法，或在界面上切换其它模型重试。"
                    : reply.Content;
            }

            usedTool = true;
            _history.Add(new LlmMessage { Role = "assistant", Content = reply.Content, ToolCalls = reply.ToolCalls });

            var results = new StringBuilder();
            foreach (var call in reply.ToolCalls)
            {
                ct.ThrowIfCancellationRequested();

                if (cb.ToolCall is not null)
                    await cb.ToolCall($"调用 {call.Name}({DescribeArgs(call.Args)})");

                var result = await PlcTools.ExecuteAsync(call.Name, call.Args);
                _history.Add(new LlmMessage { Role = "tool", Content = result });

                if (cb.ToolCall is not null)
                    await cb.ToolCall(Indent(result));
                results.AppendLine(result);
            }

            if (cb.Status is not null)
                await cb.Status("已读取到设备数据，正在整理回答…");
        }

        // 轮次耗尽仍未收敛：让模型基于已拿到的数据直接作答
        _history.Add(new LlmMessage
        {
            Role = "user",
            Content = "请根据上面已经获取到的数据直接作答，不要再调用工具。",
        });

        var final = await LlmClient.ChatAsync(Model, BuildMessages(), null, cb.Token, ct);
        _history.Add(new LlmMessage { Role = "assistant", Content = final.Content });
        TrimHistory();

        return final.Content.Length == 0
            ? _history.LastOrDefault(m => m.Role == "tool")?.Content ?? "未获取到数据。"
            : final.Content;
    }

    /// <summary>
    /// 护栏：当模型没有调用工具却直接作答时，由程序自行读取真实数据并让它重答一次，
    /// 避免小模型凭想象编出一堆看起来很像但其实不存在的数值。
    /// </summary>
    private async Task<string> GuardAndRedoAsync(string question, AssistantCallbacks cb, CancellationToken ct)
    {
        if (cb.Status is not null) await cb.Status("正在向设备核对真实数据…");

        var data = PlcTools.RetrieveFor(question);
        if (cb.ToolCall is not null) await cb.ToolCall("检测到未调用工具，自动读取实时数据：\n" + Indent(data));

        _history.Add(new LlmMessage { Role = "tool", Content = data });
        _history.Add(new LlmMessage
        {
            Role = "user",
            Content = "上面是刚刚从设备读到的真实数据。请严格只依据它回答我最开始的问题；" +
                      "数据里没有的内容就直接回答未获取到，不要使用任何其它来源的数值，也不要沿用上一轮的旧值。",
        });

        if (cb.Status is not null) await cb.Status("数据已核对，正在组织回答…");

        var redo = await LlmClient.ChatAsync(Model, BuildMessages(), null, cb.Token, ct);
        var answer = redo.Content.Length == 0 ? data : redo.Content;

        _history.Add(new LlmMessage { Role = "assistant", Content = answer });
        TrimHistory();
        return answer;
    }

    private IEnumerable<LlmMessage> BuildMessages()
    {
        yield return new LlmMessage { Role = "system", Content = BuildSystemPrompt() };
        foreach (var m in _history) yield return m;
    }

    /// <summary>兜底路径：不带工具，直接把全量数据塞进上下文让模型作答。</summary>
    private IEnumerable<LlmMessage> BuildFallbackMessages(string question)
    {
        yield return new LlmMessage { Role = "system", Content = BuildSystemPrompt(omitToolGuidance: true) };

        var snapshot = PlcTools.SnapshotAll();
        var dump = new StringBuilder("以下是全部设备的实时数据快照：\n");
        foreach (var d in snapshot)
        {
            dump.AppendLine($"【{d.Name}】状态：{d.StateText}");
            foreach (var t in d.Tags)
                dump.AppendLine($"  {t.Name} = {t.Display}（更新于 {t.Timestamp:HH:mm:ss}）");
        }

        yield return new LlmMessage { Role = "user", Content = dump.ToString() };
        yield return new LlmMessage { Role = "user", Content = question };
    }

    /// <summary>系统提示：当前时间 + 设备名录（不含实时值，取值必须走工具）。</summary>
    public static string BuildSystemPrompt(bool omitToolGuidance = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine("你是运行在 PLC 监控上位机里的智能助手，可以读取这台电脑上正在采集的实时数据。");
        sb.AppendLine();

        if (!omitToolGuidance)
        {
            sb.AppendLine("硬性规则：");
            sb.AppendLine("1. 凡是涉及设备数据、状态、报警、数值的问题，必须先调用工具拿到真实数据，再据此作答。严禁凭记忆或常识编造任何数值。");
            sb.AppendLine("2. 即使是极短的追问（例如「XX 是多少？」），也必须重新调用工具读取最新值。绝对不要沿用上一轮对话里的旧数值作答。");
            sb.AppendLine("3. 工具返回什么就说什么。查不到的数据要明确说没有找到，不要猜测，也不要编示例。");
            sb.AppendLine("4. 用户口语里说的「1号线」「2号设备」指的是下面名录里对应编号的设备。");
            sb.AppendLine("5. 没有把握时，先用 list_devices 确认设备清单，再取具体数据。");
            sb.AppendLine("6. 用户问「所有设备」「全部」「各台」时，工具的 device 参数必须留空，否则只会查到其中一台。");
            sb.AppendLine("7. 工具结果里标明设备离线时，必须如实告知用户该设备读不到实时数据，绝不能把残留数值当作真实读数。");
            sb.AppendLine("8. 回答用简洁的中文，突出关键数值；多个数据点用短列表。不要复述工具报错原文。");
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("请根据下面给出的实时数据快照回答问题，不要编造快照里没有的数值。");
            sb.AppendLine();
        }

        sb.AppendLine($"当前时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        IReadOnlyList<DeviceSnapshot> devices;
        try
        {
            devices = PlcTools.SnapshotAll();
        }
        catch
        {
            devices = Array.Empty<DeviceSnapshot>();
        }

        if (devices.Count == 0)
        {
            sb.AppendLine("当前尚未配置任何设备。请提示用户先到「设备管理」页面添加设备和数据点。");
        }
        else
        {
            sb.AppendLine("已配置设备（这里只有名称，实时数值请调用工具读取）：");
            foreach (var d in devices)
            {
                sb.Append($"#{d.Index} {d.Name}（{d.StateText}）");
                sb.Append(d.Tags.Count > 0
                    ? $"：{string.Join("、", d.Tags.Select(t => t.Name))}"
                    : "：暂无数据点");
                sb.AppendLine();
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string DescribeArgs(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return "";
        var parts = args.EnumerateObject()
            .Select(p => $"{p.Name}={p.Value.ToString()?.Replace("\n", " ")}");
        var text = string.Join(", ", parts);
        return text.Length > 120 ? text[..120] + "…" : text;
    }

    private static string Indent(string result)
    {
        var lines = result.Split('\n');
        var shown = lines.Take(12).Select(l => "    " + l.TrimEnd());
        return string.Join("\n", shown) + (lines.Length > 12 ? $"\n    … 共 {lines.Length} 行" : "");
    }

    private void TrimHistory()
    {
        if (_history.Count <= MaxHistory) return;
        _history.RemoveRange(0, _history.Count - MaxHistory);
    }
}
