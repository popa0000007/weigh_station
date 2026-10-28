using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WeighStation.Core;
using WeighStation.Core.Abstractions;
using WeighStation.Presentation.Services;

namespace WeighStation.Presentation.Pages;

/// <summary>Weighing rules (stability, thresholds, auto-capture), operator name, backup of the database.</summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly SettingsService _settings;
    private readonly IDatabaseBackup _backup;
    private readonly IDialogService _dialogs;
    private readonly TimeProvider _time;

    [ObservableProperty] private string _operatorName = "";
    [ObservableProperty] private string _minWeightKg = "";
    [ObservableProperty] private string _toleranceKg = "";
    [ObservableProperty] private string _windowSeconds = "";
    [ObservableProperty] private bool _requireTerminalStable;
    [ObservableProperty] private bool _requirePlate;
    [ObservableProperty] private string _minConfidencePercent = "";
    [ObservableProperty] private bool _autoCapture;
    [ObservableProperty] private string _autoCaptureSeconds = "";
    [ObservableProperty] private string _scaleTimeoutSeconds = "";
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private string _infoMessage = "";

    public SettingsViewModel(SettingsService settings, IDatabaseBackup backup, IDialogService dialogs, TimeProvider time)
    {
        _settings = settings;
        _backup = backup;
        _dialogs = dialogs;
        _time = time;
        Load(settings.Current);
    }

    public override string Title => "Настройки";

    public override string IconData => "M12 8 A4 4 0 1 0 12 16 A4 4 0 1 0 12 8 Z M12 2 V5 M12 19 V22 M2 12 H5 M19 12 H22";

    public string DatabasePath => _backup.DatabasePath;

    public override Task ActivateAsync()
    {
        Load(_settings.Current);
        ErrorMessage = InfoMessage = "";
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void Save()
    {
        ErrorMessage = InfoMessage = "";
        var error = Validate(out var options);
        if (error is not null)
        {
            ErrorMessage = error;
            return;
        }

        try
        {
            _settings.Update(_settings.Current with { OperatorName = OperatorName.Trim(), Weighing = options! });
            InfoMessage = "Настройки сохранены и применены.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Не удалось сохранить настройки: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ResetDefaults()
    {
        Load(_settings.Current with { Weighing = new WeighingOptions() });
        InfoMessage = "Значения по умолчанию подставлены. Нажмите «Сохранить», чтобы применить.";
    }

    [RelayCommand]
    private async Task BackupAsync()
    {
        ErrorMessage = InfoMessage = "";
        var path = _dialogs.PickSaveFile(
            "Резервная копия базы данных", $"weighings-{_time.GetLocalNow():yyyy-MM-dd-HHmm}.db", "База данных (*.db)|*.db");
        if (path is null)
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path); // the user already confirmed overwriting in the save dialog
            }

            await _backup.BackupAsync(path);
            InfoMessage = $"Копия сохранена: {path}";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Не удалось создать копию: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenDataFolder() => _dialogs.OpenFolder(Path.GetDirectoryName(Path.GetFullPath(_backup.DatabasePath))!);

    private void Load(AppSettings s)
    {
        var w = s.Weighing;
        OperatorName = s.OperatorName;
        MinWeightKg = w.MinWeightKg.ToString(CultureInfo.InvariantCulture);
        ToleranceKg = w.StabilityToleranceKg.ToString(CultureInfo.InvariantCulture);
        WindowSeconds = Seconds(w.StabilityWindow);
        RequireTerminalStable = w.RequireTerminalStable;
        RequirePlate = w.RequirePlate;
        MinConfidencePercent = Math.Round(w.MinPlateConfidence * 100).ToString(CultureInfo.InvariantCulture);
        AutoCapture = w.AutoCapture;
        AutoCaptureSeconds = Seconds(w.AutoCaptureDelay);
        ScaleTimeoutSeconds = Seconds(w.ScaleTimeout);
    }

    private string? Validate(out WeighingOptions? options)
    {
        options = null;
        if (!TryInt(MinWeightKg, 0, 100_000, out var min))
        {
            return "Минимальный вес — целое число от 0 до 100 000 кг.";
        }

        if (!TryInt(ToleranceKg, 1, 1_000, out var tolerance))
        {
            return "Допуск стабильности — целое число от 1 до 1 000 кг.";
        }

        if (!TrySeconds(WindowSeconds, 0.5, 60, out var window))
        {
            return "Время стабильности — от 0,5 до 60 секунд.";
        }

        if (!TryInt(MinConfidencePercent, 1, 100, out var confidence))
        {
            return "Уверенность распознавания — от 1 до 100 %.";
        }

        if (!TrySeconds(AutoCaptureSeconds, 1, 300, out var autoDelay))
        {
            return "Задержка автофиксации — от 1 до 300 секунд.";
        }

        if (!TrySeconds(ScaleTimeoutSeconds, 1, 60, out var timeout))
        {
            return "Тайм-аут весов — от 1 до 60 секунд.";
        }

        options = new WeighingOptions
        {
            MinWeightKg = min,
            StabilityToleranceKg = tolerance,
            StabilityWindow = window,
            RequireTerminalStable = RequireTerminalStable,
            RequirePlate = RequirePlate,
            MinPlateConfidence = confidence / 100.0,
            AutoCapture = AutoCapture,
            AutoCaptureDelay = autoDelay,
            ScaleTimeout = timeout,
        };
        return null;
    }

    private static bool TryInt(string text, int min, int max, out int value) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= min && value <= max;

    private static bool TrySeconds(string text, double min, double max, out TimeSpan value)
    {
        value = default;
        if (!double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            || seconds < min || seconds > max)
        {
            return false;
        }

        value = TimeSpan.FromSeconds(seconds);
        return true;
    }

    private static string Seconds(TimeSpan t) => t.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture);
}
