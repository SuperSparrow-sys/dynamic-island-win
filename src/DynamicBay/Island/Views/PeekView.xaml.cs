using System.Windows;
using System.Windows.Controls;

namespace DynamicBay.Island.Views;

public partial class PeekView : UserControl
{
    public PeekView() => InitializeComponent();

    /// <summary>Raised by the question buttons: true = action ("Installieren"), false = dismiss ("Später").</summary>
    public event Action<bool>? Answered;

    private void Action_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Answered?.Invoke(true);
    }

    private void Dismiss_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Answered?.Invoke(false);
    }
}
