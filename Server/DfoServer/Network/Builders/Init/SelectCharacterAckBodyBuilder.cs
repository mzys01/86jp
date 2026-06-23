using DfoServer.Game.Characters;
using DfoServer.Game.Quests;
using DfoServer.Game.SelectCharacter;
using System;
using System.Collections.Generic;

namespace DfoServer.Network.Builders
{
    public static class SelectCharacterAckBodyBuilder
    {
        public static bool TryBuild(SelectCharacterDataSnapshot snapshot, out byte[] body)
        {
            var initSnap = snapshot.InitializationSnapshot;
            var record = snapshot.CharacterRecord;

            if (record == null)
            {
                body = null;
                return false;
            }

            var writer = new GamePacketWriter();

            // [0] u8 resultCode
            writer.WriteByte(1);

            // [1] u32 accountRegTime
            writer.WriteInt32(initSnap.AckAccountRegTime);

            // [5] u32 characterCreatedTime
            if (record != null)
                writer.WriteInt32((int)((DateTimeOffset)record.CreatedAt).ToUnixTimeSeconds());
            else
                writer.WriteInt32(initSnap.AckCharCreatedTime);

            // [9] u16 uniqueId
            writer.WriteInt16(record != null ? (short)record.CharacterId : (short)initSnap.AckUniqueId);

            // [11] i16 totalFatigue
            writer.WriteInt16(0);

            writer.WriteInt16(188);

            // [15] i16 usedFatigue
            writer.WriteInt16(0);

            // [17] u8 premiumCount + N × (u8 type + u8[8] endTime)
            var premiums = initSnap.AckPremiums;
            writer.WriteByte((byte)premiums.Count);
            for (int i = 0; i < premiums.Count; i++)
            {
                writer.WriteByte(premiums[i].PremiumType);
                writer.WriteBytes(premiums[i].EndTime);
            }

            // u32 cera
            writer.WriteInt32(initSnap.AckCera);

            // 30 × (u16 questId + u32 triggerValue) — active quest slots
            List<ActiveQuest> activeQuests = null;
            if (record != null && record.CharacterId > 0)
            {
                try
                {
                    var connStr = Infrastructure.SqliteDatabaseBootstrap.Initialize(
                        Infrastructure.ServerPaths.DatabasePath, Infrastructure.ServerPaths.SchemaFilePath);
                    activeQuests = QuestService.LoadActiveQuests(connStr, record.CharacterId);
                }
                catch { }
            }
            for (int i = 0; i < 30; i++)
            {
                if (activeQuests != null && i < activeQuests.Count)
                {
                    writer.WriteUInt16(activeQuests[i].QuestId);
                    writer.WriteUInt32(activeQuests[i].TriggerValue);
                }
                else
                {
                    writer.WriteUInt16(0xFFFF);
                    writer.WriteInt32(0);
                }
            }

            {
                var p = initSnap.AckQuestDisplayIds;
                for (int j = 0; j < 16; j++)
                    writer.WriteByte(p != null && j < p.Length ? p[j] : (byte)0);
            }

            writer.WriteByte(initSnap.AckCharSlotIndex);

            if (record.Level <= 1)
            {
                writer.WriteByte(0x00);  // v15 flag
                writer.WriteByte(0x00);  // v16 count = 0
            }
            else
            {
                writer.WriteByte(0x00);  // v15 flag
                writer.WriteByte(0x01);  // v16 count = 1
                writer.WriteByte(0x4E);  // flagIndex = 78
            }

            writer.WriteUInt16(initSnap.AckFatigueBattery);
            writer.WriteUInt16(initSnap.AckFatigueGrownUpBuff);
            writer.WriteByte(initSnap.AckTradePunishFlag);
            writer.WriteUInt16(initSnap.AckExtraField86JP);
            {
                var r = initSnap.AckReserved8B;
                for (int j = 0; j < 8; j++)
                    writer.WriteByte(r != null && j < r.Length ? r[j] : (byte)0);
            }
            writer.WriteByte(initSnap.AckTutorialSkipable);
            writer.WriteUInt16(initSnap.AckPostTutorialU16);
            {
                var tail = initSnap.AckUnreadTail;
                int tailLen = tail != null ? tail.Length : 22;
                for (int j = 0; j < tailLen; j++)
                    writer.WriteByte(tail != null && j < tail.Length ? tail[j] : (byte)0);
            }

            body = writer.ToArray();
            return true;
        }
    }
}
