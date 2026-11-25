using CloudInlet.Core.Connectors;
using CloudInlet.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CloudInlet.Views;

internal sealed class ConnectorInstallDialog : ContentDialog
{
    private readonly ConnectorPlan _plan;
    private readonly Dictionary<string, ComboBox> _choices = new();
    public ConnectorPlan SelectedPlan => new(_plan.Ordered.Select(o => _choices.TryGetValue(o.Manifest.Id, out var choice)
        ? o with { Locations = new[] { (string)choice.SelectedItem }.Concat(o.Locations).Distinct().ToArray() } : o).ToArray(), _plan.Notices);

    public ConnectorInstallDialog(ConnectorPlan plan, StoreConnectorDelivery delivery)
    {
        _plan = plan;
        Title = "Install required connectors";
        PrimaryButtonText = "Install"; CloseButtonText = "Cancel"; DefaultButton = ContentDialogButton.Close;
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(SourceImportDialog.Text("Executable connectors run with your Windows account's access to files, credentials and the network."));
        foreach (var offer in plan.Ordered)
        {
            var m = offer.Manifest;
            panel.Children.Add(SourceImportDialog.Heading($"{m.Name} {m.Version}"));
            panel.Children.Add(SourceImportDialog.Text($"{m.Publisher}\nSHA-256 publisher fingerprint {m.Fingerprint}\n{offer.Source}", true));
            if (m.Kind == "provider") panel.Children.Add(SourceImportDialog.Text($"Features · {m.Capabilities}", true));
            foreach (var dependency in m.Dependencies)
                panel.Children.Add(SourceImportDialog.Text($"{(dependency.Optional ? "Optional" : "Requires")} {dependency.Id} [{dependency.Versions.Min}, {dependency.Versions.Max}) · {dependency.PublisherKey}", true));
            if (delivery.UsesStore(offer.Hash))
                panel.Children.Add(SourceImportDialog.Text(delivery.StoreLabel(offer.Hash)));
            else
            {
                if (delivery.Packaged && offer.Source != ConnectorManager.Local)
                    panel.Children.Add(SourceImportDialog.Text("Microsoft Store has no download for this version. Install will use the signed catalog source below."));
                if (offer.Locations.Length > 1)
                {
                    var list = new ComboBox { Header = "Download source", ItemsSource = offer.Locations, SelectedIndex = 0,
                        HorizontalAlignment = HorizontalAlignment.Stretch };
                    panel.Children.Add(list); _choices.Add(m.Id, list);
                }
                else if (offer.Locations.Length == 1) panel.Children.Add(SourceImportDialog.Text(offer.Locations[0], true));
            }
            if (m.ReleaseNotes.Length > 0) panel.Children.Add(SourceImportDialog.Text(m.ReleaseNotes));
        }
        foreach (var notice in plan.Notices) panel.Children.Add(SourceImportDialog.Text(notice));
        Content = new ScrollViewer { Content = panel, MaxHeight = 410 };
    }
}
