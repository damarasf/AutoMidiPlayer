using System.Windows.Controls;
using System.Windows.Input;
using AutoMidiPlayer.WPF.ViewModels;

namespace AutoMidiPlayer.WPF.Views;

public partial class BrowseView : UserControl
{
    public BrowseView()
    {
        InitializeComponent();
    }

    private void OnSearchBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is BrowseViewModel viewModel)
            viewModel.Search();
    }
}
