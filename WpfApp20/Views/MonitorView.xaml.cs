using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using WpfApp20.Services;

namespace WpfApp20.Views;

public partial class MonitorView : UserControl
{
    private DeviceManager Mgr => App.Manager;
    private ICollectionView? _view;
    private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    public MonitorView()
    {
        InitializeComponent();
        _view = CollectionViewSource.GetDefaultView(Mgr.AllTags);
        Grid.ItemsSource = _view;
        RebuildFilter();
        Mgr.DevicesChanged += RebuildFilter;
        _statsTimer.Tick += (_, _) => UpdateStats();
        _statsTimer.Start();
        UpdateStats();
    }

    private void RebuildFilter()
    {
        Dispatcher.BeginInvoke(() =>
        {
            CbDeviceFilter.Items.Clear();
            CbDeviceFilter.Items.Add(new ComboItem("全部设备", null));
            foreach (var d in Mgr.Devices)
                CbDeviceFilter.Items.Add(new ComboItem(d.Name, d));
            CbDeviceFilter.SelectedIndex = 0;
            TbTags.Text = Mgr.AllTags.Count.ToString();
        });
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_view is null) return;
        if (CbDeviceFilter.SelectedItem is not ComboItem item) return;
        _view.Filter = item.Device is null
            ? null
            : (object o) => o is TagValueItem t && t.DeviceName == item.Device.Name;
    }

    private void UpdateStats()
    {
        TbOnline.Text = $"{Mgr.Devices.Count(d => d.State == DeviceState.已连接)} / {Mgr.Devices.Count}";
        TbAlarms.Text = Mgr.Alarms.ActiveAlarms.Count.ToString();
        TbTags.Text = Mgr.AllTags.Count.ToString();
        var avg = Mgr.Devices.Where(d => d.LastPollTime != null)
            .Select(d => d.LastRoundTripMs).DefaultIfEmpty(0).Average();
        TbLatency.Text = avg > 0 ? $"{avg:0} ms" : "-- ms";
    }

    private void OnReadNow(object sender, RoutedEventArgs e)
    {
        // 立即对所有轮询中的设备执行一轮读
        foreach (var d in Mgr.Devices.Where(d => d.IsPolling).ToList())
            _ = Mgr.PollOnceAsync(d);
    }

    private void OnWrite(object sender, RoutedEventArgs e) => OpenWriteDialog();

    private void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DataGridCell) return;
        OpenWriteDialog();
    }

    private void OpenWriteDialog()
    {
        if (Grid.SelectedItem is not TagValueItem tag)
        {
            MessageBox.Show("请先选择一个标签。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!tag.Writable)
        {
            MessageBox.Show("该标签所在区域只读（离散输入/输入寄存器），仅线圈与保持寄存器可写。",
                "不可写", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var dlg = new WriteValueWindow(tag) { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();
    }

    private record ComboItem(string Name, DeviceRuntime? Device)
    {
        public override string ToString() => Name;
    }
}
