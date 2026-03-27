using System.Collections.ObjectModel;
using System.Windows;

namespace Voip.Client.Wpf;

public partial class LogWindow : Window
{
    public LogWindow(ObservableCollection<string> logEntries)
    {
        InitializeComponent();
        LogsList.ItemsSource = logEntries;
    }
}
