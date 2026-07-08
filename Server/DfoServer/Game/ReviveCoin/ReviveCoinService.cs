using System;
using DfoServer.Game.DailyReset;
using DfoServer.Game.Inventory;

namespace DfoServer.Game.ReviveCoin
{
    // 复活币功能域 — 常量与全部用例的唯一归属地。
    //
    // ── 每日/周常功能打样 ──
    // 后续同类功能(签到/进图次数/限购等)照此模式:
    //   1) Game/<功能>/<功能>Service 一个文件: 功能常量(自带账本 counter_key) + 用例方法;
    //   2) 组合写入 = IAssetService.OpenScope + DailyResetService 的 (conn,tx) 变体, 同事务提交;
    //   3) Network 层 handler 只做协议壳(解析请求 → 调用例 → 回包), 不写业务规则;
    //   4) 机制服务(DailyResetService)保持零业务知识, 任何功能常量不得写入基建。
    //
    // 复活币实体: itemId=1 固定 Main slot1(86种子实证: 角色1002 list0/slot1/id1/stackable
    // x3368; 钱包区布局 slot0=金币/slot1=复活币/slot2=SP)。PVF 无 id=1 词条, 属服务端
    // 合成物品, 拾取走 SqliteInventoryStore.TryPickupItemCore 专线(不能过 metadata 解析)。
    public sealed class ReviveCoinService
    {
        public const int ItemId = 1;
        public const short WalletSlot = 1;
        // PVF: stackable/cash/coin_general.stk, name=復活コイン, type=[waste]
        public const int ConsumableItemId = 42;

        public static bool IsReviveCoinReward(int itemTemplateId)
        {
            return itemTemplateId == ItemId || itemTemplateId == ConsumableItemId;
        }

        public static int GrantToWallet(Microsoft.Data.Sqlite.SqliteConnection conn, Microsoft.Data.Sqlite.SqliteTransaction tx, int characterId, int count)
        {
            var effectiveCount = System.Math.Max(1, count);
            int current = 0;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT stack_count FROM character_items WHERE character_id=@cid AND list_type=0 AND slot_index=@slot AND item_template_id=@iid";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@slot", (int)WalletSlot);
                cmd.Parameters.AddWithValue("@iid", ItemId);
                var val = cmd.ExecuteScalar();
                if (val != null && val != System.DBNull.Value)
                    current = System.Convert.ToInt32(val);
            }

            var newTotal = current + effectiveCount;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
UPDATE character_items SET stack_count=@total, instance_value=@total, updated_at=CURRENT_TIMESTAMP
WHERE character_id=@cid AND list_type=0 AND slot_index=@slot AND item_template_id=@iid";
                cmd.Parameters.AddWithValue("@total", newTotal);
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@slot", (int)WalletSlot);
                cmd.Parameters.AddWithValue("@iid", ItemId);
                if (cmd.ExecuteNonQuery() > 0)
                    return newTotal;
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
INSERT OR IGNORE INTO character_items (
    owner_scope, owner_id, character_id, list_type, slot_index, item_template_id, item_kind,
    stack_count, instance_value, durability, seal_flag, option_value, expire_time, marker_16,
    pet_serial_or_handle, extra_json)
VALUES ('character', @cid, @cid, 0, @slot, @iid, 'stackable', @total, @total, 0, 0, 0, 0, 0, 0, '{}')";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@slot", (int)WalletSlot);
                cmd.Parameters.AddWithValue("@iid", ItemId);
                cmd.Parameters.AddWithValue("@total", newTotal);
                cmd.ExecuteNonQuery();
            }

            return newTotal;
        }

        // 每日领取标记(账本 key, cap=1)
        public const string DailyClaimKey = "revive_coin_daily_claim";

        private readonly IInventoryStore _inventoryStore;
        private readonly IAssetService _assetService;
        private readonly DailyResetService _dailyReset;

        public ReviveCoinService(IInventoryStore inventoryStore, IAssetService assetService, DailyResetService dailyReset)
        {
            _inventoryStore = inventoryStore ?? throw new ArgumentNullException(nameof(inventoryStore));
            _assetService = assetService ?? throw new ArgumentNullException(nameof(assetService));
            _dailyReset = dailyReset ?? throw new ArgumentNullException(nameof(dailyReset));
        }

        // 每日免费领取: 每角色每日一次, 且身上复活币为 0 才发(救济性质, 规则沿用
        // PR#338 引用的 149.diff)。领取标记与发币同一事务, 任一步失败整体回滚,
        // 不会出现"标记了没发币"; 有币被拒发生在领取之前, 不消耗当日标记。
        public bool TryGrantDaily(int characterId, int accountId, out short assignedSlot)
        {
            assignedSlot = -1;
            try
            {
                if (_inventoryStore.CountItem(characterId, ItemId) > 0)
                    return false;

                using (var scope = _assetService.OpenScope(characterId, accountId))
                {
                    if (!_dailyReset.TryClaimFlag(scope.Connection, scope.Transaction, characterId, DailyClaimKey)
                        || !_assetService.TryAddItem(scope, ItemId, 1, out assignedSlot))
                        return false;   // Dispose 回滚, 领取位随之撤销

                    scope.Commit();
                    return true;
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[ReviveCoin] TryGrantDaily ERROR: {ex.Message}");
                return false;
            }
        }

        // 死亡复活消耗 1 枚: 单事务, 不足返回 false 无副作用。
        public bool TryConsume(int characterId, int accountId, out short slot, out int remaining)
        {
            slot = -1;
            remaining = 0;
            try
            {
                using (var scope = _assetService.OpenScope(characterId, accountId))
                {
                    if (!_assetService.TryRemoveItem(scope, ItemId, 1, out slot, out remaining))
                        return false;

                    scope.Commit();
                    return true;
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[ReviveCoin] TryConsume ERROR: {ex.Message}");
                return false;
            }
        }
    }
}
