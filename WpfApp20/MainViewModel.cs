using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using WpfApp20.Core;
using WpfApp20.Services;
using WpfApp20.Views;

namespace WpfApp20;

public class NavItem
{
    public required string Title { get; init; }
    public required string Icon { get; init; }
    public required UserControl Page { get; init; }
}

public class MainViewModel : ObservableObject
{
    private UserControl? _currentPage;
    private NavItem? _currentNav;
    private int _onlineCount;
    private int _activeAlarms;
    private string _clock = "";
    private bool _anyPolling;

    public DeviceManager Mgr { get; }
    public ObservableCollection<NavItem> NavItems { get; } = [];

    public NavItem? CurrentNav
    {
        get => _currentNav;
        set
        {
            if (SetProperty(ref _currentNav, value) && value != null)
                CurrentPage = value.Page;
        }
    }

    public UserControl? CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    public int OnlineCount { get => _onlineCount; private set => SetProperty(ref _onlineCount, value); }
    public int ActiveAlarms { get => _activeAlarms; private set => SetProperty(ref _activeAlarms, value); }
    public bool AnyPolling { get => _anyPolling; private set => SetProperty(ref _anyPolling, value); }
    public string Clock { get => _clock; private set => SetProperty(ref _clock, value); }

    public ICommand StartAllCommand { get; }
    public ICommand StopAllCommand { get; }

    public MainViewModel()
    {
        Mgr = App.Manager;
        NavItems.Add(new NavItem { Title = "设备管理", Icon = "\u2699", Page = new DeviceManageView() });
        NavItems.Add(new NavItem { Title = "实时监控", Icon = "\u25C9", Page = new MonitorView() });
        NavItems.Add(new NavItem { Title = "报警中心", Icon = "\u26A0", Page = new AlarmView() });
        NavItems.Add(new NavItem { Title = "数据记录", Icon = "\u2261", Page = new DataLogView() });
        NavItems.Add(new NavItem { Title = "AI 问答", Icon = "\u2726", Page = new AiChatView() });
        CurrentNav = NavItems[0];

        StartAllCommand = new RelayCommand(() => Mgr.StartAll(), () => !Mgr.IsAnyPolling);
        StopAllCommand = new RelayCommand(() => Mgr.StopAll(), () => Mgr.IsAnyPolling);

        Mgr.Alarms.ActiveCountChanged += n =>
            Application.Current?.Dispatcher.BeginInvoke(() => ActiveAlarms = n);
        Mgr.DevicesChanged += UpdateStatus;
        UpdateStatus();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => { Clock = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"); UpdateStatus(); };
        timer.Start();

        if (Mgr.Config.AutoStartMonitoring && Mgr.Devices.Any(d => d.Config.Enabled))
            Mgr.StartAll();
    }

    private void UpdateStatus()
    {
        OnlineCount = Mgr.Devices.Count(d => d.State == DeviceState.已连接);
        AnyPolling = Mgr.IsAnyPolling;
    }
}
