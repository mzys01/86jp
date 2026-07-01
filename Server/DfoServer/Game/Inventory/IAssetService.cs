using DfoServer.Game.Currency;

namespace DfoServer.Game.Inventory
{
    public interface IAssetService
    {
        DbScope OpenScope(int characterId, int accountId);

        bool TryAddItem(DbScope scope, int itemTemplateId, int count, out short assignedSlot);
        bool TryRemoveItem(DbScope scope, int itemTemplateId, int count, out short slot, out int remaining);
        int CountItem(DbScope scope, int itemTemplateId);

        WalletSnapshot LoadWallet(DbScope scope);
        void AddGold(DbScope scope, int delta);
        void AddCera(DbScope scope, int delta);
        void AddTokenCera(DbScope scope, int delta);
        void AddHappyTokenCera(DbScope scope, int delta);
        void AddLuckyStar(DbScope scope, int delta);

        CharacterItemListSnapshot LoadSnapshot(DbScope scope);
    }
}
