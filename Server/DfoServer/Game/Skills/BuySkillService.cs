using DfoServer.Game.CharacterData;
using DfoServer.Game.SelectCharacter;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Skills
{
    
    public sealed class BuySkillEntry
    {
        public byte SkillIndex;   
        public byte IsRefund;     
        public byte Level;        
    }

    
    public sealed class BuySkillResultEntry
    {
        public byte Slot;         
        public ushort SkillId;
        public byte Level;
        public bool HasCmd;       
    }

    public sealed class BuySkillResult
    {
        public bool Success;
        public byte SkillTree;
        public ushort RemainSp;
        public ushort RemainTp;
        public readonly List<BuySkillResultEntry> Entries = new List<BuySkillResultEntry>();
        public byte ErrorCode;    
    }

    
    
    
    
    
    
    
    public static class BuySkillService
    {
        public static BuySkillResult Execute(SqliteCharacterProgressRepository repo, int cid, int job, int skillTree, IList<BuySkillEntry> entries,
            int bonusSp = 0, byte level = 1, int bonusTp = 0)
        {
            var snapshot = repo.LoadSkills(cid);
            int pageIdx = skillTree == 1 ? 1 : 0;
            while (snapshot.Pages.Count <= pageIdx)
                snapshot.Pages.Add(new SkillInfoPageSnapshot());
            var page = snapshot.Pages[pageIdx];

            var persistedPoints = repo.LoadSkillPointState(cid);
            var points = ResolvePagePointState(
                snapshot, persistedPoints, (byte)job, level, bonusSp, bonusTp, pageIdx);
            int remainSp = points.RemainingSp;
            int remainTp = points.RemainingTp;

            var result = new BuySkillResult { Success = true, SkillTree = (byte)skillTree };

            
            var occupied = new HashSet<int>();
            foreach (var e in page.Entries) occupied.Add(e.Slot);

            foreach (var req in entries)
            {
                var sd = SkillDataProvider.GetSkill(job, req.SkillIndex);
                if (sd == null) continue; 

                int levels = req.Level <= 0 ? 1 : req.Level;
                var existing = page.Entries.Find(x => x.SkillId == req.SkillIndex);
                int curLevel = existing != null ? existing.Level : 0;

                if (req.IsRefund == 0)
                {
                    
                    int newLevel = curLevel + levels;
                    if (sd.MaxLevel > 0 && newLevel > sd.MaxLevel) newLevel = sd.MaxLevel;
                    if (newLevel <= curLevel) continue; 

                    byte slotForEntry;
                    int allocatedSlot = -1;
                    if (existing != null)
                    {
                        slotForEntry = existing.Slot;
                    }
                    else
                    {
                        int group = SkillSlotAllocator.ReformGroup(sd.RawGroup, sd.IsActive, sd.NumGrowtypes);
                        allocatedSlot = SkillSlotAllocator.AllocateNewSlot(sd.IsActive, group, job, occupied);
                        if (allocatedSlot < 0)
                        {
                            result.Success = false;
                            result.ErrorCode = 1;
                            return result;
                        }
                        slotForEntry = (byte)allocatedSlot;
                    }

                    if (sd.IsTpSkill)
                    {
                        int tpCost = sd.TpCostFor(curLevel, newLevel);
                        if (remainTp < tpCost) { result.Success = false; result.ErrorCode = 2; return result; }
                        remainTp -= tpCost;
                    }
                    else
                    {
                        int cost = sd.SpCostFor(curLevel, newLevel);
                        if (remainSp < cost) { result.Success = false; result.ErrorCode = 2; return result; }
                        remainSp -= cost;
                    }

                    if (existing != null)
                    {
                        existing.Level = (byte)newLevel;
                    }
                    else
                    {
                        occupied.Add(allocatedSlot);
                        page.Entries.Add(new SkillInfoEntrySnapshot
                        {
                            Slot = slotForEntry,
                            SkillId = (ushort)req.SkillIndex,
                            Level = (byte)newLevel,
                        });
                    }

                    result.Entries.Add(new BuySkillResultEntry
                    {
                        Slot = (byte)(sd.IsSpecial ? 0xFF : slotForEntry),
                        SkillId = (ushort)req.SkillIndex,
                        Level = (byte)newLevel,
                        HasCmd = false,
                    });
                }
                else
                {
                    
                    if (existing == null || curLevel == 0) continue;
                    byte refundSlot = existing.Slot;
                    int baseLevel = GetInitialLevel((byte)job, req.SkillIndex);
                    int newLevel = curLevel - levels;
                    if (newLevel < baseLevel) newLevel = baseLevel;
                    if (newLevel >= curLevel) continue;

                    if (sd.IsTpSkill)
                    {
                        int refund = sd.TpCostFor(newLevel, curLevel);
                        remainTp += refund;
                    }
                    else
                    {
                        int refund = sd.SpCostFor(newLevel, curLevel);
                        remainSp += refund;
                    }

                    if (newLevel == 0)
                    {
                        page.Entries.Remove(existing);
                        occupied.Remove(existing.Slot);
                    }
                    else
                    {
                        existing.Level = (byte)newLevel;
                    }

                    result.Entries.Add(new BuySkillResultEntry
                    {
                        Slot = (byte)(sd.IsSpecial ? 0xFF : refundSlot),
                        SkillId = (ushort)req.SkillIndex,
                        Level = (byte)newLevel,
                        HasCmd = false,
                    });
                }
            }

            points.RemainingSp = Math.Max(0, Math.Min(remainSp, points.TotalSp));
            points.RemainingTp = Math.Max(0, Math.Min(remainTp, points.TotalTp));
            page.HeaderValue = ToUInt16(points.RemainingSp);
            repo.SaveSkillProgress(cid, snapshot, BuildPersistedPointState(
                snapshot, points, pageIdx, (byte)job, level, bonusSp, bonusTp));

            result.RemainSp = (ushort)points.RemainingSp;
            result.RemainTp = (ushort)points.RemainingTp;
            return result;
        }

        private static SkillPointState ResolvePagePointState(
            SkillInfoSnapshot snapshot,
            SkillPointState persisted,
            byte job,
            byte level,
            int bonusSp,
            int bonusTp,
            int pageIdx)
        {
            var calculated = SkillPointCalculator.Calculate(job, level, bonusSp, bonusTp, snapshot, pageIdx);
            var page = snapshot != null && pageIdx >= 0 && pageIdx < snapshot.Pages.Count
                ? snapshot.Pages[pageIdx]
                : null;
            var pageRemainingSp = ResolvePageRemainingSp(page, calculated.RemainingSp);
            var totalSp = Math.Max(calculated.TotalSp, pageRemainingSp);

            var state = new SkillPointState
            {
                TotalSp = totalSp,
                TotalTp = calculated.TotalTp,
                SyncedLevel = level,
                HasPersistedState = persisted != null && persisted.HasPersistedState
            };

            if (pageIdx == 0 && persisted != null && persisted.HasPersistedState && (page == null || page.HeaderValue == 0))
            {
                var gainedSp = calculated.TotalSp - persisted.TotalSp;
                state.RemainingSp = Clamp(persisted.RemainingSp + gainedSp, 0, totalSp);
            }
            else
            {
                state.RemainingSp = Clamp(pageRemainingSp, 0, totalSp);
            }

            if (persisted != null && persisted.HasPersistedState)
            {
                var gainedTp = calculated.TotalTp - persisted.TotalTp;
                state.RemainingTp = Clamp(persisted.RemainingTp + gainedTp, 0, calculated.TotalTp);
            }
            else
            {
                state.RemainingTp = ResolveRemainingTp(snapshot, calculated.RemainingTp);
            }

            return state;
        }

        private static SkillPointState BuildPersistedPointState(
            SkillInfoSnapshot snapshot,
            SkillPointState currentPagePoints,
            int pageIdx,
            byte job,
            byte level,
            int bonusSp,
            int bonusTp)
        {
            var page0Calculated = SkillPointCalculator.Calculate(job, level, bonusSp, bonusTp, snapshot, 0);
            var page0 = snapshot != null && snapshot.Pages.Count > 0 ? snapshot.Pages[0] : null;
            var remainingSp = pageIdx == 0
                ? currentPagePoints.RemainingSp
                : ResolvePageRemainingSp(page0, page0Calculated.RemainingSp);
            var totalSp = Math.Max(page0Calculated.TotalSp, remainingSp);

            return new SkillPointState
            {
                TotalSp = totalSp,
                RemainingSp = Clamp(remainingSp, 0, totalSp),
                TotalTp = currentPagePoints.TotalTp,
                RemainingTp = Clamp(currentPagePoints.RemainingTp, 0, currentPagePoints.TotalTp),
                SyncedLevel = level,
                HasPersistedState = true,
            };
        }

        private static int ResolvePageRemainingSp(SkillInfoPageSnapshot page, int calculatedRemainingSp)
        {
            if (page == null)
                return Math.Max(0, calculatedRemainingSp);
            if (page.HeaderValue > 0 || calculatedRemainingSp == 0)
                return page.HeaderValue;
            return calculatedRemainingSp;
        }

        private static int ResolveRemainingTp(SkillInfoSnapshot snapshot, int calculatedRemainingTp)
        {
            if (snapshot != null && snapshot.HasTailValues)
            {
                if (snapshot.Tail1 > 0)
                    return snapshot.Tail1;
                if (snapshot.Tail0 > 0)
                    return snapshot.Tail0;
            }
            return Math.Max(0, calculatedRemainingTp);
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

        private static int GetInitialLevel(byte job, int skillId)
        {
            var initial = InitialCharacterSkills.Build(job);
            if (initial == null || initial.Pages.Count == 0) return 0;

            foreach (var entry in initial.Pages[0].Entries)
                if (entry.SkillId == skillId)
                    return entry.Level;
            return 0;
        }
    }
}
