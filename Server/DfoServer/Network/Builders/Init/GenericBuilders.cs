using DfoServer.Game.SelectCharacter;
using System;
using Microsoft.Data.Sqlite;

namespace DfoServer.Network.Builders
{
    public sealed class SimpleByteBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType { get; }

        private readonly Func<SelectCharacterInitializationSnapshot, byte> _valueSelector;

        public SimpleByteBodyBuilder(ushort notiType, Func<SelectCharacterInitializationSnapshot, byte> valueSelector)
        {
            NotiType = notiType;
            _valueSelector = valueSelector;
        }

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            body = new byte[] { _valueSelector(snapshot.InitializationSnapshot) };
            return true;
        }
    }

    public sealed class EmptyBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType { get; }

        public EmptyBodyBuilder(ushort notiType)
        {
            NotiType = notiType;
        }

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            body = Array.Empty<byte>();
            return true;
        }
    }

    
    
    
    
    public sealed class DbFieldBuilder : IInitPacketBuilder
    {
        public ushort NotiType { get; }
        private readonly byte _command;

        public DbFieldBuilder(ushort notiType, byte command = 0x00)
        {
            NotiType = notiType;
            _command = command;
        }

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            int cid = snapshot.CharacterRecord?.CharacterId ?? 0;
            if (cid <= 0) { body = null; return false; }

            var connStr = Infrastructure.SqliteDatabaseBootstrap.Initialize(
                Infrastructure.ServerPaths.DatabasePath, Infrastructure.ServerPaths.SchemaFilePath);
            byte[] src = null;
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(
                    "SELECT body FROM character_init_bodies WHERE character_id=@cid AND noti_type=@nt AND occurrence_index=@oi", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", cid);
                    cmd.Parameters.AddWithValue("@nt", (int)NotiType);
                    cmd.Parameters.AddWithValue("@oi", occurrenceIndex);
                    src = cmd.ExecuteScalar() as byte[];
                }
            }
            if (src == null) { body = null; return false; }

            var writer = new GamePacketWriter();
            for (int i = 0; i < src.Length; i++)
                writer.WriteByte(src[i]);
            body = writer.ToArray();
            return true;
        }
    }

    
    
    
    
    
    public sealed class UserPositionBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x0016;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var c = snapshot.CharacterRecord;
            if (c == null) { body = null; return false; }
            var w = new GamePacketWriter();
            w.WriteUInt16((ushort)c.CharacterId);
            w.WriteUInt16((ushort)c.PosX);
            w.WriteUInt16((ushort)c.PosY);
            w.WriteByte(c.Direction);
            w.WriteUInt16(100);
            body = w.ToArray();
            return true;
        }
    }

    
    
    
    
    public sealed class CeraBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x0035;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var c = snapshot.CharacterRecord;
            if (c == null) { body = null; return false; }

            int cera = (int)c.Coin;

            var w = new GamePacketWriter();
            w.WriteByte(1);           
            w.WriteInt32(cera);       
            w.WriteInt32(0);          
            w.WriteInt32(0);          
            body = w.ToArray();
            return true;
        }
    }

}
