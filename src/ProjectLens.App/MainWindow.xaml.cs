using System.Windows;
using ProjectLens.App.Services;
using ProjectLens.App.ViewModels;
using ProjectLens.Core.Analysis;

namespace ProjectLens.App;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    public MainWindow()
    {
        InitializeComponent();
        ViewModel = new MainViewModel(new ProjectAnalyzer(), new WpfUserInteraction());
        DataContext = ViewModel;
        Closed += (_, _) => ViewModel.CancelActiveOperation();
    }

    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not TreeNode { File: not null } node) return;
        ViewModel.SelectedFile = node.File;
        ResultTabs.SelectedIndex = 2;
    }
}
