using System.Data;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using WpfApp20.Services;

namespace WpfApp20.Views;

public partial class DataLogView : UserControl
{
    private DeviceManager Mgr => App.Manager;

    public DataLogView()
    {
        InitializeComponent();
        DataContext = Mgr;
        RefreshFiles();
    }

    private void RefreshFiles()
    {
        FileList.Items.Clear();
        foreach (var f in Mgr.Logger.ListDataFiles())
            FileList.Items.Add(Path.GetFileName(f));
        TbStatus.Text = $"共 {FileList.Items.Count} 个数据文件 · 目录：{Mgr.Logger.DataDir}";
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => RefreshFiles();

    private void OnFileSelected(object sender, SelectionChangedEventArgs e)
    {
        PreviewGrid.Columns.Clear();
        PreviewGrid.ItemsSource = null;
        if (FileList.SelectedItem is not string name) return;

        var path = Path.Combine(Mgr.Logger.DataDir, name);
        var table = new DataTable(name);
        try
        {
            int rows = 0;
            foreach (var line in File.ReadLines(path).Take(501))
            {
                var cells = line.Split(',');
                if (rows == 0)
                {
                    for (int i = 0; i < cells.Length; i++)
                        table.Columns.Add(i < cells.Length ? cells[i].Trim('\uFEFF') : $"列{i}");
                }
                else
                {
                    // 跳过列数不匹配的行
                    if (cells.Length != table.Columns.Count) continue;
                    table.Rows.Add(cells.Cast<object>().ToArray());
                }
                rows++;
            }
            PreviewGrid.ItemsSource = table.DefaultView;
            TbPreviewTitle.Text = $"{name} · 共加载 {table.Rows.Count} 行（预览）";
        }
        catch (Exception ex)
        {
            MessageBox.Show("读取文件失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = Mgr.Logger.DataDir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("打开目录失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
