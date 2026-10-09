using FictionalOrders.Core.Models;

namespace FictionalOrders.Core.Services;

public interface IOrderRepository
{
    IReadOnlyList<Order> FindAll();
    void Save(Order order);
}
