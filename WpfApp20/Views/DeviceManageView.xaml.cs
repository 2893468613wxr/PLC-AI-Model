using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using WpfApp20.Models;
using WpfApp20.Services;
using WpfApp20.Views;

namespace WpfApp20.Views;

public partial class DeviceManageView : UserControl
{
    private DeviceManager Mgr => App.Manager;

    private DeviceRuntime? Selected => Grid.SelectedItem as DeviceRuntime;

    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public DeviceManageView()
    {
        InitializeComponent();
        DataContext = Mgr;
        // 每秒刷新"连接信息"列（已连接时长 / 心跳 / 重连倒计时）
        _refreshTimer.Tick += (_, _) =>
        {
            foreach (var d in Mgr.Devices) d.ConnectionInfoRefresher?.Invoke();
        };
        _refreshTimer.Start();
    }

    private void OnAddDevice(object sender, RoutedEventArgs e)
    {
        var cfg = new DeviceConfig();
        var dlg = new DeviceEditWindow(cfg) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true)
            Mgr.AddDevice(cfg);
    }

    private void OnEditDevice(object sender, RoutedEventArgs e) => EditSelected();

    private void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DataGridCell) return;
        EditSelected();
    }

    private void EditSelected()
    {
        if (Selected is null) return;
        var dlg = new DeviceEditWindow(Selected.Config, isNew: false) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true)
            Mgr.ApplyEdit(Selected);
    }

    private void OnDeleteDevice(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } rt) return;
        if (MessageBox.Show($"确定删除设备“{rt.Name}”？\r\n其标签配置与报警规则将一并删除。",
                "删除设备", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        Mgr.RemoveDevice(rt);
    }

    private void OnStart(object sender, RoutedEventArgs e)
    {
        if (Selected is { } rt) Mgr.Start(rt);
        else Mgr.StartAll();
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        if (Selected is { } rt) Mgr.Stop(rt);
        else Mgr.StopAll();
    }
}
