using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using TilesetEditor.Core.GameData;

namespace TilesetEditor.App.Modules.GameData;

/// <summary>The grid's columns depend on the selected table, so they are built here.</summary>
public partial class GameDataView : UserControl
{
    private readonly Dictionary<DataGridColumn, int> _columnIndex = [];
    private GameDataViewModel? _vm;

    public GameDataView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm != null) _vm.ColumnsChanged -= OnColumnsChanged;
            _vm = DataContext as GameDataViewModel;
            if (_vm != null) _vm.ColumnsChanged += OnColumnsChanged;
            BuildColumns();
        };
        Grid.BeginningEdit += (_, e) =>
        {
            if (_columnIndex.TryGetValue(e.Column, out var i) && e.Row.Item is GameDataRowVm row && !row.Cells[i].IsEditable) e.Cancel = true;
        };
        Grid.PreparingCellForEdit += (_, e) =>
        {
            var editor = FindChild<Control>(e.EditingElement);
            if (editor == null) return;
            editor.Focus();
            if (editor is TextBox tb) tb.SelectAll();
            if (editor is ComboBox cb) cb.IsDropDownOpen = true;
        };
    }

    private void OnColumnsChanged(object? sender, EventArgs e) => BuildColumns();

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T t) return t;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindChild<T>(VisualTreeHelper.GetChild(parent, i)) is { } found) return found;
        return null;
    }

    private const string Ns = "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'";

    private static DataTemplate MakeTemplate(string xaml) => (DataTemplate)XamlReader.Parse($"<DataTemplate {Ns}>{xaml}</DataTemplate>");

    private void BuildColumns()
    {
        Grid.Columns.Clear();
        _columnIndex.Clear();
        var table = _vm?.SelectedTable?.Table;
        var mono = new FontFamily("Consolas");
        Grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Constant", Binding = new Binding(nameof(GameDataRowVm.Key)) { Mode = BindingMode.OneWay }, IsReadOnly = true,
            Width = new DataGridLength(table?.Def.Id == "species" ? 230 : 250), FontFamily = mono,
        });
        if (table == null) return;
        for (int i = 0; i < table.Def.Columns.Count; i++)
        {
            var c = table.Def.Columns[i];
            string cell = $"Cells[{i}]";
            string align = c.Kind == GameDataKind.Number ? "Right" : "Left";
            var display = MakeTemplate($@"
<TextBlock Text='{{Binding {cell}.Value, Mode=OneWay}}' ToolTip='{{Binding {cell}.ToolTip, Mode=OneWay}}' Padding='3,1' TextTrimming='CharacterEllipsis' TextAlignment='{align}'>
  <TextBlock.Style>
    <Style TargetType='TextBlock'>
      <Style.Triggers>
        <DataTrigger Binding='{{Binding {cell}.IsEditable}}' Value='False'><Setter Property='Foreground' Value='{{DynamicResource MutedBrush}}'/><Setter Property='FontStyle' Value='Italic'/></DataTrigger>
        <DataTrigger Binding='{{Binding {cell}.IsChanged}}' Value='True'><Setter Property='FontWeight' Value='SemiBold'/></DataTrigger>
        <DataTrigger Binding='{{Binding {cell}.HasWarning}}' Value='True'><Setter Property='Background' Value='{{DynamicResource WarnCellBrush}}'/></DataTrigger>
        <DataTrigger Binding='{{Binding {cell}.HasError}}' Value='True'><Setter Property='Background' Value='{{DynamicResource ErrorCellBrush}}'/></DataTrigger>
      </Style.Triggers>
    </Style>
  </TextBlock.Style>
</TextBlock>");
            bool combo = c.Kind == GameDataKind.Constant && table.Options[i] is { Count: > 0 };
            var editing = combo
                ? MakeTemplate($"<ComboBox ItemsSource='{{Binding {cell}.Options, Mode=OneWay}}' SelectedItem='{{Binding {cell}.Value, UpdateSourceTrigger=PropertyChanged}}' IsTextSearchEnabled='True'/>")
                : MakeTemplate($"<TextBox Text='{{Binding {cell}.Value, UpdateSourceTrigger=PropertyChanged}}' ToolTip='{{Binding {cell}.ToolTip, Mode=OneWay}}' TextAlignment='{align}'" +
                           (c.Kind == GameDataKind.Text ? "" : " FontFamily='Consolas'") + "/>");
            var col = new DataGridTemplateColumn
            {
                Header = c.Header,
                CellTemplate = display,
                CellEditingTemplate = editing,
                Width = new DataGridLength(c.Width),
                SortMemberPath = $"{cell}.SortValue",
                CanUserSort = true,
            };
            if (c.Hint != null) col.HeaderStyle = HeaderWithToolTip(c.Hint);
            _columnIndex[col] = i;
            Grid.Columns.Add(col);
        }
        Grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Source", Binding = new Binding(nameof(GameDataRowVm.Source)) { Mode = BindingMode.OneWay }, IsReadOnly = true,
            Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 120,
        });
    }

    private static Style HeaderWithToolTip(string tip)
    {
        var type = typeof(System.Windows.Controls.Primitives.DataGridColumnHeader);
        var s = new Style(type, Application.Current?.TryFindResource(type) as Style);
        s.Setters.Add(new Setter(ToolTipProperty, tip));
        return s;
    }
}
