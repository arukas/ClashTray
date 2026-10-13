using System.Globalization;
using ClashTray.Contracts;
using ClashTray.Core;

namespace ClashTray.App;

internal sealed record SettingsDraftSubmission(
    AppSettingsPatch Patch,
    SettingsSaveCheckpoint Checkpoint,
    bool LanguageChanged);

// Captures the submitted draft independently of edits made while the save awaits.
// The page still reads NumberBox text and presents values on the UI thread.
internal sealed class SettingsDraftController
{
    public SettingsEditModel Edit { get; } = new();

    public bool TryPrepareSave(
        AppSettings current,
        CultureInfo culture,
        out SettingsDraftSubmission? submission,
        out SettingsEditValidationError? error)
    {
        if (!Edit.TryCreatePatch(current, culture, out AppSettingsPatch patch, out error))
        {
            submission = null;
            return false;
        }

        submission = new(patch, Edit.CaptureSave(),
            patch.Language.IsSpecified
            && !string.Equals(patch.Language.Value, current.Language, StringComparison.OrdinalIgnoreCase));
        return true;
    }

    public void AcceptSaved(AppSettings current, SettingsDraftSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);
        Edit.AcceptSaved(current, submission.Checkpoint);
    }
}
