using System;
using System.IO;

namespace Codec.Helpers
{
    internal static class AssetUriResolver
    {
        public static string ResolveBundledAssetUri(string relativePath)
            => TryResolveToUsableUriString(relativePath, out var resolved)
                ? resolved
                : BuildAppFileUri(Path.Combine(AppContext.BaseDirectory, NormalizeRelativePath(relativePath)));

        public static string? ResolveImageSource(string? cachePath, string? url, string? placeholderRelativePath = null)
        {
            if (TryResolveToUsableUriString(cachePath, out var cached))
            {
                return cached;
            }

            if (TryResolveToUsableUriString(url, out var remoteOrLocal))
            {
                return remoteOrLocal;
            }

            return string.IsNullOrWhiteSpace(placeholderRelativePath)
                ? null
                : ResolveBundledAssetUri(placeholderRelativePath);
        }

        public static bool TryResolveToUsableUriString(string? value, out string resolved)
        {
            resolved = string.Empty;

            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            try
            {
                if (Uri.TryCreate(value, UriKind.Absolute, out var absoluteUri))
                {
                    if (absoluteUri.IsFile)
                    {
                        if (!File.Exists(absoluteUri.LocalPath))
                        {
                            return false;
                        }

                        resolved = absoluteUri.AbsoluteUri;
                        return true;
                    }

                    if (absoluteUri.Scheme.Equals("ms-appx", StringComparison.OrdinalIgnoreCase))
                    {
                        string? localPath = TryMapMsAppxToLocalPath(absoluteUri);
                        if (localPath != null)
                        {
                            resolved = BuildAppFileUri(localPath);
                            return true;
                        }

                        resolved = absoluteUri.AbsoluteUri;
                        return true;
                    }

                    if (absoluteUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                        absoluteUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                        absoluteUri.Scheme.Equals("ms-appdata", StringComparison.OrdinalIgnoreCase))
                    {
                        resolved = absoluteUri.AbsoluteUri;
                        return true;
                    }

                    return false;
                }

                string candidatePath = value;
                if (!Path.IsPathRooted(candidatePath))
                {
                    candidatePath = Path.Combine(AppContext.BaseDirectory, NormalizeRelativePath(candidatePath));
                }

                if (!File.Exists(candidatePath))
                {
                    return false;
                }

                resolved = BuildAppFileUri(candidatePath);
                return true;
            }
            catch
            {
                resolved = string.Empty;
                return false;
            }
        }

        public static bool IsBundledAssetReference(string? value, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string normalizedRelative = NormalizeRelativePath(relativePath)
                .Replace('\\', '/')
                .TrimStart('/');

            if (value.Contains(normalizedRelative, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return TryResolveToUsableUriString(value, out var resolved) &&
                   resolved.Contains(normalizedRelative, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeRelativePath(string relativePath)
            => relativePath.Replace('/', Path.DirectorySeparatorChar)
                           .Replace('\\', Path.DirectorySeparatorChar);

        private static string BuildAppFileUri(string localPath)
            => new Uri(Path.GetFullPath(localPath)).AbsoluteUri;

        private static string? TryMapMsAppxToLocalPath(Uri msAppxUri)
        {
            string localSegment = Uri.UnescapeDataString(msAppxUri.AbsolutePath.TrimStart('/'));
            if (string.IsNullOrWhiteSpace(localSegment))
            {
                return null;
            }

            string candidatePath = Path.Combine(AppContext.BaseDirectory, NormalizeRelativePath(localSegment));
            return File.Exists(candidatePath) ? candidatePath : null;
        }
    }
}
