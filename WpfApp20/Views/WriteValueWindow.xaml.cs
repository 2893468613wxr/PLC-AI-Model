using System.Windows;
using System.Windows.Input;
using WpfApp20.Models;
using WpfApp20.Services;

namespace WpfApp20.Views;

public partial class WriteValueWindow : Window
{
    private readonly TagValueItem _tag;

    public WriteValueWindow(TagValueItem tag)
    {
        InitializeComponent();
        _tag = tag;
        TbTagInfo.Text = $"{tag.DeviceName} · {tag.Name}（{tag.AddressText} {tag.AreaText}）";
        TbCurrent.Text = $"当前值：{tag.DisplayValue}   类型：{tag.DataTypeText}";
        if (tag.Tag.DataType == TagDataType.位)
        {
            PnlBool.Visibility = Visibility.Visible;
            TbValue.Visibility = Visibility.Collapsed;
            TbLabel.Text = "写入值（位）";
            RbTrue.IsChecked = tag.Value != 0;
            RbFalse.IsChecked = tag.Value == 0;
        }
        else
        {
            TbValue.Text = tag.Value.ToString("0.###");
        }
    }

    private async void OnWrite(object sender, RoutedEventArgs e)
    {
        double value;
        if (_tag.Tag.DataType == TagDataType.位)
        {
            value = RbTrue.IsChecked == true ? 1 : 0;
        }
        else if (!double.TryParse(TbValue.Text, out value))
        {
            TbResult.Text = "✗ 请输入有效的数值。";
            TbResult.Foreground = FindResource("Brush.Danger") as System.Windows.Media.Brush;
            return;
        }

        try
        {
            TbResult.Text = "写入中…";
            TbResult.Foreground = FindResource("Brush.SubText") as System.Windows.Media.Brush;
            await App.Manager.WriteTagAsync(_tag, value);
            TbResult.Text = $"✓ 写入成功，回读值：{_tag.DisplayValue}";
            TbResult.Foreground = FindResource("Brush.Ok") as System.Windows.Media.Brush;
        }
        catch (Exception ex)
        {
            TbResult.Text = "✗ 写入失败：" + ex.Message;
            TbResult.Foreground = FindResource("Brush.Danger") as System.Windows.Media.Brush;
        }
    }

    private void OnValueKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnWrite(sender, e);
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
