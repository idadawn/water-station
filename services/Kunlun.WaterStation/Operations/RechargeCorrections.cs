using System.Security.Claims;
using Kunlun.WaterStation.Auth;
using Npgsql;
using static Kunlun.WaterStation.Operations.TicketBatches;

namespace Kunlun.WaterStation.Operations;

public sealed record CorrectRecharge(Guid FromMemberId, Guid ToMemberId, string Reason, string IdempotencyKey);

public static class RechargeCorrections
{
    public static async Task EnsureSchema(NpgsqlDataSource db)
    {
        await using var cmd = db.CreateCommand("""
            CREATE TABLE IF NOT EXISTS plugin_kunlun_waterstation.recharge_corrections (
                seq bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, tenant_id uuid NOT NULL,
                recharge_id uuid NOT NULL REFERENCES plugin_kunlun_waterstation.recharge_orders(id),
                from_member uuid NOT NULL REFERENCES plugin_kunlun_waterstation.members(id),
                to_member uuid NOT NULL REFERENCES plugin_kunlun_waterstation.members(id),
                reason text NOT NULL, actor text NOT NULL, actor_id uuid NOT NULL, request_key text NOT NULL,
                created_at timestamptz NOT NULL DEFAULT now(), UNIQUE(tenant_id,request_key), CHECK(from_member<>to_member)
            );
            CREATE INDEX IF NOT EXISTS corrections_by_recharge ON plugin_kunlun_waterstation.recharge_corrections(recharge_id,seq);
            """);
        await cmd.ExecuteNonQueryAsync();
    }

    public static void MapRechargeCorrections(this WebApplication app)
    {
        var routes = app.MapGroup("/api/recharges/{id:guid}/corrections").RequireAuthorization(p => p.RequireClaim("role", "ADMIN"));
        routes.MapPost("", Move);
        routes.MapGet("", async (Guid id, NpgsqlDataSource db, KunlunAuthRepository repo, CancellationToken ct) =>
        {
            await using var conn = await db.OpenConnectionAsync(ct);
            await using var cmd = Command(conn, null, """
                SELECT f.name,f.member_no,t.name,t.member_no,c.reason,c.actor,c.created_at
                FROM plugin_kunlun_waterstation.recharge_corrections c
                JOIN plugin_kunlun_waterstation.members f ON f.id=c.from_member
                JOIN plugin_kunlun_waterstation.members t ON t.id=c.to_member
                WHERE c.tenant_id=@tid AND c.recharge_id=@id ORDER BY c.seq DESC
                """, ("tid", repo.DefaultTenantId), ("id", id));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var history = new List<object>();
            while (await r.ReadAsync(ct)) history.Add(new { fromName = r.GetString(0), fromNo = r.GetString(1), toName = r.GetString(2), toNo = r.GetString(3), reason = r.GetString(4), actor = r.GetString(5), createdAt = r.GetDateTime(6) });
            return Results.Ok(history);
        });
    }

    private static async Task<IResult> Move(Guid id, CorrectRecharge req, ClaimsPrincipal user, NpgsqlDataSource db, KunlunAuthRepository repo, CancellationToken ct)
    {
        if (req.FromMemberId == req.ToMemberId) throw new BusinessRuleException("请选择另一位会员");
        var reason = req.Reason?.Trim();
        if (reason is not { Length: > 0 and <= 500 }) throw new BusinessRuleException("请填写更正原因，最多 500 字");
        var tid = repo.DefaultTenantId;
        var actor = user.FindFirstValue("display_name") ?? "管理员";
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await LockRequest(conn, tx, tid, "recharge-correction", req.IdempotencyKey, ct);
        await using (var cmd = Command(conn, tx, """
            SELECT recharge_id,from_member,to_member,reason FROM plugin_kunlun_waterstation.recharge_corrections
            WHERE tenant_id=@tid AND request_key=@key
            """, ("tid", tid), ("key", req.IdempotencyKey)))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            if (await r.ReadAsync(ct))
            {
                if (r.GetGuid(0) != id || r.GetGuid(1) != req.FromMemberId || r.GetGuid(2) != req.ToMemberId || r.GetString(3) != reason)
                    throw new BusinessRuleException("请求标识已用于其他更正", 409);
                return Results.NoContent();
            }

        foreach (var member in new[] { req.FromMemberId, req.ToMemberId }.Order())
            await LockMember(conn, tx, tid, member, ct);
        string no, water, mode;
        int qty;
        await using (var cmd = Command(conn, tx, """
            SELECT r.recharge_no,b.water_type,b.fulfill_mode,b.fixed_qty+b.gift_qty,b.member_id,
                   b.status='ACTIVE' AND b.fixed_qty=b.remaining_buy AND b.gift_qty=b.remaining_gift AND b.reserved_buy+b.reserved_gift=0,
                   EXISTS(SELECT 1 FROM plugin_kunlun_waterstation.refund_requests f WHERE f.batch_id=b.id AND f.status='PENDING')
            FROM plugin_kunlun_waterstation.ticket_batches b JOIN plugin_kunlun_waterstation.recharge_orders r ON r.id=b.recharge_id
            WHERE b.tenant_id=@tid AND r.id=@id AND b.source='PURCHASE' FOR UPDATE OF b,r
            """, ("tid", tid), ("id", id)))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) throw new BusinessRuleException("找不到可更正的充值原单", 404);
            if (r.GetGuid(4) != req.FromMemberId) throw new BusinessRuleException("这笔充值的归属已变更，请刷新记录", 409);
            if (!r.GetBoolean(5)) throw new BusinessRuleException("这笔充值已有核销、预占或退款，不能整笔转给另一位会员", 409);
            if (r.GetBoolean(6)) throw new BusinessRuleException("这笔充值有待审核事项，请先处理审核", 409);
            no = r.GetString(0); water = r.GetString(1); mode = r.GetString(2); qty = r.GetInt32(3);
        }
        var (available, _) = await MemberTickets.Lock(conn, tx, tid, req.FromMemberId, water, mode, ct);
        if (available < qty) throw new BusinessRuleException("原会员可用水票不足，请核对流水", 409);
        var (fromBalance, _) = await MemberTickets.Add(conn, tx, tid, req.FromMemberId, water, mode, -qty, 0, ct);
        var (toBalance, _) = await MemberTickets.Add(conn, tx, tid, req.ToMemberId, water, mode, qty, 0, ct);
        await MembershipEndpoints.Ledger(conn, tx, tid, req.FromMemberId, water, mode, "RECHARGE_MOVE_OUT", -qty, fromBalance, no, actor, ct);
        await MembershipEndpoints.Ledger(conn, tx, tid, req.ToMemberId, water, mode, "RECHARGE_MOVE_IN", qty, toBalance, no, actor, ct);
        await Exec(conn, tx, """
            UPDATE plugin_kunlun_waterstation.recharge_orders SET member_id=@dest WHERE tenant_id=@tid AND id=@id;
            UPDATE plugin_kunlun_waterstation.ticket_batches SET member_id=@dest WHERE tenant_id=@tid AND recharge_id=@id;
            INSERT INTO plugin_kunlun_waterstation.recharge_corrections(tenant_id,recharge_id,from_member,to_member,reason,actor,actor_id,request_key)
            VALUES(@tid,@id,@src,@dest,@reason,@actor,@actorId,@key)
            """, ct, ("tid", tid), ("id", id), ("dest", req.ToMemberId), ("src", req.FromMemberId), ("reason", reason), ("actor", actor),
            ("actorId", Guid.Parse(user.FindFirstValue("sub")!)), ("key", req.IdempotencyKey));
        await tx.CommitAsync(ct);
        return Results.NoContent();
    }
}
