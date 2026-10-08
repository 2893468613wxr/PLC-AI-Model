using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WpfApp20.Models;

namespace WpfApp20.Views;

public partial class DeviceEditWindow : Window
{
    private readonly bool _isNew;

    public DeviceEditWindow(DeviceConfig config, bool isNew = true)
    {
        InitializeComponent();
        _isNew = isNew;
        DataContext = config;
        TitleText.Text = isNew ? "新增设备" : $"编辑设备 - {config.Name}";
        TbHost.IsEnabled = !config.Simulated;
        CbSim.Checked += (_, _) => TbHost.IsEnabled = false;
        CbSim.Unchecked += (_, _) => TbHost.IsEnabled = true;
    }

    private TagDefinition? SelectedTag => TagGrid.SelectedItem as TagDefinition;

    private void OnAddTag(object sender, RoutedEventArgs e)
    {
        var tag = new TagDefinition { Name = "新标签" };
        var dlg = new TagEditWindow(tag) { Owner = this };
        if (dlg.ShowDialog() == true && DataContext is DeviceConfig cfg)
            cfg.Tags.Add(tag);
    }

    private void OnEditTag(object sender, RoutedEventArgs e)
    {
        if (SelectedTag is not { } tag) return;
        var dlg = new TagEditWindow(tag) { Owner = this };
        dlg.ShowDialog();
        TagGrid.Items.Refresh();
    }

    private void OnRemoveTag(object sender, RoutedEventArgs e)
    {
        if (SelectedTag is not { } tag) return;
        if (MessageBox.Show($"移除标签“{tag.Name}”？", "移除标签",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (DataContext is DeviceConfig cfg) cfg.Tags.Remove(tag);
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (DataContext is not DeviceConfig cfg) return;
        if (string.IsNullOrWhiteSpace(cfg.Name))
        {
            MessageBox.Show("请填写设备名称。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!int.TryParse(TbPort.Text, out int port) || port is < 1 or > 65535)
        {
            MessageBox.Show("端口必须是 1-65535 之间的整数。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        cfg.Port = port;
        cfg.PollIntervalMs = Math.Clamp(cfg.PollIntervalMs, 100, 600000);
        cfg.UnitId = cfg.UnitId == 0 && !_isNew ? cfg.UnitId : Math.Clamp(cfg.UnitId, (byte)1, (byte)247);

        // 通信管理参数校验（非法输入回退到安全默认值）
        if (!int.TryParse(TbMaxRetry.Text, out int maxRetry) || maxRetry < 0) maxRetry = 0;
        cfg.MaxReconnectAttempts = maxRetry;
        if (!int.TryParse(TbRetryDelay.Text, out int retryDelay)) retryDelay = 3000;
        cfg.ReconnectBaseDelayMs = Math.Clamp(retryDelay, 500, 60000);
        if (!int.TryParse(TbHbInterval.Text, out int hbInterval)) hbInterval = 5000;
        cfg.HeartbeatIntervalMs = Math.Clamp(hbInterval, 1000, 600000);
        if (!ushort.TryParse(TbHbAddress.Text, out ushort hbAddr)) hbAddr = 0;
        cfg.HeartbeatAddress = hbAddr;

        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
