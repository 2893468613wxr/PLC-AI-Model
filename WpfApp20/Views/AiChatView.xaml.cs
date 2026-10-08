using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using WpfApp20.Core;
using WpfApp20.Services;

namespace WpfApp20.Views;

public enum ChatRole { User, Assistant, Tool }

/// <summary>对话区的一条消息。</summary>
public class ChatItem : ObservableObject
{
    private string _text;

    public ChatItem(ChatRole role, string text)
    {
        Role = role;
        _text = text;
    }

    public ChatRole Role { get; }
    public DateTime Time { get; } = DateTime.Now;

    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value);
    }

    public bool IsUser => Role == ChatRole.User;
    public bool IsTool => Role == ChatRole.Tool;
    public string Header => IsUser ? $"我 · {Time:HH:mm:ss}" : $"助手 · {Time:HH:mm:ss}";
}

/// <summary>AI 智能问答页：用自然语言查阅上位机的实时数据。</summary>
public partial class AiChatView : UserControl
{
    private readonly ObservableCollection<ChatItem> _items = [];
    private readonly AiAssistant _assistant = new();
    private readonly StringBuilder _pending = new();
    private readonly DispatcherTimer _flushTimer;

    private CancellationTokenSource? _cts;
    private ChatItem? _stream;
    private bool _busy;
    private bool _ready;

    public AiChatView()
    {
        InitializeComponent();
        ChatList.ItemsSource = _items;
        PlcTools.AllowWrites = false;

        _flushTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(80),
        };
        _flushTimer.Tick += (_, _) => FlushPending();

        Loaded += OnLoaded;
    }

    private bool _initialized;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        await InitAsync();
    }

    // ───────────────────────────── 初始化 ─────────────────────────────

    private async Task InitAsync()
    {
        Status("正在检测本地模型服务…");

        if (!await LlmClient.IsAliveAsync(2500))
        {
            if (!LlmClient.HasLocalInstall)
            {
                Fail($"未找到本地 Ollama 程序（已查找：{LlmClient.RootDir}\\ollama\\ollama.exe）。" +
                     "请确认已将 Ollama 便携版放在该目录下。");
                return;
            }

            Status("未检测到模型服务，正在后台启动…");
            if (!await LlmClient.EnsureServiceAsync(TimeSpan.FromSeconds(45)))
            {
                Fail("本地模型服务启动失败。请检查 ollama.exe 是否可正常执行。");
                return;
            }
        }

        await LoadModelsAsync();

        if (CbModel.Items.Count == 0)
        {
            Fail("未找到可用模型。请先在 Ollama-Harness 目录执行 python scripts\\pull_model.py qwen2.5:3b 拉取模型。");
            return;
        }

        _ready = true;
        BuildQuickQuestions();
        Status("就绪");

        var devices = App.Manager.Devices
            .Select((d, i) => $"{i + 1}. {d.Name}（{d.StateText}）")
            .ToList();

        Add(ChatRole.Assistant,
            "我已经接好了这台上位机的实时数据，可以直接用中文问我，例如：\n" +
            "· 「1号线现在的情况怎么样？」\n" +
            "· 「反应釜温度是多少？」\n" +
            "· 「当前有哪些报警？」\n" +
            "· 「2号线通讯正常吗？」\n" +
            "需要我下发控制指令（比如修改设定值）时，请先勾选上方的「允许 AI 写入 PLC」。\n" +
            (devices.Count > 0 ? "\n当前设备：\n" + string.Join("\n", devices) : "\n当前还没有配置设备。"));

        TbInput.Focus();
    }

    private async Task LoadModelsAsync()
    {
        var models = await LlmClient.GetModelsAsync();
        if (models.Count == 0) return;

        CbModel.ItemsSource = models;
        _assistant.Model = models.Contains(LlmClient.DefaultModel)
            ? LlmClient.DefaultModel
            : models[0];
        CbModel.SelectedItem = _assistant.Model;
    }

    private void BuildQuickQuestions()
    {
        QuickPanel.Children.Clear();

        var suggestions = new List<string> { "当前有哪些报警？", "所有设备通讯正常吗？" };

        foreach (var (d, i) in App.Manager.Devices.Select((d, i) => (d, i)))
            if (i < 2)
                suggestions.Add($"{i + 1}号设备现在的情况怎么样？");

        var first = App.Manager.Devices.SelectMany(d => d.Tags).FirstOrDefault();
        if (first is not null)
            suggestions.Add($"{first.Name}现在是多少？");

        foreach (var s in suggestions)
        {
            var btn = new Button
            {
                Content = s,
                Margin = new Thickness(0, 0, 8, 6),
                Padding = new Thickness(11, 5, 11, 5),
                Cursor = Cursors.Hand,
            };
            btn.Click += (_, _) => { TbInput.Text = s; _ = SendAsync(); };
            QuickPanel.Children.Add(btn);
        }
    }

    // ───────────────────────────── 对话 ─────────────────────────────

    private async Task SendAsync()
    {
        if (_busy) return;

        var text = TbInput.Text.Trim();
        if (text.Length == 0) return;

        if (!_ready)
        {
            Status("本地模型尚未就绪，无法提问。");
            return;
        }

        Add(ChatRole.User, text);
        TbInput.Clear();

        _cts = new CancellationTokenSource();
        _pending.Clear();
        _stream = null;
        SetBusy(true);

        var watch = Stopwatch.StartNew();

        var callbacks = new AssistantCallbacks
        {
            Status = s => { Ui(() => Status(s)); return Task.CompletedTask; },
            ToolCall = t => { Ui(() => _items.Add(new ChatItem(ChatRole.Tool, t))); return Task.CompletedTask; },
            Token = t => { lock (_pending) _pending.Append(t); return Task.CompletedTask; },
        };

        try
        {
            var answer = await _assistant.AskAsync(text, callbacks, _cts.Token);
            FinishAnswer(answer);
            Status($"完成 · 用时 {watch.Elapsed.TotalSeconds:0.0} 秒");
        }
        catch (OperationCanceledException)
        {
            FinishAnswer("（已停止生成）");
            Status("已停止");
        }
        catch (Exception ex)
        {
            FinishAnswer(
                $"请求失败：{ex.Message}\n\n" +
                "常见处理办法：\n" +
                "· 提示 500 Internal Server Error —— 点上方「重启本地模型服务」后重试\n" +
                "· 内存紧张时可在上方换成更小的模型（如 qwen2.5:1.5b）");
            Status("请求失败");
        }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>把模型返回的最终答案落到气泡上（流式内容到此为止）。</summary>
    private void FinishAnswer(string answer)
    {
        _flushTimer.Stop();
        FlushPending();

        if (answer.Length == 0) return;

        Ui(() =>
        {
            if (_stream is null)
            {
                _stream = new ChatItem(ChatRole.Assistant, answer);
                _items.Add(_stream);
            }
            else
            {
                _stream.Text = answer;
            }
            _stream = null;
            ScrollToEnd();
        });
    }

    /// <summary>定期把累积的流式文本刷到界面上，避免逐 token 跨线程更新。</summary>
    private void FlushPending()
    {
        string text;
        lock (_pending) text = _pending.ToString();
        if (text.Length == 0) return;

        Ui(() =>
        {
            if (_stream is null)
            {
                _stream = new ChatItem(ChatRole.Assistant, text);
                _items.Add(_stream);
            }
            else
            {
                _stream.Text = text;
            }
            ScrollToEnd();
        });
    }

    private void Add(ChatRole role, string text)
    {
        Ui(() =>
        {
            _items.Add(new ChatItem(role, text));
            ScrollToEnd();
        });
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        Ui(() =>
        {
            BtnSend.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
            BtnStop.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            TbInput.IsEnabled = _ready && !busy;
            if (busy) _flushTimer.Start(); else _flushTimer.Stop();
        });
    }

    private void Fail(string message)
    {
        _ready = false;
        Add(ChatRole.Assistant, $"无法启动：{message}");
        Status(message);
        TbInput.IsEnabled = false;
        BtnSend.IsEnabled = false;
    }

    private void Status(string text)
    {
        Ui(() =>
        {
            TbStatus.Text = text;
            TbStatus.Foreground = text.Contains("失败") || text.Contains("无法")
                ? System.Windows.Media.Brushes.IndianRed
                : (System.Windows.Media.Brush)FindResource("Brush.SubText");
        });
    }

    private void Ui(Action action)
    {
        if (Dispatcher.CheckAccess()) action();
        else Dispatcher.InvokeAsync(action);
    }

    private void ScrollToEnd() => Scroller.ScrollToEnd();

    // ─────────────────────────── 界面事件 ───────────────────────────

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        if (e.Key != Key.Enter) return;

        e.Handled = true;
        _ = SendAsync();
    }

    private async void OnSend(object sender, RoutedEventArgs e) => await SendAsync();

    private void OnStop(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void OnClear(object sender, RoutedEventArgs e)
    {
        _assistant.ClearHistory();
        _items.Clear();
        Add(ChatRole.Assistant, "对话已清空，请继续提问。");
    }

    private void OnModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CbModel.SelectedItem is string name && name.Length > 0)
            _assistant.Model = name;
    }

    private void OnWriteToggled(object sender, RoutedEventArgs e)
    {
        PlcTools.AllowWrites = ChkWrite.IsChecked == true;
        Status(PlcTools.AllowWrites ? "已开启 AI 写入权限" : "已关闭 AI 写入权限");
    }

    private async void OnRestartService(object sender, RoutedEventArgs e)
    {
        Status("正在重启本地模型服务…");
        foreach (var p in Process.GetProcessesByName("ollama"))
        {
            try { p.Kill(true); } catch { /* 已退出或无权限 */ }
        }
        await Task.Delay(1500);

        if (await LlmClient.EnsureServiceAsync(TimeSpan.FromSeconds(45)))
        {
            Status("本地模型服务已重启");
            Add(ChatRole.Assistant, "本地模型服务已重启，请重新提问。");
        }
        else
        {
            Fail("本地模型服务重启失败。");
        }
    }
}
