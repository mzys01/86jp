using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Game.Premium;
using DfoServer.Game.Skills;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DungeonData = DfoServer.GameWorld.Dungeon;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal sealed class DungeonSettlementHandler
    {
        private readonly DungeonSharedServices _svc;

        // PVF [visible on dungeon clear]=1: Delilah(1000) Gabriel(1002/1003/1004) Yunmi(1203, invalid in 86JP)
        private static readonly int[] SecretShopNpcIds = { 1000, 1002, 1003, 1004 };
        private const int SetPlayResultRankPointOffset = 10;
        private const int GrowthContractPremiumType = 84; // PVF premiumlist_new.etc: growth contract
        private const float GrowthContractBonusRate = 0.20f;
        private const float BlackDiamondBonusRate = 0.10f;
        private static readonly int[] BlackDiamondPremiumTypes = { 1, 17 };

        internal DungeonSettlementHandler(DungeonSharedServices svc) => _svc = svc;

        // Settlement result.
        // df_game_r CParty::CheckPlayResult -> CParty::SetPlayResult
        // Sends 3 NOTI packets (34, 37, 35) to show the settlement screen.
        // Card layout is deferred: a 2 s server timer sends it automatically
        // so the player sees the settlement summary first, then the cards appear.
        // After the card layout, a 4 s timer auto-flips the free card
        // (the client shows a 3 s countdown; 4 s on the server gives it room to finish).
        // If the player presses a key before the layout timer fires, the layout
        // is sent immediately and a fresh 3 s auto-flip timer starts.
        internal async Task HandleSetPlayResult(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var run = session.Player.CurrentRun;
            if (run == null) return;
            if (run.Phase != DungeonRunPhase.Cleared) return;
            run.Phase = DungeonRunPhase.ResultShown;

            var clearRank = CalculateClearRank(body);
            var clearExp = CalculateClearRewardExp(session, clearRank.RankBonusIndex);
            var prevLevel = session.Player.Level;
            if (clearExp.Total > 0)
            {
                session.Player.Exp = AddSaturating(session.Player.Exp, clearExp.Total);
                session.Player.Level = ExpTableProvider.ApplyLevelUps(session.Player.Level, session.Player.Exp);
            }
            var leveledUp = session.Player.Level > prevLevel;
            _svc.PersistLevelAndExp(session.Player.CharacterId, session.Player.Level, session.Player.Exp);

            // Pre-generate card rewards (df_game_r: clear_reward generated before NOTI 35)
            int dungeonLevel = 85;
            try { dungeonLevel = DungeonData.GetDungeonBasicLv(run.DungeonId); } catch (Exception ex) { FileLogger.Log($"[DungeonHandler] SET_PLAY_RESULT ERROR: dungeon level fallback dungeon={run.DungeonId} default={dungeonLevel}, card rewards will use the fallback level: {ex.Message}"); }
            var lcg = run.RoomLcg ?? new DnfLcg(run.Seed);
            var freeGold = ClearRewardGenerator.GenerateGoldCard(
                dungeonLevel, run.Difficulty, lcg);
            var freeItem = ClearRewardGenerator.GenerateItemCard(
                dungeonLevel, run.Difficulty, lcg);
            var paidGold = ClearRewardGenerator.GenerateGoldCard(
                dungeonLevel, run.Difficulty, lcg);
            var paidItem = ClearRewardGenerator.GenerateItemCard(
                dungeonLevel, run.Difficulty, lcg);
            run.CardRewards = new List<ClearRewardGenerator.CardReward>
            {
                freeGold, freeItem, default, default,  // free: [0]gold [1]item [2-3]empty(solo)
                paidGold, paidItem, default, default    // paid: [4]gold [5]item [6-7]empty(solo)
            };

            var monsterTotalExp = run.TotalExp;
            var bossTotalExp = Math.Min(run.BossTotalExp, monsterTotalExp);
            var championTotalExp = Math.Min(run.ChampionTotalExp, monsterTotalExp);
            var superChampionTotalExp = Math.Min(run.SuperChampionTotalExp, monsterTotalExp);
            var namedMonsterTotalExp = Math.Min(run.NamedMonsterTotalExp, monsterTotalExp);
            var monsterGrowthContractBonus = run.MonsterGrowthContractBonusExp;

            // Settlement 3 packets: NOTI 34, NOTI 37, NOTI 35
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0022,
                DungeonNotificationBuilder.BuildPlayResult(
                    session.Player.UserId, monsterTotalExp, allKill: true,
                    rankGrade: clearRank.RankGrade, clientRankPoint: clearRank.ClientRankPoint)));
            var (remainSp, remainTp) = _svc.GetRemainingSpTp(session, persist: leveledUp, logTag: "SET_PLAY_RESULT");

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0025,
                DungeonNotificationBuilder.BuildExp(session.Player.Level, session.Player.Exp, remainSp, remainTp)));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0023,
                DungeonNotificationBuilder.BuildClearDungeonReward(
                    clearExp.Base, scoreBonusExp: ToInt32Saturated(clearExp.ScoreBonus), clearBonusExp: 0,
                    blackDiamondExp: ToInt32Saturated(clearExp.BlackDiamondBonus),
                    growthContractExp: ToInt32Saturated(clearExp.GrowthContractBonus),
                    monsterGrowthContractExp: ToInt32Saturated(monsterGrowthContractBonus),
                    adventureGroupExp: ToInt32Saturated(clearExp.AdventureGroupBonus),
                    monsterExp: monsterTotalExp, bossExp: ToInt32Saturated(bossTotalExp),
                    championExp: ToInt32Saturated(championTotalExp),
                    superChampionExp: 0,
                    freeCardGold: freeGold.GoldAmount,
                    freeCardItemId: freeItem.ItemId, freeCardItemCount: freeItem.StackCount)));

            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] CLEAR_EXP: dungeon={run.DungeonId} diff={run.Difficulty} clientRank={clearRank.ClientRankPoint} rankPoint={clearRank.RankPoint} rankGrade={clearRank.RankGrade} rankBonusIndex={clearRank.RankBonusIndex} base={clearExp.Base} scoreBonus={clearExp.ScoreBonus} growthContract={clearExp.GrowthContractBonus} blackDiamond={clearExp.BlackDiamondBonus} adventureGroup={clearExp.AdventureGroupBonus} bonus={clearExp.Bonus} total={clearExp.Total} monsterTotalExp={monsterTotalExp} monsterGrowthContract={monsterGrowthContractBonus} bossTotalExp={bossTotalExp} championTotalExp={championTotalExp} superChampionTotalExp={superChampionTotalExp} namedMonsterTotalExp={namedMonsterTotalExp} charExp={session.Player.Exp}");

            if (leveledUp)
            {
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] LEVEL UP from dungeon clear: cid={session.Player.CharacterId} {prevLevel}->{session.Player.Level} exp={session.Player.Exp}");
                await _svc.SendInDungeonLevelUpFollowups(session);
            }

            // Card layout is deferred: 2 s timer -> layout, then 4 s -> auto-flip free card.
            // Phase is already ResultShown (set at method entry); the lazy-layout branches key off it.
            run.CardFlipCount = 0;
            run.FreeCardSlots = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
            run.PaidCardSlots = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };

            ScheduleAutoFlow(session, layoutDelayMs: 2000, autoFlipDelayMs: 4000);

            await _svc.UpdateDungeonPermission(session, run.DungeonId, run.Difficulty);
        }

        // Auto-flow timer.
        // Phase 1: after layoutDelayMs, send the card layout (0x0045 + 0x0046).
        // Phase 2: after autoFlipDelayMs more, flip the free card.
        // If the player presses a key before phase 1 fires, HandleSelectCard (state==4)
        // cancels this timer and shows the layout immediately, then starts a fresh
        // phase-2 timer so the free card still auto-flips after 3 s.

        private static ClearRankParts CalculateClearRank(byte[] body)
        {
            var clientRankPoint = ExtractClientRankPoint(body);
            var timeBonusPoint = 0;
            var rankPoint = Math.Min(255, clientRankPoint + timeBonusPoint);
            var rankGrade = MonsterRewardTable.GetClearRankGrade(rankPoint);
            var rankBonusIndex = MonsterRewardTable.GetClearRankBonusIndex(rankPoint);

            return new ClearRankParts(
                (byte)clientRankPoint,
                timeBonusPoint,
                rankPoint,
                (byte)rankGrade,
                rankBonusIndex);
        }

        private static int ExtractClientRankPoint(byte[] body)
        {
            if (body == null || body.Length == 0)
                return 0;

            if (body.Length > SetPlayResultRankPointOffset)
                return body[SetPlayResultRankPointOffset];

            return body[0];
        }

        private ClearExpParts CalculateClearRewardExp(EnhancedClientSession session, int rankBonusIndex)
        {
            var run = session.Player.CurrentRun;
            int dungeonLevel;
            try { dungeonLevel = DungeonData.GetDungeonBasicLv(run.DungeonId); }
            catch (Exception ex) { dungeonLevel = session.Player.Level; FileLogger.Log($"[DungeonHandler] CLEAR_EXP ERROR: dungeon level fallback to player level {dungeonLevel}: {ex.Message}"); }

            var baseExp = ExpTableProvider.GetExpRewardBase(dungeonLevel);
            if (baseExp <= 0)
                return default;

            float expWeight;
            try { expWeight = DungeonData.GetExperienceWeight(run.DungeonId); }
            catch { expWeight = 1.0f; }

            var scaledBase = baseExp * expWeight * MonsterRewardTable.GetDifficultyExpRate(run.Difficulty);
            var clearBaseExp = ToUInt32Floor(scaledBase);
            if (clearBaseExp == 0)
                return default;

            var connStr = SqliteDatabaseBootstrap.Initialize(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
            var accountId = session.Account?.AccountId ?? 1;
            var scoreBonusRate = MonsterRewardTable.GetClearRankExpBonusRate(rankBonusIndex);
            var scoreBonus = ToUInt32Floor(clearBaseExp * scoreBonusRate);
            var growthContractBonus = PremiumService.HasActivePremium(connStr, accountId, GrowthContractPremiumType)
                ? ToUInt32Floor(clearBaseExp * GrowthContractBonusRate)
                : 0;
            var blackDiamondBonus = PremiumService.HasActivePremium(connStr, accountId, BlackDiamondPremiumTypes)
                ? ToUInt32Floor(clearBaseExp * BlackDiamondBonusRate)
                : 0;
            var adventureGroupBonus = CalculateAdventureGroupClearExpBonus(session, accountId, clearBaseExp);

            return new ClearExpParts(clearBaseExp, scoreBonus, growthContractBonus, blackDiamondBonus, adventureGroupBonus);
        }

        private uint CalculateAdventureGroupClearExpBonus(EnhancedClientSession session, int accountId, uint clearBaseExp)
        {
            if (session == null || clearBaseExp == 0)
                return 0;

            try
            {
                var characters = _svc.CharacterRepository.ListByAccount(accountId);
                var summary = AdventureGroupDataProvider.Calculate(characters);
                if (summary.ExpBonusPercent == 0 || IsHighestLevelCharacter(session, characters))
                    return 0;

                return ToUInt32Floor(clearBaseExp * (summary.ExpBonusPercent / 100.0f));
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] CLEAR_EXP adventure group bonus skipped: {ex.Message}");
                return 0;
            }
        }

        private static bool IsHighestLevelCharacter(EnhancedClientSession session, IReadOnlyList<Game.Characters.CharacterRecord> characters)
        {
            if (session?.Player == null || characters == null || characters.Count == 0)
                return true;

            var highestLevel = 0;
            foreach (var character in characters)
            {
                if (character == null || character.Deleted)
                    continue;
                if (character.Level > highestLevel)
                    highestLevel = character.Level;
            }

            return session.Player.Level >= highestLevel;
        }

        private static uint ToUInt32Floor(float value)
        {
            if (value <= 0)
                return 0;
            return value >= uint.MaxValue ? uint.MaxValue : (uint)value;
        }

        private static uint AddSaturating(uint current, uint add)
        {
            var value = (ulong)current + add;
            return value > uint.MaxValue ? uint.MaxValue : (uint)value;
        }

        private static int ToInt32Saturated(uint value)
        {
            return value > int.MaxValue ? int.MaxValue : (int)value;
        }

        private readonly struct ClearRankParts
        {
            internal ClearRankParts(byte clientRankPoint, int timeBonusPoint, int rankPoint, byte rankGrade, int rankBonusIndex)
            {
                ClientRankPoint = clientRankPoint;
                TimeBonusPoint = timeBonusPoint;
                RankPoint = rankPoint;
                RankGrade = rankGrade;
                RankBonusIndex = rankBonusIndex;
            }

            internal byte ClientRankPoint { get; }
            internal int TimeBonusPoint { get; }
            internal int RankPoint { get; }
            internal byte RankGrade { get; }
            internal int RankBonusIndex { get; }
        }

        private readonly struct ClearExpParts
        {
            internal ClearExpParts(uint baseExp, uint scoreBonus, uint growthContractBonus, uint blackDiamondBonus, uint adventureGroupBonus)
            {
                Base = baseExp;
                ScoreBonus = scoreBonus;
                GrowthContractBonus = growthContractBonus;
                BlackDiamondBonus = blackDiamondBonus;
                AdventureGroupBonus = adventureGroupBonus;
            }

            internal uint Base { get; }
            internal uint ScoreBonus { get; }
            internal uint GrowthContractBonus { get; }
            internal uint BlackDiamondBonus { get; }
            internal uint AdventureGroupBonus { get; }
            internal uint Bonus => AddSaturating(AddSaturating(AddSaturating(ScoreBonus, GrowthContractBonus), BlackDiamondBonus), AdventureGroupBonus);
            internal uint Total => AddSaturating(Base, Bonus);
        }

        // The detached timer captures its own DungeonRun instance and re-checks that it is
        // still the current run before acting -- a leftover timer from a previous run must
        // never touch the next run's state or send packets after returning to town.
        private void ScheduleAutoFlow(EnhancedClientSession session, int layoutDelayMs, int autoFlipDelayMs)
        {
            DungeonRunLifecycle.CancelAutoFlip(session);
            var run = session.Player.CurrentRun;
            if (run == null) return;

            var cts = new CancellationTokenSource();
            run.AutoFlipCts = cts;
            var token = cts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    // Phase 1: wait, then show card layout.
                    await Task.Delay(layoutDelayMs, token);
                    if (token.IsCancellationRequested) return;
                    if (!ReferenceEquals(session.Player.CurrentRun, run)) return;
                    if (run.Phase != DungeonRunPhase.ResultShown) return;

                    FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] Auto-layout timer fired, sending card layout");
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0045, new byte[] { 0x01 }));
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0046, BuildCardLayoutAck()));
                    run.Phase = DungeonRunPhase.CardsRevealed;

                    // Phase 2: wait, then auto-flip free card.
                    await Task.Delay(autoFlipDelayMs, token);
                    if (token.IsCancellationRequested) return;
                    if (!ReferenceEquals(session.Player.CurrentRun, run)) return;

                    FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] Auto-flip timer fired, flipping free card");
                    await AutoFlipFreeCard(session, run);
                }
                catch (TaskCanceledException) { /* player acted before timer */ }
                catch (Exception ex)
                {
                    FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] Auto-flow timer error: {ex}");
                }
            }, token);
        }

        // Starts a server-side 3-second auto-flip timer for the free card.
        // Used when the layout was shown early via a player key-press (so the
        // combined ScheduleAutoFlow timer was cancelled and we need a standalone
        // auto-flip timer).
        private void StartDelayedAutoFlip(EnhancedClientSession session, int delayMs)
        {
            DungeonRunLifecycle.CancelAutoFlip(session);
            var run = session.Player.CurrentRun;
            if (run == null) return;

            var cts = new CancellationTokenSource();
            run.AutoFlipCts = cts;
            var token = cts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delayMs, token);
                    if (token.IsCancellationRequested) return;
                    if (!ReferenceEquals(session.Player.CurrentRun, run)) return;
                    FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] Standalone auto-flip timer fired");
                    await AutoFlipFreeCard(session, run);
                }
                catch (TaskCanceledException) { }
                catch (Exception ex)
                {
                    FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] Auto-flip timer error: {ex}");
                }
            }, token);
        }

        // Auto-flips only the free card (never the paid card).
        // Sends ACK 0x0047 with flipped card info, then delivers free card
        // rewards via NOTI 14. CardRewards is NOT cleared; the paid card
        // stays available for the player to flip/EPLP.
        private async Task AutoFlipFreeCard(EnhancedClientSession session, Game.Dungeon.DungeonRun run)
        {
            if (run.FreeCardSlots[0] != 0xFF) return; // already flipped

            run.CardFlipCount++;
            run.FreeCardSlots[0] = 0x00;

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0047,
                BuildCardInfoAck(session)));

            bool hasPaid = HasPaidCardReward(run.CardRewards);
            bool paidAlreadyFlipped = hasPaid && run.PaidCardSlots[0] != 0xFF;
            if (!hasPaid || paidAlreadyFlipped)
            {
                // No paid card, or paid card was already manually flipped:
                // deliver all rewards and clear cards so EPLP works.
                await DeliverCardRewards(session);
                run.CardRewards = null;
            }
            else
            {
                // Paid card still pending: only deliver free card rewards; keep cards alive.
                await DeliverFreeCardRewardsOnly(session);
            }
        }

        // SELECT_CARD (CMD 0x0047): card flip only.
        // body[0]: 0=free card, 1=paid card
        // body[1]: cardIndex (0-3)
        // EPLP buttons come via CMD 0x0048 -> HandleEplpCommand, never here.
        internal async Task HandleSelectCard(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var run = session.Player.CurrentRun;
            if (run == null) return;
            if (body.Length < 2) return;
            byte cardType = body[0];
            byte cardIndex = body[1];

            // Lazy card layout: player pressed a key while settlement is showing.
            if (run.Phase == DungeonRunPhase.ResultShown)
            {
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] Lazy card layout by SELECT_CARD: type={cardType} idx={cardIndex}");
                DungeonRunLifecycle.CancelAutoFlip(session);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0045, new byte[] { 0x01 }));
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0046, BuildCardLayoutAck()));
                run.Phase = DungeonRunPhase.CardsRevealed;
                StartDelayedAutoFlip(session, delayMs: 4000);
                return;
            }

            // Only card flips here; EPLP goes through CMD 0x0048.
            if (cardType > 1 || cardIndex > 3) return;

            // Only cancel auto-flip timer when user manually flips a free card.
            // Flipping a paid card must not cancel the timer so the free card
            // still gets auto-flipped when the timer expires.
            if (cardType == 0)
                DungeonRunLifecycle.CancelAutoFlip(session);

            run.CardFlipCount++;
            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] SELECT_CARD flip#{run.CardFlipCount} type={cardType} idx={cardIndex}");

            if (cardType == 0)
                run.FreeCardSlots[cardIndex] = 0x00;
            else
                run.PaidCardSlots[cardIndex] = 0x00;

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0047,
                BuildCardInfoAck(session)));

            bool freeSelected = run.FreeCardSlots[0] != 0xFF;
            bool paidSelected = run.PaidCardSlots[0] != 0xFF;
            bool allDone = freeSelected && (paidSelected || !HasPaidCardReward(run.CardRewards));

            if (allDone)
            {
                await DeliverCardRewards(session);
                run.CardRewards = null;
            }
        }

        // EPLP (CMD 0x0048): settlement option buttons.
        // body[0]: 1=confirm, 2=status update
        // body[1]: 0=retry, 1=select another dungeon, 2=return to town
        // If a paid card is still pending, auto-flip it before returning to town
        // (matches DNF behaviour: clicking any EPLP button auto-pays the card).
        internal async Task HandleEplpCommand(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body.Length < 2) return;
            byte state = body[0];
            byte option = body[1];

            // run 可能为空(如重复收到 EPLP, 首次已返城): 跳过翻牌相关分支,
            // 仍回 ACK 并按 state 返城, 与旧行为一致。
            var run = session.Player.CurrentRun;

            // Lazy card layout via EPLP button press
            if (run != null && run.Phase == DungeonRunPhase.ResultShown)
            {
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] Lazy card layout by EPLP: state={state} option={option}");
                DungeonRunLifecycle.CancelAutoFlip(session);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0045, new byte[] { 0x01 }));
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0046, BuildCardLayoutAck()));
                run.Phase = DungeonRunPhase.CardsRevealed;
                StartDelayedAutoFlip(session, delayMs: 4000);
                return;
            }

            DungeonRunLifecycle.CancelAutoFlip(session);

            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] EPLP: state={state} option={option}");

            // Auto-flip pending paid card (DNF: clicking EPLP auto-pays remaining card)
            bool pendingPaidCard = run != null
                                   && HasPaidCardReward(run.CardRewards)
                                   && run.PaidCardSlots[0] == 0xFF;
            if (state == 1 && pendingPaidCard)
            {
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] EPLP auto-flipping pending paid card");
                run.PaidCardSlots[0] = 0x00;
                run.CardFlipCount++;
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0047,
                    BuildCardInfoAck(session)));
                await DeliverCardRewards(session);
                run.CardRewards = null;
            }

            // Send EPLP ACK
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0048,
                new byte[] { 0x01, state, option }));

            if (state == 1)
            {
                await ReturnToVillage(session);
            }
        }

        // Extracted reward delivery shared by auto-flip, manual flip, and EPLP-combo.
        // Delivers both free and paid card rewards via NOTI 0x000E.
        private async Task DeliverCardRewards(EnhancedClientSession session)
        {
            var cards = session.Player.CurrentRun?.CardRewards;
            if (cards == null) return;

            var entries = new List<byte[]>();
            var accountId = session.Account?.AccountId ?? 1;

            // Free card gold
            if (cards.Count > 0 && cards[0].IsGold && cards[0].GoldAmount > 0)
            {
                _svc.PersistGold(session.Player.CharacterId, accountId, cards[0].GoldAmount);
                int totalGold = _svc.ReadGold(session.Player.CharacterId, accountId);
                entries.Add(ItemListUpdateBuilder.BuildRawItemEntry(0, 0, (uint)totalGold));
            }
            AddCardItemEntry(session, accountId, cards, 1, entries);

            // Paid card gold
            if (cards.Count > 4 && cards[4].IsGold && cards[4].GoldAmount > 0)
            {
                _svc.PersistGold(session.Player.CharacterId, accountId, cards[4].GoldAmount);
                int totalGold = _svc.ReadGold(session.Player.CharacterId, accountId);
                entries.Add(ItemListUpdateBuilder.BuildRawItemEntry(0, 0, (uint)totalGold));
            }
            AddCardItemEntry(session, accountId, cards, 5, entries);

            if (entries.Count > 0)
            {
                var w = new GamePacketWriter();
                w.WriteByte(0);
                w.WriteUInt16((ushort)entries.Count);
                foreach (var e in entries)
                    w.WriteBytes(e);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, w.ToArray()));
            }
            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] Card rewards delivered: {entries.Count} entries");
        }

        // Free card rewards only (used when paid card is still pending after auto-flip).
        private async Task DeliverFreeCardRewardsOnly(EnhancedClientSession session)
        {
            var cards = session.Player.CurrentRun?.CardRewards;
            if (cards == null) return;

            var entries = new List<byte[]>();
            var accountId = session.Account?.AccountId ?? 1;

            if (cards.Count > 0 && cards[0].IsGold && cards[0].GoldAmount > 0)
            {
                _svc.PersistGold(session.Player.CharacterId, accountId, cards[0].GoldAmount);
                int totalGold = _svc.ReadGold(session.Player.CharacterId, accountId);
                entries.Add(ItemListUpdateBuilder.BuildRawItemEntry(0, 0, (uint)totalGold));
            }
            AddCardItemEntry(session, accountId, cards, 1, entries);

            if (entries.Count > 0)
            {
                var w = new GamePacketWriter();
                w.WriteByte(0);
                w.WriteUInt16((ushort)entries.Count);
                foreach (var e in entries)
                    w.WriteBytes(e);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, w.ToArray()));
            }
            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] Free card rewards delivered: {entries.Count} entries");
        }

        private void AddCardItemEntry(EnhancedClientSession session, int accountId,
            List<ClearRewardGenerator.CardReward> cards, int cardIndex, List<byte[]> entries)
        {
            if (cards.Count <= cardIndex || cards[cardIndex].IsGold || cards[cardIndex].ItemId <= 0)
                return;

            var card = cards[cardIndex];
            short slot;
            if (!_svc.TryPickupItemToInventory(session.Player.CharacterId, accountId, card.ItemId, card.StackCount, out slot))
                return;

            var sealFlag = card.IsEquipment && ItemMetadataResolver.Resolve(card.ItemId).IsSealed ? (byte)1 : (byte)0;
            entries.Add(card.IsEquipment
                ? ItemListUpdateBuilder.BuildRawEquipEntry(slot, (uint)card.ItemId, durability: card.Durability, sealFlag: sealFlag)
                : ItemListUpdateBuilder.BuildRawItemEntry(slot, (uint)card.ItemId, (uint)card.StackCount));
        }

        // CMD 0x0045: client requests card layout after settlement screen.
        // Send the deferred card layout and start a fresh 3 s auto-flip timer.
        internal async Task HandleCardStartRequest(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var run = session.Player.CurrentRun;
            if (run == null) return;
            if (run.Phase != DungeonRunPhase.ResultShown) return;
            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] Card start requested by client (CMD 0x0045), sending deferred layout");

            DungeonRunLifecycle.CancelAutoFlip(session);

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0045, new byte[] { 0x01 }));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0046, BuildCardLayoutAck()));
            run.Phase = DungeonRunPhase.CardsRevealed;

            StartDelayedAutoFlip(session, delayMs: 4000);
        }

        // df_game_r CParty::ClearDungeon (0x85A9330)
        // Preamble: if (!cleared_flag) return; Epilogue: cleared_flag = 1;
        // Normal dungeon sends NOTI 31 (ENABLE_CLEAR_DUNGEON), advances phase to Cleared
        // + NOTI 279 (0x0117) SECRET_SHOP_NPC: settlement mystery merchant NPC ID
        internal async Task TryClearDungeon(EnhancedClientSession session, string reason, int bossCode = 0)
        {
            var run = session.Player.CurrentRun;
            if (run == null) return;
            if (run.Phase != DungeonRunPhase.InProgress) return;
            run.Phase = DungeonRunPhase.Cleared;
            if (bossCode != 0) run.BossCode = bossCode;
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x001F, DungeonNotificationBuilder.BuildEnableClearDungeon()));
            var npcId = SecretShopNpcIds[ServerRandom.Next(SecretShopNpcIds.Length)];
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0117, BitConverter.GetBytes(npcId)));
            if (session.GameSession?.QuestManager != null)
            {
                var currentMapId = ResolveCurrentMapId(session);
                await session.GameSession.QuestManager.SyncClearMapQuestProgressAsync(
                    run.DungeonId,
                    currentMapId);
                if (ShouldSyncQuestConnectedStartMapOnDungeonClear(session, currentMapId))
                {
                    FileLogger.Log($"[DungeonHandler] CLEAR_MAP sync deferred quest-connected start map: dungeon={run.DungeonId} maze={run.MazeIndex} map={run.MazeStartMapId}");
                    await session.GameSession.QuestManager.SyncClearMapQuestProgressAsync(
                        0,
                        run.MazeStartMapId);
                }
            }
            FileLogger.Log($"[DungeonHandler] ClearDungeon: {reason} secretShopNpc={npcId}");
        }

        private static int ResolveCurrentMapId(EnhancedClientSession session)
        {
            var run = session?.Player?.CurrentRun;
            if (run == null)
                return 0;

            RoomState state;
            if (run.RoomStates != null
                && run.RoomStates.TryGetValue(run.RoomKey, out state)
                && state != null
                && state.Maze.Index > 0)
                return state.Maze.Index;

            return 0;
        }

        private static bool ShouldSyncQuestConnectedStartMapOnDungeonClear(EnhancedClientSession session, int currentMapId)
        {
            var run = session?.Player?.CurrentRun;
            if (run == null || !run.MazeQuestConnected)
                return false;
            if (run.MazeStartMapId <= 0 || run.MazeStartMapId == currentMapId)
                return false;
            return true;
        }

        // Returns true if paid card rewards are available (gold or item at indices 4/5 in card list).
        private static bool HasPaidCardReward(List<ClearRewardGenerator.CardReward> cards)
        {
            if (cards == null) return false;
            return (cards.Count > 4 && cards[4].IsGold && cards[4].GoldAmount > 0) ||
                   (cards.Count > 5 && !cards[5].IsGold && cards[5].ItemId > 0);
        }

        // Synchronous return-to-town: mirrors DungeonTutorialHandler.ReturnToVillage packet sequence.
        // Key points: UserState=0x00 (not 0x01), sync await (not fire-and-forget), includes NOTI 0x00CA.
        private async Task ReturnToVillage(EnhancedClientSession session)
        {
            await DungeonRunLifecycle.EndRunToTownAsync(session);
            session.Player.UserState = 0x00;

            var snapshot = TownAreaNotificationBuilder.CreateCurrentSnapshot(session.Player);

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0003,
                EnterSelectDungeonStateBuilder.BuildUserState(session.Player)));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0017,
                TownAreaNotificationBuilder.BuildUserArea(snapshot)));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0018,
                TownAreaNotificationBuilder.BuildAreaUsers(snapshot)));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x00CA,
                new byte[] { 0x00 }));

            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] ReturnToVillage: 4 town packets sent");
        }

        // CMD ACK 71 body: 86JP 8-seat format
        // seat[0-3]: active seats (solo uses seat0 only)
        // seat[4-7]: 0xFF*4 (hidden/disabled)
        private byte[] BuildCardInfoAck(EnhancedClientSession session)
        {
            var run = session.Player.CurrentRun;
            var w = new GamePacketWriter();
            w.WriteByte(0x01);  // resultCode

            for (int i = 0; i < 8; i++)
            {
                if (i >= 4)
                {
                    // Hidden seat: type=FF flag=FF count=FF(-1,skip) flipped=FF
                    w.WriteByte(0xFF);
                    w.WriteByte(0xFF);
                    w.WriteByte(0xFF);
                    w.WriteByte(0xFF);
                    continue;
                }

                bool freeSelected = run.FreeCardSlots[i] != 0xFF;
                bool paidSelected = run.PaidCardSlots[i] != 0xFF;

                if (i != 0)
                {
                    // Solo mode seat1-3 empty: type=FF flag=FF count=0 flipped=0
                    w.WriteByte(0xFF);
                    w.WriteByte(0xFF);
                    w.WriteByte(0x00);
                    w.WriteByte(0x00);
                    continue;
                }

                // seat0: active card seat
                w.WriteByte(freeSelected ? (byte)0x00 : (byte)0xFF);  // cardType
                w.WriteByte(paidSelected ? (byte)0x00 : (byte)0xFF);  // seatFlag

                if (paidSelected)
                {
                    // Paid card: count=2, item[0]=gold{0,gold}, item[1]=item{id,count}
                    var cards = run.CardRewards;
                    int paidGoldAmt = (cards != null && cards.Count > 4 && cards[4].IsGold) ? cards[4].GoldAmount : 0;
                    int paidItemId = (cards != null && cards.Count > 5 && !cards[5].IsGold) ? cards[5].ItemId : 0;
                    int paidItemCnt = (cards != null && cards.Count > 5 && !cards[5].IsGold) ? cards[5].StackCount : 0;

                    w.WriteByte(2);                         // itemCount = 2
                    w.WriteUInt32(0);                       // item[0] itemId=0 (gold)
                    w.WriteInt32(paidGoldAmt);              // item[0] amount
                    w.WriteUInt32((uint)paidItemId);        // item[1] itemId
                    w.WriteInt32(paidItemCnt);              // item[1] count
                }
                else
                {
                    w.WriteByte(0x00);  // itemCount = 0 (free card sends no content)
                }

                w.WriteByte(0x00);  // flippedFlag
            }

            return w.ToArray();
        }

        // CMD ACK 70: card layout, u8 resultCode + u16[8] slotStatus
        // Solo: slot[0]=0x0001(flippable) slot[1-7]=0xFFFF(disabled)
        private static byte[] BuildCardLayoutAck()
        {
            var w = new GamePacketWriter();
            w.WriteByte(0x01);
            w.WriteUInt16(0x0001);
            for (int i = 1; i < 8; i++)
                w.WriteUInt16(0xFFFF);
            return w.ToArray();
        }

        internal static byte[] HexToBytes(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }
    }
}
