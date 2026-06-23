using System;
using System.Collections.Generic;

namespace DfoServer.Game.Dungeon
{
    public struct RoomKey : IEquatable<RoomKey>
    {
        public int X;
        public int Y;
        public int OverrideMapId;

        public RoomKey(int x, int y, int overrideMapId)
        {
            X = x;
            Y = y;
            OverrideMapId = overrideMapId;
        }

        public bool Equals(RoomKey other) =>
            X == other.X && Y == other.Y && OverrideMapId == other.OverrideMapId;

        public override bool Equals(object obj) => obj is RoomKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = X * 397;
                hash = (hash ^ Y) * 397;
                hash ^= OverrideMapId;
                return hash;
            }
        }
    }

    public class RoomState
    {
        public GameWorld.Dungeon.MazeSumInfo Maze;
        public ushort FirstSeqId;
        public ushort MonsterCount;
        public HashSet<ushort> KilledSeqIds;
        public uint Seed;
        public DnfLcg Lcg;

        public bool IsCleared => KilledSeqIds.Count >= MonsterCount && MonsterCount > 0;
    }
}
