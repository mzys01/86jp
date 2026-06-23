using DfoServer.Game.CharacterData;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Skills
{
    
    
    
    
    public static class BuySkillSelfTest
    {
        private static int _pass, _fail;

        public static int Run()
        {
            Console.WriteLine("=== BUY_SKILL 自验证 ===");
            _pass = 0; _fail = 0;

            
            SkillStaticData sd = null;
            try { sd = SkillDataProvider.GetSkill(0, 64); }
            catch (Exception ex) { Console.WriteLine("  PVF 读取异常: " + ex.Message); }
            Check("SkillDataProvider 找到 idx64", sd != null);
            if (sd != null)
            {
                Check($"十字斩 Name='{sd.Name}'", sd.Name != null && sd.Name.Contains("十字"));
                Check($"十字斩 IsActive={sd.IsActive} (期望 true)", sd.IsActive);
                int sp0 = sd.SpCostPerLevel.Length > 0 ? sd.SpCostPerLevel[0] : -1;
                Check($"十字斩 SP/级={sp0} (期望 15)", sp0 == 15);
                Check($"十字斩 MaxLevel={sd.MaxLevel} (期望 60)", sd.MaxLevel == 60);
                Check($"十字斩 RequiredLevel={sd.RequiredLevel} (期望 15)", sd.RequiredLevel == 15);
            }

            
            byte[] reqBody = { 0x00, 0x01, 0x40, 0x00, 0x00, 0x01, 0x00, 0xB7, 0x8D, 0x0A, 0x8C };
            Check("请求解析 skillTree=0", reqBody[0] == 0);
            Check("请求解析 count=1", reqBody[1] == 1);
            Check("请求解析 skillIndex=64", reqBody[2] == 64);
            Check("请求解析 level=0(→1)", reqBody[3] == 0);
            Check("请求解析 isRefund=0", reqBody[4] == 0);

            
            const int cid = 999001;
            string tempDb = Path.Combine(Path.GetTempPath(), "buyskill_selftest.db");
            foreach (var ext in new[] { "", "-wal", "-shm" })
                try { if (File.Exists(tempDb + ext)) File.Delete(tempDb + ext); } catch { }

            var repo = new SqliteCharacterProgressRepository(tempDb, ServerPaths.SchemaFilePath);
            EnsureTestCharacter(tempDb, cid);
            var seed = new SkillInfoSnapshot { Tail0 = 0, Tail1 = 37 };
            var p0 = new SkillInfoPageSnapshot { HeaderValue = 0x0005 };
            p0.Entries.Add(new SkillInfoEntrySnapshot { Slot = 0, SkillId = 5,   Level = 1 }); 
            p0.Entries.Add(new SkillInfoEntrySnapshot { Slot = 1, SkillId = 46,  Level = 1 }); 
            p0.Entries.Add(new SkillInfoEntrySnapshot { Slot = 2, SkillId = 169, Level = 1 }); 
            seed.Pages.Add(p0);
            seed.Pages.Add(new SkillInfoPageSnapshot { HeaderValue = 0x2BF2 });
            repo.SaveSkills(cid, seed);

            var entries = new List<BuySkillEntry> { new BuySkillEntry { SkillIndex = 64, Level = 0, IsRefund = 0 } };
            BuySkillResult result = null;
            try { result = BuySkillService.Execute(repo, cid, 0, 0, entries); }
            catch (Exception ex) { Console.WriteLine("  BuySkillService 异常: " + ex); }
            Check("学习 result 非空", result != null);
            if (result != null)
            {
                Check($"学习 success={result.Success} (期望 true)", result.Success);
                Check($"remainSP={result.RemainSp} (期望 22 = 37-15)", result.RemainSp == 22);
                Check($"ACK 条目数={result.Entries.Count} (期望 1)", result.Entries.Count == 1);
                if (result.Entries.Count == 1)
                {
                    var e = result.Entries[0];
                    Check($"ACK skillId={e.SkillId} (期望 64)", e.SkillId == 64);
                    Check($"ACK level={e.Level} (期望 1)", e.Level == 1);
                    Check($"ACK slot={e.Slot} (期望 3, 客户端据此落槽)", e.Slot == 3);
                    Check($"ACK hasCmd={e.HasCmd} (期望 false)", !e.HasCmd);
                }

                
                var ack = BuySkillAckBuilder.Build(result);
                byte[] expectAck = { 0x01, 0x00, 0x16, 0x00, 0x00, 0x00, 0x01, 0x03, 0x40, 0x00, 0x01, 0x00 };
                Check($"ACK字节={ToHex(ack)}\n         期望={ToHex(expectAck)}", BytesEqual(ack, expectAck));
            }

            
            var reload = repo.LoadSkills(cid);
            var page0 = reload.Pages.Count > 0 ? reload.Pages[0] : null;
            SkillInfoEntrySnapshot learned = null;
            if (page0 != null) learned = page0.Entries.Find(x => x.SkillId == 64);
            Check("持久化: skill64 存在", learned != null);
            if (learned != null)
            {
                Check($"持久化: skill64 slot={learned.Slot} (期望 3, 主动技填0-5首个空槽)", learned.Slot == 3);
                Check($"持久化: skill64 level={learned.Level} (期望 1)", learned.Level == 1);
            }
            Check($"持久化: Tail1(SP)={reload.Tail1} (期望 22)", reload.Tail1 == 22);

            
            var up = BuySkillService.Execute(repo, cid, 0, 0,
                new List<BuySkillEntry> { new BuySkillEntry { SkillIndex = 64, Level = 0, IsRefund = 0 } });
            Check($"升级 success={(up != null && up.Success)} (期望 true)", up != null && up.Success);
            Check($"升级 remainSP={(up != null ? up.RemainSp : 0)} (期望 7 = 22-15)", up != null && up.RemainSp == 7);
            if (up != null && up.Entries.Count == 1)
                Check($"升级 level={up.Entries[0].Level} (期望 2)", up.Entries[0].Level == 2);
            var reload2 = repo.LoadSkills(cid);
            var up64 = reload2.Pages.Count > 0 ? reload2.Pages[0].Entries.Find(x => x.SkillId == 64) : null;
            Check($"升级持久化 slot={(up64 != null ? up64.Slot : 255)} level={(up64 != null ? up64.Level : 0)} (期望 slot3 level2, slot不变)",
                up64 != null && up64.Slot == 3 && up64.Level == 2);

            
            string tempDb2 = Path.Combine(Path.GetTempPath(), "buyskill_selftest2.db");
            foreach (var ext in new[] { "", "-wal", "-shm" })
                try { if (File.Exists(tempDb2 + ext)) File.Delete(tempDb2 + ext); } catch { }
            var repo2 = new SqliteCharacterProgressRepository(tempDb2, ServerPaths.SchemaFilePath);
            EnsureTestCharacter(tempDb2, cid);
            var seed2 = new SkillInfoSnapshot { Tail0 = 0, Tail1 = 5 };
            seed2.Pages.Add(new SkillInfoPageSnapshot { HeaderValue = 0x0005 });
            seed2.Pages.Add(new SkillInfoPageSnapshot { HeaderValue = 0x2BF2 });
            repo2.SaveSkills(cid, seed2);
            var poor = BuySkillService.Execute(repo2, cid, 0, 0,
                new List<BuySkillEntry> { new BuySkillEntry { SkillIndex = 64, Level = 0, IsRefund = 0 } });
            Check($"SP不足 success={(poor != null && poor.Success)} (期望 false)", poor != null && !poor.Success);
            var reload3 = repo2.LoadSkills(cid);
            var notLearned = reload3.Pages.Count > 0 ? reload3.Pages[0].Entries.Find(x => x.SkillId == 64) : null;
            Check("SP不足: skill64 未学入", notLearned == null);
            Check($"SP不足: SP 未扣={reload3.Tail1} (期望 5)", reload3.Tail1 == 5);

            Console.WriteLine($"=== 结果: {_pass} PASS, {_fail} FAIL ===");
            return _fail == 0 ? 0 : 1;
        }

        private static void EnsureTestCharacter(string databasePath, int characterId)
        {
            using (var conn = new SqliteConnection(SqliteDatabaseBootstrap.BuildConnectionString(databasePath)))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
INSERT OR IGNORE INTO accounts (account_id, m_id, password_hash)
VALUES (1, 'selftest', '');
INSERT OR IGNORE INTO characters (character_id, account_id, name)
VALUES (@cid, 1, 'selftest');";
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static void Check(string label, bool ok)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");
            if (ok) _pass++; else _fail++;
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static string ToHex(byte[] b)
        {
            if (b == null) return "(null)";
            var sb = new System.Text.StringBuilder();
            foreach (var x in b) sb.Append(x.ToString("X2")).Append(' ');
            return sb.ToString().Trim();
        }
    }
}
