using CloudBay.Core.Filters;
using CloudBay.Core.Folders;
using CloudBay.Core.Links;
using CloudBay.Core.Safety;
using Microsoft.Extensions.DependencyInjection;

namespace CloudBay.Application;

public static class ServiceBootstrap
{
    public static IServiceProvider CreateProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOperationJournal, OperationJournal>();
        services.AddSingleton<PreferencesStore>();
        services.AddSingleton<CloudRootProbe>();
        services.AddSingleton<IKnownFolderService, KnownFolderService>();
        services.AddSingleton<IOneDriveRecoveryService, OneDriveRecoveryService>();
        services.AddSingleton<FolderLinkService>();
        services.AddSingleton<DeveloperFilterService>();
        services.AddSingleton<ICloudBayFacade, CloudBayFacade>();
        return services.BuildServiceProvider(validateScopes: true);
    }
}
