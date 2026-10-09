using System.Windows;
using DynamicBay.Core;
using DynamicBay.Island;

namespace DynamicBay.Settings;

public partial class SettingsWindow : Window
{
    public SettingsWindow(AppSettings settings, IslandViewModel vm)
    {
        InitializeComponent();
    }
}
