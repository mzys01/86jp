using PvfLib;
using System;
using System.Collections.Generic;
using System.IO;

namespace DfoServer.Game.Dungeon
{
    internal static class EquipmentDropPoolProvider
    {
        private static readonly object LockObj = new object();
        private static Dictionary<long, List<(int Id, int Weight)>> _pool;
        private static bool _loaded;

        internal static Dictionary<long, List<(int Id, int Weight)>> GetPool()
        {
            EnsureLoaded();
            return _pool;
        }

        internal static void WarmUp()
        {
            EnsureLoaded();
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (LockObj)
            {
                if (_loaded) return;
                _pool = LoadEquipmentPool();
                _loaded = true;
            }
        }

        private static Dictionary<long, List<(int Id, int Weight)>> LoadEquipmentPool()
        {
            var pool = new Dictionary<long, List<(int Id, int Weight)>>();
            try
            {
                var equipmentListText = GameWorld.PvfArchiveAccessor.ReadText("equipment/equipment.lst");
                var equipmentList = LstFile.Parse(equipmentListText);
                if (equipmentList == null || equipmentList.Entries.Count == 0)
                {
                    FileLogger.Log("[EquipmentDropPoolProvider] equipment.lst empty/not found");
                    return pool;
                }

                var added = 0;
                var errors = 0;
                for (var i = 0; i < equipmentList.Entries.Count; i++)
                {
                    var entry = equipmentList.Entries[i];
                    if (entry == null || string.IsNullOrWhiteSpace(entry.FilePath))
                        continue;

                    try
                    {
                        var equipment = EquipmentFile.Parse(GameWorld.PvfArchiveAccessor.ReadText(Path.Combine("equipment", entry.FilePath)));
                        if (TryAddEquipment(pool, entry.Id, equipment))
                            added++;
                    }
                    catch
                    {
                        errors++;
                    }
                }

                FileLogger.Log($"[EquipmentDropPoolProvider] equipment pool from .equ: items={added} errors={errors} buckets={pool.Count}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[EquipmentDropPoolProvider] equipment pool parse error: {ex.Message}");
            }

            return pool;
        }

        private static bool TryAddEquipment(
            Dictionary<long, List<(int Id, int Weight)>> pool,
            int itemId,
            EquipmentFile equipment)
        {
            if (itemId <= 0 || equipment == null)
                return false;

            var rarity = equipment.Rarity;
            var grade = equipment.Grade;
            var creationRate = equipment.CreationRate;

            if (creationRate <= 0 || grade <= 0 || rarity < 0 || rarity > 5)
                return false;

            var key = (long)grade * 10 + rarity;
            if (!pool.TryGetValue(key, out var list))
            {
                list = new List<(int Id, int Weight)>();
                pool[key] = list;
            }
            list.Add((itemId, creationRate));
            return true;
        }
    }
}
