using System;
using System.IO;
using PvfLib;

namespace DfoServer.GameWorld
{
    public struct CeraRoomInfo
    {
        public byte Town { get; set; }

        public byte Area { get; set; }

        public short X { get; set; }

        public short Y { get; set; }
    }

    public class Town
    {
        public static CeraRoomInfo GetCeraRoomInfo(int townId)
        {
            var roomInfo = new CeraRoomInfo();

            var twnlst = LstFile.Parse(PvfArchiveAccessor.ReadText("town/town.lst"));
            if (twnlst == null)
                throw new Exception("未能成功解析城镇LST文件 town/town.lst");

            var entry = twnlst.GetById(townId);
            if (entry == null || string.IsNullOrEmpty(entry.FilePath))
                throw new Exception($"未找到城镇编号{townId}");

            var twnFile = TownFile.Parse(PvfArchiveAccessor.ReadText(Path.Combine("town", entry.FilePath)));
            if (twnFile.Areas == null || twnFile.Areas.Count == 0)
                throw new Exception("未解析到城镇区域信息");

            foreach (var item in twnFile.Areas)
            {
                if (string.Equals(item.AreaType, "gate", StringComparison.OrdinalIgnoreCase))
                {
                    roomInfo.Town = (byte)townId;
                    roomInfo.Area = (byte)item.Id;
                    roomInfo.X = (short)item.LinkedId;
                    roomInfo.Y = (short)item.LinkedId2;
                    break;
                }
            }

            return roomInfo;
        }
    }
}