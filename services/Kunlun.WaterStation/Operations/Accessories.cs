using System.Security.Claims;
using System.Text.Json;
using Kunlun.WaterStation.Auth;
using Npgsql;
using static Kunlun.WaterStation.Operations.TicketBatches;

namespace Kunlun.WaterStation.Operations;

public sealed record AccessoryProductInput(string Name, string Unit, decimal Price, bool Active);
public sealed record AccessoryItem(Guid ProductId, int Quantity, decimal Price);
public sealed record AccessoryBasket(AccessoryItem[] Items, string Payment);
public sealed record SellAccessories(string IdempotencyKey, Guid? MemberId, AccessoryBasket Basket);
public sealed record AccessoryPayment(string PayMethod, string? Reason);

public static class Accessories
{
    private static bool Admin(ClaimsPrincipal u) => u.HasClaim("role", "ADMIN");
    private static bool Clerk(ClaimsPrincipal u) => u.HasClaim("perm", KunlunPermissions.OrderCreate);
    private static bool Collect(ClaimsPrincipal u) => Clerk(u) || u.HasClaim("perm", KunlunPermissions.OrderFulfill) || u.HasClaim("perm", KunlunPermissions.OrderAccept);
    private static string Actor(ClaimsPrincipal u) => u.FindFirstValue("display_name") ?? "员工";
    private static bool Payment(string? method) => method is "WECHAT" or "ALIPAY" or "CASH" or "BANK";

    public static async Task EnsureSchema(NpgsqlDataSource db)
    {
        await using var cmd = db.CreateCommand("""
            CREATE TABLE IF NOT EXISTS plugin_kunlun_waterstation.accessory_products (
                id uuid PRIMARY KEY, tenant_id uuid NOT NULL, name text NOT NULL, unit text NOT NULL,
                price numeric(10,2) NOT NULL CHECK(price>0), active boolean NOT NULL, UNIQUE(tenant_id,name)
            );
            CREATE TABLE IF NOT EXISTS plugin_kunlun_waterstation.accessory_sales (
                id uuid PRIMARY KEY, tenant_id uuid NOT NULL, sale_no text NOT NULL UNIQUE, request_key text NOT NULL,
                request_payload jsonb NOT NULL, order_id uuid UNIQUE REFERENCES plugin_kunlun_waterstation.water_orders(id),
                member_id uuid REFERENCES plugin_kunlun_waterstation.members(id), items jsonb NOT NULL,
                amount numeric(10,2) NOT NULL CHECK(amount>0), status text NOT NULL CHECK(status IN ('PENDING','PAID','REFUNDED','VOID')),
                operator_name text NOT NULL, created_at timestamptz NOT NULL DEFAULT now(),
                paid_at timestamptz, paid_by text, pay_method text,
                refunded_at timestamptz, refunded_by text, refund_method text, refund_reason text,
                UNIQUE(tenant_id,request_key)
            );
            CREATE INDEX IF NOT EXISTS accessory_paid_at ON plugin_kunlun_waterstation.accessory_sales(tenant_id,paid_at);
            CREATE INDEX IF NOT EXISTS accessory_refunded_at ON plugin_kunlun_waterstation.accessory_sales(tenant_id,refunded_at);
            """);
        await cmd.ExecuteNonQueryAsync();
    }

    public static void MapAccessories(this WebApplication app)
    {
        var api = app.MapGroup("/api/accessories").RequireAuthorization();
        api.MapGet("/products", async (bool? all, ClaimsPrincipal user, NpgsqlDataSource db, KunlunAuthRepository repo, CancellationToken ct) =>
        {
            if (!Clerk(user) && !Admin(user) || all == true && !Admin(user)) return Results.Forbid();
            await using var conn = await db.OpenConnectionAsync(ct);
            await using var cmd = Command(conn, null, "SELECT id,name,unit,price,active FROM plugin_kunlun_waterstation.accessory_products WHERE tenant_id=@tid AND (@all OR active) ORDER BY active DESC,name,id", ("tid", repo.DefaultTenantId), ("all", all == true));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var products = new List<object>();
            while (await r.ReadAsync(ct)) products.Add(new { id = r.GetGuid(0), name = r.GetString(1), unit = r.GetString(2), price = r.GetDecimal(3), active = r.GetBoolean(4) });
            return Results.Ok(products);
        });
        api.MapPost("/products", (AccessoryProductInput req, ClaimsPrincipal user, NpgsqlDataSource db, KunlunAuthRepository repo, CancellationToken ct) => Save(null, req, user, db, repo, ct));
        api.MapPut("/products/{id:guid}", (Guid id, AccessoryProductInput req, ClaimsPrincipal user, NpgsqlDataSource db, KunlunAuthRepository repo, CancellationToken ct) => Save(id, req, user, db, repo, ct));
        api.MapPost("/sales", async (SellAccessories req, ClaimsPrincipal user, NpgsqlDataSource db, KunlunAuthRepository repo, CancellationToken ct) =>
        {
            if (!Clerk(user)) return Results.Forbid();
            await using var conn = await db.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            var tid = repo.DefaultTenantId;
            await LockRequest(conn, tx, tid, "accessory-sale", req.IdempotencyKey, ct);
            var payload = Payload(req.Basket, req.MemberId);
            await using (var cmd = Command(conn, tx, "SELECT id,request_payload=@payload::jsonb FROM plugin_kunlun_waterstation.accessory_sales WHERE tenant_id=@tid AND request_key=@key", ("tid", tid), ("key", "sale:" + req.IdempotencyKey), ("payload", payload)))
            await using (var r = await cmd.ExecuteReaderAsync(ct)) if (await r.ReadAsync(ct))
            {
                if (!r.GetBoolean(1)) throw new BusinessRuleException("请求标识已用于其他配件销售", 409);
                return Results.Ok(new { id = r.GetGuid(0) });
            }
            if (req.MemberId is Guid mid) await LockMember(conn, tx, tid, mid, ct);
            var id = await Create(conn, tx, tid, req.MemberId, null, req.Basket, "sale:" + req.IdempotencyKey, false, Actor(user), ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { id });
        });
        api.MapGet("/sales", ListSales);
        api.MapPost("/sales/{id:guid}/collect", (Guid id, AccessoryPayment req, ClaimsPrincipal user, NpgsqlDataSource db, KunlunAuthRepository repo, CancellationToken ct) => Settle(id, req, false, user, db, repo, ct));
        api.MapPost("/sales/{id:guid}/refund", (Guid id, AccessoryPayment req, ClaimsPrincipal user, NpgsqlDataSource db, KunlunAuthRepository repo, CancellationToken ct) => Settle(id, req, true, user, db, repo, ct));
    }

    private static async Task<IResult> Save(Guid? id, AccessoryProductInput req, ClaimsPrincipal user, NpgsqlDataSource db, KunlunAuthRepository repo, CancellationToken ct)
    {
        if (!Admin(user)) return Results.Forbid();
        var name = req.Name?.Trim(); var unit = req.Unit?.Trim();
        if (name is not { Length: > 0 and <= 80 } || unit is not { Length: > 0 and <= 12 } || !Money.Valid(req.Price, false))
            throw new BusinessRuleException("请填写配件名称、单位和大于零的单价，金额精确到分");
        var productId = id ?? Guid.NewGuid();
        await using var conn = await db.OpenConnectionAsync(ct);
        var sql = id.HasValue
            ? "UPDATE plugin_kunlun_waterstation.accessory_products SET name=@name,unit=@unit,price=@price,active=@active WHERE tenant_id=@tid AND id=@id"
            : "INSERT INTO plugin_kunlun_waterstation.accessory_products(id,tenant_id,name,unit,price,active) VALUES(@id,@tid,@name,@unit,@price,@active)";
        try
        {
            if (await Exec(conn, null, sql, ct, ("id", productId), ("tid", repo.DefaultTenantId), ("name", name), ("unit", unit), ("price", req.Price), ("active", req.Active)) == 0) return Results.NotFound();
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { throw new BusinessRuleException("配件名称已存在", 409); }
        return Results.Ok(new { id = productId });
    }

    private static string Payload(AccessoryBasket? basket, Guid? member)
    {
        if (basket?.Items is not { Length: > 0 and <= 100 } items || items.Any(i => i is null || i.Quantity is <= 0 or > 9999 || !Money.Valid(i.Price, false)) ||
            items.Select(i => i.ProductId).Distinct().Count() != items.Length) throw new BusinessRuleException("请核对配件、单价和数量");
        return JsonSerializer.Serialize(new { member, basket.Payment, items = items.OrderBy(i => i.ProductId) });
    }

    internal static async Task CheckReplay(NpgsqlConnection conn, NpgsqlTransaction tx, Guid tid, Guid orderId, Guid mid, AccessoryBasket? basket, CancellationToken ct)
    {
        await using var cmd = Command(conn, tx, "SELECT request_payload=@payload::jsonb FROM plugin_kunlun_waterstation.accessory_sales WHERE tenant_id=@tid AND order_id=@id",
            ("tid", tid), ("id", orderId), ("payload", basket == null ? "null" : Payload(basket, mid)));
        var matches = await cmd.ExecuteScalarAsync(ct);
        if (basket == null ? matches != null : matches is not true) throw new BusinessRuleException("此水单已提交，配件信息与原单不同", 409);
    }

    internal static async Task<Guid> Create(NpgsqlConnection conn, NpgsqlTransaction tx, Guid tid, Guid? mid, Guid? order, AccessoryBasket basket, string key, bool delivery, string actor, CancellationToken ct)
    {
        var payload = Payload(basket, mid);
        var pending = basket.Payment == "COD";
        if (pending ? !delivery : !Payment(basket.Payment)) throw new BusinessRuleException("请确认配件收款方式，只有配送单可选送达收款");
        decimal amount = 0;
        var lines = new List<object>();
        foreach (var item in basket.Items.OrderBy(i => i.ProductId))
        {
            await using var cmd = Command(conn, tx, "SELECT name,unit,price FROM plugin_kunlun_waterstation.accessory_products WHERE tenant_id=@tid AND id=@id AND active FOR SHARE", ("tid", tid), ("id", item.ProductId));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) throw new BusinessRuleException("配件已停售，请重新选购", 409);
            var price = r.GetDecimal(2);
            if (item.Price != price) throw new BusinessRuleException("配件价格已变更，请刷新后核对", 409);
            amount += price * item.Quantity;
            lines.Add(new { productId = item.ProductId, name = r.GetString(0), unit = r.GetString(1), price, quantity = item.Quantity });
        }
        if (!Money.Valid(amount, false)) throw new BusinessRuleException("配件总金额超出范围");
        var id = Guid.NewGuid();
        await Exec(conn, tx, """
            INSERT INTO plugin_kunlun_waterstation.accessory_sales(id,tenant_id,sale_no,request_key,request_payload,order_id,member_id,items,amount,status,
                operator_name,paid_at,paid_by,pay_method)
            VALUES (@id,@tid,@no,@key,@payload::jsonb,@order::uuid,@mid::uuid,@items::jsonb,@amount,@status,@actor,
                CASE WHEN @pending THEN NULL ELSE now() END,CASE WHEN @pending THEN NULL ELSE @actor END,CASE WHEN @pending THEN NULL ELSE @method END)
            """, ct, ("id", id), ("tid", tid), ("no", "PJ" + id.ToString("N")[..20]), ("key", key), ("payload", payload), ("order", (object?)order ?? DBNull.Value),
            ("mid", (object?)mid ?? DBNull.Value), ("items", JsonSerializer.Serialize(lines)), ("amount", amount), ("status", pending ? "PENDING" : "PAID"), ("actor", actor), ("pending", pending), ("method", basket.Payment));
        return id;
    }

    private static async Task<IResult> Settle(Guid id, AccessoryPayment req, bool refund, ClaimsPrincipal user, NpgsqlDataSource db, KunlunAuthRepository repo, CancellationToken ct)
    {
        if (refund ? !Admin(user) : !Collect(user)) return Results.Forbid();
        var reason = req.Reason?.Trim();
        if (!Payment(req.PayMethod) || refund && reason is not { Length: > 0 and <= 500 }) throw new BusinessRuleException("请核对收付方式，并在退款时填写原因");
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var tid = repo.DefaultTenantId;
        await using (var cmd = Command(conn, tx, "SELECT status,pay_method,refund_method,refund_reason FROM plugin_kunlun_waterstation.accessory_sales WHERE tenant_id=@tid AND id=@id FOR UPDATE", ("tid", tid), ("id", id)))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return Results.NotFound();
            var status = r.GetString(0);
            if (refund && status == "REFUNDED" && r.GetString(2) == req.PayMethod && r.GetString(3) == reason) return Results.NoContent();
            if (!refund && status == "PAID" && r.GetString(1) == req.PayMethod) return Results.NoContent();
            if (status != (refund ? "PAID" : "PENDING")) throw new BusinessRuleException("当前配件单状态已变化，请刷新核对", 409);
        }
        var update = refund ? "status='REFUNDED',refund_method=@method,refunded_at=now(),refunded_by=@actor,refund_reason=@reason"
                          : "status='PAID',pay_method=@method,paid_at=now(),paid_by=@actor";
        await Exec(conn, tx, $"UPDATE plugin_kunlun_waterstation.accessory_sales SET {update} WHERE tenant_id=@tid AND id=@id", ct,
            ("tid", tid), ("id", id), ("method", req.PayMethod), ("reason", reason ?? ""), ("actor", Actor(user)));
        await tx.CommitAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ListSales(string? status, DateTimeOffset? from, DateTimeOffset? to, int? page, int? pageSize, ClaimsPrincipal user, NpgsqlDataSource db, KunlunAuthRepository repo, CancellationToken ct)
    {
        if (!Admin(user) && !(status == "PENDING" && Collect(user))) return Results.Forbid();
        await using var conn = await db.OpenConnectionAsync(ct);
        return await Paging.From(page ?? 1, pageSize, 20).Query(conn, """
            s.id,s.sale_no,s.items::text,s.amount,s.status,s.operator_name,s.created_at,s.pay_method,s.paid_at,s.paid_by,
            s.refund_method,s.refunded_at,s.refunded_by,s.refund_reason,m.name,o.order_no,o.address_snapshot->>'address'
            """, """
            plugin_kunlun_waterstation.accessory_sales s
            LEFT JOIN plugin_kunlun_waterstation.members m ON m.id=s.member_id
            LEFT JOIN plugin_kunlun_waterstation.water_orders o ON o.id=s.order_id
            WHERE s.tenant_id=@tid AND (@status IS NULL OR s.status=@status)
              AND EXISTS(SELECT 1 FROM (VALUES(s.created_at),(s.paid_at),(s.refunded_at)) events(at)
                WHERE at IS NOT NULL AND (@from IS NULL OR at>=@from) AND (@to IS NULL OR at<@to))
            """, "COALESCE(s.refunded_at,s.paid_at,s.created_at) DESC,s.id", p =>
        {
            p.AddWithValue("tid", repo.DefaultTenantId); p.Add(Paging.Text("status", status)); p.Add(Paging.Time("from", from)); p.Add(Paging.Time("to", to));
        }, r => new
        {
            id = r.GetGuid(0),
            saleNo = r.GetString(1),
            items = JsonSerializer.Deserialize<JsonElement>(r.GetString(2)),
            amount = r.GetDecimal(3),
            status = r.GetString(4),
            operatorName = r.GetString(5),
            createdAt = r.GetDateTime(6),
            payMethod = r.IsDBNull(7) ? null : r.GetString(7),
            paidAt = r.IsDBNull(8) ? (DateTime?)null : r.GetDateTime(8),
            paidBy = r.IsDBNull(9) ? null : r.GetString(9),
            refundMethod = r.IsDBNull(10) ? null : r.GetString(10),
            refundedAt = r.IsDBNull(11) ? (DateTime?)null : r.GetDateTime(11),
            refundedBy = r.IsDBNull(12) ? null : r.GetString(12),
            refundReason = r.IsDBNull(13) ? null : r.GetString(13),
            memberName = r.IsDBNull(14) ? null : r.GetString(14),
            orderNo = r.IsDBNull(15) ? null : r.GetString(15),
            address = r.IsDBNull(16) ? null : r.GetString(16)
        }, ct);
    }

    internal static async Task<(decimal Received, decimal Refunded)> Totals(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid tid, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        await using var cmd = Command(conn, tx, """
            SELECT (SELECT COALESCE(sum(amount),0) FROM plugin_kunlun_waterstation.accessory_sales WHERE tenant_id=@tid AND paid_at IS NOT NULL
                    AND (@from IS NULL OR paid_at>=@from) AND (@to IS NULL OR paid_at<@to)),
                   (SELECT COALESCE(sum(amount),0) FROM plugin_kunlun_waterstation.accessory_sales WHERE tenant_id=@tid AND refunded_at IS NOT NULL
                    AND (@from IS NULL OR refunded_at>=@from) AND (@to IS NULL OR refunded_at<@to))
            """, ("tid", tid));
        cmd.Parameters.Add(Paging.Time("from", from)); cmd.Parameters.Add(Paging.Time("to", to));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return (r.GetDecimal(0), r.GetDecimal(1));
    }
}
