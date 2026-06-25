using DfoServer.Game.SelectCharacter;
using DfoServer.Game.CharacterData;

namespace DfoServer.Game.Skills
{
    public sealed class SkillPointState
    {
        public int TotalSp { get; set; }

        public int RemainingSp { get; set; }

        public int TotalSfp { get; set; }

        public int RemainingSfp { get; set; }

        public int TotalTp { get; set; }

        public int RemainingTp { get; set; }

        public byte SyncedLevel { get; set; }

        public bool HasPersistedState { get; set; }
    }

    public static class SkillStateService
    {
        public static SkillPointState ResolvePointState(
            SkillInfoSnapshot skills,
            SkillPointState persisted,
            byte job,
            byte level,
            int bonusSp,
            int bonusTp)
        {
            var calculated = SkillPointCalculator.Calculate(job, level, bonusSp, bonusTp, skills);

            var state = new SkillPointState
            {
                TotalSp = calculated.TotalSp,
                TotalTp = calculated.TotalTp,
                SyncedLevel = level,
                HasPersistedState = persisted != null && persisted.HasPersistedState
            };

            if (persisted != null && persisted.HasPersistedState)
            {
                var gainedSp = calculated.TotalSp - persisted.TotalSp;
                var gainedTp = calculated.TotalTp - persisted.TotalTp;
                var totalSfp = Max(persisted.TotalSfp, persisted.RemainingSfp);
                state.RemainingSp = Clamp(persisted.RemainingSp + gainedSp, 0, calculated.TotalSp);
                state.RemainingTp = Clamp(persisted.RemainingTp + gainedTp, 0, calculated.TotalTp);
                state.TotalSfp = totalSfp;
                state.RemainingSfp = Clamp(persisted.RemainingSfp, 0, totalSfp);
            }
            else
            {
                state.RemainingSp = calculated.RemainingSp;
                state.RemainingTp = calculated.RemainingTp;
                state.TotalSfp = 0;
                state.RemainingSfp = 0;
            }

            return state;
        }

        public static void ApplyProtocolMirrors(SkillInfoSnapshot skills, SkillPointState state)
        {
            if (skills == null || state == null) return;
            while (skills.Pages.Count < 2)
                skills.Pages.Add(new SkillInfoPageSnapshot());

            skills.Pages[0].HeaderValue = ToUInt16(state.RemainingSp);
            skills.Tail0 = ToUInt16(state.RemainingSfp);
            skills.Tail1 = ToUInt16(state.RemainingSp);
            skills.HasTailValues = true;
        }

        public static void Persist(
            SqliteCharacterProgressRepository repository,
            int characterId,
            SkillInfoSnapshot skills,
            SkillPointState state)
        {
            if (repository == null || skills == null || state == null) return;
            ApplyProtocolMirrors(skills, state);
            repository.SaveSkillProgress(characterId, skills, state);
        }

        public static (SkillInfoSnapshot Skills, SkillPointState Points) ResetToInitial(
            SqliteCharacterProgressRepository repository,
            int characterId,
            byte job,
            byte level,
            int bonusSp,
            int bonusTp)
        {
            var skills = InitialCharacterSkills.Build(job);
            var points = ResolvePointState(skills, null, job, level, bonusSp, bonusTp);
            points.RemainingSp = points.TotalSp;
            points.RemainingTp = points.TotalTp;
            points.RemainingSfp = points.TotalSfp;
            Persist(repository, characterId, skills, points);
            return (skills, points);
        }

        public static (SkillInfoSnapshot Skills, SkillPointState Points) LoadAndSync(
            SqliteCharacterProgressRepository repository,
            int characterId,
            byte job,
            byte level,
            int bonusSp,
            int bonusTp,
            bool persist)
        {
            var skills = repository.LoadSkills(characterId);
            var persisted = repository.LoadSkillPointState(characterId);
            var points = ResolvePointState(skills, persisted, job, level, bonusSp, bonusTp);
            ApplyProtocolMirrors(skills, points);
            if (persist)
            {
                repository.SaveSkillProgress(characterId, skills, points);
            }
            return (skills, points);
        }

        private static ushort ToUInt16(int value)
        {
            return (ushort)Clamp(value, 0, ushort.MaxValue);
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private static int Max(int a, int b)
        {
            return a > b ? a : b;
        }
    }
}
