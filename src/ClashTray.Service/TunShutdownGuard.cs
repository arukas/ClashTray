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
    private static readonly TimeSpan TransitionTimeout = TimeSpan.FromSeconds(3);

    public static async Task<TunShutdownResult> EnsureDisabledAsync(
        MihomoApiClient api,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(api);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TransitionTimeout);

        try
        {
            bool? current = await ReadTunStateAsync(api, timeout.Token);
            if (current is not bool currentValue)
            {
                return Failed("停止核心前无法确认 TUN 状态，核心保持运行。");
            }

            if (currentValue)
            {
                await api.SetTunAsync(false, timeout.Token);
                if (!await ConfirmTunStateAsync(api, expected: false, timeout.Token))
                {
                    return Failed("停止核心前无法确认 TUN 已关闭，核心保持运行。");
                }
            }

            return new TunShutdownResult(true, TunState.Off, null);
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

    private static async Task<bool?> ReadTunStateAsync(
        MihomoApiClient api,
        CancellationToken cancellationToken)
    {
        using JsonDocument document = await api.GetConfigurationAsync(force: false, cancellationToken);
        return MihomoDataParser.ParseTunEnabled(document);
    }

    private static async Task<bool> ConfirmTunStateAsync(
        MihomoApiClient api,
        bool expected,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            using JsonDocument document = await api.GetConfigurationAsync(force: false, cancellationToken);
            bool? value = MihomoDataParser.ParseTunEnabled(document);
            if (value == expected)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
        }

        return false;
    }
}
