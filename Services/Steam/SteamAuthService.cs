using QRCoder;
using SteamKit2;
using SteamKit2.Authentication;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Codec.Services.Scanning;

namespace Codec.Services.Steam;

public sealed record SteamOwnedApp(uint AppId, string Name, string AppType);

public sealed record SteamAccountSnapshot(string AccountName, IReadOnlyList<SteamOwnedApp> Apps);

public sealed class SteamAuthService
{
    private readonly ScanResourceLimiter? _resourceLimiter;
    private readonly int _backgroundWorkers;

    public SteamAuthService(ScanResourceLimiter? resourceLimiter = null, ScanConcurrencyOptions? concurrency = null)
    {
        _resourceLimiter = resourceLimiter;
        _backgroundWorkers = concurrency?.BackgroundWorkers ?? 4;
    }

    private static readonly ELicenseFlags InvalidLicenseFlags =
        ELicenseFlags.Expired |
        ELicenseFlags.CancelledByUser |
        ELicenseFlags.CancelledByAdmin |
        ELicenseFlags.CancelledByFriendlyFraudLock |
        ELicenseFlags.CancelledByPartner |
        ELicenseFlags.NotActivated |
        ELicenseFlags.PendingRefund |
        ELicenseFlags.Borrowed;

    private readonly string _tokenPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Storage.LibraryStorageService.AppDataFolderName,
        "steam-token.dat");

    public event Action<byte[]>? QrCodeChanged;

    public bool HasStoredToken => File.Exists(_tokenPath);

    public async Task<SteamAccountSnapshot> SignInAndFetchAsync(
        string? storedAccountName,
        bool useQr,
        CancellationToken cancellationToken = default)
    {
        string? refreshToken = useQr ? null : await LoadTokenAsync(cancellationToken).ConfigureAwait(false);
        if (!useQr && (string.IsNullOrWhiteSpace(storedAccountName) || string.IsNullOrWhiteSpace(refreshToken)))
            throw new InvalidOperationException("Steam needs to be connected again.");

        var client = new SteamClient();
        var callbacks = new CallbackManager(client);
        var user = client.GetHandler<SteamUser>()!;
        var apps = client.GetHandler<SteamApps>()!;
        var connected = NewSignal<bool>();
        var loggedOn = NewSignal<SteamID>();
        var licenses = NewSignal<SteamApps.LicenseListCallback>();
        string accountName = storedAccountName ?? string.Empty;

        callbacks.Subscribe<SteamClient.ConnectedCallback>(_ => connected.TrySetResult(true));
        callbacks.Subscribe<SteamClient.DisconnectedCallback>(_ =>
        {
            connected.TrySetException(new IOException("Steam disconnected."));
            loggedOn.TrySetException(new IOException("Steam disconnected during login."));
            licenses.TrySetException(new IOException("Steam disconnected during library sync."));
        });
        callbacks.Subscribe<SteamUser.LoggedOnCallback>(callback =>
        {
            if (callback.Result == EResult.OK && callback.ClientSteamID != null)
                loggedOn.TrySetResult(callback.ClientSteamID);
            else
                loggedOn.TrySetException(new InvalidOperationException($"Steam login failed: {callback.Result}."));
        });
        callbacks.Subscribe<SteamApps.LicenseListCallback>(callback => licenses.TrySetResult(callback));

        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task callbackLoop = Task.Run(() => RunCallbacks(callbacks, loopCts.Token), CancellationToken.None);

        try
        {
            client.Connect();
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);

            if (useQr)
            {
                var authSession = await client.Authentication.BeginAuthSessionViaQRAsync(new AuthSessionDetails
                {
                    DeviceFriendlyName = "Codec",
                    IsPersistentSession = true
                }).ConfigureAwait(false);

                void PublishQr() => QrCodeChanged?.Invoke(CreateQrPng(authSession.ChallengeURL));
                authSession.ChallengeURLChanged = PublishQr;
                PublishQr();

                var result = await authSession.PollingWaitForResultAsync(cancellationToken).ConfigureAwait(false);
                accountName = result.AccountName;
                refreshToken = result.RefreshToken;
                if (string.IsNullOrWhiteSpace(accountName) || string.IsNullOrWhiteSpace(refreshToken))
                    throw new InvalidOperationException("Steam did not return reusable sign-in credentials.");
                await SaveTokenAsync(refreshToken, cancellationToken).ConfigureAwait(false);
            }

            user.LogOn(new SteamUser.LogOnDetails
            {
                Username = accountName,
                AccessToken = refreshToken,
                ShouldRememberPassword = true
            });

            SteamID steamId = await loggedOn.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
            SteamApps.LicenseListCallback licenseList = await licenses.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            if (licenseList.Result != EResult.OK)
                throw new InvalidOperationException($"Steam license lookup failed: {licenseList.Result}.");

            var ownLicenses = licenseList.LicenseList
                .Where(license => license.PackageID != 0)
                .Where(license => license.LicenseType != ELicenseType.NoLicense)
                .Where(license => license.OwnerAccountID == steamId.AccountID)
                .Where(license => (license.LicenseFlags & InvalidLicenseFlags) == 0)
                .GroupBy(license => license.PackageID)
                .Select(group => group.First())
                .ToList();

            IReadOnlyList<SteamOwnedApp> ownedApps = await ResolveOwnedAppsAsync(apps, ownLicenses, cancellationToken).ConfigureAwait(false);
            return new SteamAccountSnapshot(accountName, ownedApps);
        }
        finally
        {
            try { user.LogOff(); } catch { }
            try { client.Disconnect(); } catch { }
            loopCts.Cancel();
            try { await callbackLoop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    public Task DeleteTokenAsync()
    {
        try
        {
            if (File.Exists(_tokenPath))
                File.Delete(_tokenPath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SteamAuth] Failed to delete token: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    private async Task SaveTokenAsync(string token, CancellationToken cancellationToken)
    {
        byte[] protectedToken = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(token),
            optionalEntropy: null,
            DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(_tokenPath)!);
        await File.WriteAllBytesAsync(_tokenPath, protectedToken, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> LoadTokenAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_tokenPath)) return null;
            byte[] protectedToken = await File.ReadAllBytesAsync(_tokenPath, cancellationToken).ConfigureAwait(false);
            byte[] token = ProtectedData.Unprotect(protectedToken, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(token);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[SteamAuth] Failed to read token: {ex.Message}");
            return null;
        }
    }

    private async Task<IReadOnlyList<SteamOwnedApp>> ResolveOwnedAppsAsync(
        SteamApps steamApps,
        IReadOnlyList<SteamApps.LicenseListCallback.License> licenses,
        CancellationToken cancellationToken)
    {
        using var batchGate = new SemaphoreSlim(_backgroundWorkers, _backgroundWorkers);

        async Task<HashSet<uint>> ResolvePackageBatchAsync(SteamApps.LicenseListCallback.License[] batch)
        {
            await batchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var packageRequests = batch.Select(license => new SteamApps.PICSRequest(license.PackageID, license.AccessToken));
                var result = await RunPicsAsync(ct => steamApps
                    .PICSGetProductInfo(apps: Array.Empty<SteamApps.PICSRequest>(), packages: packageRequests)
                    .ToTask().WaitAsync(TimeSpan.FromSeconds(30), ct), cancellationToken).ConfigureAwait(false);

                var ids = new HashSet<uint>();
                foreach (var callback in result.Results ?? Enumerable.Empty<SteamApps.PICSProductInfoCallback>())
                foreach (var package in callback.Packages.Values)
                {
                    var idsNode = package.KeyValues["appids"];
                    if (idsNode == KeyValue.Invalid) continue;
                    foreach (var child in idsNode.Children)
                    {
                        if (uint.TryParse(child.Value, out uint id) || uint.TryParse(child.Name, out id))
                            ids.Add(id);
                    }
                }

                return ids;
            }
            finally
            {
                batchGate.Release();
            }
        }

        HashSet<uint>[] packageAppIds = await Task.WhenAll(
            licenses.Chunk(100).Select(batch => ResolvePackageBatchAsync(batch.ToArray()))).ConfigureAwait(false);
        uint[] appIds = packageAppIds.SelectMany(ids => ids).Distinct().ToArray();

        async Task<List<SteamOwnedApp>> ResolveAppBatchAsync(uint[] ids)
        {
            await batchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var tokens = await RunPicsAsync(ct => steamApps.PICSGetAccessTokens(ids, Array.Empty<uint>())
                    .ToTask().WaitAsync(TimeSpan.FromSeconds(20), ct), cancellationToken).ConfigureAwait(false);
                var requests = ids.Select(id => new SteamApps.PICSRequest(
                    id,
                    tokens.AppTokens.TryGetValue(id, out ulong token) ? token : 0));
                var result = await RunPicsAsync(ct => steamApps
                    .PICSGetProductInfo(apps: requests, packages: Array.Empty<SteamApps.PICSRequest>())
                    .ToTask().WaitAsync(TimeSpan.FromSeconds(30), ct), cancellationToken).ConfigureAwait(false);

                var apps = new List<SteamOwnedApp>(ids.Length);
                foreach (var callback in result.Results ?? Enumerable.Empty<SteamApps.PICSProductInfoCallback>())
                foreach (var app in callback.Apps)
                {
                    var common = app.Value.KeyValues["common"];
                    string name = common["name"].Value ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    string type = common["type"].Value ?? "app";
                    if (!IsGameAppType(type)) continue;
                    apps.Add(new SteamOwnedApp(app.Key, name, type));
                }

                return apps;
            }
            finally
            {
                batchGate.Release();
            }
        }

        List<SteamOwnedApp>[] resolvedBatches = await Task.WhenAll(
            appIds.Chunk(100).Select(batch => ResolveAppBatchAsync(batch.ToArray()))).ConfigureAwait(false);
        var owned = resolvedBatches.SelectMany(batch => batch).DistinctBy(app => app.AppId).ToList();
        return owned.OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private Task<T> RunPicsAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
        => _resourceLimiter == null
            ? work(cancellationToken)
            : _resourceLimiter.RunNetworkAsync(work, cancellationToken);

    internal static bool IsGameAppType(string? appType) =>
        string.Equals(appType, "game", StringComparison.OrdinalIgnoreCase);

    private static byte[] CreateQrPng(string challengeUrl)
    {
        using var generator = new QRCodeGenerator();
        using QRCodeData data = generator.CreateQrCode(challengeUrl, QRCodeGenerator.ECCLevel.L);
        using var qr = new PngByteQRCode(data);
        return qr.GetGraphic(10, new byte[] { 28, 26, 23 }, new byte[] { 243, 237, 201 }, drawQuietZones: true);
    }

    private static void RunCallbacks(CallbackManager callbacks, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
            callbacks.RunWaitCallbacks(TimeSpan.FromMilliseconds(250));
    }

    private static TaskCompletionSource<T> NewSignal<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
