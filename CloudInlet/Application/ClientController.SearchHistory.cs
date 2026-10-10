namespace CloudInlet.Application;

public sealed partial class ClientController
{
    internal IReadOnlyList<string> LoadSearchHistory() => _storage.LoadSearchHistory();

    internal void SaveSearchHistory(IReadOnlyList<string> queries) => _storage.SaveSearchHistory(queries);
}
