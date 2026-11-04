using ClashTray.Core;
using ClashTray.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashTray.App;

public sealed partial class EndpointSettingsSection : UserControl
{
    private ClashTrayRuntime _runtime = null!;
    internal event Action<string?>? StatusChanged;
    private string? Status { set => StatusChanged?.Invoke(value); }
    private SettingsEndpointEditor _endpointEditor = null!;
    private string _endpointSignature = string.Empty;
    private bool _updatingEndpointControls;
    public EndpointSettingsSection() => InitializeComponent();

    internal void Initialize(ClashTrayRuntime runtime, SettingsPageCommands commands)
    {
        _runtime = runtime;
        _endpointEditor = new(runtime, commands.Endpoints);
    }

    private async void SaveEndpointButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(EndpointNameBox.Text)
            || string.IsNullOrWhiteSpace(EndpointUriBox.Text))
        {
            Status = LocalizationService.Get("EndpointFieldsRequired");
            return;
        }

        EndpointTransportChoice transportChoice = EndpointDraftMapper.ParseTransportChoice(
            (EndpointTransportBox.SelectedItem as ComboBoxItem)?.Tag?.ToString());
        bool explicitHttp = transportChoice == EndpointTransportChoice.HttpExplicitlyConfirmed;
        if (explicitHttp && EndpointHttpRiskCheckBox.IsChecked != true)
        {
            Status = LocalizationService.Get("EndpointHttpRiskRequired");
            return;
        }

        bool customHttps = transportChoice == EndpointTransportChoice.HttpsCustomCertificate;
        if (customHttps
            && string.IsNullOrWhiteSpace(EndpointCustomCaBox.Text)
            && _endpointEditor.EditingId is null)
        {
            Status = LocalizationService.Get("EndpointCustomCaRequired");
            return;
        }

        if (!customHttps && !string.IsNullOrWhiteSpace(EndpointCustomCaBox.Text))
        {
            Status = LocalizationService.Get("EndpointCustomCaOnly");
            return;
        }

        if (!Uri.TryCreate(EndpointUriBox.Text.Trim(), UriKind.Absolute, out Uri? endpointUri)
            || endpointUri is null)
        {
            Status = LocalizationService.Get("EndpointUriInvalid");
            return;
        }

        try
        {
            EndpointId endpointId = _endpointEditor.EditingId
                ?? new EndpointId($"remote-{Guid.NewGuid():N}");
            EndpointDraftSubmission submission = EndpointDraftMapper.CreateSubmission(
                endpointId,
                EndpointNameBox.Text.Trim(),
                endpointUri,
                transportChoice,
                httpRiskConfirmed: EndpointHttpRiskCheckBox.IsChecked == true,
                secret: EndpointSecretBox.Password,
                customCaPem: EndpointCustomCaBox.Text,
                isEditing: _endpointEditor.EditingId is not null,
                nowUtc: DateTimeOffset.UtcNow);
            PageCommandResult result = await _endpointEditor.SaveAsync(submission);
            if (result.CanPresent)
            {
                if (result.Succeeded) { UpdateEndpointList(_runtime.Endpoints); ClearEndpointEditor(); }
                Status = result.Succeeded ? LocalizationService.Get("EndpointSaved") : result.Error;
            }        }
        catch (Exception exception)
        {
            Status = ErrorSanitizer.Sanitize(exception);
        }
    }

    private async void RemoveEndpointButton_Click(object sender, RoutedEventArgs e)
    {
        if (EndpointListView.SelectedItem is not EndpointSettingsRow { Endpoint: EndpointDescriptor endpoint }
            || endpoint.Kind != EndpointKind.Remote)
        {
            Status = LocalizationService.Get("EndpointSelectRemote");
            return;
        }

        EndpointRemovalFeedback feedback = await _endpointEditor.RemoveAsync(endpoint.Id);
        PageCommandResult result = feedback.Command;
        if (result.CanPresent)
        {
            if (result.Succeeded) { UpdateEndpointList(_runtime.Endpoints); ClearEndpointEditor(); }
            Status = !result.Succeeded ? result.Error
                : LocalizationService.Get(feedback.Removal!.Removed ? "EndpointRemoved" : "EndpointNotFound");
        }
    }
    private void EndpointListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingEndpointControls)
        {
            return;
        }

        if (EndpointListView.SelectedItem is EndpointSettingsRow { Endpoint: EndpointDescriptor endpoint }
            && endpoint.Kind == EndpointKind.Remote)
        {
            _endpointEditor.EditingId = endpoint.Id;
            EndpointNameBox.Text = endpoint.DisplayName;
            EndpointUriBox.Text = endpoint.BaseUri.AbsoluteUri.TrimEnd('/');
            EndpointTransportBox.SelectedItem = EndpointTransportBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Tag?.ToString(),
                    endpoint.Security switch
                    {
                        EndpointTransportSecurity.HttpExplicitlyConfirmed => "http-explicit",
                        EndpointTransportSecurity.HttpsCustomCertificate => "https-custom",
                        _ => "https-system"
                    },
                    StringComparison.Ordinal));
            EndpointSecretBox.Password = string.Empty;
            EndpointCustomCaBox.Text = string.Empty;
            EndpointHttpRiskCheckBox.IsChecked = endpoint.Security == EndpointTransportSecurity.HttpExplicitlyConfirmed;
        }
        else
        {
            _endpointEditor.EditingId = null;
        }

        UpdateEndpointRemoveButton();
    }

    private async void SelectEndpointButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: EndpointDescriptor endpoint })
        {
            return;
        }

        PageCommandResult result = await _endpointEditor.SelectAsync(endpoint.Id);
        if (result.CanPresent)
        {
            if (result.Succeeded) { UpdateEndpointList(_runtime.Endpoints); }
            Status = result.Succeeded ? LocalizationService.Format("EndpointSelected", endpoint.DisplayName) : result.Error;
        }
    }
    private async void TestEndpointButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button
            {
                Tag: EndpointDescriptor endpoint,
            }
            || endpoint.Kind != EndpointKind.Remote
            || _endpointEditor.TestingId is not null)
        {
            return;
        }

        Task<EndpointTestFeedback> pending = _endpointEditor.TestAsync(endpoint.Id);
        UpdateEndpointList(_runtime.Endpoints);
        Status = LocalizationService.Get("EndpointTesting");
        EndpointTestFeedback feedback = await pending;
        PageCommandResult result = feedback.Command;
        if (result.CanPresent)
        {
            UpdateEndpointList(_runtime.Endpoints);
            Status = result.Succeeded
                ? LocalizationService.Format("EndpointTestSucceeded", endpoint.DisplayName,
                    feedback.Handshake!.Version ?? LocalizationService.Get("EndpointVersionUnknown"))
                : result.Error;
        }
    }
    private void EndpointTransportBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EndpointHttpRiskCheckBox is null)
        {
            return;
        }

        EndpointTransportChoice transportChoice = EndpointDraftMapper.ParseTransportChoice(
            (EndpointTransportBox.SelectedItem as ComboBoxItem)?.Tag?.ToString());
        bool explicitHttp = transportChoice == EndpointTransportChoice.HttpExplicitlyConfirmed;
        bool customHttps = transportChoice == EndpointTransportChoice.HttpsCustomCertificate;
        EndpointHttpRiskCheckBox.IsEnabled = explicitHttp;
        EndpointCustomCaBox.IsEnabled = customHttps;
        if (!explicitHttp)
        {
            EndpointHttpRiskCheckBox.IsChecked = false;
        }

        if (!customHttps)
        {
            EndpointCustomCaBox.Text = string.Empty;
        }
    }

    internal void UpdateEndpointList(IReadOnlyList<EndpointDescriptor> endpoints)
    {
        if (EndpointListView is null)
        {
            return;
        }

        EndpointId activeEndpointId = _runtime.AppSnapshot.ActiveController.Endpoint.Id;
        string signature = activeEndpointId.Value
            + '\u001D'
            + (_endpointEditor.TestingId?.Value ?? string.Empty)
            + '\u001D'
            + string.Join(
                '\u001F',
                endpoints.Select(endpoint => $"{endpoint.Id.Value}\u001E{endpoint.DisplayName}\u001E{endpoint.BaseUri.AbsoluteUri}\u001E{endpoint.Security}\u001E{endpoint.IsEnabled}"));
        if (string.Equals(_endpointSignature, signature, StringComparison.Ordinal))
        {
            return;
        }

        EndpointId? selectedId = EndpointListView.SelectedItem is EndpointSettingsRow { Endpoint: EndpointDescriptor selected }
            ? selected.Id
            : null;
        _updatingEndpointControls = true;
        try
        {
            EndpointListView.Items.Clear();
            foreach (EndpointDescriptor endpoint in endpoints)
            {
                EndpointListView.Items.Add(new EndpointSettingsRow(endpoint, activeEndpointId, _endpointEditor.TestingId));
            }

            if (selectedId is EndpointId previousId)
            {
                EndpointListView.SelectedItem = EndpointListView.Items
                    .OfType<EndpointSettingsRow>()
                    .FirstOrDefault(item => item.Endpoint.Id == previousId);
            }
        }
        finally
        {
            _updatingEndpointControls = false;
        }

        _endpointSignature = signature;
        UpdateEndpointRemoveButton();
    }

    private void ClearEndpointEditor()
    {
        _endpointEditor.EditingId = null;
        _updatingEndpointControls = true;
        try
        {
            EndpointListView.SelectedIndex = -1;
            EndpointNameBox.Text = string.Empty;
            EndpointUriBox.Text = string.Empty;
            EndpointTransportBox.SelectedIndex = 0;
            EndpointSecretBox.Password = string.Empty;
            EndpointCustomCaBox.Text = string.Empty;
            EndpointHttpRiskCheckBox.IsChecked = false;
        }
        finally
        {
            _updatingEndpointControls = false;
        }

        RemoveEndpointButton.IsEnabled = false;
    }

    private void UpdateEndpointRemoveButton()
    {
        RemoveEndpointButton.IsEnabled = EndpointListView.SelectedItem is EndpointSettingsRow
        {
            Endpoint: EndpointDescriptor { Kind: EndpointKind.Remote }
        };
    }
}