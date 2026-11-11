using Avalonia.Controls;
using MFAAvalonia.Helper;

namespace MFAAvalonia.Views.Pages;

public partial class ScheduleView : UserControl
{
    public ScheduleView()
    {
        DataContext = Instances.TimerSettingsUserControlModel;
        InitializeComponent();
    }
}
