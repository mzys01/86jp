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
            var points = SkillStateService.ResolvePointState(
                snapshot, persistedPoints, (byte)job, level, bonusSp, bonusTp);
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
            SkillStateService.Persist(repo, cid, snapshot, points);

            result.RemainSp = (ushort)points.RemainingSp;
            result.RemainTp = (ushort)points.RemainingTp;
            return result;
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
