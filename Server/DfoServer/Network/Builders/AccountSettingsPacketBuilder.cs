using DfoServer.Game.Settings;
using System;
using System.Collections.Generic;

namespace DfoServer.Network.Builders
{
    /// 构造账号级游戏选项/热键设置通知包。
    /// 普通攻击连发等输入运行态依赖账号生命周期的早期设置下发；
    /// 只在选择角色初始化阶段下发时，客户端会恢复 UI 勾选，但很多不会实际应用
    public static class AccountSettingsPacketBuilder
    {
        public static IReadOnlyList<byte[]> BuildLoginAccountSettings(AccountSettings settings)
        {
            var main = settings?.MainGameOption ?? AccountSettings.DefaultMainGameOption;
            var quick0 = settings?.QuickchatBank0 ?? Array.Empty<byte>();
            var quick1 = settings?.QuickchatBank1 ?? Array.Empty<byte>();
            var hotkeys = settings?.HotkeySlots ?? AccountSettings.DefaultHotkeySlots;
            var keyType = ResolveHotkeyKeyType(settings, hotkeys);

            return new[]
            {
                GamePacketEnvelopeBuilder.Build(0x00, 0x00AD, BuildGameOptionBody(main, quick0, quick1)),
                GamePacketEnvelopeBuilder.Build(0x00, 0x01C7, BuildHotkeyOptionBody(keyType, hotkeys)),
            };
        }

        public static byte[] BuildGameOptionBody(byte[] main, byte[] quick0, byte[] quick1)
        {
            var writer = new GamePacketWriter();
            WriteLengthPrefixed(writer, main);
            WriteLengthPrefixed(writer, quick0);
            WriteLengthPrefixed(writer, quick1);
            return writer.ToArray();
        }

        public static byte[] BuildHotkeyOptionBody(byte keyType, byte[] hotkeys)
        {
            hotkeys = hotkeys ?? Array.Empty<byte>();
            var body = new byte[1 + 4 + hotkeys.Length];
            body[0] = keyType;
            Buffer.BlockCopy(BitConverter.GetBytes(hotkeys.Length), 0, body, 1, 4);
            if (hotkeys.Length > 0)
                Buffer.BlockCopy(hotkeys, 0, body, 5, hotkeys.Length);
            return body;
        }

        public static byte[] BuildHotkeyOptionBody(byte keyType, IReadOnlyList<ushort> slots)
        {
            var slotCount = slots?.Count ?? 0;
            var hotkeys = new byte[slotCount * 2];
            for (var i = 0; i < slotCount; i++)
                Buffer.BlockCopy(BitConverter.GetBytes(slots[i]), 0, hotkeys, i * 2, 2);
            return BuildHotkeyOptionBody(keyType, hotkeys);
        }

        private static byte ResolveHotkeyKeyType(AccountSettings settings, byte[] hotkeys)
        {
            if (settings != null)
                return settings.HotkeyKeyType;

            // 默认热键保存体第 1 字节就是客户端期望的 key type。
            return hotkeys != null && hotkeys.Length > 0 ? hotkeys[0] : (byte)0;
        }

        private static void WriteLengthPrefixed(GamePacketWriter writer, byte[] body)
        {
            body = body ?? Array.Empty<byte>();
            writer.WriteInt32(body.Length);
            writer.WriteBytes(body);
        }
    }
}
