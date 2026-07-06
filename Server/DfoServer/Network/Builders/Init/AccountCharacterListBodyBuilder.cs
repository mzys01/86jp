using DfoServer.Game.Accounts;
using DfoServer.Game.Appearance;
using DfoServer.Game.Characters;
using DfoServer.Game.SelectCharacter;
using System;
using System.Collections.Generic;

namespace DfoServer.Network.Builders
{
    public static class AccountCharacterListBodyBuilder
    {
        public static byte[] Build(
            IReadOnlyList<CharacterRecord> characters,
            GetUserInfoTemplate template,
            out AdventureGroupSummary adventureGroup)
        {
            characters = characters ?? Array.Empty<CharacterRecord>();
            adventureGroup = AdventureGroupDataProvider.Calculate(characters);

            var writer = new GamePacketWriter();
            var slotLimit = CharacterSlotPolicy.ResolveSlotLimit(template?.GateOrCount1, template?.GateOrCount2);
            writer.WriteByte(2);
            writer.WriteUInt16(slotLimit);
            writer.WriteUInt16(template != null ? template.GateOrCount2 : slotLimit);
            writer.WriteByte(adventureGroup.ManageLevel);
            writer.WriteInt32(adventureGroup.TotalPoint);
            writer.WriteUInt16(template != null ? template.Unknown16 : (ushort)0);
            writer.WriteInt32(template != null ? template.Unknown32 : 0);
            writer.WriteUInt16((ushort)Math.Min(ushort.MaxValue, characters.Count));

            for (var i = 0; i < characters.Count && i < ushort.MaxValue; i++)
            {
                var ch = characters[i];
                if (ch == null)
                    continue;

                writer.WriteUInt16((ushort)i);
                writer.WriteDstr(ch.Name);
                writer.WriteByte(0x00);
                writer.WriteByte(0x00);
                writer.WriteByte(ch.Job);
                writer.WriteByte(ch.GrowType);
                writer.WriteByte(ch.Level);
                writer.WriteZeroBytes(10);

                var appearances = AppearanceService.LoadAppearanceFromEquipEntries(ch.CharacterId);
                writer.WriteByte((byte)Math.Min(byte.MaxValue, appearances.Length));
                for (var j = 0; j < appearances.Length && j < byte.MaxValue; j++)
                    UserInfoSubtype0Builder.WriteAppearanceEntry(writer, appearances[j]);

                var cloneTitleItemId = AppearanceService.LoadCloneTitleItemId(ch.CharacterId);
                UserInfoType2RosterTailBuilder.Write(writer, cloneTitleItemId > 0 ? (uint)cloneTitleItemId : 0);
            }

            return writer.ToArray();
        }
    }
}
