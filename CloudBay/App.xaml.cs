using System;
using CloudBay.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace CloudBay;

public partial class App : Microsoft.UI.Xaml.Application
{
    private IServiceProvider? _services;

    public static MainWindow? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _services = ServiceBootstrap.CreateProvider();
        var facade = _services.GetRequiredService<ICloudBayFacade>();
        MainWindow = new MainWindow(facade);
        MainWindow.Activate();
    }
}
