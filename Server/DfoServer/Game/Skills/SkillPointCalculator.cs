namespace DfoServer.Game.Skills
{
    public static class SkillPointCalculator
    {
        public struct Result
        {
            public int TotalSp;
            public int SpentSp;
            public int RemainingSp;
            public int TotalTp;
            public int SpentTp;
            public int RemainingTp;
        }

        public static Result Calculate(byte job, byte level, int bonusSp, int bonusTp,
            SelectCharacter.SkillInfoSnapshot skills)
        {
            int totalSp = SpTableProvider.GetTotalSp(level) + bonusSp;
            int spentSp = 0;

            
            var initialSkills = SelectCharacter.InitialCharacterSkills.Build(job);
            var initialLevels = new System.Collections.Generic.Dictionary<ushort, byte>();
            if (initialSkills != null && initialSkills.Pages.Count > 0)
            {
                foreach (var ie in initialSkills.Pages[0].Entries)
                    initialLevels[ie.SkillId] = ie.Level;
            }

            if (skills != null && skills.Pages.Count > 0)
            {
                foreach (var e in skills.Pages[0].Entries)
                {
                    var sd = SkillDataProvider.GetSkill(job, e.SkillId);
                    if (sd == null) continue;
                    
                    byte baseLevel = initialLevels.ContainsKey(e.SkillId) ? initialLevels[e.SkillId] : (byte)0;
                    if (e.Level > baseLevel)
                        spentSp += sd.SpCostFor(baseLevel, e.Level);
                }
            }

            int totalTp = TpTableProvider.GetTotalTp(level) + bonusTp;
            int spentTp = 0;
            

            return new Result
            {
                TotalSp = totalSp,
                SpentSp = spentSp,
                RemainingSp = System.Math.Max(0, totalSp - spentSp),
                TotalTp = totalTp,
                SpentTp = spentTp,
                RemainingTp = System.Math.Max(0, totalTp - spentTp),
            };
        }
    }
}
