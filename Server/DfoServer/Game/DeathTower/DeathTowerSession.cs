using System;

namespace DfoServer.Game.DeathTower
{
    public enum DeathTowerRunMode : byte
    {
        Practice = 0,
        Formal = 1,
    }

    public sealed class DeathTowerSession
    {
        public DeathTowerData.TowerConfig Config { get; }
        public DeathTowerRunMode EntryMode { get; }
        public bool IsFormalRun => EntryMode == DeathTowerRunMode.Formal;
        public int CurrentStage { get; private set; }
        public int EndStage => Config.TotalStages - 1;
        public ushort MonsterSequence { get; private set; }
        public ushort ItemSequence { get; private set; }
        public int State { get; private set; }  // 0=init, 1=fighting, 2=cleared

        public DeathTowerSession(DeathTowerData.TowerConfig config, DeathTowerRunMode entryMode = DeathTowerRunMode.Formal)
        {
            Config = config ?? throw new ArgumentNullException(nameof(config));
            EntryMode = entryMode;
            CurrentStage = 0;
            MonsterSequence = 1;
            ItemSequence = 1;
            State = 0;
        }

        public int GetCurrentMapId()
        {
            if (CurrentStage < 0 || CurrentStage >= Config.StageMapIds.Length)
                return -1;
            return Config.StageMapIds[CurrentStage];
        }

        public ushort NextMonsterSeq() => MonsterSequence++;

        public ushort NextItemSeq() => ItemSequence++;

        public void SetFighting() { State = 1; }

        public void SetCleared() { State = 2; }

        // 允许从 state>=1 推进(state==1: 86JP可能不发0x009F(2)直接MOVE_MAP; state==2: 正常流程)
        // state==0(init, 未开始战斗)不允许推进。
        public bool TryAdvanceStage()
        {
            if (State < 1)
                return false;
            if (CurrentStage >= EndStage)
                return false;
            CurrentStage++;
            State = 0;
            return true;
        }

        public bool IsLastStage => CurrentStage >= EndStage;
    }
}
