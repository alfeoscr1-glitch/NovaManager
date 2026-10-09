using System.Collections.ObjectModel;
using System.Windows;

namespace NovaManager;

public partial class ChangeLogWindow : Window
{
    private readonly ObservableCollection<ReleaseHistoryItem> releases = new();

    public ChangeLogWindow()
    {
        InitializeComponent();
        DataContext = releases;
    }

    internal void SetBundledReleases(IEnumerable<ReleaseHistoryItem> items)
    {
        releases.Clear();
        foreach (var item in items)
        {
            releases.Add(item);
        }

        ChangeLogScrollViewer.ScrollToTop();
    }

    internal void SetStatus(string message) => ChangeLogStatusText.Text = message;

    internal void SetFooter(string message) => ChangeLogFooterText.Text = message;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
