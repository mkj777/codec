using System;

namespace Codec.Helpers
{
    internal static class StringSimilarity
    {
        public static int LevenshteinDistance(string source, string target)
        {
            if (string.IsNullOrEmpty(source)) return target?.Length ?? 0;
            if (string.IsNullOrEmpty(target)) return source.Length;

            var d = new int[source.Length + 1, target.Length + 1];
            for (int i = 0; i <= source.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= target.Length; j++) d[0, j] = j;

            for (int i = 1; i <= source.Length; i++)
            {
                for (int j = 1; j <= target.Length; j++)
                {
                    int cost = (target[j - 1] == source[i - 1]) ? 0 : 1;
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                }
            }
            return d[source.Length, target.Length];
        }

        public static double Calculate(string source, string target)
        {
            int distance = LevenshteinDistance(source, target);
            int maxLength = Math.Max(source.Length, target.Length);
            return maxLength > 0 ? 1.0 - ((double)distance / maxLength) : 0.0;
        }
    }
}
