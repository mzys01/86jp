using DfoServer.Game.CharacterData;
using DfoServer.Game.SelectCharacter;
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
        public ushort RemainSfp;
        public readonly List<BuySkillResultEntry> Entries = new List<BuySkillResultEntry>();
        public byte ErrorCode;    
    }

    
    
    
    
    
    
    
    public static class BuySkillService
    {
        public static BuySkillResult Execute(SqliteCharacterProgressRepository repo, int cid, int job, int skillTree, IList<BuySkillEntry> entries,
            int bonusSp = 0, byte level = 1)
        {
            var snapshot = repo.LoadSkills(cid);
            int pageIdx = skillTree == 1 ? 1 : 0;
            while (snapshot.Pages.Count <= pageIdx)
                snapshot.Pages.Add(new SkillInfoPageSnapshot());
            var page = snapshot.Pages[pageIdx];

            var calc = SkillPointCalculator.Calculate((byte)job, level, bonusSp, 0, snapshot);
            int remainSp = calc.RemainingSp;
            int remainSfp = snapshot.Tail0;

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

                    int cost = sd.SpCostFor(curLevel, newLevel);
                    if (sd.IsSpecial)
                    {
                        if (remainSfp < cost) { result.Success = false; result.ErrorCode = 2; return result; }
                        remainSfp -= cost;
                    }
                    else
                    {
                        if (remainSp < cost) { result.Success = false; result.ErrorCode = 2; return result; }
                        remainSp -= cost;
                    }

                    byte slotForEntry;
                    if (existing != null)
                    {
                        existing.Level = (byte)newLevel;
                        slotForEntry = existing.Slot;
                    }
                    else
                    {
                        int group = SkillSlotAllocator.ReformGroup(sd.RawGroup, sd.IsActive, sd.NumGrowtypes);
                        int slot = SkillSlotAllocator.AllocateNewSlot(sd.IsActive, group, job, occupied);
                        if (slot < 0) continue; 
                        occupied.Add(slot);
                        page.Entries.Add(new SkillInfoEntrySnapshot
                        {
                            Slot = (byte)slot,
                            SkillId = (ushort)req.SkillIndex,
                            Level = (byte)newLevel,
                        });
                        slotForEntry = (byte)slot;
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
                    int newLevel = curLevel - levels;
                    if (newLevel < 0) newLevel = 0;

                    int refund = sd.SpCostFor(newLevel, curLevel);
                    if (sd.IsSpecial) remainSfp += refund; else remainSp += refund;

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

            page.HeaderValue = (ushort)remainSp;
            snapshot.Tail0 = (ushort)remainSfp;
            repo.SaveSkills(cid, snapshot);

            result.RemainSp = (ushort)remainSp;
            result.RemainSfp = (ushort)remainSfp;
            return result;
        }
    }
}
