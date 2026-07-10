using System;
using System.Collections.Generic;
using DfoServer.Game.Characters;

namespace DfoServer.Game.Session
{
    /// <summary>
    /// </summary>
    public partial class PlayerContext
    {
        /// <summary>客户端上报的真实 P2P UDP 端口(来自 CS 0x0002 SET_UDP_IP_PORT; 0=未上报, 回落 10000)。
        /// 每次开游戏客户端动态选端口, 组队 P2P(0x0B)必须用这个真实值而非硬编 10000。</summary>
        public ushort P2pPort { get; set; }

        /// <summary>
        /// </summary>
        public void HydrateFrom(CharacterRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));

            CharacterId = record.CharacterId;
            Name = record.Name ?? Name;
            UserId = (ushort)record.CharacterId;
            DungeonSceneUniqueId = 0;
            Job = record.Job;
            GrowType = record.GrowType;
            Level = record.Level == 0 ? Level : record.Level;
            Exp = record.Exp;
            {
                byte townId = record.TownId > 0 ? record.TownId : (byte)1;
                var gate = GameWorld.Town.GetCeraRoomInfo(townId);
                CurTownId = gate.Town > 0 ? gate.Town : (byte)1;
                CurAreaId = gate.Town > 0 ? gate.Area : (byte)0;
                CurPosX = gate.Town > 0 ? gate.X : (short)474;
                CurPosY = gate.Town > 0 ? gate.Y : (short)234;
                CurDirection = 5;
                CurAreaState = 3;
            }

            if (record.Appearance != null && record.Appearance.Length > 0)
                AppearanceEntries = record.Appearance;

            Subtype0Tail = record.Subtype0Tail;
        }
    }
}
