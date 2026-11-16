using Avalonia.Controls;
using MFAAvalonia.Helper;

namespace MFAAvalonia.Views.Pages;

public partial class StatsView : UserControl
{
    public StatsView()
    {
        DataContext = Instances.StatsViewModel;
        InitializeComponent();
    }
}
