using System.Collections.Generic;

namespace WachuMakeyMaking.Utils
{
    public static class DictionaryExtensions
    {
        public static void MergeUnion<K, V>(
            this Dictionary<K, HashSet<V>> target,
            Dictionary<K, HashSet<V>> source) where K : notnull
        {
            foreach (var (key, set) in source)
            {
                if (target.TryGetValue(key, out var targetSet))
                {
                    targetSet.UnionWith(set);
                }
                else
                {
                    target.Add(key, [.. set]);
                }
            }
        }
    }
}
