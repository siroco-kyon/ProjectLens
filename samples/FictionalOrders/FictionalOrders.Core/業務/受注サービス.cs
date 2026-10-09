using FictionalOrders.Core.Models;
using FictionalOrders.Core.Services;

namespace FictionalOrders.Core.業務;

// 日本語のファイル名・名前空間・コメントの解析を確認する。
public sealed partial class 受注サービス
{
    private readonly IOrderRepository repository;

    public 受注サービス(IOrderRepository repository) => this.repository = repository;

    public IReadOnlyList<Order> 一覧取得() => repository.FindAll();

    public Order 登録(string customerName, decimal amount = 100m)
    {
        var order = new Order { CustomerName = customerName, Amount = amount };
        repository.Save(order);
        return order;
    }

    public sealed class 検証結果
    {
        public bool 正常 { get; init; }
        public string メッセージ { get; init; } = "";
    }
}
