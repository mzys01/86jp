using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Network.Builders;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.CharacterData;
using System;
using System.IO;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class HonorLevelSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== HONOR_LEVEL selftest ===");
            try
            {
                var accountChars = new[]
                {
                    new CharacterRecord { CharacterId = 1, Level = 85, Exp = 500000000u },
                    new CharacterRecord { CharacterId = 2, Level = 86, Exp = 0u },
                    new CharacterRecord { CharacterId = 3, Level = 86, Exp = 10000000u },
                    new CharacterRecord { CharacterId = 4, Level = 86, Exp = 25000000u },
                    new CharacterRecord { CharacterId = 5, Level = 86, Exp = 10000000u, Deleted = true },
                };
                var mixed = HonorLevelDataProvider.CalculateFromHonorExp(35000000u, accountChars);
                Check("honor total exp comes from account honor progress, not character total exp", mixed.TotalHonorExp == 35000000u);
                Check("honor exp is current level segment exp", mixed.HonorExp == 5000000u);
                Check("honor level uses PVF segment requirements", mixed.HonorLevel == 3);
                Check("honor grade maps through PVF grade sections", mixed.HonorGrade == 1);
                Check("full-level count ignores deleted and non-max characters", mixed.FullLevelCharacterCount == 3);

                var capped = HonorLevelDataProvider.CalculateFromHonorExp(ulong.MaxValue, new[]
                {
                    new CharacterRecord { CharacterId = 6, Level = 86, Exp = uint.MaxValue },
                });
                Check("honor total exp is capped by summed PVF segment requirements", capped.TotalHonorExp == HonorLevelDataProvider.MaxTotalHonorExp);
                Check("honor current exp is capped by PVF [maxexp on maxlevel]", capped.HonorExp == (uint)HonorLevelDataProvider.MaxExpOnMaxLevel);
                Check("capped honor reaches PVF max level", capped.HonorLevel == 59);
                Check("capped honor reaches PVF max grade", capped.HonorGrade == 6);

                Check("honor exp gained while already max level",
                    HonorLevelDataProvider.CalculateHonorExpGain(86, 123456u, 1000u) == 1000u);
                var maxEntryExp = (uint)DfoServer.Game.Dungeon.ExpTableProvider.GetLevelThreshold(DfoServer.Game.Dungeon.ExpTableProvider.MaxLevel - 1);
                Check("honor exp gained only for overflow when reaching max level",
                    HonorLevelDataProvider.CalculateHonorExpGain(85, maxEntryExp - 100u, 250u) == 150u);


                var tempDb = Path.Combine(Path.GetTempPath(), "dfo_honor_selftest_" + Guid.NewGuid().ToString("N") + ".db");
                try
                {
                    var repo = new HonorLevelProgressRepository(tempDb, ServerPaths.SchemaFilePath);
                    var connStr = SqliteDatabaseBootstrap.BuildConnectionString(tempDb);
                    using (var conn = new SqliteConnection(connStr))
                    {
                        conn.Open();
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = @"
INSERT INTO accounts(account_id, m_id) VALUES(100, 'honor_selftest');
INSERT INTO characters(character_id, account_id, name, level) VALUES(10001, 100, 'honor_char', 86);
INSERT INTO character_subtype1_fields(character_id, progress1, progress2) VALUES(10001, 59, 123456789);";
                            cmd.ExecuteNonQuery();
                        }
                    }

                    var emptyAccount = repo.LoadSummary(100);
                    Check("account honor repository ignores legacy character progress2", emptyAccount.TotalHonorExp == 0 && emptyAccount.HonorLevel == 1);
                    var afterAdd = repo.AddHonorExp(100, 273u);
                    Check("account honor repository stores account scoped total exp", afterAdd.TotalHonorExp == 273u && afterAdd.HonorExp == 273u);
                    using (var conn = new SqliteConnection(connStr))
                    {
                        conn.Open();
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = "SELECT total_exp FROM account_honor_level WHERE account_id=100;";
                            Check("account_honor_level row is the persisted honor source", Convert.ToInt64(cmd.ExecuteScalar()) == 273L);
                        }
                    }
                }
                finally
                {
                    try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }
                }

                var body = HonorLevelPacketBuilder.BuildInfoBody(mixed);
                Check("HONOR_LEVEL_INFO body is 8 bytes", body.Length == 8);
                Check("HONOR_LEVEL_INFO first u32 is honor level", BitConverter.ToUInt32(body, 0) == mixed.HonorLevel);
                Check("HONOR_LEVEL_INFO second u32 is honor exp", BitConverter.ToUInt32(body, 4) == mixed.HonorExp);

                var addition = new UserInfoAdditionSnapshot { ManageLevel = 4, FlagByte = 4 };
                HonorLevelDataProvider.ApplyToUserInfoAddition(addition, capped);
                Check("honor sync writes subtype1 progress1 as honor level", addition.Progress1 == capped.HonorLevel);
                Check("honor sync writes subtype1 progress2 as honor exp", addition.Progress2 == capped.HonorExp);
                Check("honor sync does not touch adventure manage level", addition.ManageLevel == 4 && addition.FlagByte == 4);

                var tail = new UserInfoMinimumTailSnapshot();
                HonorLevelDataProvider.ApplyToSubtype0Tail(tail, capped);
                Check("honor sync writes subtype0 progressA as honor level", tail.ProgressA == capped.HonorLevel);
                Check("honor sync writes subtype0 progressB as honor exp", tail.ProgressB == capped.HonorExp);

                var rosterBody = AccountCharacterListBodyBuilder.Build(new[]
                {
                    new CharacterRecord { CharacterId = 8, Name = new byte[] { (byte)'a' }, Job = 1, GrowType = 0, Level = 86 },
                    new CharacterRecord { CharacterId = 9, Name = new byte[] { (byte)'b' }, Job = 2, GrowType = 0, Level = 1 },
                }, new GetUserInfoTemplate { GateOrCount1 = 32, GateOrCount2 = 32 }, out _, mixed);
                var rosterNeedle = new byte[]
                {
                    mixed.HonorLevel, 0, 0, 0,
                    (byte)(mixed.HonorExp & 0xFF), (byte)((mixed.HonorExp >> 8) & 0xFF),
                    (byte)((mixed.HonorExp >> 16) & 0xFF), (byte)((mixed.HonorExp >> 24) & 0xFF),
                    0, 0
                };
                var firstRosterHonor = IndexOf(rosterBody, rosterNeedle);
                Check("roster subtype2 writes shared honor display fields", firstRosterHonor >= 0);
                Check("roster subtype2 writes shared honor for every listed character", IndexOf(rosterBody, rosterNeedle, firstRosterHonor + 1) >= 0);

                var roundTripRecord = new CharacterRecord { CharacterId = 7, Name = new byte[] { (byte)'x' }, Job = 0, Level = 86, Subtype0Tail = tail };
                var roundTripBody = UserInfoSubtype0Builder.BuildNotificationBody(roundTripRecord);
                var tailOffset = roundTripBody.Length - UserInfoMinimumTailSnapshot.TailLength;
                var parsedTail = UserInfoMinimumTailSnapshot.FromBytes(roundTripBody.AsSpan(tailOffset, UserInfoMinimumTailSnapshot.TailLength).ToArray());
                Check("subtype0 builder keeps honor level at progressA offset", parsedTail.ProgressA == capped.HonorLevel);
                Check("subtype0 builder keeps honor exp at progressB offset", parsedTail.ProgressB == capped.HonorExp);

                Console.WriteLine("HonorLevelSelfTest OK");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("HonorLevelSelfTest FAILED: " + ex.Message);
                return 1;
            }
        }

        private static int IndexOf(byte[] haystack, byte[] needle, int start = 0)
        {
            if (haystack == null || needle == null || needle.Length == 0)
                return -1;
            for (var i = Math.Max(0, start); i <= haystack.Length - needle.Length; i++)
            {
                var ok = true;
                for (var j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        ok = false;
                        break;
                    }
                }
                if (ok)
                    return i;
            }
            return -1;
        }

        private static void Check(string name, bool condition)
        {
            if (!condition)
                throw new InvalidOperationException(name);
            Console.WriteLine("  PASS " + name);
        }
    }
}

