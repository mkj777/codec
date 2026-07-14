using SteamKit2;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Codec.Services.Scanning;

namespace Codec.Services.Fetching
{
    /// <summary>
    /// Anonymous SteamKit2 client used to resolve hashed library asset URLs via PICS.
    /// One long-lived connection; PICS results cached in-memory for the session.
    /// </summary>
    public sealed class SteamKitService : IAsyncDisposable
    {
        private const string LogPrefix = "[SteamKit]";
        private const string AssetCdnBase = "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps";

        private readonly SteamClient _client;
        private readonly CallbackManager _manager;
        private readonly SteamUser _steamUser;
        private readonly SteamApps _steamApps;
        private readonly ScanResourceLimiter? _resourceLimiter;

        private readonly ConcurrentDictionary<uint, SteamLibraryAssets> _assetCache = new();
        private readonly SemaphoreSlim _connectGate = new(1, 1);

        private CancellationTokenSource _runCts = new();
        private Task? _runLoop;

        private TaskCompletionSource<bool>? _connectedTcs;
        private TaskCompletionSource<bool>? _loggedOnTcs;

        private volatile bool _isLoggedOn;
        private volatile bool _disposed;

        public SteamKitService(ScanResourceLimiter? resourceLimiter = null)
        {
            _resourceLimiter = resourceLimiter;
            Log("ctor: initializing SteamClient");
            _client = new SteamClient();
            _manager = new CallbackManager(_client);
            _steamUser = _client.GetHandler<SteamUser>()!;
            _steamApps = _client.GetHandler<SteamApps>()!;

            _manager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
            _manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
            _manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
            _manager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
        }

        public async Task<bool> EnsureConnectedAsync(CancellationToken ct = default)
        {
            if (_disposed)
            {
                Log("EnsureConnected: already disposed");
                return false;
            }
            if (_isLoggedOn) return true;

            await _connectGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_isLoggedOn) return true;

                if (_runLoop == null || _runLoop.IsCompleted)
                {
                    Log("starting callback loop");
                    _runCts = new CancellationTokenSource();
                    _runLoop = Task.Run(() => RunCallbackLoop(_runCts.Token));
                }

                _connectedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _loggedOnTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                var sw = Stopwatch.StartNew();
                Log("calling SteamClient.Connect()");
                _client.Connect();

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linked.CancelAfter(TimeSpan.FromSeconds(15));

                var connectTask = _connectedTcs.Task;
                var connected = await Task.WhenAny(connectTask, Task.Delay(Timeout.Infinite, linked.Token)).ConfigureAwait(false);
                if (connected != connectTask)
                {
                    Log($"connect TIMED OUT after {sw.ElapsedMilliseconds}ms");
                    return false;
                }
                if (!await connectTask.ConfigureAwait(false))
                {
                    Log($"connect callback returned FAIL after {sw.ElapsedMilliseconds}ms");
                    return false;
                }
                Log($"connected in {sw.ElapsedMilliseconds}ms; awaiting logon");

                var logonTask = _loggedOnTcs.Task;
                var loggedOn = await Task.WhenAny(logonTask, Task.Delay(Timeout.Infinite, linked.Token)).ConfigureAwait(false);
                if (loggedOn != logonTask)
                {
                    Log($"logon TIMED OUT after {sw.ElapsedMilliseconds}ms");
                    return false;
                }
                bool ok = await logonTask.ConfigureAwait(false);
                Log($"logon {(ok ? "OK" : "FAILED")} after {sw.ElapsedMilliseconds}ms total");
                return ok;
            }
            catch (Exception ex)
            {
                Log($"EnsureConnected exception: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
            finally
            {
                _connectGate.Release();
            }
        }

        public async Task<SteamLibraryAssets?> GetLibraryAssetsAsync(uint appId, CancellationToken ct = default)
        {
            if (appId == 0)
            {
                Log("GetLibraryAssets: appId == 0, skipping");
                return null;
            }
            if (_assetCache.TryGetValue(appId, out var cached))
            {
                Log($"appId={appId} cache HIT (capsule={!string.IsNullOrEmpty(cached.CapsuleUrl)}, hero={!string.IsNullOrEmpty(cached.HeroUrl)}, logo={!string.IsNullOrEmpty(cached.LogoUrl)})");
                return cached;
            }

            Log($"appId={appId} cache MISS, ensuring connection");
            if (!await EnsureConnectedAsync(ct).ConfigureAwait(false))
            {
                Log($"appId={appId}: connection failed, aborting PICS lookup");
                return null;
            }

            var sw = Stopwatch.StartNew();
            try
            {
                Log($"appId={appId} issuing PICSGetProductInfo");
                var result = _resourceLimiter == null
                    ? await FetchProductInfoAsync(appId, ct).ConfigureAwait(false)
                    : await _resourceLimiter.RunNetworkAsync(innerCt => FetchProductInfoAsync(appId, innerCt), ct).ConfigureAwait(false);

                int callbackCount = result.Results?.Count ?? 0;
                Log($"appId={appId} PICS returned {callbackCount} callback(s) in {sw.ElapsedMilliseconds}ms (Complete={result.Complete}, Failed={result.Failed})");

                if (result.Failed)
                {
                    Log($"appId={appId} PICS reports Failed=true");
                }

                foreach (var callback in result.Results ?? Enumerable.Empty<SteamApps.PICSProductInfoCallback>())
                {
                    Log($"appId={appId} callback Apps.Count={callback.Apps.Count}, UnknownApps.Count={callback.UnknownApps.Count}");
                    if (callback.UnknownApps.Contains(appId))
                    {
                        Log($"appId={appId} reported as UNKNOWN by Steam");
                        continue;
                    }
                    if (!callback.Apps.TryGetValue(appId, out var app))
                    {
                        Log($"appId={appId} not in Apps dict for this callback");
                        continue;
                    }

                    Log($"appId={appId} PICS app present, ChangeNumber={app.ChangeNumber}, KeyValues children={app.KeyValues.Children.Count}");

                    var assets = ExtractAssets(appId, app.KeyValues);
                    if (assets != null)
                    {
                        Log($"appId={appId} extracted: capsule={assets.CapsuleHash ?? "<null>"} hero={assets.HeroHash ?? "<null>"} logo={assets.LogoHash ?? "<null>"}");
                        Log($"appId={appId} URLs: cap={assets.CapsuleUrl ?? "<null>"}");
                        Log($"appId={appId} URLs: hero={assets.HeroUrl ?? "<null>"}");
                        Log($"appId={appId} URLs: logo={assets.LogoUrl ?? "<null>"}");
                        _assetCache[appId] = assets;
                        return assets;
                    }

                    Log($"appId={appId} EXTRACT RETURNED NULL — dumping common KV tree:");
                    DumpKv(app.KeyValues["common"], maxDepth: 4);
                }

                Log($"appId={appId} no usable callback contained the app");
            }
            catch (TimeoutException)
            {
                Log($"appId={appId} PICS TIMEOUT after {sw.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                Log($"appId={appId} PICS exception: {ex.GetType().Name}: {ex.Message}");
            }

            return null;
        }

        private async Task<AsyncJobMultiple<SteamApps.PICSProductInfoCallback>.ResultSet> FetchProductInfoAsync(uint appId, CancellationToken cancellationToken)
        {
            var request = new SteamApps.PICSRequest(appId);
            var job = _steamApps.PICSGetProductInfo(request, package: null);
            return await job.ToTask().WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        }

        private static SteamLibraryAssets? ExtractAssets(uint appId, KeyValue root)
        {
            if (root == KeyValue.Invalid)
            {
                Log($"appId={appId} root KV is Invalid");
                return null;
            }

            var common = root["common"];
            if (common == KeyValue.Invalid)
            {
                Log($"appId={appId} no 'common' node");
                return null;
            }

            var full = common["library_assets_full"];
            string? capsuleHash = null, heroHash = null, logoHash = null;
            string source = "<none>";

            if (full != KeyValue.Invalid)
            {
                source = "library_assets_full";
                capsuleHash = ReadHash(full["library_capsule"]);
                heroHash = ReadHash(full["library_hero"]);
                logoHash = ReadHash(full["library_logo"]);
                Log($"appId={appId} {source}: capsule={(capsuleHash != null ? "hit" : "miss")} hero={(heroHash != null ? "hit" : "miss")} logo={(logoHash != null ? "hit" : "miss")}");
            }
            else
            {
                Log($"appId={appId} 'library_assets_full' not present in common");
            }

            if (capsuleHash == null && heroHash == null && logoHash == null)
            {
                var legacy = common["library_assets"];
                if (legacy != KeyValue.Invalid)
                {
                    source = "library_assets (legacy)";
                    capsuleHash = legacy["library_capsule"].Value;
                    heroHash = legacy["library_hero"].Value;
                    logoHash = legacy["library_logo"].Value;
                    Log($"appId={appId} {source}: capsule={(capsuleHash != null ? "hit" : "miss")} hero={(heroHash != null ? "hit" : "miss")} logo={(logoHash != null ? "hit" : "miss")}");
                }
                else
                {
                    Log($"appId={appId} 'library_assets' (legacy) also not present");
                }
            }

            if (string.IsNullOrEmpty(capsuleHash) && string.IsNullOrEmpty(heroHash) && string.IsNullOrEmpty(logoHash))
            {
                return null;
            }

            return new SteamLibraryAssets(
                AppId: appId,
                CapsuleUrl: BuildUrl(appId, capsuleHash, "jpg"),
                CapsuleHash: capsuleHash,
                HeroUrl: BuildUrl(appId, heroHash, "jpg"),
                HeroHash: heroHash,
                LogoUrl: BuildUrl(appId, logoHash, "png"),
                LogoHash: logoHash);
        }

        private static string? ReadHash(KeyValue assetNode)
        {
            if (assetNode == KeyValue.Invalid) return null;

            // Try image2x first (higher resolution), then image. Each may be:
            //  - a node with localized children (english, schinese, ...)
            //  - a leaf with a direct .Value
            string? v = ReadLocalized(assetNode["image2x"])
                     ?? ReadLocalized(assetNode["image"]);
            if (!string.IsNullOrEmpty(v)) return v;

            // Some schemas store hashes directly: library_capsule = "<filename>"
            // or with sibling _2x keys at the same level.
            if (!string.IsNullOrEmpty(assetNode.Value)) return assetNode.Value;

            return null;
        }

        private static string? ReadLocalized(KeyValue node)
        {
            if (node == KeyValue.Invalid) return null;

            // Leaf with direct value.
            if (!string.IsNullOrEmpty(node.Value)) return node.Value;

            // Localized: prefer english, fall back to first non-empty child.
            var english = node["english"];
            if (english != KeyValue.Invalid && !string.IsNullOrEmpty(english.Value))
            {
                return english.Value;
            }

            return FirstChildValue(node);
        }

        private static string? FirstChildValue(KeyValue node)
        {
            foreach (var child in node.Children)
            {
                if (!string.IsNullOrEmpty(child.Value)) return child.Value;
            }
            return null;
        }

        private static string? BuildUrl(uint appId, string? value, string defaultExtension)
        {
            if (string.IsNullOrEmpty(value)) return null;

            // PICS values often already include an extension (e.g. "<hash>.jpg") or even
            // a relative path. Trust the value verbatim. Only append the default
            // extension when the value clearly lacks one.
            string path = value.Trim();
            string lower = path.ToLowerInvariant();
            bool hasImageExt =
                lower.EndsWith(".jpg") ||
                lower.EndsWith(".jpeg") ||
                lower.EndsWith(".png") ||
                lower.EndsWith(".webp") ||
                lower.EndsWith(".gif");

            if (!hasImageExt)
            {
                path = $"{path}.{defaultExtension}";
            }

            return $"{AssetCdnBase}/{appId}/{path}";
        }

        private static void DumpKv(KeyValue node, int depth = 0, int maxDepth = 3)
        {
            if (node == KeyValue.Invalid)
            {
                Log("  <Invalid>");
                return;
            }
            if (depth > maxDepth)
            {
                Log($"{new string(' ', depth * 2)}... (depth cutoff)");
                return;
            }

            string indent = new(' ', depth * 2);
            string val = string.IsNullOrEmpty(node.Value) ? "" : $" = \"{Trunc(node.Value!, 80)}\"";
            Log($"{indent}{node.Name ?? "<noname>"}{val}");

            foreach (var child in node.Children)
            {
                DumpKv(child, depth + 1, maxDepth);
            }

        }

        private static string Trunc(string s, int max) => s.Length <= max ? s : s[..max] + "...";

        private void RunCallbackLoop(CancellationToken ct)
        {
            Log("callback loop started");
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    _manager.RunWaitCallbacks(TimeSpan.FromMilliseconds(500));
                }
            }
            catch (Exception ex)
            {
                Log($"callback loop CRASHED: {ex.GetType().Name}: {ex.Message}");
            }
            Log("callback loop exited");
        }

        private void OnConnected(SteamClient.ConnectedCallback _)
        {
            Log("OnConnected callback; calling LogOnAnonymous()");
            try
            {
                _steamUser.LogOnAnonymous();
                _connectedTcs?.TrySetResult(true);
            }
            catch (Exception ex)
            {
                Log($"LogOnAnonymous threw: {ex.GetType().Name}: {ex.Message}");
                _connectedTcs?.TrySetResult(false);
            }
        }

        private void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            Log($"OnDisconnected (UserInitiated={callback.UserInitiated})");
            _isLoggedOn = false;
            _connectedTcs?.TrySetResult(false);
            _loggedOnTcs?.TrySetResult(false);
        }

        private void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            bool ok = callback.Result == EResult.OK;
            _isLoggedOn = ok;
            Log($"OnLoggedOn: Result={callback.Result} Extended={callback.ExtendedResult} ok={ok}");
            _loggedOnTcs?.TrySetResult(ok);
        }

        private void OnLoggedOff(SteamUser.LoggedOffCallback callback)
        {
            Log($"OnLoggedOff: Result={callback.Result}");
            _isLoggedOn = false;
        }

        private static void Log(string msg)
        {
            Debug.WriteLine($"{LogPrefix} {msg}");
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            Log("DisposeAsync");
            try
            {
                if (_isLoggedOn) _steamUser.LogOff();
                _client.Disconnect();
            }
            catch { }

            try { _runCts.Cancel(); } catch { }
            if (_runLoop != null)
            {
                try { await _runLoop.ConfigureAwait(false); } catch { }
            }

            _runCts.Dispose();
            _connectGate.Dispose();
        }
    }

    public sealed record SteamLibraryAssets(
        uint AppId,
        string? CapsuleUrl,
        string? CapsuleHash,
        string? HeroUrl,
        string? HeroHash,
        string? LogoUrl,
        string? LogoHash);
}
