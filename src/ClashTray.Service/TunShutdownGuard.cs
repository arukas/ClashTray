using System.Text.Json;
using ClashTray.Contracts;
using ClashTray.Core;

namespace ClashTray.Service;

internal readonly record struct TunShutdownResult(
    bool Succeeded,
    TunState State,
    string? Error);

/// <summary>
/// Confirms that Mihomo no longer owns the TUN path before its process is stopped.
/// A missing or unconfirmed state is treated as unsafe to stop so a restart cannot
/// tear down the process while leaving the machine in an unknown routing state.
/// </summary>
internal static class TunShutdownGuard
{
    private static readonly TimeSpan TransitionTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(350);
    private const int StableSamples = 2;

    public static async Task<TunShutdownResult> EnsureDisabledAsync(
        MihomoApiClient api,
        CancellationToken cancellationToken)
        => await EnsureDisabledAsync(
            api,
            new WindowsTunNetworkHealthProbe(),
            cancellationToken).ConfigureAwait(false);

    public static async Task<TunShutdownResult> EnsureDisabledAsync(
        MihomoApiClient api,
        ITunNetworkHealthProbe healthProbe,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(healthProbe);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TransitionTimeout);

        try
        {
            using JsonDocument initialConfiguration = await api.GetConfigurationAsync(force: false, timeout.Token);
            bool? current = MihomoDataParser.ParseTunEnabled(initialConfiguration);
            if (current is not bool currentValue)
            {
                return Failed("停止核心前无法确认 TUN 状态，核心保持运行。");
            }

            if (currentValue)
            {
                await api.SetTunAsync(false, timeout.Token);
            }

            return await ConfirmDisabledAsync(api, healthProbe, timeout.Token).ConfigureAwait(false)
                ? new TunShutdownResult(true, TunState.Off, null)
                : Failed("停止核心前无法确认 TUN 已关闭，核心保持运行。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Failed("停止核心前确认 TUN 超时，核心保持运行。");
        }
        catch (HttpRequestException)
        {
            return Failed("停止核心前无法确认 TUN 已关闭，核心保持运行。");
        }
        catch (InvalidDataException)
        {
            return Failed("停止核心前无法确认 TUN 已关闭，核心保持运行。");
        }
        catch (JsonException)
        {
            return Failed("停止核心前无法确认 TUN 已关闭，核心保持运行。");
        }
        catch (IOException)
        {
            return Failed("停止核心前无法确认 TUN 已关闭，核心保持运行。");
        }
        catch (InvalidOperationException)
        {
            return Failed("停止核心前无法确认 TUN 已关闭，核心保持运行。");
        }
    }

    private static TunShutdownResult Failed(string error) =>
        new TunShutdownResult(false, TunState.Unknown, error);

    private static async Task<bool> ConfirmDisabledAsync(
        MihomoApiClient api,
        ITunNetworkHealthProbe healthProbe,
        CancellationToken cancellationToken)
    {
        int stableSamples = 0;
        while (true)
        {
            using JsonDocument document = await api.GetConfigurationAsync(force: false, cancellationToken);
            bool? value = MihomoDataParser.ParseTunEnabled(document);
            MihomoTunConfiguration configuration = MihomoDataParser.ParseTunConfiguration(document);
            TunNetworkHealth health = await healthProbe.ProbeAsync(
                configuration,
                TunNetworkExpectation.Disabled,
                cancellationToken).ConfigureAwait(false);
            if (value is false && health.MeetsDisabled)
            {
                stableSamples++;
                if (stableSamples >= StableSamples)
                {
                    return true;
                }
            }
            else
            {
                stableSamples = 0;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }
}
