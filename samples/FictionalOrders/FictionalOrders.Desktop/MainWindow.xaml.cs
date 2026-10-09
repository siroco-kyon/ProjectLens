using System.Windows;
using FictionalOrders.Desktop.ViewModels;

namespace FictionalOrders.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new OrdersViewModel();
    }
}
