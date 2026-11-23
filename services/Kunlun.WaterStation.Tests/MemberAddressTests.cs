using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kunlun.WaterStation.Auth;
using Kunlun.WaterStation.Operations;
using Xunit;

namespace Kunlun.WaterStation.Tests;

[Collection("KunlunIntegration")]
public sealed class MemberAddressTests(TestHost host)
{
    private static string Key() => Guid.NewGuid().ToString("N");
    private static string Phone() => $"137{Random.Shared.Next(10000000, 99999999)}";
    private static async Task<JsonElement> Ok(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(); Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonSerializer.Deserialize<JsonElement>(body);
    }
    private async Task<HttpClient> Staff(string account = "YG0001")
    {
        var c = host.CreateClient(); var s = await Ok(await c.PostAsJsonAsync("/api/auth/staff/login", new StaffLoginRequest(account, TestHost.Password)));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("accessToken").GetString()); return c;
    }
    private static async Task<(Guid Id, string Phone)> Member(HttpClient c, string? location = "幸福里小区 3 栋", string? door = "2 单元 501 室")
    {
        var phone = Phone();
        return ((await Ok(await c.PostAsJsonAsync("/api/members", new CreateMemberRequest("地址验收", phone, location, null, door)))).GetProperty("id").GetGuid(), phone);
    }
    private static async Task<JsonElement[]> List(HttpClient c, Guid member) => (await c.GetFromJsonAsync<JsonElement[]>($"/api/members/{member}/addresses"))!;
    private static async Task<JsonElement> Add(HttpClient c, Guid member, string location, string door, bool isDefault = false, string name = "李四", string phone = "13600001111") =>
        await Ok(await c.PostAsJsonAsync($"/api/members/{member}/addresses", new SaveMemberAddressRequest(name, phone, location, door, isDefault)));
    private static async Task<string?> DefaultText(HttpClient c, Guid member) => (await c.GetFromJsonAsync<JsonElement>($"/api/members/{member}/profile")).GetProperty("defaultAddress").GetString();
    private static async Task GiveDeliveryTickets(HttpClient c, Guid member)
    {
        var plan = await Ok(await c.PostAsJsonAsync("/api/plans", new SaveRechargePlanRequest("地址验收套餐-" + Key(), "BUCKET_18_9L", "DELIVERY", 100, 13, 0)));
        await Ok(await c.PostAsJsonAsync("/api/recharges", new CreateRechargeOrderRequest(member, plan.GetProperty("id").GetGuid(), "CASH", Key())));
    }
    private static Task<HttpResponseMessage> Deliver(HttpClient c, Guid member, Guid? addressId = null) =>
        c.PostAsJsonAsync("/api/orders", new CreateWaterOrderRequest(member, "BUCKET_18_9L", "DELIVERY", 1, Key(), addressId));

    [Fact]
    public async Task Search_matches_secondary_addresses_without_duplicating_members()
    {
        using var c = await Staff();
        var area = $"锦绣花园{Key()[..8]}";
        var (first, _) = await Member(c);
        var (second, _) = await Member(c, area, "9栋");
        var contact = $"收货人{Key()[..6]}";
        var phone = Phone();
        await Add(c, first, area, "1 栋 301", name: contact, phone: phone);
        await Add(c, first, area, "2 栋 102", name: contact, phone: phone);
        async Task<JsonElement> Search(string query) =>
            await c.GetFromJsonAsync<JsonElement>($"/api/members?pageSize=1&q={Uri.EscapeDataString(query)}");
        Assert.Equal(2, (await Search(area)).GetProperty("total").GetInt32());
        var row = Assert.Single((await Search(area + "1栋301")).GetProperty("rows").EnumerateArray());
        Assert.Equal(first, row.GetProperty("id").GetGuid());
        var match = Assert.Single(row.GetProperty("matchedAddresses").EnumerateArray());
        Assert.Equal(area + " 1 栋 301", match.GetProperty("fullAddress").GetString());
        Assert.NotEqual(match.GetProperty("fullAddress").GetString(), row.GetProperty("defaultAddress").GetString());
        foreach (var query in new[] { contact, phone })
        {
            var result = await Search(query);
            Assert.Equal(1, result.GetProperty("total").GetInt32());
            var matches = Assert.Single(result.GetProperty("rows").EnumerateArray()).GetProperty("matchedAddresses").EnumerateArray().ToList();
            Assert.Equal(2, matches.Count);
            Assert.All(matches, a => Assert.Equal(phone, a.GetProperty("contactPhone").GetString()));
        }
        Assert.Equal(second, Assert.Single((await Search(area + "9栋")).GetProperty("rows").EnumerateArray()).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Editing_contact_keeps_phones_unique_and_updates_only_the_members_own_delivery_contacts()
    {
        using var c = await Staff();
        var (first, oldPhone) = await Member(c);
        var (second, otherPhone) = await Member(c);
        await Add(c, first, "公司", "前台", phone: oldPhone);
        var url = $"/api/members/{first}/contact";
        using var reader = await Staff("YG0008");
        var edit = new UpdateMemberContactRequest("王女士", Phone());
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync(url, edit)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync(url, edit with { Phone = "123" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await c.PutAsJsonAsync(url, edit with { Phone = otherPhone })).StatusCode);
        await Ok(await c.PutAsJsonAsync(url, edit with { Phone = oldPhone }));
        await Ok(await c.PutAsJsonAsync(url, edit));
        var addresses = await List(c, first);
        Assert.Contains(addresses, a => a.GetProperty("contactName").GetString() == edit.Name && a.GetProperty("contactPhone").GetString() == edit.Phone);
        Assert.Contains(addresses, a => a.GetProperty("contactName").GetString() == "李四" && a.GetProperty("contactPhone").GetString() == oldPhone);
        var reset = await Ok(await c.PostAsync($"/api/members/{second}/reset-password", null));
        var next = Phone();
        var competing = await Task.WhenAll(c.PutAsJsonAsync(url, edit with { Phone = next }),
            c.PutAsJsonAsync($"/api/members/{second}/contact", new UpdateMemberContactRequest("王先生", next)));
        Assert.Single(competing, r => r.IsSuccessStatusCode);
        Assert.Single(competing, r => r.StatusCode == HttpStatusCode.Conflict);
        var secondPhone = competing[1].IsSuccessStatusCode ? next : otherPhone;
        using var member = host.CreateClient();
        await TestHost.CompleteFirstLogin(member, secondPhone, reset.GetProperty("temporaryPassword").GetString()!, TestHost.Password);
        var corrected = Phone();
        await Ok(await c.PutAsJsonAsync($"/api/members/{second}/contact", new UpdateMemberContactRequest("王先生", corrected)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await member.PostAsJsonAsync("/api/auth/member/login", new StaffLoginRequest(secondPhone, TestHost.Password))).StatusCode);
        await Ok(await member.PostAsJsonAsync("/api/auth/member/login", new StaffLoginRequest(corrected, TestHost.Password)));
    }

    [Fact]
    public async Task New_Member_Address_Defaults_Contact_To_Member_And_Fills_Default_Text()
    {
        using var c = await Staff(); var (id, phone) = await Member(c);
        var only = Assert.Single(await List(c, id));
        Assert.Equal("地址验收", only.GetProperty("contactName").GetString());
        Assert.Equal(phone, only.GetProperty("contactPhone").GetString());
        Assert.Equal("幸福里小区 3 栋", only.GetProperty("location").GetString());
        Assert.Equal("2 单元 501 室", only.GetProperty("doorNo").GetString());
        Assert.True(only.GetProperty("isDefault").GetBoolean());
        Assert.Equal("幸福里小区 3 栋 2 单元 501 室", await DefaultText(c, id));

        var (pickupOnly, _) = await Member(c, null, null);
        Assert.Empty(await List(c, pickupOnly));
        Assert.Equal("", await DefaultText(c, pickupOnly));
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/members", new CreateMemberRequest("缺门牌", Phone(), "金桂花园 2 栋", null))).StatusCode);
    }

    [Fact]
    public async Task Members_Keep_Several_Addresses_With_Exactly_One_Default()
    {
        using var c = await Staff(); var (id, _) = await Member(c);
        var first = (await List(c, id))[0].GetProperty("id").GetGuid();
        var office = await Add(c, id, "科技园 A 座", "12 楼 1203", isDefault: true, name: "王五", phone: "13700002222");
        var spare = await Add(c, id, "父母家 金桂花园 2 栋", "101 室");
        Assert.False(spare.GetProperty("isDefault").GetBoolean());

        var list = await List(c, id);
        Assert.Equal(3, list.Length);
        Assert.Single(list, a => a.GetProperty("isDefault").GetBoolean());
        Assert.Equal(office.GetProperty("id").GetGuid(), list[0].GetProperty("id").GetGuid());
        Assert.Equal("科技园 A 座 12 楼 1203", await DefaultText(c, id));

        // Editing without asking for default never moves or clears it.
        await Ok(await c.PutAsJsonAsync($"/api/members/{id}/addresses/{office.GetProperty("id")}", new SaveMemberAddressRequest("王五", "13700002222", "科技园 B 座", "8 楼", false)));
        Assert.Equal("科技园 B 座 8 楼", await DefaultText(c, id));
        await Ok(await c.PostAsync($"/api/members/{id}/addresses/{first}/default", null));
        Assert.Equal("幸福里小区 3 栋 2 单元 501 室", await DefaultText(c, id));
        Assert.Single(await List(c, id), a => a.GetProperty("isDefault").GetBoolean());

        // Deleting the default promotes the most recently changed address; deleting all clears the text.
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/members/{id}/addresses/{first}")).StatusCode);
        var promoted = Assert.Single(await List(c, id), a => a.GetProperty("isDefault").GetBoolean());
        Assert.Equal(office.GetProperty("id").GetGuid(), promoted.GetProperty("id").GetGuid());
        foreach (var a in await List(c, id)) await Ok(await c.DeleteAsync($"/api/members/{id}/addresses/{a.GetProperty("id")}"));
        Assert.Equal("", await DefaultText(c, id));
        Assert.Equal(HttpStatusCode.NotFound, (await c.DeleteAsync($"/api/members/{id}/addresses/{first}")).StatusCode);
    }

    [Fact]
    public async Task Address_Fields_Are_Validated_And_Capped()
    {
        using var c = await Staff(); var (id, _) = await Member(c);
        foreach (var bad in new[]
        {
            new SaveMemberAddressRequest("李四", "13600001111", "", "1-101"),
            new SaveMemberAddressRequest("李四", "13600001111", "幸福里", " "),
            new SaveMemberAddressRequest("", "13600001111", "幸福里", "1-101"),
            new SaveMemberAddressRequest("李四", "0571-8888", "幸福里", "1-101"),
            new SaveMemberAddressRequest("李四", "13600001111", new string('长', 201), "1-101"),
        })
            Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync($"/api/members/{id}/addresses", bad)).StatusCode);

        for (var i = 1; i < MemberAddresses.MaxPerMember; i++) await Add(c, id, $"批量地址 {i}", $"{i} 号");
        var full = await c.PostAsJsonAsync($"/api/members/{id}/addresses", new SaveMemberAddressRequest("李四", "13600001111", "超限地址", "1 号"));
        Assert.Equal(HttpStatusCode.BadRequest, full.StatusCode);
        Assert.Contains("最多保存", await full.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Delivery_Orders_Snapshot_The_Chosen_Or_Default_Address()
    {
        using var c = await Staff(); var (id, phone) = await Member(c); await GiveDeliveryTickets(c, id);
        var proxy = await Add(c, id, "公司前台 科技园 A 座", "1 楼大堂", name: "前台小张", phone: "13500003333");

        var chosen = (await Ok(await Deliver(c, id, proxy.GetProperty("id").GetGuid()))).GetProperty("addressSnapshot");
        Assert.Equal(proxy.GetProperty("id").GetGuid(), chosen.GetProperty("addressId").GetGuid());
        Assert.Equal("前台小张", chosen.GetProperty("contactName").GetString());
        Assert.Equal("13500003333", chosen.GetProperty("contactPhone").GetString());
        Assert.Equal("1 楼大堂", chosen.GetProperty("doorNo").GetString());
        Assert.Equal("公司前台 科技园 A 座 1 楼大堂", chosen.GetProperty("address").GetString());

        var byDefault = (await Ok(await Deliver(c, id))).GetProperty("addressSnapshot");
        Assert.Equal("幸福里小区 3 栋 2 单元 501 室", byDefault.GetProperty("address").GetString());
        Assert.Equal(phone, byDefault.GetProperty("contactPhone").GetString());

        // Later edits never rewrite an existing order's snapshot.
        await Ok(await c.PutAsJsonAsync($"/api/members/{id}/addresses/{proxy.GetProperty("id")}", new SaveMemberAddressRequest("前台小李", "13500004444", "公司前台 科技园 A 座", "2 楼", false)));
        var orders = (await c.GetFromJsonAsync<JsonElement[]>($"/api/orders?memberId={id}"))!;
        Assert.Contains(orders, o => o.GetProperty("addressSnapshot").GetProperty("contactName").GetString() == "前台小张");

        var (other, _) = await Member(c);
        var foreign = await Deliver(c, id, (await List(c, other))[0].GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        Assert.Contains("不属于该会员", await foreign.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Delivery_Requires_An_Address_But_Pickup_Does_Not()
    {
        using var c = await Staff(); var (id, _) = await Member(c, null, null); await GiveDeliveryTickets(c, id);
        var refused = await Deliver(c, id);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("收货地址", await refused.Content.ReadAsStringAsync());
        var profile = await c.GetFromJsonAsync<JsonElement>($"/api/members/{id}/profile");
        Assert.Equal(13, profile.Tickets("BUCKET_18_9L", "DELIVERY"));

        var plan = await Ok(await c.PostAsJsonAsync("/api/plans", new SaveRechargePlanRequest("地址验收自提-" + Key(), "BUCKET_18_9L", "PICKUP", 100, 16, 0)));
        await Ok(await c.PostAsJsonAsync("/api/recharges", new CreateRechargeOrderRequest(id, plan.GetProperty("id").GetGuid(), "CASH", Key())));
        var pickup = (await Ok(await c.PostAsJsonAsync("/api/orders", new CreateWaterOrderRequest(id, "BUCKET_18_9L", "PICKUP", 1, Key())))).GetProperty("addressSnapshot");
        Assert.Equal("", pickup.GetProperty("address").GetString());
        Assert.Equal("地址验收", pickup.GetProperty("contactName").GetString());
    }

    [Fact]
    public async Task Couriers_Cannot_Edit_Addresses_And_Legacy_Text_Becomes_Default()
    {
        using var admin = await Staff(); var (id, _) = await Member(admin);
        using var courier = await Staff("YG0008");
        Assert.Single(await List(courier, id));
        Assert.Equal(HttpStatusCode.Forbidden, (await courier.PostAsJsonAsync($"/api/members/{id}/addresses", new SaveMemberAddressRequest("李四", "13600001111", "幸福里", "1-101"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await courier.DeleteAsync($"/api/members/{id}/addresses/{(await List(admin, id))[0].GetProperty("id")}")).StatusCode);

        // Legacy single-text addresses become the default entry, with the door number left for the store to fill in.
        var zhou = Guid.Parse("20000000-0000-0000-0000-000000008620");
        var legacy = Assert.Single(await List(admin, zhou));
        Assert.Equal("金桂花园 2 栋 101 室", legacy.GetProperty("location").GetString());
        Assert.Equal("", legacy.GetProperty("doorNo").GetString());
        Assert.Equal("周明", legacy.GetProperty("contactName").GetString());
        Assert.True(legacy.GetProperty("isDefault").GetBoolean());
    }
}
