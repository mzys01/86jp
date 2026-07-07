using DfoServer.Game.Currency;
using System;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    // 账号金库(account_cargo_state): selection_key=容量档位, value32=存入金币。
    // 原先散在 InventoryHandler.Trade.cs 的裸SQL, 下沉为 store 方法; handler 只留解析+ACK。
    public sealed partial class SqliteInventoryStore
    {
        private const int CargoInitialCapacity = 1;
        private const int AccountCargoCreateGoldCost = 100000;
        private const int AccountCargoUpgradeVoidMagicStoneItemId = 3299;
        private const int AccountCargoUpgradeVoidMagicStoneCost = 250;
        // Normal UI commands pay here; mall/tool upgrades call the core overload
        // because purchasing or consuming the upgrade item is already the cost.
        private static readonly int[] CargoCapacityTiers = { 1, 8, 16, 24, 32, 40, 48, 56, 64 };
        private static readonly ushort[] PersonalCargoCapacityTiers =
        {
            24, 40, 56, 72, 88, 104, 120, 136, 152
        };
        private static readonly Regex LegacyPersonalCargoUpgradePathRegex = new Regex(
            @"(?:^|/)safe_upgradekit(?<tier>\d*)\.stk$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static readonly Regex PersonalCargoCapacityTextRegex = new Regex(
            @"(?<capacity>\d+)\s*格",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        // 存款: 角色金币条件扣减 + 金库原子增量, 同一事务。
        // 任一步失败(余额不足/金库未开通)整体回滚返回 false。
        public bool TryDepositCargoGold(int characterId, int accountId, int amount, out int newCharGold, out int newCargoGold)
        {
            newCharGold = 0;
            newCargoGold = 0;
            if (amount <= 0)
                return false;

            using (var conn = new SqliteConnection(ConnectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    var wallet = CurrencyService.LoadWallet(conn, tx, characterId);
                    int cargoGold = LoadCargoStateField(conn, tx, accountId, "value32");

                    if (!CurrencyService.TrySpendGold(conn, tx, characterId, amount) ||
                        !TryAddCargoGold(conn, tx, accountId, amount))
                        return false;

                    newCharGold = wallet.Gold - amount;
                    newCargoGold = cargoGold + amount;
                    tx.Commit();
                    return true;
                }
            }
        }

        // 取出: 金库条件扣减 + 角色金币原子增量, 同一事务。
        public bool TryWithdrawCargoGold(int characterId, int accountId, int amount, out int newCharGold, out int newCargoGold)
        {
            newCharGold = 0;
            newCargoGold = 0;
            if (amount <= 0)
                return false;

            using (var conn = new SqliteConnection(ConnectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    var wallet = CurrencyService.LoadWallet(conn, tx, characterId);
                    int cargoGold = LoadCargoStateField(conn, tx, accountId, "value32");

                    if (!TrySpendCargoGold(conn, tx, accountId, amount))
                        return false;
                    CurrencyService.GrantGold(conn, tx, characterId, amount);

                    newCharGold = wallet.Gold + amount;
                    newCargoGold = cargoGold - amount;
                    tx.Commit();
                    return true;
                }
            }
        }

        // 开通金库(初始容量档位)。已开通返回 false。
        public bool TryCreateAccountCargo(int characterId, int accountId, out InventoryMutationResult costResult, out byte errorCode)
        {
            costResult = null;
            errorCode = 0;
            using (var conn = new SqliteConnection(ConnectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    int existing = LoadCargoStateField(conn, tx, accountId, "selection_key");
                    if (existing > 0)
                    {
                        errorCode = 0x14;
                        return false;
                    }

                    var wallet = CurrencyService.LoadWallet(conn, tx, characterId);
                    if (!CurrencyService.TrySpendGold(conn, tx, characterId, AccountCargoCreateGoldCost))
                    {
                        errorCode = 0x14;
                        return false;
                    }

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = @"
INSERT OR REPLACE INTO account_cargo_state (account_id, selection_key, value32, updated_at)
VALUES (@aid, @cap, 0, CURRENT_TIMESTAMP);";
                        cmd.Parameters.AddWithValue("@aid", accountId);
                        cmd.Parameters.AddWithValue("@cap", CargoInitialCapacity);
                        cmd.ExecuteNonQuery();
                    }

                    var newGold = wallet.Gold - AccountCargoCreateGoldCost;
                    costResult = new InventoryMutationResult
                    {
                        ListType = InventoryListType.Main,
                        SlotIndex = 0,
                        ItemTemplateId = 0,
                        RemainingStackCount = newGold,
                        InstanceValue = newGold,
                        UpdatedGold = newGold,
                        UpdatedSp = wallet.Sp,
                        UpdatedCoin = wallet.Cera,
                        GoldSpent = true,
                    };
                    tx.Commit();
                    return true;
                }
            }
        }

        // 升级容量档位。errorCode: 0x15=未开通, 0x13=已满级。
        public bool TryUpgradeAccountCargo(int characterId, int accountId, out InventoryMutationResult costResult, out byte errorCode)
        {
            costResult = null;
            errorCode = 0;
            using (var conn = new SqliteConnection(ConnectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    if (!TryUpgradeAccountCargoCore(
                            conn,
                            tx,
                            accountId,
                            (int previousSelectionKey, int newSelectionKey, out InventoryMutationResult resolvedCostResult, out byte resolvedErrorCode) =>
                                TryApplyAccountCargoUpgradeCost(conn, tx, characterId, previousSelectionKey, newSelectionKey, out resolvedCostResult, out resolvedErrorCode),
                            out _,
                            out _,
                            out costResult,
                            out errorCode))
                        return false;

                    tx.Commit();
                    return true;
                }
            }
        }

        internal static bool TryUpgradeAccountCargoCore(
            SqliteConnection conn,
            SqliteTransaction tx,
            int accountId,
            out int previousSelectionKey,
            out int newSelectionKey,
            out byte errorCode)
        {
            return TryUpgradeAccountCargoCore(
                conn,
                tx,
                accountId,
                null,
                out previousSelectionKey,
                out newSelectionKey,
                out _,
                out errorCode);
        }

        private delegate bool AccountCargoCostApplier(
            int previousSelectionKey,
            int newSelectionKey,
            out InventoryMutationResult costResult,
            out byte errorCode);

        private static bool TryUpgradeAccountCargoCore(
            SqliteConnection conn,
            SqliteTransaction tx,
            int accountId,
            AccountCargoCostApplier costApplier,
            out int previousSelectionKey,
            out int newSelectionKey,
            out InventoryMutationResult costResult,
            out byte errorCode)
        {
            previousSelectionKey = LoadCargoStateField(conn, tx, accountId, "selection_key");
            newSelectionKey = previousSelectionKey;
            costResult = null;
            errorCode = 0;

            if (previousSelectionKey <= 0)
            {
                errorCode = 0x15;
                return false;
            }

            int nextTierIndex = Array.IndexOf(CargoCapacityTiers, previousSelectionKey) + 1;
            if (nextTierIndex <= 0 || nextTierIndex >= CargoCapacityTiers.Length)
            {
                errorCode = 0x13;
                return false;
            }

            newSelectionKey = CargoCapacityTiers[nextTierIndex];
            if (costApplier != null && !costApplier(previousSelectionKey, newSelectionKey, out costResult, out errorCode))
                return false;

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE account_cargo_state SET selection_key=@cap, updated_at=CURRENT_TIMESTAMP WHERE account_id=@aid;";
                cmd.Parameters.AddWithValue("@cap", newSelectionKey);
                cmd.Parameters.AddWithValue("@aid", accountId);
                cmd.ExecuteNonQuery();
            }

            return true;
        }

        private static bool TryApplyAccountCargoUpgradeCost(
            SqliteConnection conn,
            SqliteTransaction tx,
            int characterId,
            int previousSelectionKey,
            int newSelectionKey,
            out InventoryMutationResult costResult,
            out byte errorCode)
        {
            costResult = null;
            errorCode = 0;
            var wallet = CurrencyService.LoadWallet(conn, tx, characterId);

            if (TryResolveAccountCargoUpgradeGoldCost(previousSelectionKey, out var goldCost))
            {
                if (!CurrencyService.TrySpendGold(conn, tx, characterId, goldCost))
                {
                    errorCode = 0x14;
                    return false;
                }

                var newGold = wallet.Gold - goldCost;
                costResult = new InventoryMutationResult
                {
                    ListType = InventoryListType.Main,
                    SlotIndex = 0,
                    ItemTemplateId = 0,
                    RemainingStackCount = newGold,
                    InstanceValue = newGold,
                    UpdatedGold = newGold,
                    UpdatedSp = wallet.Sp,
                    UpdatedCoin = wallet.Cera,
                    GoldSpent = true,
                };
                return true;
            }

            if (TryResolveAccountCargoUpgradeCeraCost(previousSelectionKey, out var ceraCost))
            {
                if (!CurrencyService.TrySpendCera(conn, tx, characterId, ceraCost))
                {
                    errorCode = 0x14;
                    return false;
                }

                costResult = new InventoryMutationResult
                {
                    UpdatedGold = wallet.Gold,
                    UpdatedSp = wallet.Sp,
                    UpdatedCoin = wallet.Cera - ceraCost,
                    UpdatedTokenCera = wallet.TokenCera,
                    UpdatedHappyTokenCera = wallet.HappyTokenCera,
                };
                return true;
            }

            if (TryResolveAccountCargoUpgradeMaterialCost(previousSelectionKey, out var materialItemTemplateId, out var materialCount))
            {
                var material = InventoryDbPrimitives.RemoveItemByTemplateId(conn, tx, characterId, materialItemTemplateId, materialCount);
                if (material == null)
                {
                    errorCode = 0x14;
                    return false;
                }

                costResult = new InventoryMutationResult
                {
                    ListType = InventoryListType.Main,
                    SlotIndex = material.Value.SlotIndex,
                    ItemTemplateId = materialItemTemplateId,
                    RemainingStackCount = material.Value.RemainingCount,
                    InstanceValue = material.Value.RemainingCount,
                    UpdatedGold = wallet.Gold,
                    UpdatedSp = wallet.Sp,
                    UpdatedCoin = wallet.Cera,
                    CostItemTemplateId = materialItemTemplateId,
                    CostItemNewStackCount = material.Value.RemainingCount,
                    CostItemSlotIndex = material.Value.SlotIndex,
                    RequestedCount = (short)Math.Min(short.MaxValue, materialCount),
                    AppliedCount = (short)Math.Min(short.MaxValue, material.Value.RemovedCount),
                };
                return true;
            }

            return true;
        }

        private static bool TryResolveAccountCargoUpgradeGoldCost(int previousSelectionKey, out int goldCost)
        {
            goldCost = previousSelectionKey == 1 ? 2000000 : 0;
            return goldCost > 0;
        }

        private static bool TryResolveAccountCargoUpgradeCeraCost(int previousSelectionKey, out int ceraCost)
        {
            ceraCost = previousSelectionKey switch
            {
                8 => 2000,
                32 => 2000,
                40 => 2500,
                48 => 3000,
                56 => 5000,
                _ => 0,
            };
            return ceraCost > 0;
        }

        private static bool TryResolveAccountCargoUpgradeMaterialCost(int previousSelectionKey, out int itemTemplateId, out int count)
        {
            itemTemplateId = 0;
            count = 0;
            if (previousSelectionKey != 16 && previousSelectionKey != 24)
                return false;

            itemTemplateId = AccountCargoUpgradeVoidMagicStoneItemId;
            count = AccountCargoUpgradeVoidMagicStoneCost;
            return true;
        }

        public bool TryUpgradePersonalCargo(int characterId, int accountId, out ushort newListParam16, out byte errorCode)
        {
            newListParam16 = 0;
            errorCode = 0;

            using (var conn = new SqliteConnection(ConnectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    var current = LoadPersonalCargoListParam16(conn, tx, characterId, accountId);
                    if (!TryGetNextPersonalCargoTier(current, out newListParam16))
                    {
                        errorCode = 0x13;
                        return false;
                    }

                    SavePersonalCargoListParam16(conn, tx, characterId, accountId, newListParam16);
                    tx.Commit();
                    return true;
                }
            }
        }

        public bool TryUsePersonalCargoUpgradeTicket(
            int characterId,
            int accountId,
            InventoryListType listType,
            short slotIndex,
            int expectedItemTemplateId,
            out PersonalCargoUpgradeTicketResult result)
        {
            result = new PersonalCargoUpgradeTicketResult
            {
                Status = PersonalCargoUpgradeTicketStatus.NotApplicable,
                ListType = listType,
                SlotIndex = slotIndex,
                ItemTemplateId = expectedItemTemplateId,
            };

            if (!IsSupportedDeleteOrSellListType(listType))
                return false;

            using (var conn = new SqliteConnection(ConnectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    var dbListType = MapToDbListType(listType);
                    var item = _db.LoadItemRecord(conn, tx, characterId, dbListType, slotIndex);
                    if (item == null)
                    {
                        if (!IsPersonalCargoUpgradeTicket(expectedItemTemplateId)
                            && !TryResolvePersonalCargoUpgradeTarget(expectedItemTemplateId, out _))
                            return false;

                        result.Status = PersonalCargoUpgradeTicketStatus.MissingItem;
                        return true;
                    }

                    result.ItemTemplateId = item.ItemTemplateId;

                    var hasExplicitTarget = TryResolvePersonalCargoUpgradeTarget(item.ItemTemplateId, out var targetListParam16);
                    if (!hasExplicitTarget && !IsPersonalCargoUpgradeTicket(item.ItemTemplateId))
                        return false;

                    if (IsEquipmentItemLocked(conn, tx, characterId, item))
                    {
                        result.Status = PersonalCargoUpgradeTicketStatus.Locked;
                        return true;
                    }

                    if (IsStackCountedRecord(item) && item.StackCount <= 0)
                    {
                        result.Status = PersonalCargoUpgradeTicketStatus.MissingItem;
                        return true;
                    }

                    var previous = LoadPersonalCargoListParam16(conn, tx, characterId, accountId);
                    ushort next;
                    if (hasExplicitTarget)
                    {
                        targetListParam16 = NormalizePersonalCargoListParam(targetListParam16);
                        if (!IsPersonalCargoCapacityTier(targetListParam16) || targetListParam16 <= previous)
                        {
                            result.Status = PersonalCargoUpgradeTicketStatus.Maxed;
                            result.PreviousListParam16 = previous;
                            result.NewListParam16 = previous;
                            return true;
                        }

                        next = targetListParam16;
                    }
                    else if (!TryGetNextPersonalCargoTier(previous, out next))
                    {
                        result.Status = PersonalCargoUpgradeTicketStatus.Maxed;
                        result.PreviousListParam16 = previous;
                        result.NewListParam16 = previous;
                        return true;
                    }

                    if (!TryDeleteItemCore(conn, tx, characterId, listType, dbListType, slotIndex, 1, out var consumed))
                    {
                        result.Status = PersonalCargoUpgradeTicketStatus.MissingItem;
                        return true;
                    }

                    SavePersonalCargoListParam16(conn, tx, characterId, accountId, next);
                    tx.Commit();

                    result.Status = PersonalCargoUpgradeTicketStatus.Upgraded;
                    result.PreviousListParam16 = previous;
                    result.NewListParam16 = next;
                    result.ConsumedItem = consumed;
                    result.ItemTemplateId = item.ItemTemplateId;
                    return true;
                }
            }
        }

        internal static bool TryUpgradePersonalCargoToCapacityCore(
            SqliteConnection conn,
            SqliteTransaction tx,
            int characterId,
            int accountId,
            ushort targetListParam16,
            out ushort previousListParam16,
            out ushort newListParam16,
            out byte errorCode)
        {
            previousListParam16 = LoadPersonalCargoListParam16Core(conn, tx, characterId);
            newListParam16 = previousListParam16;
            errorCode = 0;

            targetListParam16 = NormalizePersonalCargoListParam(targetListParam16);
            if (!IsPersonalCargoCapacityTier(targetListParam16) || targetListParam16 <= previousListParam16)
            {
                errorCode = 0x13;
                return false;
            }

            SavePersonalCargoListParam16Core(conn, tx, characterId, targetListParam16);
            newListParam16 = targetListParam16;
            return true;
        }

        public bool TryUseAccountCargoUpgradeTool(
            int characterId,
            int accountId,
            InventoryListType listType,
            short slotIndex,
            out AccountCargoUpgradeToolResult result)
        {
            result = new AccountCargoUpgradeToolResult
            {
                Status = AccountCargoUpgradeToolStatus.NotApplicable,
                ListType = listType,
                SlotIndex = slotIndex,
            };

            if (!IsSupportedDeleteOrSellListType(listType))
                return false;

            using (var conn = new SqliteConnection(ConnectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    var dbListType = MapToDbListType(listType);
                    var item = _db.LoadItemRecord(conn, tx, characterId, dbListType, slotIndex);
                    if (item == null)
                        return false;

                    result.ItemTemplateId = item.ItemTemplateId;
                    if (!IsAccountCargoUpgradeToolItem(item.ItemTemplateId))
                        return false;

                    if (IsEquipmentItemLocked(conn, tx, characterId, item))
                    {
                        result.Status = AccountCargoUpgradeToolStatus.Locked;
                        return true;
                    }

                    if (IsStackCountedRecord(item) && item.StackCount <= 0)
                    {
                        result.Status = AccountCargoUpgradeToolStatus.MissingItem;
                        return true;
                    }

                    if (!TryUpgradeAccountCargoCore(conn, tx, accountId, out var previousSelectionKey, out var newSelectionKey, out var errorCode))
                    {
                        result.Status = errorCode == 0x15
                            ? AccountCargoUpgradeToolStatus.NotOpened
                            : AccountCargoUpgradeToolStatus.Maxed;
                        result.PreviousSelectionKey = previousSelectionKey;
                        result.NewSelectionKey = previousSelectionKey;
                        return true;
                    }

                    if (!TryDeleteItemCore(conn, tx, characterId, listType, dbListType, slotIndex, 1, out var consumed))
                    {
                        result.Status = AccountCargoUpgradeToolStatus.MissingItem;
                        result.PreviousSelectionKey = previousSelectionKey;
                        result.NewSelectionKey = previousSelectionKey;
                        return true;
                    }

                    tx.Commit();

                    result.Status = AccountCargoUpgradeToolStatus.Upgraded;
                    result.PreviousSelectionKey = previousSelectionKey;
                    result.NewSelectionKey = newSelectionKey;
                    result.ConsumedItem = consumed;
                    result.ItemTemplateId = item.ItemTemplateId;
                    return true;
                }
            }
        }

        private ushort LoadPersonalCargoListParam16(
            SqliteConnection conn,
            SqliteTransaction tx,
            int characterId,
            int accountId)
        {
            var states = _equipStore.LoadContainerState(conn, tx, characterId, accountId);
            return states.TryGetValue(InventoryListType.PersonalCargo, out var current)
                ? NormalizePersonalCargoListParam(current)
                : DefaultPersonalCargoCapacity;
        }

        private void SavePersonalCargoListParam16(
            SqliteConnection conn,
            SqliteTransaction tx,
            int characterId,
            int accountId,
            ushort listParam16)
        {
            _equipStore.UpsertContainerState(conn, tx, characterId, accountId, InventoryListType.PersonalCargo, listParam16);
        }

        private static bool TryGetNextPersonalCargoTier(ushort current, out ushort nextTier)
        {
            current = NormalizePersonalCargoListParam(current);
            foreach (var tier in PersonalCargoCapacityTiers)
            {
                if (tier > current)
                {
                    nextTier = tier;
                    return true;
                }
            }

            nextTier = current;
            return false;
        }

        private static bool IsPersonalCargoCapacityTier(ushort capacity)
        {
            foreach (var tier in PersonalCargoCapacityTiers)
            {
                if (tier == capacity)
                    return true;
            }

            return false;
        }

        internal static ushort NormalizePersonalCargoListParam(ushort listParam16)
        {
            return listParam16 == 0 ? DefaultPersonalCargoCapacity : listParam16;
        }

        internal static bool TryResolvePersonalCargoUpgradeTarget(int itemTemplateId, out ushort targetListParam16)
        {
            targetListParam16 = 0;
            if (itemTemplateId <= 0)
                return false;

            var entry = ItemMetadataResolver.GetStackableEntry(itemTemplateId);
            var path = NormalizeStackablePath(entry?.FilePath);
            if (string.IsNullOrWhiteSpace(path))
                return false;

            if (path.StartsWith("cash/chn_account_cargo/", StringComparison.OrdinalIgnoreCase))
                return false;

            if (TryResolvePersonalCargoUpgradeTargetFromPath(path, out targetListParam16))
                return true;

            var stackable = InventoryDbPrimitives.LoadStackableItem(itemTemplateId);
            if (!LooksLikePersonalCargoUpgradeTool(stackable?.Name, stackable?.Explain))
                return false;

            return TryResolvePersonalCargoUpgradeTargetFromText(stackable.Explain, out targetListParam16);
        }

        private static bool TryResolvePersonalCargoUpgradeTargetFromPath(string path, out ushort targetListParam16)
        {
            targetListParam16 = 0;
            var match = LegacyPersonalCargoUpgradePathRegex.Match(path);
            if (!match.Success)
                return false;

            var tierText = match.Groups["tier"].Value;
            var tierIndex = 0;
            if (!string.IsNullOrWhiteSpace(tierText))
            {
                if (!int.TryParse(tierText, out var oneBasedTier) || oneBasedTier <= 0)
                    return false;
                tierIndex = oneBasedTier - 1;
            }

            if (tierIndex < 0 || tierIndex >= PersonalCargoCapacityTiers.Length)
                return false;

            targetListParam16 = PersonalCargoCapacityTiers[tierIndex];
            return true;
        }

        private static bool TryResolvePersonalCargoUpgradeTargetFromText(string text, out ushort targetListParam16)
        {
            targetListParam16 = 0;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            foreach (Match match in PersonalCargoCapacityTextRegex.Matches(text))
            {
                if (!ushort.TryParse(match.Groups["capacity"].Value, out var capacity))
                    continue;

                if (!IsPersonalCargoCapacityTier(capacity))
                    continue;

                targetListParam16 = capacity;
                return true;
            }

            return false;
        }

        private static bool LooksLikePersonalCargoUpgradeTool(string name, string explain)
        {
            var text = ((name ?? string.Empty) + " " + (explain ?? string.Empty)).Replace("`", string.Empty);
            if (text.IndexOf("账号金库", StringComparison.Ordinal) >= 0
                || text.IndexOf("帐号金库", StringComparison.Ordinal) >= 0)
                return false;

            return text.IndexOf("金库", StringComparison.Ordinal) >= 0
                && (text.IndexOf("升级", StringComparison.Ordinal) >= 0
                    || text.IndexOf("增加", StringComparison.Ordinal) >= 0
                    || text.IndexOf("空间", StringComparison.Ordinal) >= 0);
        }

        private static string NormalizeStackablePath(string path)
        {
            return string.IsNullOrWhiteSpace(path)
                ? string.Empty
                : path.Replace('\\', '/').Trim();
        }

        private static bool IsPersonalCargoUpgradeTicket(int itemTemplateId)
        {
            if (itemTemplateId <= 0)
                return false;

            var stackable = InventoryDbPrimitives.LoadStackableItem(itemTemplateId);
            if (stackable == null)
                return false;

            return string.Equals(
                NormalizeStackableTag(stackable.ActionTypeName),
                "upgrade cargo",
                StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsAccountCargoUpgradeToolItem(int itemTemplateId)
        {
            if (itemTemplateId <= 0)
                return false;

            var entry = ItemMetadataResolver.GetStackableEntry(itemTemplateId);
            var path = entry?.FilePath;
            if (string.IsNullOrWhiteSpace(path))
                return false;

            return path.Replace('\\', '/')
                .StartsWith("cash/chn_account_cargo/account_cargo", StringComparison.OrdinalIgnoreCase);
        }

        private static ushort LoadPersonalCargoListParam16Core(SqliteConnection conn, SqliteTransaction tx, int characterId)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
SELECT list_param16
FROM character_container_state
WHERE character_id=@cid AND list_type=@listType;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@listType", (int)InventoryListType.PersonalCargo);
                var value = cmd.ExecuteScalar();
                if (value == null || value == DBNull.Value)
                    return DefaultPersonalCargoCapacity;

                return NormalizePersonalCargoListParam(Convert.ToUInt16(Convert.ToInt32(value)));
            }
        }

        private static void SavePersonalCargoListParam16Core(SqliteConnection conn, SqliteTransaction tx, int characterId, ushort listParam16)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
INSERT OR REPLACE INTO character_container_state (character_id, list_type, list_param16)
VALUES (@cid, @listType, @listParam16);";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@listType", (int)InventoryListType.PersonalCargo);
                cmd.Parameters.AddWithValue("@listParam16", listParam16);
                cmd.ExecuteNonQuery();
            }
        }

        private static string NormalizeStackableTag(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            return value
                .Replace("`", string.Empty)
                .Trim()
                .Trim('[', ']')
                .Trim();
        }

        private static int LoadCargoStateField(SqliteConnection conn, SqliteTransaction tx, int accountId, string column)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = $"SELECT {column} FROM account_cargo_state WHERE account_id=@aid;";
                cmd.Parameters.AddWithValue("@aid", accountId);
                var result = cmd.ExecuteScalar();
                return result != null && result != DBNull.Value ? Convert.ToInt32(result) : 0;
            }
        }

        // 金库入账: 原子增量; 金库行不存在(未开通)命中0行返回false
        private static bool TryAddCargoGold(SqliteConnection conn, SqliteTransaction tx, int accountId, int amount)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE account_cargo_state SET value32=value32+@amt, updated_at=CURRENT_TIMESTAMP WHERE account_id=@aid;";
                cmd.Parameters.AddWithValue("@amt", amount);
                cmd.Parameters.AddWithValue("@aid", accountId);
                return cmd.ExecuteNonQuery() > 0;
            }
        }

        // 金库取出: 条件扣减, 余额不足或未开通返回false
        private static bool TrySpendCargoGold(SqliteConnection conn, SqliteTransaction tx, int accountId, int amount)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE account_cargo_state SET value32=value32-@amt, updated_at=CURRENT_TIMESTAMP WHERE account_id=@aid AND value32>=@amt;";
                cmd.Parameters.AddWithValue("@amt", amount);
                cmd.Parameters.AddWithValue("@aid", accountId);
                return cmd.ExecuteNonQuery() > 0;
            }
        }
    }
}
