using System.Collections.ObjectModel;
using FictionalOrders.Core.Models;
using FictionalOrders.Core.Services;
using FictionalOrders.Core.業務;

namespace FictionalOrders.Desktop.ViewModels;

public sealed class OrdersViewModel : ViewModelBase
{
    private readonly 受注サービス service = new(new InMemoryOrderRepository());
    public ObservableCollection<Order> Orders { get; }

    public OrdersViewModel() => Orders = new ObservableCollection<Order>(service.一覧取得());

    public void AddExample()
    {
        Orders.Add(service.登録("架空製作所", 500m));
        Notify(nameof(Orders));
    }
}
