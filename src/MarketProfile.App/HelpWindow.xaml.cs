using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using MarketProfile.App.Help;

namespace MarketProfile.App;

/// <summary>
/// Pomoc: parametry profili po angielsku (wg J. Daltona) i po polsku, z opisem, jak liczy je aplikacja.
/// Jedno okno na całą aplikację – kolejne wywołanie przywołuje istniejące.
/// </summary>
public partial class HelpWindow : Window
{
    private static HelpWindow? _instance;
    private readonly ICollectionView _view;

    private HelpWindow()
    {
        InitializeComponent();
        _view = CollectionViewSource.GetDefaultView(HelpTopics.All);
        _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(HelpTopic.Group)));
        _view.Filter = item => item is HelpTopic topic && topic.Matches(SearchBox.Text);
        TopicList.ItemsSource = _view;
        TopicList.SelectedIndex = 0;
    }

    /// <summary>Otwiera (albo przywołuje) okno pomocy.</summary>
    public static void ShowHelp(Window? owner)
    {
        if (_instance is { IsLoaded: true })
        {
            _instance.Activate();
            return;
        }

        _instance = new HelpWindow { Owner = owner };
        _instance.Closed += (_, _) => _instance = null;
        _instance.Show();
        _instance.SearchBox.Focus();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _view.Refresh();
        if (TopicList.SelectedItem is null && !TopicList.Items.IsEmpty)
        {
            TopicList.SelectedIndex = 0;
        }
    }

    private void OnTopicSelected(object sender, SelectionChangedEventArgs e)
    {
        Details.DataContext = TopicList.SelectedItem as HelpTopic;
        Details.Visibility = TopicList.SelectedItem is null ? Visibility.Hidden : Visibility.Visible;
    }
}
