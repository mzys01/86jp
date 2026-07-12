using DfoServer.Game.Inventory;
using System;

namespace DfoServer.Game.Accounts
{
    public enum GrowthCapsuleClaimStatus
    {
        Success,
        InsufficientExp,
        InventoryFull,
        InvalidOwner,
    }

    public sealed class GrowthCapsuleClaimResult
    {
        public GrowthCapsuleClaimStatus Status { get; set; }
        public short AssignedSlot { get; set; } = -1;
        public int ItemId { get; set; }
        public int ItemCount { get; set; }
        public GrowthCapsuleSummary Summary { get; set; }
        public bool Success => Status == GrowthCapsuleClaimStatus.Success;
    }

    public sealed class GrowthCapsuleClaimService
    {
        private readonly IAssetService _assetService;

        public GrowthCapsuleClaimService(IAssetService assetService)
        {
            _assetService = assetService ?? throw new ArgumentNullException(nameof(assetService));
        }

        public GrowthCapsuleClaimResult Claim(int characterId, int accountId)
        {
            if (characterId <= 0 || accountId <= 0)
            {
                return new GrowthCapsuleClaimResult
                {
                    Status = GrowthCapsuleClaimStatus.InvalidOwner,
                    Summary = GrowthCapsuleDataProvider.Calculate(0),
                };
            }

            using (var scope = _assetService.OpenScope(characterId, accountId))
            {
                if (!IsCharacterOwnedByAccount(scope))
                {
                    return new GrowthCapsuleClaimResult
                    {
                        Status = GrowthCapsuleClaimStatus.InvalidOwner,
                        Summary = GrowthCapsuleDataProvider.Calculate(0),
                    };
                }

                var totalExp = GrowthCapsuleProgressRepository.LoadTotalExp(
                    scope.Connection, scope.Transaction, accountId);
                var summary = GrowthCapsuleDataProvider.Calculate(totalExp);
                if (totalExp < summary.RequiredExp)
                {
                    return new GrowthCapsuleClaimResult
                    {
                        Status = GrowthCapsuleClaimStatus.InsufficientExp,
                        Summary = summary,
                    };
                }

                if (!_assetService.TryAddItem(
                        scope,
                        GrowthCapsuleDataProvider.RewardItemId,
                        GrowthCapsuleDataProvider.RewardItemCount,
                        out var assignedSlot))
                {
                    return new GrowthCapsuleClaimResult
                    {
                        Status = GrowthCapsuleClaimStatus.InventoryFull,
                        Summary = summary,
                    };
                }

                GrowthCapsuleProgressRepository.UpdateTotalExpInTransaction(
                    scope.Connection, scope.Transaction, accountId, 0);
                scope.Commit();
                return new GrowthCapsuleClaimResult
                {
                    Status = GrowthCapsuleClaimStatus.Success,
                    AssignedSlot = assignedSlot,
                    ItemId = GrowthCapsuleDataProvider.RewardItemId,
                    ItemCount = GrowthCapsuleDataProvider.RewardItemCount,
                    Summary = GrowthCapsuleDataProvider.Calculate(0),
                };
            }
        }

        private static bool IsCharacterOwnedByAccount(DbScope scope)
        {
            using (var command = scope.Connection.CreateCommand())
            {
                command.Transaction = scope.Transaction;
                command.CommandText = @"
SELECT 1
FROM characters
WHERE character_id=@cid AND account_id=@aid AND delete_flag=0;";
                command.Parameters.AddWithValue("@cid", scope.CharacterId);
                command.Parameters.AddWithValue("@aid", scope.AccountId);
                return command.ExecuteScalar() != null;
            }
        }
    }
}
