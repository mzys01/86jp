using System;
using System.Text.RegularExpressions;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.GameWorld;

namespace DfoServer.SelfTests
{
    public static class MonsterCardDropSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== MONSTER_CARD_DROP selftest ===");
            var failures = 0;

            Check("independent drop PVF entry mob=907 can produce card 3610",
                IndependentDropCanProduceKnownCard(), ref failures);

            Check("world drop PVF table does not contain monster cards",
                !WorldDropContainsMonsterCards(), ref failures);

            Check("type4 monster drop rate can produce monster cards",
                Type4DropRateCanProduceMonsterCard(), ref failures);

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static bool IndependentDropCanProduceKnownCard()
        {
            ushort slotCounter = 0;
            var drops = IndependentDropSystem.GenerateDrops(
                monsterCode: 907,
                difficulty: 0,
                dungeonLevel: 1,
                lcg: new DnfLcg(37),
                slotCounter: ref slotCounter);

            for (int i = 0; i < drops.Count; i++)
            {
                if (drops[i].TemplateId == 3610 && IsMonsterCard((int)drops[i].TemplateId))
                    return true;
            }
            return false;
        }

        private static bool Type4DropRateCanProduceMonsterCard()
        {
            const int MonsterLevelWithType4 = 16;
            const int MonsterTypeNormal = 0;
            const int NoIndependentDropMonsterCode = -392000;
            const int DifficultyNormal = 0;
            const int DungeonLevel = MonsterLevelWithType4;

            uint seed = FindType4OnlySeed(MonsterLevelWithType4, MonsterTypeNormal, DifficultyNormal);
            ushort slotCounter = 0;
            var generator = new DropGenerator(new DnfLcg(seed));
            var (_, drops) = generator.GenerateMonsterDrops(
                monsterLevel: MonsterLevelWithType4,
                monsterType: MonsterTypeNormal,
                monsterCode: NoIndependentDropMonsterCode,
                difficulty: DifficultyNormal,
                dungeonLevel: DungeonLevel,
                slotCounter: ref slotCounter);

            return drops.Count > 0
                && drops[0].SceneSlot == 1
                && drops[0].TemplateId > 0
                && IsMonsterCard((int)drops[0].TemplateId);
        }

        private static uint FindType4OnlySeed(int monsterLevel, int monsterType, int difficulty)
        {
            const int DropDenominator = 10000;
            const int SeedProbeCount = 200000;

            var diffBonus = difficulty >= 0 && difficulty < 5
                ? 1.0f + 0.2f * difficulty
                : 1.0f;

            MonsterDropConfig.GetAllDropRates(monsterLevel, monsterType,
                out var goldRate, out var type1Rate, out var type2Rate,
                out var type3Rate, out var type4Rate);

            goldRate = Math.Min((int)(goldRate * diffBonus), DropDenominator);
            type1Rate = Math.Min((int)(type1Rate * diffBonus), DropDenominator);
            type2Rate = Math.Min((int)(type2Rate * diffBonus), DropDenominator);
            type3Rate = Math.Min((int)(type3Rate * diffBonus), DropDenominator);
            type4Rate = Math.Min((int)(type4Rate * diffBonus), DropDenominator);

            ExpTableProvider.GetMonsterGold(monsterLevel, out int variancePct);

            for (uint seed = 0; seed < SeedProbeCount; seed++)
            {
                var lcg = new DnfLcg(seed);
                if (variancePct > 0)
                    lcg.Next(2 * variancePct + 1);

                bool goldHit = goldRate > lcg.Next(DropDenominator);
                bool type1Hit = type1Rate > lcg.Next(DropDenominator);
                bool type2Hit = type2Rate > lcg.Next(DropDenominator);
                bool type3Hit = type3Rate > lcg.Next(DropDenominator);
                bool type4Hit = type4Rate > lcg.Next(DropDenominator);

                if (!goldHit && !type1Hit && !type2Hit && !type3Hit && type4Hit)
                    return seed;
            }

            throw new InvalidOperationException("Could not find a deterministic type4-only seed for monster card drop selftest.");
        }

        private static bool WorldDropContainsMonsterCards()
        {
            string text;
            try { text = PvfArchiveAccessor.ReadText("Etc/WorldDrop.etc"); }
            catch { return false; }

            var match = Regex.Match(text, @"\[world drop\]\s*([\s\S]*?)\s*\[/world drop\]", RegexOptions.IgnoreCase);
            if (!match.Success) return false;

            var section = Regex.Replace(match.Groups[1].Value ?? string.Empty, @"//.*$", string.Empty, RegexOptions.Multiline);
            var values = Regex.Matches(section, @"-?\d+");
            int index = 0;
            while (index + 1 < values.Count)
            {
                index += 2;
                while (index < values.Count)
                {
                    int itemId = int.Parse(values[index++].Value);
                    if (itemId == -1)
                        break;
                    if (index >= values.Count)
                        break;

                    index++;
                    if (itemId > 0 && IsMonsterCard(itemId))
                        return true;
                }
            }

            return false;
        }

        private static bool IsMonsterCard(int itemTemplateId)
        {
            var metadata = ItemMetadataResolver.Resolve(itemTemplateId);
            var path = (metadata.PvfFilePath ?? string.Empty).Replace('\\', '/');
            return path.StartsWith("monsterCard/", StringComparison.OrdinalIgnoreCase);
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok)
                failures++;
        }
    }
}
