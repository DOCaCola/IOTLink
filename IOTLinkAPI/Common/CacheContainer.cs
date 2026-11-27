using System;
using System.Collections.Generic;
using System.Linq;
using System.Timers;

namespace IOTLinkAPI.Common
{
    public class CacheContainer<TKey, TValue>
    {
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

        public class CacheItem
        {
            public TKey Key { get; set; }
            public TValue Value { get; set; }

            public DateTime LastAccessed { get; set; }
            public DateTime LastUpdated { get; set; }
        }

        private Dictionary<TKey, CacheItem> _cache = new Dictionary<TKey, CacheItem>();
        private readonly object _cacheLock = new object();
        private readonly Func<DateTime> _utcNow;
        private readonly Timer _cleanupTimer;

        public CacheContainer() : this(() => DateTime.UtcNow)
        {
            _cleanupTimer = new Timer(CacheLifetime.TotalMilliseconds);
            _cleanupTimer.Elapsed += (sender, args) => RemoveExpiredItems();
            _cleanupTimer.Start();
        }

        internal CacheContainer(Func<DateTime> utcNow)
        {
            _utcNow = utcNow;
        }

        internal int RemoveExpiredItems()
        {
            lock (_cacheLock)
            {
                var now = _utcNow();
                var previousCount = _cache.Count;
                _cache = _cache.Values.Where(item => !IsExpired(item, now)).ToDictionary(item => item.Key);
                return previousCount - _cache.Count;
            }
        }

        public TValue GetItem(TKey key, Func<TValue> createItem = null)
        {
            if (key == null)
                return default;

            lock (_cacheLock)
            {
                var now = _utcNow();
                if (_cache.TryGetValue(key, out var item))
                {
                    if (!IsExpired(item, now))
                    {
                        item.LastAccessed = now;
                        return item.Value;
                    }

                    _cache.Remove(key);
                }
            }

            if (createItem == null)
                return default;

            var value = createItem();
            UpdateItem(key, value);
            return value;
        }

        public bool UpdateItem(TKey key, TValue value)
        {
            if (value == null)
                return false;

            lock (_cacheLock)
            {
                var now = _utcNow();
                _cache[key] = new CacheItem
                {
                    Key = key,
                    Value = value,
                    LastUpdated = now,
                    LastAccessed = now
                };
            }

            return true;
        }

        public bool RemoveKey(TKey key)
        {
            if (key == null)
                return false;

            lock (_cacheLock)
                _cache.Remove(key);

            return true;
        }

        private static bool IsExpired(CacheItem item, DateTime now)
        {
            return now - item.LastUpdated >= CacheLifetime;
        }
    }
}
