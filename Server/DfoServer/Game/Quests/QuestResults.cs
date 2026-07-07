using System.Collections.Generic;

namespace DfoServer.Game.Quests
{
    // 任务四个命令(接取/放弃/触发器/完成)的结构化处理结果。
    // QuestService 只产出这些对象; 序列化成应答包字节的工作全部在
    // QuestAckBuilder。ErrorCode==0 表示成功, 非零值直接进失败应答包。
    public sealed class QuestAcceptResult
    {
        public byte ErrorCode;
        public ushort QuestId;
        public uint InitTrigger;
        public List<QuestEventItemGrant> EventItems = new List<QuestEventItemGrant>();

        public bool Success => ErrorCode == 0;

        public static QuestAcceptResult Fail(byte errorCode) => new QuestAcceptResult { ErrorCode = errorCode };
    }

    // 接取时发放的事件道具(应答包里逐条回显给客户端)。
    public sealed class QuestEventItemGrant
    {
        public ushort SlotIndex;
        public int ItemId;
        public int Count;
    }

    public sealed class QuestGiveupResult
    {
        public byte ErrorCode;
        public ushort QuestId;

        public bool Success => ErrorCode == 0;

        public static QuestGiveupResult Fail(byte errorCode) => new QuestGiveupResult { ErrorCode = errorCode };
    }

    public sealed class QuestSetTriggerResult
    {
        public byte ErrorCode;
        public ushort QuestId;
        public uint TriggerValue;

        public bool Success => ErrorCode == 0;

        public static QuestSetTriggerResult Fail(byte errorCode) => new QuestSetTriggerResult { ErrorCode = errorCode };
    }

    public sealed class QuestFinishResult
    {
        public byte ErrorCode;
        public ushort QuestId;
        // 经验/金币已含倍率。
        public uint Exp;
        public uint Gold;
        // 经验结算后的等级与总经验(与奖励同一事务已落库; Exp 为 0 时等于结算前取值)。
        public byte NewLevel;
        public uint NewExp;
        public int ChainType;
        // chainType 1/2=转职号, 20=专家职类型, 30=开孔的装备栏位号。
        public int GrowNumber;
        public List<ConsumedItemEntry> ConsumedEntries = new List<ConsumedItemEntry>();
        public List<InsertedItemEntry> InsertedEntries = new List<InsertedItemEntry>();

        public bool Success => ErrorCode == 0;

        public static QuestFinishResult Fail(byte errorCode) => new QuestFinishResult { ErrorCode = errorCode };
    }

    public sealed class ConsumedItemEntry
    {
        public byte UpdateType;
        public ushort SlotIndex;
        public uint RemainingCount;
    }

    public sealed class InsertedItemEntry
    {
        public ushort SlotIndex;
        public int ItemId;
        public bool IsEquipment;
        public uint CountOrSeed;
        public ushort EquipDurability;
    }
}
