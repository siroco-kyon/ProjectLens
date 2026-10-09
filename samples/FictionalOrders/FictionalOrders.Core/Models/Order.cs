namespace FictionalOrders.Core.Models;

public sealed class Order : EntityBase
{
    public string CustomerName { get; init; } = "";
    public decimal Amount { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.Today;
}
