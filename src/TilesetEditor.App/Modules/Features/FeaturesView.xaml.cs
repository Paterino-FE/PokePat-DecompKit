using System.Windows.Controls;
using TilesetEditor.App.Infrastructure;

namespace TilesetEditor.App.Modules.Features;

public partial class FeaturesView : UserControl
{
    public FeaturesView()
    {
        InitializeComponent();
        DataGridLayout.KeepDeclaredColumnWidths(OptionsGrid);
    }
}
