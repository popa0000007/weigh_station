using WeighStation.Core.Abstractions;

namespace WeighStation.Core;

/// <summary>
/// The one place that holds the current settings. Saving writes the file and pushes the weighing options
/// into the running session, so changes apply immediately and everyone reads the same values.
/// </summary>
public sealed class SettingsService
{
    private readonly ISettingsStore _store;
    private readonly WeighingSession _session;
    private readonly string _defaultOperator;

    public SettingsService(ISettingsStore store, WeighingSession session, string defaultOperator)
    {
        _store = store;
        _session = session;
        _defaultOperator = defaultOperator;
        Current = store.Load();
        session.UpdateOptions(Current.Weighing);
    }

    public AppSettings Current { get; private set; }

    /// <summary>The name written into the history of each weighing.</summary>
    public string OperatorName => string.IsNullOrWhiteSpace(Current.OperatorName) ? _defaultOperator : Current.OperatorName.Trim();

    public event EventHandler? Changed;

    public void Update(AppSettings settings)
    {
        _store.Save(settings);
        Current = settings;
        _session.UpdateOptions(settings.Weighing);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
