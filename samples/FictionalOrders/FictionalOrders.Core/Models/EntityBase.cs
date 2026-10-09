namespace FictionalOrders.Core.Models;

// 共通の識別子。解析対象の継承関係を確認するための架空モデル。
public abstract class EntityBase
{
    public Guid Id { get; init; } = Guid.NewGuid();
}
