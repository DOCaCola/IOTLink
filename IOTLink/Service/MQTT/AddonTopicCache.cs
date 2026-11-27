using IOTLinkAPI.Helpers;
using System.Collections.Generic;
using System.Linq;

namespace IOTLinkService.Service.MQTT
{
    internal sealed class AddonTopicCache
    {
        // Bound retention even for add-ons that announce a succession of short-lived sensors.
        internal const int Capacity = 1024;
        private readonly Dictionary<(string AddonId, string Topic), string> _suffixes =
            new Dictionary<(string, string), string>();
        private readonly Queue<(string AddonId, string Topic)> _registrationOrder =
            new Queue<(string, string)>();

        internal string GetSuffix(string addonId, string topic, bool register = false)
        {
            if (addonId == null)
                return MQTTHelper.NormalizeTopicSuffix(topic);

            var key = (addonId, topic);
            if (_suffixes.TryGetValue(key, out string suffix))
                return suffix;

            suffix = MQTTHelper.NormalizeTopicSuffix(FormatAddonTopic(addonId, topic));
            if (register)
            {
                if (_suffixes.Count == Capacity)
                    _suffixes.Remove(_registrationOrder.Dequeue());
                _suffixes.Add(key, suffix);
                _registrationOrder.Enqueue(key);
            }
            return suffix;
        }

        internal static string FormatAddonTopic(string addonId, string topic)
        {
            topic = topic.Split('/').Select(x => StringHelper.PascalToKebabCase(x)).Aggregate((x, y) => x + '/' + y);
            addonId = StringHelper.PascalToKebabCase(addonId);
            return MQTTHelper.SanitizeTopic(string.Format("{0}/{1}", addonId, topic));
        }
    }
}
