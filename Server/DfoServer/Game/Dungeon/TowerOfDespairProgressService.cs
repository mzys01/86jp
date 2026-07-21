using DungeonData = DfoServer.GameWorld.Dungeon;

namespace DfoServer.Game.Dungeon
{
    public sealed class TowerOfDespairProgressService
    {
        private readonly TowerOfDespairProgressRepository _repository;

        public TowerOfDespairProgressService(TowerOfDespairProgressRepository repository)
        {
            _repository = repository ?? throw new System.ArgumentNullException(nameof(repository));
        }

        public int ResolveEntryDungeonId(int characterId, int requestedDungeonId)
        {
            if (!DungeonData.TryGetTowerOfDespairFloor(requestedDungeonId, out _))
                return requestedDungeonId;

            var nextFloor = _repository.GetNextFloor(characterId);
            return DungeonData.TryGetTowerOfDespairDungeonId(nextFloor, out var dungeonId)
                ? dungeonId
                : requestedDungeonId;
        }

        public int GetNextFloor(int characterId)
        {
            return _repository.GetNextFloor(characterId);
        }

        public int RecordClear(int characterId, int clearedDungeonId)
        {
            if (!DungeonData.TryGetTowerOfDespairFloor(clearedDungeonId, out var clearedFloor))
                return 0;

            return _repository.RecordClear(characterId, clearedFloor);
        }
    }
}
