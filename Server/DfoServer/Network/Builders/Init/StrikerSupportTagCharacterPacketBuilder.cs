using DfoServer.Game.Characters;
using DfoServer.Game.Mercenary;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DfoServer.Network.Builders
{
    public static class StrikerSupportTagCharacterPacketBuilder
    {
        private const ushort TagCharacterInfoNotiType = 0x019F;
        // 当前 86JP 的 0x019F tag character record 模板，只作为服务端构造包体的固定布局。
        // 模板不承载任何角色状态；角色名、职业/grow、技能、装备和 owner cid 都从当前数据库状态写入。
        private static readonly byte[] TagCharacterRecordTemplate = new byte[]
        {
            0xEA, 0x03, 0x04, 0x00, 0x00, 0x00, 0x32, 0x30, 0x30, 0x32, 0x56, 0x0B, 0x14, 0x5E, 0x00, 0x52,
            0x00, 0x00, 0x00, 0x78, 0xCD, 0x00, 0x00, 0xC0, 0x76, 0x00, 0x00, 0x62, 0x16, 0x62, 0x16, 0xDA,
            0x12, 0xDA, 0x12, 0x00, 0x00, 0x00, 0x00, 0x40, 0x01, 0xB0, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x70, 0xBC, 0x0A,
            0x00, 0x00, 0x00, 0x6A, 0x0E, 0xF0, 0x23, 0x00, 0x00, 0x28, 0x23, 0x40, 0x1F, 0xCE, 0x1D, 0x30,
            0x11, 0x20, 0xA1, 0x07, 0x00, 0x1B, 0x00, 0x8F, 0x60, 0xB5, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x14, 0x61, 0xB5, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1E, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x99, 0x87, 0xB5, 0x06,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0xB2, 0x87, 0xB5, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x1E, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x02, 0xA9, 0xAE, 0xB5, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0x00, 0x08, 0xAF, 0xB5, 0x06,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1E, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0x43, 0x9D, 0xB4, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x39,
            0x00, 0xC6, 0x9D, 0xB4, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1E, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 0x53, 0xC4, 0xB4, 0x06, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0xCF, 0xC4, 0xB4, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x1E, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05,
            0x91, 0x39, 0xB5, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0C, 0x3A, 0xB5, 0x06, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1E, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x06, 0x5E, 0xEB, 0xB4, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0x00,
            0xDD, 0xEB, 0xB4, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1E, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x07, 0x75, 0x12, 0xB5, 0x06, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x04, 0x00, 0xE7, 0x12, 0xB5, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x1E, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x08, 0x09,
            0xD6, 0xB5, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1E, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x09, 0xBA, 0xFC, 0xB5, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1E, 0x00, 0x00, 0x00, 0xEF,
            0xFF, 0x2A, 0xE9, 0x26, 0x00, 0xEF, 0xFF, 0xCF, 0x4C, 0x26, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0A, 0xD8, 0x23, 0xB6, 0x06, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x1E, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0B, 0x73, 0x4A,
            0x05, 0x06, 0xFE, 0xC9, 0x9A, 0x3B, 0x0B, 0x2D, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x03, 0x07, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x06, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0C, 0x4C, 0x8D, 0xDC, 0x17, 0x56, 0x38, 0x9A,
            0x1A, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x0D, 0x82, 0xA4, 0xF6, 0x05, 0xFE, 0xC9, 0x9A, 0x3B, 0x0B, 0x1C, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0x07, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0E, 0xFC,
            0x2A, 0xF8, 0x05, 0xFE, 0xC9, 0x9A, 0x3B, 0x0B, 0x15, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x03, 0x07, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0F, 0xAC, 0x67, 0xF7, 0x05, 0xFE, 0xC9,
            0x9A, 0x3B, 0x0B, 0x18, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0x07,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x10, 0x9D, 0xB1, 0xF9, 0x05, 0xFE, 0xC9, 0x9A, 0x3B, 0x0B, 0x12, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0x07, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x11,
            0x4D, 0xEE, 0xF8, 0x05, 0xFE, 0xC9, 0x9A, 0x3B, 0x0B, 0x12, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x03, 0x07, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x12, 0x56, 0x9F, 0xFA, 0x05, 0xFE,
            0xC9, 0x9A, 0x3B, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03,
            0x07, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x13, 0xFF, 0x9D, 0xFA, 0x05, 0xFE, 0xC9, 0x9A, 0x3B, 0x06, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0x07, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x14, 0xB9, 0xC5, 0xFA, 0x05, 0xFE, 0xC9, 0x9A, 0x3B, 0x09, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0x07, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x15, 0xF3, 0x17, 0xFB, 0x05,
            0xFE, 0xC9, 0x9A, 0x3B, 0x0B, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x03, 0x07, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x16, 0xAC, 0x3B, 0xFB, 0x05, 0xFE, 0xC9, 0x9A, 0x3B, 0x0A,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0x05, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x98, 0x11, 0x0C, 0x46, 0xFF, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x17, 0xA1, 0xBF, 0x05, 0x06, 0xFE, 0xC9, 0x9A, 0x3B, 0x00,
            0x30, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x18, 0x66, 0x9F, 0xE6, 0x17, 0x11, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x19, 0xE5, 0xB0, 0x98, 0x00, 0x8B, 0x8A, 0x43, 0x4D, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1A, 0x26, 0xEB, 0x29, 0x00,
            0x1A, 0xA4, 0x57, 0x19, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x29, 0x00, 0x00, 0x07, 0x00, 0x01,
            0x01, 0x56, 0x00, 0x22, 0x02, 0x5E, 0x00, 0x1A, 0x03, 0x5D, 0x00, 0x0E, 0x04, 0x59, 0x00, 0x13,
            0x05, 0x62, 0x00, 0x07, 0x06, 0x60, 0x00, 0x01, 0x07, 0xC5, 0x00, 0x02, 0x08, 0x01, 0x00, 0x02,
            0x09, 0x2E, 0x00, 0x01, 0x0A, 0x08, 0x00, 0x01, 0x0B, 0x01, 0x00, 0x01, 0x0C, 0x02, 0x00, 0x01,
            0x36, 0x5F, 0x00, 0x01, 0x37, 0xFB, 0x00, 0x01, 0x38, 0x19, 0x00, 0x01, 0x39, 0x48, 0x00, 0x0F,
            0x3A, 0x51, 0x00, 0x01, 0x3B, 0x53, 0x00, 0x0F, 0x3C, 0x54, 0x00, 0x14, 0x40, 0x50, 0x00, 0x01,
            0x66, 0xB5, 0x00, 0x02, 0x67, 0xB6, 0x00, 0x02, 0x68, 0xB8, 0x00, 0x01, 0x69, 0xB3, 0x00, 0x07,
            0x6A, 0xAE, 0x00, 0x01, 0x6B, 0xA9, 0x00, 0x01, 0x6C, 0xBA, 0x00, 0x0A, 0x6D, 0xB2, 0x00, 0x0A,
            0x96, 0x87, 0x00, 0x01, 0x97, 0x8F, 0x00, 0x05, 0x98, 0x8E, 0x00, 0x05, 0x99, 0x8C, 0x00, 0x05,
            0x9A, 0x8A, 0x00, 0x01, 0x9B, 0xA1, 0x00, 0x01, 0xC6, 0x58, 0x00, 0x1F, 0xC7, 0x5B, 0x00, 0x1D,
            0xC8, 0x57, 0x00, 0x1C, 0xC9, 0x61, 0x00, 0x10, 0xCA, 0x5C, 0x00, 0x1A, 0xCB, 0x49, 0x00, 0x01,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
        };

        public static bool TryBuildOwnerSupportBody(int activeCharacterId, out byte[] body)
        {
            body = null;
            if (activeCharacterId <= 0)
                return false;

            try
            {
                var repo = CreateSupportRepository();
                var selectedStates = GetSelectedSupportStates(repo.LoadForOwner(activeCharacterId));
                if (selectedStates.Count == 0)
                    return false;

                body = BuildOwnerMappedBody(activeCharacterId, selectedStates, null);
                return body != null && body.Length > 2;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[GameProtocol] MERCENARY/STRIKER init 0x{TagCharacterInfoNotiType:X4} dynamic build failed cid={activeCharacterId}: {ex.Message}");
                body = null;
                return false;
            }
        }

        public static bool TryBuildDungeonOwnerMappedSupportBody(int activeCharacterId, out byte[] body)
        {
            body = null;
            if (activeCharacterId <= 0)
                return false;

            try
            {
                var repo = CreateSupportRepository();
                var selectedStates = GetSelectedSupportStates(repo.LoadForOwner(activeCharacterId));
                if (selectedStates.Count == 0)
                    return false;

                var primary = selectedStates.FirstOrDefault(s => s.Slot == 0) ?? selectedStates[0];
                var supportName = ResolveCharacterName(null, primary.SupportCharacterId);
                var raw = LoadTagCharacterRawRecord();
                if (raw == null || raw.Length < 2)
                    return false;

                raw = PatchTagRecordCharacterName(raw, supportName);
                raw = PatchVisibleSelectedSkillOnly(raw, primary);
                raw = PatchDisplayContextOnly(raw, primary);
                PatchResolverGrowNibble(raw, primary);
                raw = PatchMainApplyOnly(raw, primary);
                raw = PatchSupportEquipmentEntries(raw, primary.SupportCharacterId, "dungeon");
                PatchTagRecordCharacterId(raw, activeCharacterId);

                var writer = new GamePacketWriter();
                writer.WriteUInt16(1);
                writer.WriteBytes(raw);
                body = writer.ToArray();
                return body.Length > 2;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[GameProtocol] MERCENARY/STRIKER dungeon owner-mapped 0x{TagCharacterInfoNotiType:X4} build failed cid={activeCharacterId}: {ex.Message}");
                body = null;
                return false;
            }
        }

        public static byte[] BuildOwnerMappedBody(
            int activeCharacterId,
            IReadOnlyList<MercenarySupportState> selectedStates,
            IReadOnlyList<CharacterRecord> candidates)
        {
            var records = new List<byte[]>();
            var seen = new HashSet<int>();
            var candidateNames = candidates?
                .Where(c => c != null && c.CharacterId > 0 && c.Name != null && c.Name.Length > 0)
                .GroupBy(c => c.CharacterId)
                .ToDictionary(g => g.Key, g => g.First().Name) ?? new Dictionary<int, byte[]>();

            var primary = selectedStates.FirstOrDefault(s => s.Slot == 0) ?? selectedStates[0];
            AddOwnerMappedTagRecord(records, seen, activeCharacterId, primary, ResolveCharacterName(candidateNames, primary.SupportCharacterId));

            var writer = new GamePacketWriter();
            writer.WriteUInt16((ushort)Math.Min(ushort.MaxValue, records.Count));
            foreach (var record in records.Take(ushort.MaxValue))
                writer.WriteBytes(record);

            return writer.ToArray();
        }

        internal static byte[] PatchSelectedSkillIntoTagRecord(byte[] rawRecord, MercenarySupportState state)
        {
            var patched = new byte[rawRecord.Length];
            Buffer.BlockCopy(rawRecord, 0, patched, 0, rawRecord.Length);

            var table = FindLikelyMainSkillTable(patched);
            if (table == null)
            {
                FileLogger.Log($"[GameProtocol] MERCENARY/STRIKER patch 0x019F skipped cid={state.SupportCharacterId} skill={state.SkillId}: main skill table not found");
                return patched;
            }

            PatchVisibleSelectedSkill(patched, state);
            PatchResolverGrowNibble(patched, state);
            PatchDisplayJobContext(patched, state);
            PatchMainApplySelectedSkill(patched, table, state.SkillId);
            PatchMainApplyRequiredSkillEntry(patched, table, state);
            PatchMainApplySkillEntry(patched, table, state, patchEntryKey: false);
            return patched;
        }

        private static byte[] PatchMainApplyOnly(byte[] rawRecord, MercenarySupportState state)
        {
            var patched = new byte[rawRecord.Length];
            Buffer.BlockCopy(rawRecord, 0, patched, 0, rawRecord.Length);

            var table = FindLikelyMainSkillTable(patched);
            if (table == null)
            {
                FileLogger.Log($"[GameProtocol] MERCENARY/STRIKER dungeon main apply patch skipped cid={state.SupportCharacterId} skill={state.SkillId}: main skill table not found");
                return patched;
            }

            PatchMainApplySelectedSkill(patched, table, state.SkillId);
            PatchMainApplyRequiredSkillEntry(patched, table, state);
            PatchMainApplySkillEntry(patched, table, state, patchEntryKey: false);
            return patched;
        }

        private static byte[] PatchDisplayContextOnly(byte[] rawRecord, MercenarySupportState state)
        {
            var patched = new byte[rawRecord.Length];
            Buffer.BlockCopy(rawRecord, 0, patched, 0, rawRecord.Length);
            PatchDisplayJobContext(patched, state);
            return patched;
        }

        private static byte[] PatchVisibleSelectedSkillOnly(byte[] rawRecord, MercenarySupportState state)
        {
            var patched = new byte[rawRecord.Length];
            Buffer.BlockCopy(rawRecord, 0, patched, 0, rawRecord.Length);

            if (state == null || state.SkillId == 0 || patched.Length < 16)
                return patched;

            var nameLength = BitConverter.ToInt32(patched, 2);
            var selectedOffset = 6 + nameLength + 3;
            if (nameLength < 0 || selectedOffset < 0 || selectedOffset + 1 >= patched.Length)
                return patched;

            patched[selectedOffset] = (byte)(state.SkillId & 0xFF);
            patched[selectedOffset + 1] = (byte)((state.SkillId >> 8) & 0xFF);

            return patched;
        }

        private static void PatchResolverGrowNibble(byte[] rawRecord, MercenarySupportState state)
        {
            if (rawRecord == null || rawRecord.Length < 16 || state == null || state.SupportCharacterId <= 0)
                return;

            var nameLength = BitConverter.ToInt32(rawRecord, 2);
            var packedOffset = 6 + nameLength + 2;
            if (nameLength < 0 || packedOffset < 0 || packedOffset >= rawRecord.Length)
                return;

            var support = LoadCharacterSummary(state.SupportCharacterId);
            if (support == null)
                return;

            var normalizedGrow = StrikerSkillDataProvider.NormalizeGrowType(support.GrowType);
            if (normalizedGrow < 0 || normalizedGrow > 0x0F)
                return;

            var before = rawRecord[packedOffset];
            var after = (byte)((before & 0xF0) | (normalizedGrow & 0x0F));
            rawRecord[packedOffset] = after;
        }

        private static int ReadInt32OrZero(byte[] body, int offset)
        {
            if (body == null || offset < 0 || offset + 3 >= body.Length)
                return 0;

            return BitConverter.ToInt32(body, offset);
        }

        internal static void PatchTagRecordCharacterId(byte[] rawRecord, int characterId)
        {
            if (rawRecord == null || rawRecord.Length < 2 || characterId <= 0)
                return;

            rawRecord[0] = (byte)(characterId & 0xFF);
            rawRecord[1] = (byte)((characterId >> 8) & 0xFF);
        }

        private static List<MercenarySupportState> GetSelectedSupportStates(IReadOnlyList<MercenarySupportState> states)
        {
            return states?
                .Where(s => s != null && s.SupportCharacterId > 0 && s.SkillId != 0)
                .OrderBy(s => s.Slot)
                .ToList() ?? new List<MercenarySupportState>();
        }

        private static void AddOwnerMappedTagRecord(List<byte[]> records, HashSet<int> seen, int ownerCharacterId, MercenarySupportState state, byte[] supportName)
        {
            if (ownerCharacterId <= 0 || state == null || seen.Contains(ownerCharacterId))
                return;

            var raw = LoadTagCharacterRawRecord();
            if (raw == null || raw.Length < 2)
                return;

            raw = PatchTagRecordCharacterName(raw, supportName);
            raw = PatchSelectedSkillIntoTagRecord(raw, state);
            raw = PatchSupportEquipmentEntries(raw, state.SupportCharacterId, "owner");
            PatchTagRecordCharacterId(raw, ownerCharacterId);

            records.Add(raw);
            seen.Add(ownerCharacterId);
        }

        private static byte[] PatchTagRecordCharacterName(byte[] rawRecord, byte[] characterName)
        {
            if (rawRecord == null || rawRecord.Length < 6 || characterName == null || characterName.Length == 0)
                return rawRecord;

            var oldLength = BitConverter.ToInt32(rawRecord, 2);
            if (oldLength < 0 || oldLength > rawRecord.Length - 6)
                return rawRecord;

            var nameBytes = characterName;
            if (nameBytes.Length == oldLength)
            {
                var sameLength = new byte[rawRecord.Length];
                Buffer.BlockCopy(rawRecord, 0, sameLength, 0, rawRecord.Length);
                Buffer.BlockCopy(nameBytes, 0, sameLength, 6, nameBytes.Length);
                return sameLength;
            }

            var patched = new byte[rawRecord.Length - oldLength + nameBytes.Length];
            Buffer.BlockCopy(rawRecord, 0, patched, 0, 2);
            patched[2] = (byte)(nameBytes.Length & 0xFF);
            patched[3] = (byte)((nameBytes.Length >> 8) & 0xFF);
            patched[4] = (byte)((nameBytes.Length >> 16) & 0xFF);
            patched[5] = (byte)((nameBytes.Length >> 24) & 0xFF);
            Buffer.BlockCopy(nameBytes, 0, patched, 6, nameBytes.Length);
            Buffer.BlockCopy(rawRecord, 6 + oldLength, patched, 6 + nameBytes.Length, rawRecord.Length - 6 - oldLength);
            return patched;
        }

        private static byte[] ResolveCharacterName(Dictionary<int, byte[]> candidateNames, int characterId)
        {
            if (candidateNames != null && candidateNames.TryGetValue(characterId, out var name) && name != null && name.Length > 0)
                return name;

            return LoadCharacterNameBytes(characterId);
        }

        private static byte[] LoadCharacterNameBytes(int characterId)
        {
            if (characterId <= 0)
                return null;

            try
            {
                var connStr = SqliteDatabaseBootstrap.Initialize(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
                using (var conn = new SqliteConnection(connStr))
                {
                    conn.Open();
                    using (var cmd = new SqliteCommand("SELECT CAST(name AS BLOB) FROM characters WHERE character_id=@cid", conn))
                    {
                        cmd.Parameters.AddWithValue("@cid", characterId);
                        return cmd.ExecuteScalar() as byte[];
                    }
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[GameProtocol] MERCENARY/STRIKER load character name failed cid={characterId}: {ex.Message}");
                return null;
            }
        }

        private static CharacterSummary LoadCharacterSummary(int characterId)
        {
            if (characterId <= 0)
                return null;

            try
            {
                var connStr = SqliteDatabaseBootstrap.Initialize(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
                using (var conn = new SqliteConnection(connStr))
                {
                    conn.Open();
                    using (var cmd = new SqliteCommand("SELECT job, grow_type, level FROM characters WHERE character_id=@cid", conn))
                    {
                        cmd.Parameters.AddWithValue("@cid", characterId);
                        using (var reader = cmd.ExecuteReader())
                        {
                            if (!reader.Read())
                                return null;

                            return new CharacterSummary
                            {
                                Job = reader.GetInt32(0),
                                GrowType = reader.GetInt32(1),
                                Level = reader.GetInt32(2),
                            };
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[GameProtocol] MERCENARY/STRIKER load character summary failed cid={characterId}: {ex.Message}");
                return null;
            }
        }

        private static void PatchVisibleSelectedSkill(byte[] rawRecord, MercenarySupportState state)
        {
            if (rawRecord == null || rawRecord.Length < 16 || state == null || state.SkillId == 0)
                return;

            var nameLength = BitConverter.ToInt32(rawRecord, 2);
            var selectedOffset = 6 + nameLength + 3;
            if (nameLength < 0 || selectedOffset < 0 || selectedOffset + 1 >= rawRecord.Length)
                return;

            rawRecord[selectedOffset] = (byte)(state.SkillId & 0xFF);
            rawRecord[selectedOffset + 1] = (byte)((state.SkillId >> 8) & 0xFF);
        }

        private static void PatchDisplayJobContext(byte[] rawRecord, MercenarySupportState state)
        {
            if (rawRecord == null || rawRecord.Length < 12 || state == null || state.SupportCharacterId <= 0)
                return;

            var nameLength = BitConverter.ToInt32(rawRecord, 2);
            var contextOffset = 6 + nameLength + 1;
            if (nameLength < 0 || contextOffset >= rawRecord.Length)
                return;

            var support = LoadCharacterSummary(state.SupportCharacterId);
            if (support == null)
                return;

            var displayJob = (byte)Math.Max(0, Math.Min(byte.MaxValue, support.Job));
            rawRecord[contextOffset] = displayJob;
        }

        private static void PatchMainApplySelectedSkill(byte[] rawRecord, MainSkillTable table, ushort selectedSkill)
        {
            if (selectedSkill == 0 || table.SelectedOffset < 0 || table.SelectedOffset + 1 >= rawRecord.Length)
                return;

            rawRecord[table.SelectedOffset] = (byte)(selectedSkill & 0xFF);
            rawRecord[table.SelectedOffset + 1] = (byte)((selectedSkill >> 8) & 0xFF);
        }

        private static void PatchMainApplyRequiredSkillEntry(byte[] rawRecord, MainSkillTable table, MercenarySupportState state)
        {
            if (state == null || state.SupportCharacterId <= 0 || state.SkillId == 0)
                return;

            var support = LoadCharacterSummary(state.SupportCharacterId);
            if (support == null)
                return;

            var skill = StrikerSkillDataProvider.FindBySkill(
                support.Job,
                support.GrowType,
                state.SkillId,
                state.StrikerSkillId);
            if (skill == null || skill.RequiredSkillIndex <= 0 || skill.RequiredSkillIndex == state.SkillId)
                return;

            var requiredSkillId = (ushort)Math.Min(ushort.MaxValue, skill.RequiredSkillIndex);
            var requiredEntryKey = (byte)Math.Max(0, Math.Min(byte.MaxValue, skill.RequiredSkillIndex));
            PatchMainApplySkillEntry(
                rawRecord,
                table,
                requiredSkillId,
                requiredEntryKey,
                StrikerSupportSkillLevelSource.ResolveBaseLevel(state.SupportCharacterId, requiredSkillId),
                patchEntryKey: false);
        }

        private static void PatchMainApplySkillEntry(byte[] rawRecord, MainSkillTable table, MercenarySupportState state, bool patchEntryKey)
        {
            if (state == null)
                return;

            PatchMainApplySkillEntry(
                rawRecord,
                table,
                state.SkillId,
                (byte)Math.Max(0, Math.Min(byte.MaxValue, (int)state.StrikerSkillId)),
                ResolveBaseSkillLevel(state),
                patchEntryKey);
        }

        private static void PatchMainApplySkillEntry(
            byte[] rawRecord,
            MainSkillTable table,
            ushort skillId,
            byte entryKey,
            byte levelOrFlag,
            bool patchEntryKey)
        {
            var count = rawRecord[table.CountOffset];
            if (skillId == 0 || count == 0 || table.EntriesOffset + 3 >= rawRecord.Length)
                return;

            for (var i = 0; i < count; i++)
            {
                var entry = table.EntriesOffset + i * 4;
                var existingSkillId = rawRecord[entry + 1] | (rawRecord[entry + 2] << 8);
                if (existingSkillId != skillId)
                    continue;

                if (patchEntryKey)
                    rawRecord[entry] = entryKey;
                rawRecord[entry + 3] = levelOrFlag;
                return;
            }

            if (table.EndOffset + 4 > rawRecord.Length)
                return;

            for (var i = table.EndOffset; i < table.EndOffset + 4; i++)
                if (rawRecord[i] != 0)
                    return;

            rawRecord[table.EndOffset] = entryKey;
            rawRecord[table.EndOffset + 1] = (byte)(skillId & 0xFF);
            rawRecord[table.EndOffset + 2] = (byte)((skillId >> 8) & 0xFF);
            rawRecord[table.EndOffset + 3] = levelOrFlag;
            rawRecord[table.CountOffset] = (byte)(count + 1);
            table.EndOffset += 4;
        }

        private static byte ResolveBaseSkillLevel(MercenarySupportState state)
        {
            if (state == null)
                return 0;

            return StrikerSupportSkillLevelSource.ResolveBaseLevel(
                state.SupportCharacterId,
                state.SkillId);
        }

        private static byte[] PatchSupportEquipmentEntries(byte[] rawRecord, int supportCharacterId, string context)
        {
            if (rawRecord == null || rawRecord.Length < 1100 || supportCharacterId <= 0)
                return rawRecord;

            var equipBlockOffset = FindDungeonEquipmentBlockOffset(rawRecord);
            if (equipBlockOffset < 0)
            {
                FileLogger.Log($"[GameProtocol] STRIKER {context} equipment sync skipped cid={supportCharacterId}: equip block not found");
                return rawRecord;
            }

            var equipped = LoadCurrentEquippedEntries(supportCharacterId, 0, 25);
            var entryOffsets = FindDungeonEquipmentEntryOffsets(rawRecord, equipBlockOffset, 25);

            PatchMissingAvatarSlotsWithDefaultItems(rawRecord, supportCharacterId, entryOffsets, equipped);

            foreach (var pair in equipped)
            {
                var slot = pair.Key;
                var rawEntry = pair.Value;
                if (!entryOffsets.TryGetValue(slot, out var entry))
                {
                    continue;
                }

                if (entry.Offset < 0 || entry.Offset + entry.Length > rawRecord.Length || rawRecord[entry.Offset] != slot)
                    continue;
                if (rawEntry.Length != entry.Length)
                {
                    PatchMismatchedLengthDungeonEquipmentEntry(rawRecord, entry, rawEntry);
                    continue;
                }

                Buffer.BlockCopy(rawEntry, 0, rawRecord, entry.Offset, entry.Length);
            }

            rawRecord = RemoveMissingTemplateEquipmentEntries(rawRecord, equipBlockOffset, entryOffsets, equipped, supportCharacterId, context);
            return rawRecord;
        }

        private static byte[] RemoveMissingTemplateEquipmentEntries(
            byte[] rawRecord,
            int equipBlockOffset,
            Dictionary<byte, EquipmentEntrySlice> entryOffsets,
            Dictionary<byte, byte[]> equipped,
            int supportCharacterId,
            string context)
        {
            if (rawRecord == null || entryOffsets == null || equipped == null)
                return rawRecord;

            var countOffset = equipBlockOffset - 1;
            if (countOffset < 0 || countOffset >= rawRecord.Length || rawRecord[countOffset] == 0)
                return rawRecord;

            // 如果slot0-8缺失则从职业默认形象中获取
            var removals = entryOffsets
                .Where(pair => pair.Key >= 9 && pair.Key <= 25 && !equipped.ContainsKey(pair.Key))
                .OrderByDescending(pair => pair.Value.Offset)
                .ToList();
            if (removals.Count == 0)
                return rawRecord;

            var patched = rawRecord;
            var removedSlots = new List<byte>();
            foreach (var pair in removals)
            {
                var slot = pair.Key;
                var entry = pair.Value;
                if (entry.Offset < 0 || entry.Length <= 0 || entry.Offset + entry.Length > patched.Length)
                    continue;

                if (patched[entry.Offset] != slot || patched[countOffset] == 0)
                    continue;

                var next = new byte[patched.Length];
                Buffer.BlockCopy(patched, 0, next, 0, entry.Offset);
                Buffer.BlockCopy(patched, entry.Offset + entry.Length, next, entry.Offset, patched.Length - entry.Offset - entry.Length);
                next[countOffset] = (byte)(patched[countOffset] - 1);
                patched = next;
                removedSlots.Add(slot);
            }

            if (removedSlots.Count > 0)
                FileLogger.Log($"[GameProtocol] STRIKER {context} equipment sync removed missing template slots cid={supportCharacterId}: slots=[{string.Join(",", removedSlots.OrderBy(x => x))}] len={rawRecord.Length} count {rawRecord[countOffset]}->{patched[countOffset]}");

            return patched;
        }

        private static void PatchMissingAvatarSlotsWithDefaultItems(
            byte[] rawRecord,
            int supportCharacterId,
            Dictionary<byte, EquipmentEntrySlice> entryOffsets,
            Dictionary<byte, byte[]> equipped)
        {
            if (rawRecord == null || entryOffsets == null || equipped == null || supportCharacterId <= 0)
                return;

            var support = LoadCharacterSummary(supportCharacterId);
            var defaults = ResolveDefaultAvatarItemIds(support?.Job ?? -1, support?.GrowType ?? -1);
            if (defaults == null || defaults.Length == 0)
                return;

            for (var slot = 0; slot <= 8 && slot < defaults.Length; slot++)
            {
                var slotByte = (byte)slot;
                if (equipped.ContainsKey(slotByte))
                    continue;

                var itemId = defaults[slot];

                if (itemId <= 0)
                    continue;

                if (!entryOffsets.TryGetValue(slotByte, out var entry))
                    continue;

                PatchDungeonEquipmentItemId(rawRecord, entry, slotByte, itemId);
            }
        }

        private static bool PatchDungeonEquipmentItemId(byte[] rawRecord, EquipmentEntrySlice entry, byte slot, int itemId)
        {
            if (rawRecord == null || entry.Offset < 0 || entry.Offset + 5 > rawRecord.Length)
                return false;

            if (rawRecord[entry.Offset] != slot)
                return false;

            var bytes = BitConverter.GetBytes(itemId);
            Buffer.BlockCopy(bytes, 0, rawRecord, entry.Offset + 1, 4);
            return true;
        }

        private static int[] ResolveDefaultAvatarItemIds(int job, int growType)
        {
            if (job < 0)
                return null;

            try
            {
                var text = PvfArchiveAccessor.ReadText("character/chn_1stawaken_defaultavatarinfo.chr");
                if (string.IsNullOrWhiteSpace(text))
                    return null;

                var tokens = Regex.Matches(text, @"-?\d+").Cast<Match>().Select(m => int.Parse(m.Value)).ToList();
                var normalizedGrow = StrikerSkillDataProvider.NormalizeGrowType(growType);
                int[] fallback = null;

                for (var start = 0; start < 13; start++)
                {
                    for (var i = start; i + 12 < tokens.Count; i += 13)
                    {
                        var rowJob = tokens[i];
                        var rowGrow = tokens[i + 1];
                        if (rowJob != job)
                            continue;

                        var values = new int[11];
                        for (var slot = 0; slot < values.Length; slot++)
                            values[slot] = tokens[i + 2 + slot];

                        var hasDefaultItems = values.Any(v => v > 0);
                        if (rowGrow == normalizedGrow && hasDefaultItems)
                            return values;

                        if (fallback == null && hasDefaultItems)
                            fallback = values;
                    }
                }

                return fallback;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[GameProtocol] STRIKER default avatar lookup failed job={job} grow={growType}: {ex.Message}");
                return null;
            }
        }

        internal static int[] ResolveDefaultAvatarItemIdsForTest(int job, int growType)
        {
            return ResolveDefaultAvatarItemIds(job, growType);
        }

        internal static byte[] PatchMissingTemplateEquipmentEntriesForTest(byte[] rawRecord, params byte[] equippedSlots)
        {
            var equipBlockOffset = FindDungeonEquipmentBlockOffset(rawRecord);
            var entryOffsets = FindDungeonEquipmentEntryOffsets(rawRecord, equipBlockOffset, 25);
            var equipped = (equippedSlots ?? Array.Empty<byte>()).Distinct().ToDictionary(slot => slot, _ => Array.Empty<byte>());
            return RemoveMissingTemplateEquipmentEntries(rawRecord, equipBlockOffset, entryOffsets, equipped, 0, "selftest");
        }

        private static bool PatchMismatchedLengthDungeonEquipmentEntry(byte[] rawRecord, EquipmentEntrySlice entry, byte[] rawEntry)
        {
            return TryCompactAndPatchDungeonEquipmentEntry(rawRecord, entry, rawEntry) ||
                TryPatchDungeonEquipmentFixedPrefix(rawRecord, entry, rawEntry) ||
                TryPatchDungeonEquipmentItemIdOnly(rawRecord, entry, rawEntry);
        }

        private static bool TryCompactAndPatchDungeonEquipmentEntry(byte[] rawRecord, EquipmentEntrySlice entry, byte[] rawEntry)
        {
            if (rawRecord == null || rawEntry == null || rawEntry.Length != 51 || entry.Length != 43)
                return false;

            if (entry.Offset < 0 || entry.Offset + entry.Length > rawRecord.Length)
                return false;

            if (rawRecord[entry.Offset] != rawEntry[0])
                return false;

            var compacted = new byte[43];
            Buffer.BlockCopy(rawEntry, 0, compacted, 0, 29);
            Buffer.BlockCopy(rawEntry, 37, compacted, 29, 14);
            Buffer.BlockCopy(compacted, 0, rawRecord, entry.Offset, compacted.Length);
            return true;
        }

        private static bool TryPatchDungeonEquipmentItemIdOnly(byte[] rawRecord, EquipmentEntrySlice entry, byte[] rawEntry)
        {
            if (rawRecord == null || rawEntry == null || entry.Length < 5 || rawEntry.Length < 5)
                return false;

            if (entry.Offset < 0 || entry.Offset + 5 > rawRecord.Length)
                return false;

            if (rawRecord[entry.Offset] != rawEntry[0])
                return false;

            Buffer.BlockCopy(rawEntry, 1, rawRecord, entry.Offset + 1, 4);
            return true;
        }

        private static bool TryPatchDungeonEquipmentFixedPrefix(byte[] rawRecord, EquipmentEntrySlice entry, byte[] rawEntry)
        {
            if (rawRecord == null || rawEntry == null || rawEntry.Length != 51 || entry.Length != 48)
                return false;

            if (entry.Offset < 0 || entry.Offset + entry.Length > rawRecord.Length)
                return false;

            if (rawRecord[entry.Offset] != rawEntry[0])
                return false;
            // slot22 的 51->48 不能整段裁剪，只同步固定字段，保留模板里的可变尾部布局。
            const int fixedPrefixLength = 29;
            if (rawEntry.Length < fixedPrefixLength || entry.Length < fixedPrefixLength)
                return false;

            Buffer.BlockCopy(rawEntry, 0, rawRecord, entry.Offset, fixedPrefixLength);
            return true;
        }

        private static int FindDungeonEquipmentBlockOffset(byte[] rawRecord)
        {
            for (var offset = 0; offset + 11 * 85 + 4 < rawRecord.Length; offset++)
            {
                var ok = true;
                for (var slot = 0; slot <= 11; slot++)
                {
                    var slotOffset = offset + slot * 85;
                    if (slotOffset + 4 >= rawRecord.Length || rawRecord[slotOffset] != slot)
                    {
                        ok = false;
                        break;
                    }

                    var itemId = BitConverter.ToInt32(rawRecord, slotOffset + 1);
                    if (itemId <= 0 || itemId > 500000000)
                    {
                        ok = false;
                        break;
                    }
                }

                if (ok)
                    return offset;
            }

            return -1;
        }

        private static Dictionary<byte, EquipmentEntrySlice> FindDungeonEquipmentEntryOffsets(byte[] rawRecord, int equipBlockOffset, int maxSlot)
        {
            var result = new Dictionary<byte, EquipmentEntrySlice>();
            if (rawRecord == null || equipBlockOffset < 0 || equipBlockOffset >= rawRecord.Length)
                return result;

            for (var slot = 0; slot <= 10; slot++)
            {
                var offset = equipBlockOffset + slot * 85;
                if (offset + 85 <= rawRecord.Length && rawRecord[offset] == slot)
                    result[(byte)slot] = new EquipmentEntrySlice(offset, 85);
            }

            var currentOffset = equipBlockOffset + 11 * 85;
            for (var slot = 11; slot <= maxSlot; slot++)
            {
                if (currentOffset < 0 || currentOffset >= rawRecord.Length || rawRecord[currentOffset] != slot)
                    break;

                var nextOffset = FindNextDungeonEquipmentSlotOffset(rawRecord, currentOffset + 1, (byte)(slot + 1));
                var endOffset = nextOffset >= 0 ? nextOffset : Math.Min(rawRecord.Length, currentOffset + 85);
                if (endOffset <= currentOffset)
                    break;

                result[(byte)slot] = new EquipmentEntrySlice(currentOffset, endOffset - currentOffset);
                if (nextOffset < 0)
                    break;

                currentOffset = nextOffset;
            }

            return result;
        }

        private static int FindNextDungeonEquipmentSlotOffset(byte[] rawRecord, int searchOffset, byte nextSlot)
        {
            var searchEnd = Math.Min(rawRecord.Length - 5, searchOffset + 90);
            for (var offset = searchOffset; offset <= searchEnd; offset++)
            {
                if (rawRecord[offset] != nextSlot)
                    continue;

                var itemId = BitConverter.ToInt32(rawRecord, offset + 1);
                if (itemId > 0 && itemId < 500000000)
                    return offset;
            }

            return -1;
        }

        private static Dictionary<byte, byte[]> LoadCurrentEquippedEntries(int characterId, int minSlot, int maxSlot)
        {
            var result = new Dictionary<byte, byte[]>();
            try
            {
                var connStr = SqliteDatabaseBootstrap.Initialize(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
                using (var conn = new SqliteConnection(connStr))
                {
                    conn.Open();
                    using (var cmd = new SqliteCommand(@"
SELECT slot, item_id, raw_entry
FROM character_equipped_entries
WHERE character_id=@cid AND slot>=@minSlot AND slot<=@maxSlot
ORDER BY slot", conn))
                    {
                        cmd.Parameters.AddWithValue("@cid", characterId);
                        cmd.Parameters.AddWithValue("@minSlot", minSlot);
                        cmd.Parameters.AddWithValue("@maxSlot", maxSlot);
                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                var slot = reader.GetInt32(0);
                                var itemId = reader.GetInt32(1);
                                var rawEntry = reader.GetValue(2) as byte[];
                                if (slot >= 0 && slot <= byte.MaxValue && itemId > 0 && rawEntry != null && rawEntry.Length > 0)
                                    result[(byte)slot] = rawEntry;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[GameProtocol] STRIKER load equipped failed cid={characterId}: {ex.Message}");
            }

            return result;
        }

        private static MainSkillTable FindLikelyMainSkillTable(byte[] rawRecord)
        {
            if (rawRecord == null || rawRecord.Length < 16)
                return null;

            MainSkillTable best = null;
            var bestScore = int.MinValue;

            for (var offset = Math.Max(0, rawRecord.Length - 512); offset < rawRecord.Length; offset++)
            {
                var count = rawRecord[offset];
                if (count <= 0 || count > 80)
                    continue;

                if (offset + 2 >= rawRecord.Length)
                    continue;

                var unknown = rawRecord[offset + 1];
                if (unknown > 16)
                    continue;

                var entriesOffset = offset + 2;
                var end = entriesOffset + count * 4;
                if (end > rawRecord.Length)
                    continue;

                var trailing = rawRecord.Length - end;
                if (trailing > 32)
                    continue;

                var plausible = 0;
                for (var i = 0; i < count; i++)
                {
                    var entry = entriesOffset + i * 4;
                    var skillId = rawRecord[entry + 1] | (rawRecord[entry + 2] << 8);
                    var flagOrLevel = rawRecord[entry + 3];
                    if (skillId > 0 && skillId < 10000 && flagOrLevel <= 100)
                        plausible++;
                }

                var selectedOffset = offset - 7;
                var selectedBonus = 0;
                if (selectedOffset >= 0 && selectedOffset + 1 < rawRecord.Length)
                {
                    var selectedSkill = ReadUInt16(rawRecord, selectedOffset);
                    if (selectedSkill > 0 && selectedSkill < 10000)
                        selectedBonus = 24;
                }

                var score = plausible * 12 + count + selectedBonus - trailing;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = new MainSkillTable
                    {
                        CountOffset = offset,
                        SelectedOffset = selectedOffset,
                        EntriesOffset = entriesOffset,
                        EndOffset = end,
                    };
                }
            }

            return best;
        }

        private static byte[] LoadTagCharacterRawRecord()
        {
            return CloneTagCharacterRawRecordTemplate();
        }

        internal static byte[] CloneTagCharacterRawRecordTemplate()
        {
            var clone = new byte[TagCharacterRecordTemplate.Length];
            Buffer.BlockCopy(TagCharacterRecordTemplate, 0, clone, 0, TagCharacterRecordTemplate.Length);
            return clone;
        }

        private static SqliteMercenarySupportRepository CreateSupportRepository()
        {
            return new SqliteMercenarySupportRepository(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
        }

        private static ushort ReadUInt16(byte[] body, int offset)
        {
            return (ushort)(body[offset] | (body[offset + 1] << 8));
        }
        private sealed class CharacterSummary
        {
            public int Job { get; set; }
            public int GrowType { get; set; }
            public int Level { get; set; }
        }

        private sealed class MainSkillTable
        {
            public int CountOffset { get; set; }
            public int SelectedOffset { get; set; }
            public int EntriesOffset { get; set; }
            public int EndOffset { get; set; }
        }

        private readonly struct EquipmentEntrySlice
        {
            public EquipmentEntrySlice(int offset, int length)
            {
                Offset = offset;
                Length = length;
            }

            public int Offset { get; }
            public int Length { get; }
        }
    }
}
