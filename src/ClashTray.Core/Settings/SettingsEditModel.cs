using System.Globalization;
using ClashTray.Contracts;

namespace ClashTray.Core;

public enum SettingsNumberField { HttpPort, SocksPort, MixedPort, ControllerPort, SubscriptionRefreshHours }
public sealed record SettingsNumberInput(double Value, string Text, bool HasUncommittedText = false);
public sealed record SettingsNumberDraft(SettingsNumberInput Input, bool IsModified);
public sealed record SettingsEditValidationError(SettingsNumberField? Field, string? Detail);
public sealed record SettingsSaveCheckpoint(AppSettings Values, IReadOnlyDictionary<SettingsNumberField, SettingsNumberDraft> Numbers);

// Owns loaded baseline, draft, merge and validation. It has no UI, service or
// network dependencies. Draft values and raw NumberBox text remain distinct.
public sealed class SettingsEditModel
{
    private AppSettings? _baseline;
    private readonly Dictionary<SettingsNumberField, SettingsNumberDraft> _numbers = [];
    public bool IsLoaded => _baseline is not null;
    public AppSettings Values { get; private set; } = new();
    public IReadOnlyDictionary<SettingsNumberField, SettingsNumberDraft> Numbers => _numbers;
    public bool IsModified => !EditableDiff(_baseline ?? Values, Values).IsEmpty || _numbers.Values.Any(number => number.IsModified);

    public void Load(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _baseline = settings;
        Values = settings;
        foreach (SettingsNumberField field in Enum.GetValues<SettingsNumberField>()) { _numbers[field] = Number(settings, field); }
    }

    public void Capture(AppSettings values, IReadOnlyDictionary<SettingsNumberField, SettingsNumberInput> numbers)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(numbers);
        AppSettings baseline = _baseline ?? throw new InvalidOperationException("Settings must be loaded before editing.");
        Values = EditableDiff(baseline, values).Apply(baseline);
        foreach (SettingsNumberField field in Enum.GetValues<SettingsNumberField>())
        {
            SettingsNumberInput input = numbers[field];
            bool modified = input.HasUncommittedText || input.Value != ReadNumber(baseline, field);
            _numbers[field] = new SettingsNumberDraft(input, modified);
        }
    }

    public void MergeExternal(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_baseline is null) { Load(settings); return; }
        AppSettingsPatch edits = EditableDiff(_baseline, Values);
        Values = edits.Apply(settings);
        foreach (SettingsNumberField field in Enum.GetValues<SettingsNumberField>())
        {
            if (!_numbers[field].IsModified) { _numbers[field] = Number(settings, field); }
        }
        _baseline = settings;
    }

    public bool TryCreatePatch(AppSettings latest, CultureInfo culture, out AppSettingsPatch patch, out SettingsEditValidationError? error)
    {
        ArgumentNullException.ThrowIfNull(latest);
        ArgumentNullException.ThrowIfNull(culture);
        AppSettings proposed = Values;
        foreach (SettingsNumberField field in Enum.GetValues<SettingsNumberField>())
        {
            SettingsNumberInput input = _numbers[field].Input;
            double number = _numbers[field].IsModified ? input.Value : ReadNumber(latest, field);
            if (input.HasUncommittedText && (input.Text.Length > 64 || !double.TryParse(input.Text, NumberStyles.Number, culture, out number)))
            { return Invalid(field, out patch, out error); }
            int maximum = field == SettingsNumberField.SubscriptionRefreshHours ? 168 : 65535;
            if (!double.IsFinite(number) || number < 1 || number > maximum || number != Math.Truncate(number))
            { return Invalid(field, out patch, out error); }
            proposed = WriteNumber(proposed, field, (int)number);
        }
        AppSettingsPatch candidate = EditableDiff(_baseline ?? latest, proposed);
        // Explicitly edited numeric fields win same-field external conflicts,
        // even while NumberBox.Value still equals the originally loaded value.
        foreach ((SettingsNumberField field, SettingsNumberDraft draft) in _numbers)
        {
            if (draft.IsModified) { candidate = SetNumber(candidate, field, ReadNumber(proposed, field)); }
        }
        patch = candidate;
        try { SettingsValidator.Validate(candidate.Apply(latest)); }
        catch (ArgumentException exception)
        {
            error = new SettingsEditValidationError(null, ErrorSanitizer.Sanitize(exception));
            return false;
        }
        error = null;
        return true;
    }

    public SettingsSaveCheckpoint CaptureSave() => new(Values, new Dictionary<SettingsNumberField, SettingsNumberDraft>(_numbers));

    public void AcceptSaved(AppSettings settings, SettingsSaveCheckpoint submitted)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(submitted);
        AppSettingsPatch laterEdits = EditableDiff(submitted.Values, Values);
        Values = laterEdits.Apply(settings);
        foreach (SettingsNumberField field in Enum.GetValues<SettingsNumberField>())
        {
            if (_numbers[field] == submitted.Numbers[field]) { _numbers[field] = Number(settings, field); }
            else
            {
                SettingsNumberInput input = _numbers[field].Input;
                _numbers[field] = new(input, input.HasUncommittedText || input.Value != ReadNumber(settings, field));
            }
        }
        _baseline = settings;
    }

    private static bool Invalid(SettingsNumberField field, out AppSettingsPatch patch, out SettingsEditValidationError? error)
    {
        patch = new AppSettingsPatch();
        error = new SettingsEditValidationError(field, null);
        return false;
    }
    private static SettingsNumberDraft Number(AppSettings settings, SettingsNumberField field)
    {
        int number = ReadNumber(settings, field);
        return new(new SettingsNumberInput(number, number.ToString(CultureInfo.CurrentCulture)), false);
    }
    private static int ReadNumber(AppSettings settings, SettingsNumberField field) => field switch
    {
        SettingsNumberField.HttpPort => settings.HttpPort,
        SettingsNumberField.SocksPort => settings.SocksPort,
        SettingsNumberField.MixedPort => settings.MixedPort,
        SettingsNumberField.ControllerPort => settings.ControllerPort,
        SettingsNumberField.SubscriptionRefreshHours => settings.SubscriptionRefreshHours,
        _ => throw new ArgumentOutOfRangeException(nameof(field))
    };
    private static AppSettings WriteNumber(AppSettings settings, SettingsNumberField field, int value) => field switch
    {
        SettingsNumberField.HttpPort => settings with { HttpPort = value },
        SettingsNumberField.SocksPort => settings with { SocksPort = value },
        SettingsNumberField.MixedPort => settings with { MixedPort = value },
        SettingsNumberField.ControllerPort => settings with { ControllerPort = value },
        SettingsNumberField.SubscriptionRefreshHours => settings with { SubscriptionRefreshHours = value },
        _ => throw new ArgumentOutOfRangeException(nameof(field))
    };
    private static AppSettingsPatch SetNumber(AppSettingsPatch patch, SettingsNumberField field, int value) => field switch
    {
        SettingsNumberField.HttpPort => patch with { HttpPort = SettingPatchValue.Set(value) },
        SettingsNumberField.SocksPort => patch with { SocksPort = SettingPatchValue.Set(value) },
        SettingsNumberField.MixedPort => patch with { MixedPort = SettingPatchValue.Set(value) },
        SettingsNumberField.ControllerPort => patch with { ControllerPort = SettingPatchValue.Set(value) },
        SettingsNumberField.SubscriptionRefreshHours => patch with { SubscriptionRefreshHours = SettingPatchValue.Set(value) },
        _ => throw new ArgumentOutOfRangeException(nameof(field))
    };
    private static AppSettingsPatch EditableDiff(AppSettings baseline, AppSettings proposed) => AppSettingsPatch.Diff(baseline, proposed) with
    {
        ActiveConfigurationId = default, SystemProxyEnabled = default, TunEnabled = default, NakhimovUnlocked = default,
        HttpPort = default, SocksPort = default, MixedPort = default, ControllerPort = default, SubscriptionRefreshHours = default
    };
}
