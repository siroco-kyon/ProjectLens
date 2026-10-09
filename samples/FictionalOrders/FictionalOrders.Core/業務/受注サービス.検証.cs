namespace FictionalOrders.Core.業務;

public sealed partial class 受注サービス
{
    public 検証結果 検証(string customerName, decimal amount) => new()
    {
        正常 = !string.IsNullOrWhiteSpace(customerName) && amount > 0,
        メッセージ = amount > 0 ? "" : "金額は正数を指定してください。"
    };
}
