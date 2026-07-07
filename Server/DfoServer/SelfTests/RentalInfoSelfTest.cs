using System;
using DfoServer.Game.SelectCharacter;
using DfoServer.Network.Builders;

namespace DfoServer.SelfTests
{
    public static class RentalInfoSelfTest
    {
        public static int Run()
        {
            var rental = new RentalInfoSnapshot();
            var shopId = 0xFC85987Au;
            var inventoryId = 0x05FAEAB2u;
            var now = 1000u;
            var expireTime = now + 86400u;

            rental.Items.Add(new RentalItemSnapshot { ItemId = shopId, ExpireTime = 1 });
            rental.UpsertItem(shopId, inventoryId, expireTime);

            if (rental.Items.Count != 1)
                return Fail("legacy shop id entry was not replaced");

            if (rental.Items[0].ItemId != shopId)
                return Fail("rental panel item id must be shop id");

            if (rental.Items[0].InventoryTemplateId != inventoryId)
                return Fail("rental panel item must preserve inventory template id");

            var body = RentalInfoBodyBuilder.BuildWireBody(60, rental, now);
            if (body.Length != 16)
                return Fail("unexpected 0x0357 body length");

            if (BitConverter.ToUInt32(body, 0) != 60)
                return Fail("lucky star field mismatch");

            if (BitConverter.ToUInt32(body, 4) != 1)
                return Fail("item count field mismatch");

            if (BitConverter.ToUInt32(body, 8) != inventoryId)
                return Fail("wire item id must be inventory template id");

            if (BitConverter.ToUInt32(body, 12) != expireTime)
                return Fail("wire item secondary field must be absolute expire time");

            var storage = RentalInfoSnapshot.BuildStorageBody(rental);
            var parsed = new RentalInfoSnapshot();
            RentalInfoSnapshot.ParseStorageBody(storage, parsed);
            if (parsed.Items.Count != 1
                || parsed.Items[0].ItemId != shopId
                || parsed.Items[0].InventoryTemplateId != inventoryId
                || parsed.Items[0].ExpireTime != expireTime)
                return Fail("storage roundtrip must preserve shop id, inventory template id, and expire time");

            rental.UpsertItem(0x7893B721u, 0x05FAEAB4u, now + 3600u);
            rental.UpsertItem(0xE32F509Fu, 0x05FAEAB3u, now + 7200u);
            rental.UpsertItem(0x1E3D6BE4u, 0x05FAEAB4u, now + 8000u);
            rental.UpsertItem(0x1E3D6BE4u, 0x05FAEAB3u, now + 9000u);

            if (rental.Items.Count != 3)
                return Fail("same shop id with different inventory templates must not collapse rental entries");

            body = RentalInfoBodyBuilder.BuildWireBody(30, rental, now);
            if (body.Length != 32)
                return Fail("0x0357 wire body must include three active rental items");

            if (BitConverter.ToUInt32(body, 4) != 3)
                return Fail("wire item count must include three active rentals");

            if (BitConverter.ToUInt32(body, 8) != inventoryId
                || BitConverter.ToUInt32(body, 16) != 0x05FAEAB4u
                || BitConverter.ToUInt32(body, 24) != 0x05FAEAB3u)
                return Fail("wire body should keep all three rental inventory template ids in storage order");

            if (BitConverter.ToUInt32(body, 12) != expireTime
                || BitConverter.ToUInt32(body, 20) != now + 8000u
                || BitConverter.ToUInt32(body, 28) != now + 9000u)
                return Fail("wire body should include absolute expire time as secondary field");

            rental.Items[0].ExpireTime = now;
            if (rental.RemoveExpired(now) != 1)
                return Fail("expired rental entries must be removed from snapshot");

            Console.WriteLine("RentalInfoSelfTest OK");
            return 0;
        }

        private static int Fail(string message)
        {
            Console.Error.WriteLine("RentalInfoSelfTest FAILED: " + message);
            return 1;
        }
    }
}
