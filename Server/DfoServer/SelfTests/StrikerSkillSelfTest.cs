using DfoServer.Game.Accounts;
using DfoServer.Game.Mercenary;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DfoServer.SelfTests
{
    public static class StrikerSkillSelfTest
    {
        public static int Run()
        {
            _failures = 0;
            Console.WriteLine("=== Striker skill self-test ===");

            var all = StrikerSkillDataProvider.GetAll();
            Check("PVF striker skill entries loaded", all.Count > 0);

            var mage = StrikerSkillDataProvider.GetAvailableSkills(job: 3, growType: 1, level: 86);
            Check("mage growType=1 Lv86 has striker skills", mage.Count > 0);

            var fighter = StrikerSkillDataProvider.GetAvailableSkills(job: 1, growType: 1, level: 86);
            Check("fighter growType=1 Lv86 has striker skills", fighter.Count > 0);

            var invalidGrow = StrikerSkillDataProvider.GetAvailableSkills(job: 3, growType: 0, level: 86);
            Check("mage growType=0 has no striker skills in PVF", invalidGrow.Count == 0);

            var packedSwordman = StrikerSkillDataProvider.GetAvailableSkills(job: 0, growType: 35, level: 86);
            Check("packed swordman growType=35 maps to PVF growType=3", packedSwordman.Count > 0);

            CheckMercenarySupportRepository();
            CheckMercenaryWireSlotMapping();
            CheckAdventureGroupLevelCalculation();
            CheckStrikerSupportSkillLevels();
            CheckMainApplyTagRecordPatch();

            Console.WriteLine("sample: " + string.Join(", ", mage.Take(3).Select(x => $"{x.SkillIndex}/{x.ComboIndex}:{x.SkillName ?? "?"}")));
            return _failures == 0 ? 0 : 1;
        }

        private static int _failures;

        private static void CheckMercenaryWireSlotMapping()
        {
            var roster = new List<DfoServer.Game.Characters.CharacterRecord>
            {
                new DfoServer.Game.Characters.CharacterRecord { CharacterId = 1002, Level = 86 },
                new DfoServer.Game.Characters.CharacterRecord { CharacterId = 1003, Level = 86 },
                new DfoServer.Game.Characters.CharacterRecord { CharacterId = 1004, Level = 86 },
                new DfoServer.Game.Characters.CharacterRecord { CharacterId = 1005, Level = 86 },
            };

            var slot2 = MercenaryHandler.FindCandidateByWireIndexForTest(roster, activeCharacterId: 1003, wireIndex: 2);
            var active = MercenaryHandler.FindCandidateByWireIndexForTest(roster, activeCharacterId: 1003, wireIndex: 1);

            Check("mercenary wire slot uses account roster index", slot2 != null && slot2.CharacterId == 1004);
            Check("mercenary wire slot rejects active character", active == null);
        }

        private static void CheckAdventureGroupLevelCalculation()
        {
            var roster = new List<DfoServer.Game.Characters.CharacterRecord>();
            for (var i = 0; i < 8; i++)
                roster.Add(new DfoServer.Game.Characters.CharacterRecord { CharacterId = 92000 + i, Level = 86 });
            roster.Add(new DfoServer.Game.Characters.CharacterRecord { CharacterId = 92010, Level = 1 });
            roster.Add(new DfoServer.Game.Characters.CharacterRecord { CharacterId = 92011, Level = 1 });

            const int expectedLevel86Point = 10785; // 40 到 86 级逐级累加。
            const int expectedTotalPoint = expectedLevel86Point * 8; // 8 个 86 级角色贡献。
            const byte expectedManageLevel = 7;
            const ushort expectedManageOption = 125;

            var level86Summary = AdventureGroupDataProvider.Calculate(new[]
            {
                new DfoServer.Game.Characters.CharacterRecord { CharacterId = 92100, Level = 86 }
            });

            var summary = AdventureGroupDataProvider.Calculate(roster);
            Check("adventure group point accumulates level40-86 bonus for one Lv86 character", level86Summary.TotalPoint == expectedLevel86Point);
            Check("adventure group point uses cumulative PVF character level table", summary.TotalPoint == expectedTotalPoint);
            Check("adventure group level uses PVF account point thresholds", summary.ManageLevel == expectedManageLevel);
            Check("adventure group exp bonus uses account manage level", summary.ExpBonusPercent == 10);
            Check("adventure group manage option uses account manage level", summary.ManageOption == expectedManageOption);

            var body = AccountCharacterListBodyBuilder.Build(roster, new GetUserInfoTemplate
            {
                GateOrCount1 = 32,
                GateOrCount2 = 32,
            }, out var bodySummary);
            Check("USERINFO subtype2 writes adventure group level", body.Length > 10 && body[5] == bodySummary.ManageLevel && body[5] == expectedManageLevel);
            Check("USERINFO subtype2 writes adventure group point", body.Length > 10 && BitConverter.ToInt32(body, 6) == expectedTotalPoint);

            var addition = new DfoServer.Game.SelectCharacter.UserInfoAdditionSnapshot
            {
                StatPhysicalAttack = 10,
                StatPhysicalDefense = 20,
                StatMagicalAttack = 30,
                StatMagicalDefense = 40,
            };
            AdventureGroupUserInfoSynchronizer.ApplyToUserInfoAddition(addition, roster);
            Check("USERINFO subtype1 applies account adventure group level", addition.ManageLevel == expectedManageLevel);
            Check("USERINFO subtype1 writes adventure group option index byte", addition.FlagByte == expectedManageLevel);
            Check("adventure group option does not modify base primary stats",
                addition.StatPhysicalAttack == 10 &&
                addition.StatPhysicalDefense == 20 &&
                addition.StatMagicalAttack == 30 &&
                addition.StatMagicalDefense == 40);
        }

        private static void CheckMercenarySupportRepository()
        {
            var tempDb = Path.Combine(Path.GetTempPath(), "dfo-striker-support-selftest-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                var connStr = SqliteDatabaseBootstrap.Initialize(tempDb, ServerPaths.SchemaFilePath);
                using (var conn = new SqliteConnection(connStr))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = @"
INSERT INTO characters (character_id, account_id, name, job, grow_type, level)
VALUES (91001, 1, 'owner', 3, 0, 1),
       (91002, 1, 'support', 0, 35, 86);";
                        cmd.ExecuteNonQuery();
                    }
                }

                var repo = new SqliteMercenarySupportRepository(tempDb, ServerPaths.SchemaFilePath);
                repo.Save(new MercenarySupportState
                {
                    OwnerCharacterId = 91001,
                    Slot = 0,
                    SupportCharacterId = 91002,
                    SkillId = 81,
                    StrikerSkillId = 3,
                });

                var loaded = repo.LoadSlot(91001, 0);
                Check("mercenary support state saved", loaded != null && loaded.SupportCharacterId == 91002 && loaded.SkillId == 81 && loaded.StrikerSkillId == 3);
                Check("mercenary support state enables subtype0 link", ReadSubtype0Link(tempDb, 91001) == "1/4/1");

                repo.Save(new MercenarySupportState
                {
                    OwnerCharacterId = 91001,
                    Slot = 0,
                    SupportCharacterId = 91002,
                    SkillId = 24,
                    StrikerSkillId = 1,
                });

                var overwritten = repo.LoadForOwner(91001).SingleOrDefault();
                Check("mercenary support state upserts by owner+slot", overwritten != null && overwritten.SkillId == 24 && overwritten.StrikerSkillId == 1);

                repo.Clear(91001, 0);
                Check("mercenary support state clears primary table", repo.LoadForOwner(91001).Count == 0);
                Check("mercenary support state clears subtype0 link", ReadSubtype0Link(tempDb, 91001) == "0/0/0");
            }
            finally
            {
                try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }
            }
        }

        private static string ReadSubtype0Link(string databasePath, int characterId)
        {
            var connStr = SqliteDatabaseBootstrap.BuildConnectionString(databasePath);
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(@"
SELECT link_slot_enabled, link_type_a, link_type_b
FROM character_subtype0_fields
WHERE character_id=@cid", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        if (!reader.Read())
                            return null;

                        return $"{reader.GetInt32(0)}/{reader.GetInt32(1)}/{reader.GetInt32(2)}";
                    }
                }
            }
        }

        private static void CheckStrikerSupportSkillLevels()
        {
            var sampleSkill = new StrikerSkillEntry
            {
                SkillIndex = 24,
                RequiredLevel = 60,
            };

            Check("striker support learned level uses real skill level",
                StrikerSupportSkillLevelSource.ResolveBaseLevel(
                    new Dictionary<ushort, byte> { { 24, 7 } },
                    sampleSkill) == 7);
            Check("striker support unlearned level defaults to one",
                StrikerSupportSkillLevelSource.ResolveBaseLevel(
                    new Dictionary<ushort, byte>(),
                    sampleSkill) == 1);
            Check("striker support explicit zero level defaults to one",
                StrikerSupportSkillLevelSource.ResolveBaseLevel(
                    new Dictionary<ushort, byte> { { 24, 0 } },
                    sampleSkill) == 1);
        }

        private static void CheckMainApplyTagRecordPatch()
        {
            var raw = new byte[1913];
            var tableOffset = 1738;
            var entryOffset = tableOffset + 2;
            var selectedOffset = tableOffset - 7;
            var appendedOffset = entryOffset + 41 * 4;
            raw[2] = 4;
            raw[6] = (byte)'2';
            raw[7] = (byte)'0';
            raw[8] = (byte)'0';
            raw[9] = (byte)'2';
            raw[10] = 86;
            raw[11] = 11;
            raw[12] = 0x14;
            raw[13] = 94;
            raw[14] = 0;
            raw[selectedOffset] = 0;
            raw[selectedOffset + 1] = 0;
            raw[tableOffset] = 41;
            raw[tableOffset + 1] = 0;

            for (var i = 0; i < raw[tableOffset]; i++)
            {
                var entry = entryOffset + i * 4;
                var skillId = i == 0 ? 86 : 90 + i;
                raw[entry] = (byte)(i + 2);
                raw[entry + 1] = (byte)(skillId & 0xFF);
                raw[entry + 2] = (byte)((skillId >> 8) & 0xFF);
                raw[entry + 3] = (byte)(1 + i);
            }

            var patched = StrikerSupportTagCharacterPacketBuilder.PatchSelectedSkillIntoTagRecord(raw, new MercenarySupportState
            {
                OwnerCharacterId = 91001,
                Slot = 0,
                SupportCharacterId = 1002,
                SkillId = 24,
                StrikerSkillId = 1,
            });
            var expectedLevel = StrikerSupportSkillLevelSource.ResolveBaseLevel(1002, 24);

            Check("0x019F main apply patch keeps record length", patched.Length == raw.Length);
            Check("0x019F main apply patch appends missing support skill",
                patched[tableOffset] == 42 &&
                patched[appendedOffset] == 1 &&
                patched[appendedOffset + 1] == 24 &&
                patched[appendedOffset + 2] == 0 &&
                patched[appendedOffset + 3] == expectedLevel);
            Check("0x019F main apply patch keeps header character level byte",
                patched[10] == 86);
            Check("0x019F main apply patch rewrites display job context",
                patched[11] == 0);
            Check("0x019F main apply patch updates packed grow and selected skill",
                patched[12] == 0x13 &&
                patched[13] == 24 &&
                patched[14] == 0);
            Check("0x019F main apply patch updates traced selected skill",
                patched[selectedOffset] == 24 &&
                patched[selectedOffset + 1] == 0);
            var asuraPatched = StrikerSupportTagCharacterPacketBuilder.PatchSelectedSkillIntoTagRecord(raw, new MercenarySupportState
            {
                OwnerCharacterId = 91001,
                Slot = 0,
                SupportCharacterId = 1009,
                SkillId = 74,
                StrikerSkillId = 43,
            });
            var asuraRequiredLevel = StrikerSupportSkillLevelSource.ResolveBaseLevel(1009, 47);
            var asuraLevel = StrikerSupportSkillLevelSource.ResolveBaseLevel(1009, 74);
            var asuraRequiredOffset = appendedOffset;
            var asuraSelectedOffset = appendedOffset + 4;
            Check("0x019F main apply patch appends striker required skill before selected skill",
                asuraPatched[tableOffset] == 43 &&
                asuraPatched[asuraRequiredOffset] == 47 &&
                asuraPatched[asuraRequiredOffset + 1] == 47 &&
                asuraPatched[asuraRequiredOffset + 2] == 0 &&
                asuraPatched[asuraRequiredOffset + 3] == asuraRequiredLevel &&
                asuraPatched[asuraSelectedOffset] == 43 &&
                asuraPatched[asuraSelectedOffset + 1] == 74 &&
                asuraPatched[asuraSelectedOffset + 2] == 0 &&
                asuraPatched[asuraSelectedOffset + 3] == asuraLevel);

            var fallback = StrikerSupportTagCharacterPacketBuilder.CloneTagCharacterRawRecordTemplate();
            var fallbackPatched = StrikerSupportTagCharacterPacketBuilder.PatchSelectedSkillIntoTagRecord(fallback, new MercenarySupportState
            {
                OwnerCharacterId = 91001,
                Slot = 0,
                SupportCharacterId = 1002,
                SkillId = 24,
                StrikerSkillId = 1,
            });

            Check("0x019F fallback template has stable record length", fallback.Length == 1913 && fallbackPatched.Length == fallback.Length);
            Check("0x019F fallback template supports selected skill patch",
                fallbackPatched[13] == 24 &&
                fallbackPatched[14] == 0);
            StrikerSupportTagCharacterPacketBuilder.PatchTagRecordCharacterId(patched, 91001);
            Check("0x019F owner mirror patch rewrites record character id",
                patched[0] == 0x79 &&
                patched[1] == 0x63);

            var missingEquipmentPatched = StrikerSupportTagCharacterPacketBuilder.PatchMissingTemplateEquipmentEntriesForTest(
                StrikerSupportTagCharacterPacketBuilder.CloneTagCharacterRawRecordTemplate(),
                11, 13, 15);
            var equipmentBlockOffset = FindAvatarEquipmentBlockOffsetForSelfTest(missingEquipmentPatched);
            Check("0x019F missing template equipment removal keeps record length",
                missingEquipmentPatched.Length == fallback.Length);
            Check("0x019F missing template equipment removal keeps slot0 default-avatar entries",
                equipmentBlockOffset > 0 &&
                missingEquipmentPatched[equipmentBlockOffset] == 0 &&
                missingEquipmentPatched[equipmentBlockOffset + 8 * 85] == 8);
            Check("0x019F missing template equipment removal removes optional slots only",
                equipmentBlockOffset > 0 &&
                missingEquipmentPatched[equipmentBlockOffset - 1] == 13 &&
                missingEquipmentPatched[equipmentBlockOffset + 9 * 85] == 11);

            var knightGrow2Defaults = StrikerSupportTagCharacterPacketBuilder.ResolveDefaultAvatarItemIdsForTest(12, 34);
            Check("0x019F default avatar lookup handles PVF header offset",
                knightGrow2Defaults != null &&
                knightGrow2Defaults.Length >= 11 &&
                knightGrow2Defaults[0] == 413550014 &&
                knightGrow2Defaults[3] == 413500014 &&
                knightGrow2Defaults[8] == 413580005 &&
                knightGrow2Defaults[9] == -1 &&
                knightGrow2Defaults[10] == -1);
        }

        private static int FindAvatarEquipmentBlockOffsetForSelfTest(byte[] rawRecord)
        {
            if (rawRecord == null)
                return -1;

            for (var offset = 0; offset + 8 * 85 + 5 <= rawRecord.Length; offset++)
            {
                var ok = true;
                for (var slot = 0; slot <= 8; slot++)
                {
                    var slotOffset = offset + slot * 85;
                    if (slotOffset + 4 >= rawRecord.Length || rawRecord[slotOffset] != slot)
                    {
                        ok = false;
                        break;
                    }

                    var itemId = BitConverter.ToInt32(rawRecord, slotOffset + 1);
                    if (itemId <= 0 || itemId > 500000000)
                    {
                        ok = false;
                        break;
                    }
                }

                if (ok)
                    return offset;
            }

            return -1;
        }

        private static void Check(string name, bool ok)
        {
            Console.WriteLine((ok ? "[PASS] " : "[FAIL] ") + name);
            if (!ok)
                _failures++;
        }
    }
}
