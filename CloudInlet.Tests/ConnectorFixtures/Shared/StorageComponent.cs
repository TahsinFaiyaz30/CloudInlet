using CloudInlet.Connectors;

namespace Fixture.Storage;

public sealed class StorageComponent : IConnectorComponent
{
    public string RootName() => "Root from shared component";
}
