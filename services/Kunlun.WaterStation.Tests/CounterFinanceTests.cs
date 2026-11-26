using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kunlun.WaterStation.Operations;
using Npgsql;
using Xunit;

namespace Kunlun.WaterStation.Tests;

public sealed partial class MembershipTests
{
    [Fact]
    public async Task Correcting_Recharge_Moves_Whole_Batch_With_Gifts_Once_And_Preserves_Receipt()
    {
        using var admin = await Admin();
        using var clerk = await Admin("YG0012");
        var from = await Member(admin);
        var to = await Member(admin);
        var charge = new CreateRechargeOrderRequest(from, await Plan(admin), "CASH", Key());
        var original = await Ok(await admin.PostAsJsonAsync("/api/recharges", charge));
        var id = original.GetProperty("id").GetGuid();
        await GiftFromRetiredCoupon(id, 3);
        var path = $"/api/recharges/{id}/corrections";
        var move = new CorrectRecharge(from, to, "同名会员充错", Key());
        Assert.Equal(HttpStatusCode.Forbidden, (await clerk.PostAsJsonAsync(path, move)).StatusCode);
        var finance = await admin.GetFromJsonAsync<JsonElement>("/api/reports/finance-summary");
        var attempts = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => admin.PostAsJsonAsync(path, move)));
        Assert.All(attempts, r => Assert.Equal(HttpStatusCode.NoContent, r.StatusCode));
        Assert.Single((await admin.GetFromJsonAsync<JsonElement>(path)).EnumerateArray());
        Assert.Equal(0, (await admin.GetFromJsonAsync<JsonElement>($"/api/members/{from}/profile")).Tickets("BUCKET_18_9L", "PICKUP"));
        Assert.Equal(19, (await admin.GetFromJsonAsync<JsonElement>($"/api/members/{to}/profile")).Tickets("BUCKET_18_9L", "PICKUP"));
        Assert.Equal(finance.GetProperty("rechargeRevenueTotal").GetDecimal(), (await admin.GetFromJsonAsync<JsonElement>("/api/reports/finance-summary")).GetProperty("rechargeRevenueTotal").GetDecimal());
        Assert.Equal(id, (await Ok(await admin.PostAsJsonAsync("/api/recharges", charge))).GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync(path, move with { IdempotencyKey = Key() })).StatusCode);
        var receipt = (await admin.GetFromJsonAsync<JsonElement>("/api/ledgers/recharges?q=" + original.GetProperty("rechargeNo").GetString()))[0];
        Assert.Equal(to, receipt.GetProperty("memberId").GetGuid());
        Assert.True(receipt.GetProperty("corrected").GetBoolean());

        var reverse = new CorrectRecharge(to, from, "测试防止转走已用票", Key());
        var order = await Order(admin, to, 1);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync(path, reverse)).StatusCode);
        await Ok(await admin.PostAsJsonAsync($"/api/orders/{order.GetProperty("id")}/fulfill", new FulfillWaterOrderRequest(1, 0, 0)));
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync(path, reverse)).StatusCode);
        await Refund(admin, id);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync(path, reverse)).StatusCode);
    }

    [Fact]
    public async Task Pending_Gift_Blocks_Correction()
    {
        using var c = await Admin();
        var from = await Member(c);
        var to = await Member(c);
        var settings = await c.GetFromJsonAsync<JsonElement>("/api/settings/review");
        await Ok(await c.PutAsJsonAsync("/api/settings/review", new SaveReviewSettingsRequest(true, settings.GetProperty("refundReview").GetBoolean())));
        try
        {
            var charge = await Ok(await c.PostAsJsonAsync("/api/recharges", new CreateRechargeOrderRequest(from, await Plan(c), "CASH", Key(), GiftQty: 1)));
            Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync($"/api/recharges/{charge.GetProperty("id")}/corrections", new CorrectRecharge(from, to, "充错人", Key()))).StatusCode);
        }
        finally { await Ok(await c.PutAsJsonAsync("/api/settings/review", new SaveReviewSettingsRequest(settings.GetProperty("giftReview").GetBoolean(), settings.GetProperty("refundReview").GetBoolean()))); }
    }

    [Fact]
    public async Task Accessory_Standalone_Sale_And_Refund_Follow_Actual_Payment_Date_And_Historical_Price()
    {
        using var admin = await Admin();
        using var clerk = await Admin("YG0012");
        var product = new AccessoryProductInput("压水器-" + Key(), "个", 15, true);
        const string products = "/api/accessories/products", sales = "/api/accessories/sales";
        Assert.Equal(HttpStatusCode.Forbidden, (await clerk.PostAsJsonAsync(products, product)).StatusCode);
        var productId = (await Ok(await admin.PostAsJsonAsync(products, product))).GetProperty("id").GetGuid();
        var basket = new AccessoryBasket([new AccessoryItem(productId, 2, 15)], "WECHAT");
        var sale = new SellAccessories(Key(), null, basket);
        var receipts = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => clerk.PostAsJsonAsync(sales, sale)));
        var ids = new HashSet<Guid>();
        foreach (var receipt in receipts) ids.Add((await Ok(receipt)).GetProperty("id").GetGuid());
        var id = Assert.Single(ids);
        var refundPath = $"{sales}/{id}/refund";
        await Ok(await admin.PutAsJsonAsync($"{products}/{productId}", product with { Price = 24, Active = false }));
        Assert.Equal(HttpStatusCode.Conflict, (await clerk.PostAsJsonAsync(sales, sale with { IdempotencyKey = Key() })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await clerk.PostAsJsonAsync(refundPath, new AccessoryPayment("CASH", "退货"))).StatusCode);
        // Put the receipt in a past period, so the refund today must not erase that day's sales.
        await using (var db = host.OpenDatabase())
        await using (var cmd = new NpgsqlCommand("UPDATE plugin_kunlun_waterstation.accessory_sales SET created_at='2011-05-01T10:00:00Z', paid_at='2011-05-01T10:00:00Z' WHERE id=@id", db))
        { cmd.Parameters.AddWithValue("id", id); await cmd.ExecuteNonQueryAsync(); }
        const string period = "?from=2011-05-01T00:00:00Z&to=2011-05-02T00:00:00Z";
        var before = await admin.GetFromJsonAsync<JsonElement>("/api/expenses/summary");
        var refund = new AccessoryPayment("CASH", "退货");
        foreach (var response in await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => admin.PostAsJsonAsync(refundPath, refund)))) Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var past = await admin.GetFromJsonAsync<JsonElement>("/api/expenses/summary" + period);
        Assert.Equal(30, past.GetProperty("accessoryReceived").GetDecimal());
        Assert.Equal(0, past.GetProperty("accessoryRefunded").GetDecimal());
        var after = await admin.GetFromJsonAsync<JsonElement>("/api/expenses/summary");
        Assert.Equal(before.GetProperty("balance").GetDecimal() - 30, after.GetProperty("balance").GetDecimal());
        var row = (await admin.GetFromJsonAsync<JsonElement>(sales + period)).GetProperty("rows")[0];
        Assert.Equal("REFUNDED", row.GetProperty("status").GetString());
        Assert.Equal(15, row.GetProperty("items")[0].GetProperty("price").GetDecimal());
    }

    [Fact]
    public async Task Separate_Sale_Can_Join_An_Open_Delivery_Once_Without_Changing_Water_Order_Replay()
    {
        using var c = await Admin();
        var mid = await Member(c);
        await Recharge(c, mid, await Plan(c, mode: "DELIVERY"));
        var water = new CreateWaterOrderRequest(mid, "BUCKET_18_9L", "DELIVERY", 2, Key());
        var order = (await Ok(await c.PostAsJsonAsync("/api/orders", water))).GetProperty("id").GetGuid();
        var eligible = $"/api/accessories/deliveries?memberId={mid}";
        Assert.Equal(order, (await c.GetFromJsonAsync<JsonElement>(eligible))[0].GetProperty("id").GetGuid());
        var product = (await Ok(await c.PostAsJsonAsync("/api/accessories/products", new AccessoryProductInput("门店配件-" + Key(), "个", 15, true)))).GetProperty("id").GetGuid();
        var sale = new SellAccessories(Key(), mid, new AccessoryBasket([new(product, 1, 15)], "COD"), order);
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/api/accessories/sales", sale with { MemberId = await Member(c) })).StatusCode);
        var id = (await Ok(await c.PostAsJsonAsync("/api/accessories/sales", sale))).GetProperty("id").GetGuid();
        Assert.Equal(id, (await Ok(await c.PostAsJsonAsync("/api/accessories/sales", sale))).GetProperty("id").GetGuid());
        Assert.Empty((await c.GetFromJsonAsync<JsonElement>(eligible)).EnumerateArray());
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/api/accessories/sales", sale with { IdempotencyKey = Key() })).StatusCode);
        Assert.Equal(order, (await Ok(await c.PostAsJsonAsync("/api/orders", water))).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Water_Order_And_Accessories_Commit_Atomically_And_Cod_Waits_For_Collection()
    {
        using var c = await Admin();
        var mid = await Member(c);
        await Recharge(c, mid, await Plan(c, mode: "DELIVERY"));
        var product = (await Ok(await c.PostAsJsonAsync("/api/accessories/products", new AccessoryProductInput("随单-" + Key(), "个", 12, true)))).GetProperty("id").GetGuid();
        var basket = new AccessoryBasket([new AccessoryItem(product, 1, 12)], "COD");
        var order = new CreateWaterOrderRequest(mid, "BUCKET_18_9L", "DELIVERY", 2, Key(), Accessories: basket);
        var wrong = order with { Accessories = basket with { Items = [new AccessoryItem(product, 1, 10)] } };
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/api/orders", wrong)).StatusCode);
        Assert.Equal(16, (await c.GetFromJsonAsync<JsonElement>($"/api/members/{mid}/profile")).Tickets("BUCKET_18_9L", "DELIVERY"));
        async Task<(Guid Order, Guid Sale)> Place(CreateWaterOrderRequest req)
        {
            var id = (await Ok(await c.PostAsJsonAsync("/api/orders", req))).GetProperty("id").GetGuid();
            var row = (await c.GetFromJsonAsync<JsonElement>("/api/orders?memberId=" + mid)).EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == id);
            var sale = row.GetProperty("accessorySale");
            Assert.Equal("PENDING", sale.GetProperty("status").GetString());
            return (id, sale.GetProperty("id").GetGuid());
        }
        var first = await Place(order);
        Assert.Equal(first, await Place(order));
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/api/orders", order with { Accessories = null })).StatusCode);
        await Ok(await c.PostAsJsonAsync($"/api/orders/{first.Order}/cancel", new CancelWaterOrderRequest("取消配送")));
        var payment = new AccessoryPayment("CASH", null);
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync($"/api/accessories/sales/{first.Sale}/collect", payment)).StatusCode);
        var next = await Place(order with { IdempotencyKey = Key() });
        var before = await c.GetFromJsonAsync<JsonElement>("/api/expenses/summary");
        foreach (var response in await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => c.PostAsJsonAsync($"/api/accessories/sales/{next.Sale}/collect", payment)))) Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var after = await c.GetFromJsonAsync<JsonElement>("/api/expenses/summary");
        Assert.Equal(before.GetProperty("accessoryReceived").GetDecimal() + 12, after.GetProperty("accessoryReceived").GetDecimal());
    }
}
