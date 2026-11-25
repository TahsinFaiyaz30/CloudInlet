using CloudInlet.Core.Connectors;
using Windows.ApplicationModel;
using Windows.Services.Store;

namespace CloudInlet.Windows;

// The signed catalog binds a particular pack hash to a Store product and package family.
internal sealed class StoreConnectorDelivery
{
    private readonly Dictionary<string, StoreProduct> _products = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
    private StoreContext? _context;
    public bool Packaged { get; private init; }
    public bool UsesStore(string hash) => _products.ContainsKey(hash) || _files.ContainsKey(hash);
    public string StoreLabel(string hash) => _products.TryGetValue(hash, out var product)
        ? $"Microsoft Store · {product.Price.FormattedPrice}" : "Microsoft Store · already downloaded";

    public static async Task<StoreConnectorDelivery> ReviewAsync(ConnectorPlan plan, nint owner, CancellationToken token)
    {
        var delivery = new StoreConnectorDelivery { Packaged = UpdateInstallation.IsPackaged };
        if (!delivery.Packaged) return delivery;
        var pending = new List<ConnectorOffer>();
        foreach (var offer in plan.Ordered.Where(o => o.Store is not null && o.Source != ConnectorManager.Local))
        {
            if (InstalledFile(offer) is { } path) delivery._files.Add(offer.Hash, path);
            else pending.Add(offer);
        }
        if (pending.Count == 0) return delivery;
        var context = StoreContext.GetDefault();
        if (owner != 0) WinRT.Interop.InitializeWithWindow.Initialize(context, owner);
        delivery._context = context;
        var page = await context.GetAssociatedStoreProductsWithPagingAsync(["Durable"], 100).AsTask(token);
        while (true)
        {
            if (page.ExtendedError is { } error)
                throw new IOException("Microsoft Store could not check the available connectors. Try again when the Store is available.", error);
            foreach (var offer in pending)
                if (page.Products.TryGetValue(offer.Store!.ProductId, out var product) && product.HasDigitalDownload)
                    delivery._products[offer.Hash] = product;
            if (!page.HasMoreResults || delivery._products.Count == pending.Count) break;
            page = await page.GetNextAsync().AsTask(token);
        }
        return delivery;
    }

    public async Task<IReadOnlyDictionary<string, string>> AcquireAsync(ConnectorPlan plan, bool interactive, CancellationToken token)
    {
        foreach (var offer in plan.Ordered)
        {
            if (!_products.TryGetValue(offer.Hash, out var product)) continue;
            if (!interactive)
                throw new InvalidOperationException($"Open Connectors to get {offer.Manifest.Name} from Microsoft Store.");
            // Both operations can display Store UI and must run on the owning window's UI thread.
            if (!product.IsInUserCollection)
            {
                var purchase = await product.RequestPurchaseAsync().AsTask(token);
                if (purchase.Status == StorePurchaseStatus.NotPurchased)
                    throw new InvalidOperationException("Microsoft Store acquisition was canceled.");
                if (purchase.Status is not (StorePurchaseStatus.Succeeded or StorePurchaseStatus.AlreadyPurchased))
                    throw new IOException($"Microsoft Store could not acquire {offer.Manifest.Name} ({purchase.Status}).", purchase.ExtendedError);
            }
            var result = await _context!.RequestDownloadAndInstallStorePackagesAsync([product.StoreId]).AsTask(token);
            if (result.OverallState != StorePackageUpdateState.Completed)
                throw new IOException($"Microsoft Store installation ended with {result.OverallState}.");
            _files[offer.Hash] = InstalledFile(offer) ?? throw new IOException(
                $"Microsoft Store has not made the reviewed {offer.Manifest.Name} {offer.Manifest.Version} package available to this installation. Reopen CloudInlet to refresh its package list.");
        }
        return _files;
    }

    private static Package? InstalledPackage(string family)
    {
        var current = Package.Current;
        return current.Dependencies.FirstOrDefault(p => p.IsOptional &&
            p.Id.FamilyName.Equals(family, StringComparison.OrdinalIgnoreCase) &&
            p.Id.PublisherId == current.Id.PublisherId);
    }

    private static string? InstalledFile(ConnectorOffer offer)
    {
        var package = InstalledPackage(offer.Store!.PackageFamilyName);
        if (package is null) return null;
        var path = Path.Combine(package.InstalledLocation.Path, "Connectors", offer.Hash.ToLowerInvariant() + ".cipack");
        return File.Exists(path) ? path : null;
    }

    public static async Task<bool> RemoveAsync(string id, ConnectorRegistry registry, nint owner, CancellationToken token)
    {
        if (!UpdateInstallation.IsPackaged || !registry.Installed.TryGetValue(id, out var installed)) return true;
        var others = registry.Installed.Where(p => p.Key != id)
            .SelectMany(p => new[] { p.Value.Current.Store, p.Value.Previous?.Store }).ToArray();
        foreach (var binding in new[] { installed.Current.Store, installed.Previous?.Store }.OfType<ConnectorStorePackage>().DistinctBy(b => b.PackageFamilyName))
        {
            if (others.Any(b => b?.PackageFamilyName == binding.PackageFamilyName) || InstalledPackage(binding.PackageFamilyName) is not { } package) continue;
            var context = StoreContext.GetDefault();
            WinRT.Interop.InitializeWithWindow.Initialize(context, owner);
            var result = await context.RequestUninstallStorePackageAsync(package).AsTask(token);
            if (result.Status == StoreUninstallStorePackageStatus.CanceledByUser) return false;
            if (result.Status is not (StoreUninstallStorePackageStatus.Succeeded or StoreUninstallStorePackageStatus.UninstallNotApplicable))
                throw new IOException($"Microsoft Store could not remove the connector package ({result.Status}).", result.ExtendedError);
        }
        return true;
    }
}
