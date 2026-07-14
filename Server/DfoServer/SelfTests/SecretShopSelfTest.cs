using DfoServer.Game.SecretShop;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using DfoServer.Network.Parsers;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace DfoServer.SelfTests
{
    public static class SecretShopSelfTest
    {
        private static int _pass;
        private static int _fail;

        public static int Run()
        {
            _pass = 0;
            _fail = 0;
            Console.WriteLine("=== SECRET_SHOP selftest ===");

            var catalog = SecretShopCatalog.Parse(SyntheticSecretShop);
            Check("global price random parsed", catalog.PriceRandomPercent == 10);
            Check("cash-user stackable count parsed", catalog.CashUserStackableCount == 200);
            Check("level section 5 parsed",
                catalog.LevelSections.TryGetValue(5, out var section)
                && section.MinimumLevel == 56
                && section.MaximumLevel == 65);

            var dungeonWeights = catalog.ResolveNpcWeights(9000, 60, 1);
            Check("dungeon npc weights take precedence",
                dungeonWeights.Count == 2
                && dungeonWeights[0].NpcId == 1002
                && dungeonWeights.Sum(x => x.Weight) == 10000);

            var levelWeights = catalog.ResolveNpcWeights(9001, 60, 1);
            Check("level npc weights are the fallback",
                levelWeights.Count == 2
                && levelWeights.Single(x => x.NpcId == 1002).Weight == 8000
                && levelWeights.Sum(x => x.Weight) == 10000);

            var dungeonPool = catalog.ResolvePool(1002, 9000, 60, useCashItems: false);
            Check("dungeon item pool takes precedence",
                dungeonPool != null
                && dungeonPool.Source == SecretShopPoolSource.Dungeon
                && dungeonPool.SelectionCount == 1
                && dungeonPool.Candidates.Single().ItemId == 440016);

            var levelPool = catalog.ResolvePool(1002, 9001, 60, useCashItems: false);
            Check("level item pool preserves PVF price and count",
                levelPool != null
                && levelPool.Source == SecretShopPoolSource.Level
                && levelPool.SelectionCount == 2
                && levelPool.Candidates.Count == 2
                && levelPool.Candidates.All(x => x.Price == 302505 && x.Count == 1));

            var cashPool = catalog.ResolvePool(1002, 9001, 60, useCashItems: true);
            Check("cash item pool stays independent from level pool",
                cashPool != null
                && cashPool.Source == SecretShopPoolSource.CashItem
                && cashPool.Candidates.Count == 3
                && levelPool.Candidates.Count == 2);

            var pickedNpc = SecretShopSelector.SelectNpc(levelWeights, _ => 0);
            Check("weighted npc selector uses bounded roll", pickedNpc == 1002);

            var pickedItems = SecretShopSelector.SelectItems(levelPool, _ => 0);
            Check("weighted item selection is without replacement",
                pickedItems.Count == 2
                && pickedItems.Select(x => x.ItemId).Distinct().Count() == 2);

            var twoItemOffer = new SecretShopOffer(1002, pickedItems);
            var twoItemBody = SecretShopItemListBodyBuilder.Build(twoItemOffer);
            Check("two item-list rows stay aligned at 22 bytes each",
                twoItemBody.Length == 48
                && BitConverter.ToInt32(twoItemBody, 0) == 2
                && BitConverter.ToInt32(twoItemBody, 4) == pickedItems[0].ItemId
                && BitConverter.ToInt32(twoItemBody, 26) == pickedItems[1].ItemId);

            var clearPackets = SecretShopClearPacketBuilder.Build(twoItemOffer);
            Check("dungeon clear sends npc context before item list",
                clearPackets.Count == 2
                && BitConverter.ToUInt16(clearPackets[0], 1) == 0x0117
                && BitConverter.ToInt32(clearPackets[0], 15) == 1002
                && BitConverter.ToUInt16(clearPackets[1], 1) == 0x0118
                && clearPackets[1].Skip(15).SequenceEqual(twoItemBody));

            var noShopPackets = SecretShopClearPacketBuilder.Build(
                new SecretShopOffer(1000, Array.Empty<SecretShopItemCandidate>()));
            Check("no-shop result sends only npc context reset",
                noShopPackets.Count == 1
                && BitConverter.ToUInt16(noShopPackets[0], 1) == 0x0117
                && BitConverter.ToInt32(noShopPackets[0], 15) == 1000);

            var offer = SecretShopOfferFactory.Create(
                catalog, dungeonId: 9000, dungeonBasisLevel: 60, partySize: 1, next: _ => 0);
            Check("offer snapshots selected npc and PVF row",
                offer.NpcId == 1002
                && offer.Items.Count == 1
                && offer.Items[0].ItemId == 440016
                && offer.Items[0].Price == 302505
                && offer.Items[0].Count == 1);

            Check("gold item-list row matches the client 22-byte layout",
                SecretShopItemListBodyBuilder.Build(offer)
                    .SequenceEqual(new byte[]
                    {
                        1, 0, 0, 0,
                        0xD0, 0xB6, 0x06, 0x00,
                        0,
                        0xA9, 0x9D, 0x04, 0x00,
                        1, 0, 0, 0,
                        0, 0, 0, 0,
                        0, 0, 0, 0,
                        0,
                    }));

            var materialCurrencyOffer = new SecretShopOffer(1002, new[]
            {
                new SecretShopItemCandidate
                {
                    ItemId = 22163,
                    RawFlag = 1,
                    Price = 3300,
                    RequiredItemId = 600,
                    Count = 1,
                    Weight = 50,
                },
            });
            Check("material-currency item-list row matches the client 22-byte layout",
                SecretShopItemListBodyBuilder.Build(materialCurrencyOffer)
                    .SequenceEqual(new byte[]
                    {
                        1, 0, 0, 0,
                        0x93, 0x56, 0x00, 0x00,
                        1,
                        0, 0, 0, 0,
                        1, 0, 0, 0,
                        0x58, 0x02, 0x00, 0x00,
                        0xE4, 0x0C, 0x00, 0x00,
                        0,
                    }));
            Check("offer item can be sold only once",
                offer.MarkSold(440016) && !offer.MarkSold(440016));
            Check("sold items disappear from item-list packet",
                SecretShopItemListBodyBuilder.Build(offer).SequenceEqual(new byte[4]));

            Check("open-close request accepts exact one-byte values",
                SecretShopOpenCloseRequest.TryParse(new byte[] { 1 }, out var open) && open
                && SecretShopOpenCloseRequest.TryParse(new byte[] { 0 }, out open) && !open);
            Check("open-close request rejects malformed bodies",
                !SecretShopOpenCloseRequest.TryParse(Array.Empty<byte>(), out _)
                && !SecretShopOpenCloseRequest.TryParse(new byte[] { 1, 0 }, out _)
                && !SecretShopOpenCloseRequest.TryParse(new byte[] { 2 }, out _));

            Check("captured equipment buy request parses exact item and implicit single count",
                SecretShopBuyRequest.TryParse(
                    new byte[] { 0x10, 0x0C, 0x52, 0x06, 0, 0, 0, 0 },
                    out var buyRequest)
                && buyRequest.ItemId == 106040336
                && buyRequest.RequestedCount == 0);
            Check("buy request rejects short, trailing, and negative-count bodies",
                !SecretShopBuyRequest.TryParse(new byte[7], out _)
                && !SecretShopBuyRequest.TryParse(new byte[9], out _)
                && !SecretShopBuyRequest.TryParse(
                    new byte[] { 0x10, 0x0C, 0x52, 0x06, 0xFF, 0xFF, 0xFF, 0xFF },
                    out _));
            Check("captured material buy request preserves selected quantity",
                SecretShopBuyRequest.TryParse(
                    new byte[] { 0xDD, 0x0B, 0, 0, 0xC8, 0, 0, 0 },
                    out var materialBuyRequest)
                && materialBuyRequest.ItemId == 3037
                && materialBuyRequest.RequestedCount == 200);

            var run = new DungeonRun(9000, 0);
            Check("fresh dungeon run has no secret-shop offer", run.SecretShopOffer == null);
            run.SecretShopOffer = offer;
            var nextRun = new DungeonRun(9000, 0);
            Check("new dungeon run does not inherit prior offer", nextRun.SecretShopOffer == null);

            VerifyRealPvf();
            VerifyPurchaseTransaction();
            VerifyNonStackableMultiCountFailsClosed();
            VerifyPurchaseResponseSequence();
            VerifyItemCurrencyPurchaseResponseSequence();
            VerifyMaterialPurchaseResponseSequence();

            PrintSummary();
            return _fail == 0 ? 0 : 1;
        }

        private static void Check(string label, bool ok)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");
            if (ok) _pass++; else _fail++;
        }

        private static void PrintSummary()
            => Console.WriteLine($"=== result: {_pass} PASS, {_fail} FAIL ===");

        private static void VerifyRealPvf()
        {
            var catalog = SecretShopCatalog.Parse(PvfArchiveAccessor.ReadText("etc/secretshop.etc"));
            var level5NpcWeights = catalog.ResolveNpcWeights(-1, 60, 1);
            Check("real PVF level-5 npc weights sum to 10000", level5NpcWeights.Sum(x => x.Weight) == 10000);

            var gabriel = catalog.ResolvePool(1002, -1, 60, useCashItems: false);
            var support440003 = gabriel?.Candidates.SingleOrDefault(x => x.ItemId == 440003);
            var support440016 = gabriel?.Candidates.SingleOrDefault(x => x.ItemId == 440016);
            Check("real PVF Gabriel section 5 selects two items", gabriel?.SelectionCount == 2);
            Check("real PVF support items keep 302505 price and count one",
                support440003?.Price == 302505 && support440003.Count == 1
                && support440016?.Price == 302505 && support440016.Count == 1);

            var material = catalog.ResolvePool(1003, -1, 60, useCashItems: false);
            var crystal3033 = material?.Candidates.SingleOrDefault(x => x.ItemId == 3033);
            Check("real PVF material shop selects two products", material?.SelectionCount == 2);
            Check("real PVF material 3033 keeps price 100 and count 100",
                crystal3033?.Price == 100 && crystal3033.Count == 100);

            var ancient = catalog.ResolvePool(1004, -1, 75, useCashItems: false);
            var ancient10088490 = ancient?.Candidates.SingleOrDefault(x => x.ItemId == 10088490);
            Check("real PVF ancient shop selects one product", ancient?.SelectionCount == 1);
            Check("real PVF ancient item keeps price 300000 and count one",
                ancient10088490?.Price == 300000 && ancient10088490.Count == 1);

            var ancientCash = catalog.ResolvePool(1004, -1, 75, useCashItems: true);
            Check("real PVF cash pool is parsed but not merged",
                ancient != null && ancientCash != null
                && ancientCash.Candidates.Count == ancient.Candidates.Count + 1
                && ancientCash.Candidates.Any(x => x.ItemId == 3331)
                && ancient.Candidates.All(x => x.ItemId != 3331));
        }

        private static void VerifyPurchaseTransaction()
        {
            const int accountId = 943000;
            const int characterId = 943001;
            const int itemId = 440003;
            const int price = 302505;
            var tempDb = Path.Combine(Path.GetTempPath(), "secret_shop_selftest.db");
            DeleteTempDatabase(tempDb);
            var connectionString = SqliteDatabaseBootstrap.Initialize(tempDb, ServerPaths.SchemaFilePath);

            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (@accountId, 'secret-shop-selftest', '');
INSERT INTO characters (character_id, account_id, name)
VALUES (@characterId, @accountId, 'secret-shop-selftest');
INSERT INTO character_items (
    owner_scope, owner_id, character_id, list_type, slot_index, item_template_id, item_kind,
    stack_count, instance_value, durability, seal_flag, option_value, expire_time, marker_16,
    pet_serial_or_handle, extra_json)
VALUES (
    'character', @characterId, @characterId, 0, 0, 0, 'special',
    @gold, @gold, 0, 0, 0, 0, 0,
    0, '{}');";
                command.Parameters.AddWithValue("@accountId", accountId);
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@gold", price - 1);
                command.ExecuteNonQuery();
            }

            var offer = new SecretShopOffer(1002, new[]
            {
                new SecretShopItemCandidate
                {
                    ItemId = itemId,
                    RawFlag = 0,
                    Price = price,
                    RequiredItemId = 0,
                    Count = 1,
                    Weight = 1,
                },
            });
            var store = new SqliteInventoryStore(tempDb, ServerPaths.SchemaFilePath);
            var assets = new SqliteAssetService(tempDb, ServerPaths.SchemaFilePath, store);
            var service = new SecretShopPurchaseService(store);

            Check("insufficient-gold secret-shop purchase fails",
                !service.TryPurchase(characterId, accountId, offer, itemId, 0, out _));
            Check("failed purchase keeps offer available",
                offer.TryGetAvailableItem(itemId, out _));
            Check("failed purchase rolls back item and gold",
                LoadGoldAndItemCount(connectionString, characterId, itemId) == (price - 1, 0));

            using (var scope = assets.OpenScope(characterId, accountId))
            {
                assets.GrantGold(scope, 1);
                scope.Commit();
            }

            Check("secret-shop purchase commits PVF price and item",
                service.TryPurchase(characterId, accountId, offer, itemId, 0, out var result)
                && result.ItemId == itemId
                && result.ItemCount == 1
                && result.GoldCost == price
                && result.UpdatedGold == 0);
            if (result != null)
            {
                var ack = SecretShopBuyAckBuilder.BuildSuccess(result);
                Check("secret-shop success ACK matches client 30-byte layout",
                    ack.Length == 30
                    && ack[0] == 1
                    && BitConverter.ToInt32(ack, 1) == result.UpdatedGold
                    && BitConverter.ToUInt16(ack, 5) == unchecked((ushort)result.AssignedSlot)
                    && BitConverter.ToInt32(ack, 7) == itemId
                    && BitConverter.ToInt32(ack, 11) == result.ItemValue
                    && ack[15] == result.ExtData0
                    && BitConverter.ToUInt16(ack, 16) == result.Durability
                    && BitConverter.ToInt32(ack, 18) == -1
                    && BitConverter.ToInt32(ack, 22) == 0
                    && BitConverter.ToInt32(ack, 26) == 0);
            }
            Check("secret-shop failure ACK is strict two-byte result and error",
                SecretShopBuyAckBuilder.BuildFailure().SequenceEqual(new byte[] { 0, 4 }));
            Check("successful purchase persists item and exact gold deduction",
                LoadGoldAndItemCount(connectionString, characterId, itemId) == (0, 1));
            Check("successful purchase marks offer sold",
                !offer.TryGetAvailableItem(itemId, out _));
            Check("duplicate and forged purchases are zero-change failures",
                !service.TryPurchase(characterId, accountId, offer, itemId, 0, out _)
                && !service.TryPurchase(characterId, accountId, offer, 440016, 0, out _)
                && LoadGoldAndItemCount(connectionString, characterId, itemId) == (0, 1));

            const int fullBagItemId = 440016;
            using (var scope = assets.OpenScope(characterId, accountId))
            {
                assets.GrantGold(scope, price);
                scope.Commit();
            }
            FillNaturalItemSlots(connectionString, characterId, fullBagItemId);
            var fullBagOffer = new SecretShopOffer(1002, new[]
            {
                new SecretShopItemCandidate
                {
                    ItemId = fullBagItemId,
                    RawFlag = 0,
                    Price = price,
                    Count = 1,
                    Weight = 1,
                },
            });
            Check("full-bag secret-shop purchase fails without spending",
                !service.TryPurchase(characterId, accountId, fullBagOffer, fullBagItemId, 0, out _)
                && LoadGoldAndItemCount(connectionString, characterId, fullBagItemId) == (price, 0)
                && fullBagOffer.TryGetAvailableItem(fullBagItemId, out _));

            DeleteTempDatabase(tempDb);
        }

        private static void VerifyPurchaseResponseSequence()
        {
            const int accountId = 943100;
            const int characterId = 943101;
            const int itemId = 440003;
            const int price = 302505;
            var tempDb = Path.Combine(Path.GetTempPath(), "secret_shop_handler_selftest.db");
            DeleteTempDatabase(tempDb);
            var connectionString = SqliteDatabaseBootstrap.Initialize(tempDb, ServerPaths.SchemaFilePath);

            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (@accountId, 'secret-shop-handler-selftest', '');
INSERT INTO characters (character_id, account_id, name)
VALUES (@characterId, @accountId, 'secret-shop-handler-selftest');
INSERT INTO character_items (
    owner_scope, owner_id, character_id, list_type, slot_index, item_template_id, item_kind,
    stack_count, instance_value, durability, seal_flag, option_value, expire_time, marker_16,
    pet_serial_or_handle, extra_json)
VALUES (
    'character', @characterId, @characterId, 0, 0, 0, 'special',
    @gold, @gold, 0, 0, 0, 0, 0,
    0, '{}');";
                command.Parameters.AddWithValue("@accountId", accountId);
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@gold", price);
                command.ExecuteNonQuery();
            }

            var store = new SqliteInventoryStore(tempDb, ServerPaths.SchemaFilePath);
            var assets = new SqliteAssetService(tempDb, ServerPaths.SchemaFilePath, store);
            var characters = new SqliteCharacterRepository(tempDb, ServerPaths.SchemaFilePath);
            var dataSource = new SqliteSelectCharacterDataSource(
                tempDb, ServerPaths.SchemaFilePath, characters, assets, store);
            var handler = new SecretShopHandler(
                store, new InventoryRefreshSender(store, dataSource, characters));
            var offer = new SecretShopOffer(1002, new[]
            {
                new SecretShopItemCandidate
                {
                    ItemId = itemId,
                    RawFlag = 0,
                    Price = price,
                    Count = 1,
                    Weight = 1,
                },
            });

            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            using var receiver = new TcpClient();
            var connectTask = receiver.ConnectAsync(endpoint.Address, endpoint.Port);
            using var sender = listener.AcceptTcpClient();
            connectTask.GetAwaiter().GetResult();
            listener.Stop();

            var session = new EnhancedClientSession(sender, new GamePacketHeader())
            {
                Account = new AccountRecord { AccountId = accountId },
            };
            session.Player.CharacterId = characterId;
            session.Player.CurrentRun = new DungeonRun(9000, 0)
            {
                SecretShopOffer = offer,
            };

            var requestBody = BitConverter.GetBytes(itemId)
                .Concat(BitConverter.GetBytes(0))
                .ToArray();
            handler.HandleBuyRequest(session, new GamePacketHeader(), requestBody)
                .GetAwaiter().GetResult();

            var packetTypes = ReadAvailablePacketTypes(receiver);
            Check("successful purchase sends ACK and inventory updates without rebuilding shop list",
                packetTypes.SequenceEqual(new ushort[] { 0x0128, 0x000E, 0x000E }));

            session.Close();
            DeleteTempDatabase(tempDb);
        }

        private static void VerifyNonStackableMultiCountFailsClosed()
        {
            const int accountId = 943050;
            const int characterId = 943051;
            const int itemId = 440003;
            const int unitPrice = 100;
            const int offerCount = 2;
            const int initialGold = unitPrice * offerCount;
            var tempDb = Path.Combine(Path.GetTempPath(), "secret_shop_nonstackable_count_selftest.db");
            DeleteTempDatabase(tempDb);
            var connectionString = SqliteDatabaseBootstrap.Initialize(tempDb, ServerPaths.SchemaFilePath);

            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (@accountId, 'secret-shop-nonstackable-selftest', '');
INSERT INTO characters (character_id, account_id, name)
VALUES (@characterId, @accountId, 'secret-shop-nonstackable-selftest');
INSERT INTO character_items (
    owner_scope, owner_id, character_id, list_type, slot_index, item_template_id, item_kind,
    stack_count, instance_value, durability, seal_flag, option_value, expire_time, marker_16,
    pet_serial_or_handle, extra_json)
VALUES (
    'character', @characterId, @characterId, 0, 0, 0, 'special',
    @gold, @gold, 0, 0, 0, 0, 0,
    0, '{}');";
                command.Parameters.AddWithValue("@accountId", accountId);
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@gold", initialGold);
                command.ExecuteNonQuery();
            }

            var offer = new SecretShopOffer(1002, new[]
            {
                new SecretShopItemCandidate
                {
                    ItemId = itemId,
                    RawFlag = 0,
                    Price = unitPrice,
                    Count = offerCount,
                    Weight = 1,
                },
            });
            var store = new SqliteInventoryStore(tempDb, ServerPaths.SchemaFilePath);
            var service = new SecretShopPurchaseService(store);

            Check("non-stackable multi-count purchase fails without charging or granting",
                !service.TryPurchase(characterId, accountId, offer, itemId, offerCount, out _)
                && LoadGoldAndItemCount(connectionString, characterId, itemId) == (initialGold, 0)
                && offer.TryGetAvailableItem(itemId, out var available)
                && available.RemainingCount == offerCount);

            DeleteTempDatabase(tempDb);
        }

        private static IReadOnlyList<ushort> ReadAvailablePacketTypes(TcpClient client)
            => ReadAvailablePackets(client).Select(x => x.Type).ToArray();

        private static void VerifyItemCurrencyPurchaseResponseSequence()
        {
            const int accountId = 943150;
            const int characterId = 943151;
            const int itemId = 440003;
            const int requiredItemId = 600;
            const int unitCost = 2;
            const int initialRequiredItemCount = 5;
            const int initialGold = 1234;
            var tempDb = Path.Combine(Path.GetTempPath(), "secret_shop_item_currency_handler_selftest.db");
            DeleteTempDatabase(tempDb);
            var connectionString = SqliteDatabaseBootstrap.Initialize(tempDb, ServerPaths.SchemaFilePath);

            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (@accountId, 'secret-shop-item-currency-selftest', '');
INSERT INTO characters (character_id, account_id, name)
VALUES (@characterId, @accountId, 'secret-shop-item-currency-selftest');
INSERT INTO character_items (
    owner_scope, owner_id, character_id, list_type, slot_index, item_template_id, item_kind,
    stack_count, instance_value, durability, seal_flag, option_value, expire_time, marker_16,
    pet_serial_or_handle, extra_json)
VALUES
    ('character', @characterId, @characterId, 0, 0, 0, 'special',
     @gold, @gold, 0, 0, 0, 0, 0, 0, '{}'),
    ('character', @characterId, @characterId, 0, 65, @requiredItemId, 'stackable',
     @requiredItemCount, @requiredItemCount, 0, 0, 0, 0, 0, 0, '{}');";
                command.Parameters.AddWithValue("@accountId", accountId);
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@gold", initialGold);
                command.Parameters.AddWithValue("@requiredItemId", requiredItemId);
                command.Parameters.AddWithValue("@requiredItemCount", initialRequiredItemCount);
                command.ExecuteNonQuery();
            }

            var store = new SqliteInventoryStore(tempDb, ServerPaths.SchemaFilePath);
            var assets = new SqliteAssetService(tempDb, ServerPaths.SchemaFilePath, store);
            var characters = new SqliteCharacterRepository(tempDb, ServerPaths.SchemaFilePath);
            var dataSource = new SqliteSelectCharacterDataSource(
                tempDb, ServerPaths.SchemaFilePath, characters, assets, store);
            var handler = new SecretShopHandler(
                store, new InventoryRefreshSender(store, dataSource, characters));
            var offer = new SecretShopOffer(1002, new[]
            {
                new SecretShopItemCandidate
                {
                    ItemId = itemId,
                    RawFlag = 1,
                    Price = unitCost,
                    RequiredItemId = requiredItemId,
                    Count = 1,
                    Weight = 1,
                },
            });

            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            using var receiver = new TcpClient();
            var connectTask = receiver.ConnectAsync(endpoint.Address, endpoint.Port);
            using var sender = listener.AcceptTcpClient();
            connectTask.GetAwaiter().GetResult();
            listener.Stop();

            var session = new EnhancedClientSession(sender, new GamePacketHeader())
            {
                Account = new AccountRecord { AccountId = accountId },
            };
            session.Player.CharacterId = characterId;
            session.Player.CurrentRun = new DungeonRun(9000, 0)
            {
                SecretShopOffer = offer,
            };

            var requestBody = BitConverter.GetBytes(itemId)
                .Concat(BitConverter.GetBytes(0))
                .ToArray();
            handler.HandleBuyRequest(session, new GamePacketHeader(), requestBody)
                .GetAwaiter().GetResult();

            var packets = ReadAvailablePackets(receiver);
            var ack = packets.FirstOrDefault(x => x.Type == 0x0128)?.Body;
            var persisted = LoadGoldMaterialAndItemCount(
                connectionString, characterId, requiredItemId, itemId);

            Check("item-currency purchase sends ACK and target/cost slot refreshes",
                packets.Select(x => x.Type).SequenceEqual(new ushort[] { 0x0128, 0x000E, 0x000E })
                && ack?.Length == 30
                && ack[0] == 1);
            Check("item-currency ACK reports unchanged gold and authoritative material remainder",
                ack?.Length == 30
                && BitConverter.ToInt32(ack, 1) == initialGold
                && BitConverter.ToInt32(ack, 18) == requiredItemId
                && BitConverter.ToInt32(ack, 22) == initialRequiredItemCount - unitCost
                && BitConverter.ToInt32(ack, 26) == 0);
            Check("item-currency transaction atomically grants item and deducts only required material",
                persisted == (initialGold, initialRequiredItemCount - unitCost, 1)
                && !offer.TryGetAvailableItem(itemId, out _));

            session.Close();
            DeleteTempDatabase(tempDb);
        }

        private static void VerifyMaterialPurchaseResponseSequence()
        {
            const int accountId = 943200;
            const int characterId = 943201;
            const int itemId = 3037;
            const int unitPrice = 50;
            const int initialOfferCount = 200;
            const int initialCubeCount = 500;
            const int initialGold = 1000;
            var tempDb = Path.Combine(Path.GetTempPath(), "secret_shop_material_handler_selftest.db");
            DeleteTempDatabase(tempDb);
            var connectionString = SqliteDatabaseBootstrap.Initialize(tempDb, ServerPaths.SchemaFilePath);

            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash, cube_clear)
VALUES (@accountId, 'secret-shop-material-selftest', '', @cubeCount);
INSERT INTO characters (character_id, account_id, name)
VALUES (@characterId, @accountId, 'secret-shop-material-selftest');
INSERT INTO character_items (
    owner_scope, owner_id, character_id, list_type, slot_index, item_template_id, item_kind,
    stack_count, instance_value, durability, seal_flag, option_value, expire_time, marker_16,
    pet_serial_or_handle, extra_json)
VALUES (
    'character', @characterId, @characterId, 0, 0, 0, 'special',
    @gold, @gold, 0, 0, 0, 0, 0,
    0, '{}');";
                command.Parameters.AddWithValue("@accountId", accountId);
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@cubeCount", initialCubeCount);
                command.Parameters.AddWithValue("@gold", initialGold);
                command.ExecuteNonQuery();
            }

            var store = new SqliteInventoryStore(tempDb, ServerPaths.SchemaFilePath);
            var assets = new SqliteAssetService(tempDb, ServerPaths.SchemaFilePath, store);
            var characters = new SqliteCharacterRepository(tempDb, ServerPaths.SchemaFilePath);
            var dataSource = new SqliteSelectCharacterDataSource(
                tempDb, ServerPaths.SchemaFilePath, characters, assets, store);
            var handler = new SecretShopHandler(
                store, new InventoryRefreshSender(store, dataSource, characters));
            var offer = new SecretShopOffer(1003, new[]
            {
                new SecretShopItemCandidate
                {
                    ItemId = itemId,
                    RawFlag = 0,
                    Price = unitPrice,
                    Count = initialOfferCount,
                    Weight = 1,
                },
            });

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            using var receiver = new TcpClient();
            var connectTask = receiver.ConnectAsync(endpoint.Address, endpoint.Port);
            using var sender = listener.AcceptTcpClient();
            connectTask.GetAwaiter().GetResult();
            listener.Stop();

            var session = new EnhancedClientSession(sender, new GamePacketHeader())
            {
                Account = new AccountRecord { AccountId = accountId },
            };
            session.Player.CharacterId = characterId;
            session.Player.CurrentRun = new DungeonRun(9000, 0)
            {
                SecretShopOffer = offer,
            };

            var requestBody = BitConverter.GetBytes(itemId)
                .Concat(BitConverter.GetBytes(1))
                .ToArray();
            handler.HandleBuyRequest(session, new GamePacketHeader(), requestBody)
                .GetAwaiter().GetResult();

            var packets = ReadAvailablePackets(receiver);
            var ack = packets.FirstOrDefault(x => x.Type == 0x0128)?.Body;
            var remainingBody = SecretShopItemListBodyBuilder.Build(offer);
            var refreshedCube = store.LoadCommonItemForRefresh(
                characterId, accountId, InventoryListType.Main, 358);
            var persisted = LoadGoldAndCubeCount(connectionString, accountId, characterId, "cube_clear");

            Check("material purchase sends success ACK and two authoritative inventory updates",
                packets.Select(x => x.Type).SequenceEqual(new ushort[] { 0x0128, 0x000E, 0x000E })
                && ack?.Length == 30
                && ack[0] == 1);
            Check("material purchase charges unit price for selected quantity and reports total cube count",
                ack?.Length == 30
                && BitConverter.ToInt32(ack, 1) == initialGold - unitPrice
                && BitConverter.ToInt32(ack, 7) == itemId
                && BitConverter.ToInt32(ack, 11) == initialCubeCount + 1
                && persisted == (initialGold - unitPrice, initialCubeCount + 1));
            Check("partial material purchase keeps authoritative offer remainder",
                ack?.Length == 30
                && BitConverter.ToInt32(ack, 26) == initialOfferCount - 1
                && remainingBody.Length == 26
                && BitConverter.ToInt32(remainingBody, 13) == initialOfferCount - 1);
            Check("single-slot refresh synthesizes account cube storage",
                refreshedCube != null
                && refreshedCube.SlotIndex == 358
                && refreshedCube.ItemTemplateId == itemId
                && refreshedCube.CountOrInstanceValue == initialCubeCount + 1);

            session.Close();
            DeleteTempDatabase(tempDb);
        }

        private static IReadOnlyList<CapturedPacket> ReadAvailablePackets(TcpClient client)
        {
            var bytes = new List<byte>();
            var buffer = new byte[4096];
            var stream = client.GetStream();
            while (stream.DataAvailable)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                    break;
                bytes.AddRange(buffer.Take(read));
            }

            var packets = new List<CapturedPacket>();
            var raw = bytes.ToArray();
            var offset = 0;
            while (offset + 15 <= bytes.Count)
            {
                var frameLength = BitConverter.ToInt32(raw, offset + 3);
                if (frameLength < 15 || offset + frameLength > bytes.Count)
                    break;
                var body = new byte[frameLength - 15];
                Buffer.BlockCopy(raw, offset + 15, body, 0, body.Length);
                packets.Add(new CapturedPacket
                {
                    Type = BitConverter.ToUInt16(raw, offset + 1),
                    Body = body,
                });
                offset += frameLength;
            }
            return packets;
        }

        private static (int Gold, int CubeCount) LoadGoldAndCubeCount(
            string connectionString,
            int accountId,
            int characterId,
            string cubeColumn)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $@"
SELECT
    COALESCE((SELECT stack_count FROM character_items
              WHERE character_id=@characterId AND list_type=0 AND slot_index=0), -1),
    COALESCE((SELECT {cubeColumn} FROM accounts WHERE account_id=@accountId), -1);";
            command.Parameters.AddWithValue("@accountId", accountId);
            command.Parameters.AddWithValue("@characterId", characterId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? (reader.GetInt32(0), reader.GetInt32(1)) : (-1, -1);
        }

        private static (int Gold, int MaterialCount, int ItemCount) LoadGoldMaterialAndItemCount(
            string connectionString,
            int characterId,
            int materialItemId,
            int purchasedItemId)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT
    COALESCE((SELECT stack_count FROM character_items
              WHERE character_id=@characterId AND list_type=0 AND slot_index=0), -1),
    COALESCE((SELECT stack_count FROM character_items
              WHERE character_id=@characterId AND list_type=0 AND item_template_id=@materialItemId), -1),
    (SELECT COUNT(*) FROM character_items
     WHERE character_id=@characterId AND item_template_id=@purchasedItemId);";
            command.Parameters.AddWithValue("@characterId", characterId);
            command.Parameters.AddWithValue("@materialItemId", materialItemId);
            command.Parameters.AddWithValue("@purchasedItemId", purchasedItemId);
            using var reader = command.ExecuteReader();
            return reader.Read()
                ? (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2))
                : (-1, -1, -1);
        }

        private sealed class CapturedPacket
        {
            internal ushort Type { get; init; }
            internal byte[] Body { get; init; }
        }

        private static void FillNaturalItemSlots(string connectionString, int characterId, int itemId)
        {
            var metadata = ItemMetadataResolver.Resolve(itemId);
            metadata.GetSlotRange(out var slotStart, out var slotEnd);
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();
            for (var slot = slotStart; slot <= slotEnd; slot++)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"
INSERT OR IGNORE INTO character_items (
    owner_scope, owner_id, character_id, list_type, slot_index, item_template_id, item_kind,
    stack_count, instance_value, durability, seal_flag, option_value, expire_time, marker_16,
    pet_serial_or_handle, extra_json)
VALUES (
    'character', @characterId, @characterId, 0, @slot, @dummyItemId, 'equipment',
    1, 1, 1, 0, 0, 0, 0,
    0, '{}');";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@slot", slot);
                command.Parameters.AddWithValue("@dummyItemId", 900000000 + slot);
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }

        private static (int Gold, int ItemCount) LoadGoldAndItemCount(
            string connectionString,
            int characterId,
            int itemId)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT
    COALESCE((SELECT stack_count FROM character_items
              WHERE character_id=@characterId AND list_type=0 AND slot_index=0), -1),
    (SELECT COUNT(*) FROM character_items
     WHERE character_id=@characterId AND item_template_id=@itemId);";
            command.Parameters.AddWithValue("@characterId", characterId);
            command.Parameters.AddWithValue("@itemId", itemId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? (reader.GetInt32(0), reader.GetInt32(1)) : (-1, -1);
        }

        private static void DeleteTempDatabase(string path)
        {
            SqliteConnection.ClearAllPools();
            foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
            {
                if (File.Exists(candidate))
                    File.Delete(candidate);
            }
        }

        private const string SyntheticSecretShop = @"
[price random]
10
[/price random]
[cash user stackable count]
200
[/cash user stackable count]
[dungeon npc]
9000 1002 7000 9000 1000 3000
[/dungeon npc]
[level section]
5 56 65
[/level section]
[level npc]
5 1002 8000 8000 8000 8000 8000
5 1000 2000 2000 2000 2000 2000
[/level npc]
[npc]
1002
[level]
5 2 440003 0 302505 0 1 5 440016 0 302505 0 1 5
[/level]
[cash item]
5 2 440003 0 302505 0 1 5 440016 0 302505 0 1 5 3331 0 15000 0 3 36290
[/cash item]
[dungeon]
9000 1 440016 0 302505 0 1 5
[/dungeon]
[/npc]
";
    }
}
