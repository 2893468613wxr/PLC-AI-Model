using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WpfApp20.Models;

namespace WpfApp20.Views;

public partial class TagEditWindow : Window
{
    public TagEditWindow(TagDefinition tag)
    {
        InitializeComponent();
        DataContext = tag;

        // 单元格一拿到焦点就进入编辑。
        // DataGrid 默认是「第一次点击选中行 → 第二次点击才编辑」，下拉列要点三次，
        // 用户很容易误判成「改不动」。这里改成聚焦即编辑。
        RuleGrid.AddHandler(DataGridCell.GotFocusEvent, new RoutedEventHandler(OnRuleCellGotFocus));
    }

    private void OnRuleCellGotFocus(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not DataGridCell cell) return;
        if (cell.IsEditing || !cell.IsEnabled || cell.Column.IsReadOnly) return;
        if (cell.DataContext is not AlarmRule) return;

        RuleGrid.CurrentCell = new DataGridCellInfo(cell.DataContext, cell.Column);
        if (!ReferenceEquals(RuleGrid.SelectedItem, cell.DataContext))
            RuleGrid.SelectedItem = cell.DataContext;

        // CurrentCell 切换后容器要等一次布局才就绪，立刻 BeginEdit 会被拒
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            if (cell.IsEditing || !cell.IsEnabled) return;
            RuleGrid.BeginEdit();

            // 下拉列：编辑元素出现后顺手展开，省掉第三次点击
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            {
                if (FindChild<ComboBox>(cell) is { } cb)
                {
                    cb.Focus();
                    cb.IsDropDownOpen = true;
                }
            });
        });
    }

    private void OnAddRule(object sender, RoutedEventArgs e)
    {
        if (DataContext is TagDefinition tag)
            tag.AlarmRules.Add(new AlarmRule { Operator = AlarmOperator.大于, Threshold = 100, Level = AlarmLevel.警告 });
    }

    private void OnRemoveRule(object sender, RoutedEventArgs e)
    {
        if (DataContext is TagDefinition tag && RuleGrid.SelectedItem is AlarmRule rule)
            tag.AlarmRules.Remove(rule);
    }

    private void OnClose(object sender, RoutedEventArgs e) => DialogResult = true;

    private static T? FindChild<T>(DependencyObject? parent) where T : DependencyObject
    {
        if (parent is null) return null;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            var found = FindChild<T>(child);
            if (found is not null) return found;
        }
        return null;
    }
}
