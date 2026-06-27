using System;
using System.Collections.Generic;
using DfoServer.Game.Dungeon;
using DfoServer.GameWorld;
using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public static class DungeonNotificationBuilder
    {
        // NOTI 28 (0x001C) DUNGEON_INFO
        public static byte[] BuildDungeonInfo(
            int dungeonId,
            byte difficulty,
            byte modeFlag = 0,
            byte bossX = 0,
            byte bossY = 0,
            byte hellPartyFlag0 = 0xFF,
            byte hellPartyFlag1 = 0xFF,
            byte dungeonMode = 0,
            IReadOnlyList<IReadOnlyList<(byte, byte)>> extraPairGroups = null,
            ushort value0 = 0x0000,
            ushort value1 = 0x000C,
            byte value2 = 0,
            byte flagA = 0,
            uint packetSeed = 0xFFFFFFFFu,
            byte paramA = 0,
            byte paramB = 0,
            byte paramC = 0,
            byte tailFlag0 = 0,
            byte tailFlag1 = 0,
            byte tailFlag2 = 0,
            uint tailReserved = 0)
        {
            var writer = new GamePacketWriter();

            writer.WriteInt16((short)dungeonId);
            writer.WriteByte(difficulty);
            writer.WriteByte(modeFlag);
            writer.WriteByte(bossX);
            writer.WriteByte(bossY);
            writer.WriteByte(hellPartyFlag0);
            writer.WriteByte(hellPartyFlag1);
            writer.WriteByte(dungeonMode);

            var groupCount = extraPairGroups == null ? 0 : extraPairGroups.Count;
            writer.WriteByte((byte)groupCount);
            for (var gi = 0; gi < groupCount; gi++)
            {
                var group = extraPairGroups[gi];
                writer.WriteByte((byte)group.Count);
                for (var pi = 0; pi < group.Count; pi++)
                {
                    var pair = group[pi];
                    writer.WriteByte(pair.Item1);
                    writer.WriteByte(pair.Item2);
                }
            }

            writer.WriteUInt16(value0);
            writer.WriteUInt16(value1);
            writer.WriteByte(value2);
            writer.WriteByte(flagA);
            writer.WriteInt32(unchecked((int)packetSeed));
            writer.WriteByte(paramA);
            writer.WriteByte(paramB);
            writer.WriteByte(paramC);
            writer.WriteByte(tailFlag0);
            writer.WriteByte(tailFlag1);
            writer.WriteByte(tailFlag2);
            writer.WriteInt32(unchecked((int)tailReserved));
            return writer.ToArray();
        }

        // NOTI 29 (0x001D) START_MAP
        public static byte[] BuildStartMap(
            Dungeon.MazeSumInfo maze,
            ushort firstMonsterSequence,
            int randomSeed = 0,
            byte fogOrModeFlag = 0,
            byte abyssGuardianType = 0,
            byte reserved0 = 0,
            uint stateValue0 = 1,
            byte stateValue1 = 1,
            byte fogFlag = 0,
            byte partyMemberIndex = 0xFF,
            IReadOnlyList<Game.Dungeon.PassiveObjectDropEntry> extraEntries = null,
            IReadOnlyList<Game.Dungeon.RidableObjectSpawnEntry> ridableEntries = null)
        {
            var writer = new GamePacketWriter();

            writer.WriteByte((byte)maze.X);
            writer.WriteByte((byte)maze.Y);
            writer.WriteByte(fogOrModeFlag);
            writer.WriteInt32(randomSeed);
            writer.WriteByte(abyssGuardianType);
            writer.WriteByte(reserved0);
            writer.WriteInt32(unchecked((int)stateValue0));
            writer.WriteByte(stateValue1);

            writer.WriteUInt16((ushort)maze.Index);
            writer.WriteByte((byte)maze.Monsters.Count);

            int normalIndex = 0;
            int apcIndex = 0;
            for (var i = 0; i < maze.Monsters.Count; i++)
            {
                var monster = maze.Monsters[i];
                bool isApc = monster.Type >= 5;

                writer.WriteUInt16(0x0000);
                writer.WriteInt32(isApc ? apcIndex++ : normalIndex++);
                writer.WriteUInt16((ushort)(firstMonsterSequence + i + 1));
                writer.WriteInt32(monster.Code);
                writer.WriteByte(monster.Level);
                writer.WriteByte(monster.Type);
                writer.WriteByte(0x00);
                writer.WriteByte(0x00);
                writer.WriteInt32(0x00000000);
            }

            // extra entries: passive object pre-generated drops (19B each)
            var extraCount = extraEntries?.Count ?? 0;
            writer.WriteByte((byte)extraCount);
            for (int i = 0; i < extraCount; i++)
            {
                var e = extraEntries[i];
                writer.WriteByte(e.ObjectIndex);     // +0  passive object index
                writer.WriteUInt16(e.GlobalSeq);     // +1  global sequence
                writer.WriteUInt32(e.ItemId);        // +3  item template id
                writer.WriteUInt32(e.StackCount);    // +7  stack count
                writer.WriteUInt16(e.Endurance);     // +11 endurance
                writer.WriteByte(0);                 // +13 amplify type
                writer.WriteUInt16(0);               // +14 amplify value
                writer.WriteUInt16(0);               // +16 extended
                writer.WriteByte(0);                 // +18 extended
            }

            writer.WriteByte(fogFlag);

            // ridable object spawn entries
            var ridableForThisRoom = new System.Collections.Generic.List<Game.Dungeon.RidableObjectSpawnEntry>();
            if (ridableEntries != null)
                foreach (var r in ridableEntries)
                    ridableForThisRoom.Add(r);

            if (ridableForThisRoom.Count > 0)
            {
                writer.WriteByte(1);                                     // groupCount = 1
                writer.WriteByte((byte)ridableForThisRoom.Count);        // objectsInGroup
                foreach (var r in ridableForThisRoom)
                {
                    writer.WriteInt32(r.PosX);
                    writer.WriteInt32(r.PosY);
                    writer.WriteInt32(r.ObjectIndex);
                    writer.WriteInt32(r.Faction);
                    writer.WriteInt32(0);
                }
            }
            else
            {
                writer.WriteByte(0);                                     // groupCount = 0
            }

            writer.WriteByte(partyMemberIndex);

            return writer.ToArray();
        }

        public static byte[] BuildStartMapRevisit(Dungeon.MazeSumInfo maze, uint seed)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte((byte)maze.X);
            writer.WriteByte((byte)maze.Y);
            writer.WriteByte(0);                      // fogOrModeFlag
            writer.WriteInt32(unchecked((int)seed));
            writer.WriteByte(0);                      // abyssGuardianType
            writer.WriteByte(0);                      // reserved0
            writer.WriteInt32(1);                     // stateValue0
            writer.WriteByte(0);                      // stateValue1 = 0 (revisit)
            writer.WriteByte(0x00);                   // fogFlag
            writer.WriteByte(0xFF);                   // partyMemberIndex
            return writer.ToArray();
        }

        // bodyLen = 3 + dropCount × 39 + 4
        public static byte[] BuildMonsterDie(ushort monsterSeqId, IReadOnlyList<DropInfo> drops, ushort ownerActorId)
        {
            var w = new GamePacketWriter();

            w.WriteUInt16(monsterSeqId);
            var dropCount = drops?.Count ?? 0;
            w.WriteByte((byte)dropCount);

            for (int i = 0; i < dropCount; i++)
            {
                var d = drops[i];
                w.WriteUInt16(d.SceneSlot);     // +0  sceneSlot
                w.WriteUInt32(d.TemplateId);    // +2  templateId (0=gold)
                w.WriteByte(d.UpgradeLevel);    // +6  upgradeLevel
                w.WriteUInt32(d.StackCount);    // +7  stackCount
                w.WriteUInt16(d.Endurance);     // +11 endurance
                w.WriteUInt32(0);               // +13 sealFlag
                w.WriteByte(0);                 // +17 refineLevel
                w.WriteByte(0);                 // +18 separateSign
                w.WriteUInt16(0);               // +19 amplifyAttr
                w.WriteUInt32(0);               // +21 enchantCardId
                w.WriteByte(0);                 // +25 socketCount
                w.WriteUInt16(0);               // +26 extra16
                w.WriteByte(0);                 // +28 listCount
                w.WriteZeroBytes(8);            // +29 tailPadding (8B)
                w.WriteUInt16(ownerActorId);    // +37 ownerActorId
            }

            // trailer 4B
            w.WriteByte(0x00);
            w.WriteByte(0x00);
            w.WriteByte(0xFF);
            w.WriteByte(0x00);

            return w.ToArray();
        }

        public static byte[] BuildEnableClearDungeon()
        {
            return new byte[] { 0x00 };
        }

        public static byte[] BuildPlayResult(ushort userId, int bossCode, uint totalExp, bool allKill)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x63);
            writer.WriteUInt32(totalExp);
            writer.WriteByte(0x00);              // flagB
            writer.WriteByte(0x63);
            writer.WriteByte(allKill ? (byte)1 : (byte)0);  // allKillFlag
            if (bossCode > 0)
            {
                writer.WriteByte(0x01);                      // killCount = 1
                writer.WriteUInt16((ushort)bossCode);
                writer.WriteUInt32(totalExp);                 // actorScore
                writer.WriteByte(0x01);                      // isMyKill = 1
            }
            else
            {
                writer.WriteByte(0x00);                      // killCount = 0
            }
            return writer.ToArray();
        }

        public static byte[] BuildExp(byte level, uint totalExp, ushort remainSp = 0, ushort remainTp = 0)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(level);
            writer.WriteUInt32(totalExp);
            writer.WriteUInt32(0);
            writer.WriteUInt32(0);
            writer.WriteUInt16(remainSp);
            writer.WriteUInt16(remainSp);
            writer.WriteUInt16(remainTp);
            writer.WriteUInt16(0);
            writer.WriteUInt32(0);
            writer.WriteUInt32(0);
            writer.WriteByte(0x00);
            writer.WriteUInt32(0);
            writer.WriteUInt32(0);
            writer.WriteUInt32(0);               // #14 v131
            writer.WriteUInt32(0);
            writer.WriteUInt32(0);
            writer.WriteByte(0x00);              // #17 pairCount=0
            writer.WriteUInt32(0);
            writer.WriteUInt32(0);               // #19 v134
            writer.WriteUInt32(0);
            writer.WriteUInt32(0);               // #20 v133
            writer.WriteUInt32(0);               // #21 v125
            writer.WriteUInt32(0);
            writer.WriteUInt32(0);               // #23 v126
            writer.WriteUInt32(0);
            writer.WriteUInt32(0);
            writer.WriteZeroBytes(8);
            return writer.ToArray();
        }

        //
        //
        //
        // finalize (sub_1F595D0): grandTotal = expA + endValue + Σbonus
        public static byte[] BuildClearDungeonReward(uint totalExp, int totalGold,
            int goldCardCost = 0, int freeCardGold = 0, int freeCardItemId = 0, int freeCardItemCount = 0)
        {
            var w = new GamePacketWriter();

            // === BASE BLOCK (117B = 4u32 + 1u8 + 25u32) ===
            w.WriteUInt32(totalExp);
            w.WriteInt32(totalGold);
            w.WriteUInt32(0);
            w.WriteUInt32(0);               // #4  baseExp → bonus[0]
            w.WriteByte(0);
            for (int i = 0; i < 25; i++)
                w.WriteInt32(0);

            // === ADD/MUL BONUS (2B) ===
            w.WriteByte(0);
            w.WriteByte(0);

            // === POST-BASE (32B = 8u32) ===
            for (int i = 0; i < 8; i++)
                w.WriteInt32(0);

            // === SCORE (16B = 4u32) ===
            w.WriteInt32(0);                // score[0] (dead)
            w.WriteInt32(goldCardCost);
            w.WriteInt32(0);                // score[2] (dead)
            w.WriteInt32(goldCardCost);

            // === QUEST (4B) ===
            w.WriteUInt32(0);

            // === DROPS (1B) ===
            w.WriteByte(0);

            byte freeCnt = (byte)(freeCardItemId > 0 ? 2 : 1);
            w.WriteByte(freeCnt);           // seat0.cnt
            w.WriteInt32(0);
            w.WriteInt32(freeCardGold);
            if (freeCardItemId > 0)
            {
                w.WriteInt32(freeCardItemId);
                w.WriteInt32(freeCardItemCount);
            }
            for (int i = 1; i < 8; i++)
                w.WriteByte(0);

            // === TOTAL REWARD SUM (4B) ===
            w.WriteInt32(0);

            // === BUFF TABLE 2 (8B) ===
            for (int i = 0; i < 8; i++)
                w.WriteByte(0);

            for (int i = 0; i < 8; i++)
                w.WriteByte(0);

            // === TAIL (14B) ===
            w.WriteInt32(0);                // cardItemId
            w.WriteByte(0);                 // endFlagA
            w.WriteByte(0);                 // endFlagB
            w.WriteUInt32(0);
            w.WriteInt32(0);

            return w.ToArray();             // 222B
        }
    }
}