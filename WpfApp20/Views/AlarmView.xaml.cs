using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using WpfApp20.Services;

namespace WpfApp20.Views;

public partial class AlarmView : UserControl
{
    private DeviceManager Mgr => App.Manager;

    public AlarmView()
    {
        InitializeComponent();
        DataContext = Mgr;
    }

    private void OnAckSelected(object sender, RoutedEventArgs e)
    {
        if (ActiveGrid.SelectedItem is AlarmRecord rec)
            Mgr.Alarms.Acknowledge(rec, Mgr.Logger, DateTime.Now);
    }

    private void OnAckAll(object sender, RoutedEventArgs e) => Mgr.Alarms.AcknowledgeAll(Mgr.Logger);

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Mgr.Logger.AlarmDir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show("打开目录失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
