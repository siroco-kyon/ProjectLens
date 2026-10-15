using System.Net;
using System.Net.Http.Json;
using FictionalOrders.Core.Models;

namespace FictionalOrders.Core.Services;

// 解析用の架空クライアント。サンプル画面からは生成・呼び出しません。
public sealed class OrdersApiClient
{
    private readonly HttpClient client;

    public OrdersApiClient(HttpClient client)
    {
        this.client = client;
        client.BaseAddress = new Uri("https://orders.example.invalid/api/");
    }

    public async Task<IReadOnlyList<Order>> FindAsync(string customer, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customer);
        var path = "orders?customer=" + Uri.EscapeDataString(customer);
        using var response = await client.GetAsync(path, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return [];
        response.EnsureSuccessStatusCode();
        var orders = await response.Content.ReadFromJsonAsync<List<Order>>(cancellationToken);
        return orders?.Where(order => order.Amount >= 0).ToArray() ?? [];
    }

    public async Task<Order?> SaveAsync(Order order, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync("orders", order, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Order>(cancellationToken);
    }
}
