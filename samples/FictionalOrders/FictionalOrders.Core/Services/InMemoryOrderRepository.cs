using FictionalOrders.Core.Models;

namespace FictionalOrders.Core.Services;

// 実在の顧客・業務データを使わないテスト専用リポジトリー。
public sealed class InMemoryOrderRepository : IOrderRepository
{
    private readonly List<Order> orders = [];

    public InMemoryOrderRepository()
    {
        orders.Add(new Order { CustomerName = "架空商事", Amount = 1200m });
    }

    public IReadOnlyList<Order> FindAll() => orders.AsReadOnly();

    public void Save(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);
        orders.Add(order);
    }
}
