using System.ComponentModel;
using System.Runtime.CompilerServices;
using ClashTray.Contracts;

namespace ClashTray.Presentation;

internal sealed record RuleProjection(int SourceIndex, RuleInfo Rule)
{
    public string SearchText => $"{Rule.Type} {Rule.Payload} {Rule.Proxy}";

    public string DisplayText => $"{Rule.Type}  {Rule.Payload}  → {Rule.Proxy}";
}

internal readonly record struct RuleRowIdentity(string ControllerIdentity, int SourceIndex);

public sealed class RuleRowViewModel : INotifyPropertyChanged
{
    private RuleProjection _projection;

    internal RuleRowViewModel(RuleProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        _projection = projection;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public RuleInfo Rule => _projection.Rule;

    public string DisplayText => _projection.DisplayText;

    internal bool Update(RuleProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        if (_projection == projection)
        {
            return false;
        }

        _projection = projection;
        OnPropertyChanged(nameof(Rule));
        OnPropertyChanged(nameof(DisplayText));
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
