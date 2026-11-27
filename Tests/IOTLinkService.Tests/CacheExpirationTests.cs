using System;
using IOTLinkAPI.Common;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IOTLinkService.Tests
{
    [TestClass]
    public class CacheExpirationTests
    {
        [TestMethod]
        public void ReadsDoNotExtendTheFiveMinuteLifetime()
        {
            var now = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var cache = new CacheContainer<int, string>(() => now);
            cache.UpdateItem(1, "first");

            now = now.AddMinutes(5).AddTicks(-1);
            Assert.AreEqual("first", cache.GetItem(1));
            now = now.AddTicks(1);
            Assert.IsNull(cache.GetItem(1));
            Assert.AreEqual("second", cache.GetItem(1, () => "second"));
            Assert.AreEqual("second", cache.GetItem(1));
        }

        [TestMethod]
        public void CleanupEvictsStaleEntriesAndKeepsUpdatedEntriesUntilTheyExpire()
        {
            var now = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var cache = new CacheContainer<int, string>(() => now);
            cache.UpdateItem(1, "stale");
            cache.UpdateItem(2, "initial");
            now = now.AddMinutes(4);
            cache.UpdateItem(2, "updated");

            now = now.AddMinutes(1);
            Assert.AreEqual(1, cache.RemoveExpiredItems());
            Assert.IsNull(cache.GetItem(1));
            Assert.AreEqual("updated", cache.GetItem(2));

            now = now.AddMinutes(4);
            Assert.AreEqual(1, cache.RemoveExpiredItems());
            Assert.IsNull(cache.GetItem(2));
        }
    }
}
