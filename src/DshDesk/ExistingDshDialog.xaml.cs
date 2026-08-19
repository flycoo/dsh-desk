using System.Windows;
using DshDesk.Models;

namespace DshDesk;

public partial class ExistingDshDialog : Window
{
    public ExistingDshDialog()
    {
        InitializeComponent();
    }

    public ExistingDshChoice Choice { get; private set; } = ExistingDshChoice.Cancel;

    private void ConnectButton_OnClick(object sender, RoutedEventArgs e)
    {
        Choice = ExistingDshChoice.ConnectExisting;
        DialogResult = true;
    }

    private void LaunchButton_OnClick(object sender, RoutedEventArgs e)
    {
        Choice = ExistingDshChoice.LaunchSpecified;
        DialogResult = true;
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        Choice = ExistingDshChoice.Cancel;
        DialogResult = false;
    }
}
