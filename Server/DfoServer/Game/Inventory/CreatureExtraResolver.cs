using System;
using System.Collections.Concurrent;
using System.IO;
using DfoServer.GameWorld;
using PvfLib;

namespace DfoServer.Game.Inventory
{
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    public static class CreatureExtraResolver
    {
        private static readonly ConcurrentDictionary<int, bool> Cache = new ConcurrentDictionary<int, bool>();

        private static readonly Lazy<LstFile> EquipmentList = new Lazy<LstFile>(
            () => LstFile.Parse(PvfArchiveAccessor.ReadText("equipment/equipment.lst")));

        
        
        
        
        public static bool HasCreatureExtra(int itemTemplateId)
        {
            return Cache.GetOrAdd(itemTemplateId, ResolveFromPvf);
        }

        private static bool ResolveFromPvf(int itemTemplateId)
        {
            var entry = EquipmentList.Value.GetById(itemTemplateId);
            if (entry == null)
                throw new InvalidDataException(
                    $"[CreatureExtraResolver] item {itemTemplateId} 不在 equipment.lst — 无法判定 creature extra 边界");

            var text = PvfArchiveAccessor.ReadText(Path.Combine("equipment", entry.FilePath));

            
            int typeIdx = text.IndexOf("[equipment type]", StringComparison.Ordinal);
            if (typeIdx >= 0)
            {
                int lineEnd = text.IndexOf('\n', typeIdx);
                int valueEnd = lineEnd >= 0 ? text.IndexOf('\n', lineEnd + 1) : -1;
                var valueLine = lineEnd >= 0
                    ? text.Substring(lineEnd + 1, (valueEnd >= 0 ? valueEnd : text.Length) - lineEnd - 1)
                    : "";
                return valueLine.Contains("[creature]");
            }

            return false;
        }
    }
}
