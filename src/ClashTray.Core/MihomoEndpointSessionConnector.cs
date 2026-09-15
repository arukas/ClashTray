using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;
using ClashTray.Contracts;

namespace ClashTray.Core;

/// <summary>
/// Creates a remote Mihomo session from locally protected endpoint metadata.
/// The connector performs only the read-only /version handshake before returning a session.
/// </summary>
public sealed class MihomoEndpointSessionConnector : IEndpointSessionConnector
{
    private readonly Func<EndpointId, CancellationToken, Task<EndpointRecord?>> _recordResolver;
    private readonly EndpointTransportOptionsResolver _optionsResolver;
    private readonly Func<EndpointDescriptor, EndpointTransportOptions, EndpointTransport> _transportFactory;

    public MihomoEndpointSessionConnector(
        Func<EndpointId, CancellationToken, Task<EndpointRecord?>> recordResolver,
        EndpointTransportOptionsResolver optionsResolver,
        Func<EndpointDescriptor, EndpointTransportOptions, EndpointTransport>? transportFactory = null)
    {
        _recordResolver = recordResolver ?? throw new ArgumentNullException(nameof(recordResolver));
        _optionsResolver = optionsResolver ?? throw new ArgumentNullException(nameof(optionsResolver));
        _transportFactory = transportFactory ?? EndpointTransportFactory.Create;
    }

    public async Task<EndpointSession> ConnectAsync(
        EndpointDescriptor endpoint,
        long generation,
        long selectionRevision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        EndpointDescriptorValidator.ValidateForActiveSession(endpoint);
        if (endpoint.Kind != EndpointKind.Remote)
        {
            throw new ArgumentException(
                "The Mihomo endpoint connector accepts remote endpoints only.",
                nameof(endpoint));
        }

        EndpointRecord? record;
        try
        {
            record = await _recordResolver(endpoint.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or IOException)
        {
            throw CreateSetupFailure(endpoint, "远程端点元数据无法读取。", exception);
        }

        if (record is null)
        {
            throw new EndpointSessionConnectException(
                EndpointSessionState.Failed,
                isTransient: false,
                "远程端点已不存在，请重新加载端点列表。");
        }

        if (record.Descriptor != endpoint)
        {
            throw new EndpointSessionConnectException(
                EndpointSessionState.Failed,
                isTransient: false,
                "远程端点元数据已变化，请重新加载端点列表。");
        }

        EndpointTransportOptionsLease optionsLease;
        try
        {
            optionsLease = await _optionsResolver.ResolveAsync(record, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (EndpointSessionConnectException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or IOException or CryptographicException)
        {
            throw CreateSetupFailure(endpoint, "远程端点的凭据或证书不可用。", exception);
        }

        using (optionsLease)
        {
            EndpointTransport? transport = null;
            try
            {
                transport = _transportFactory(endpoint, optionsLease.Options);
                MihomoApiClient api = new(
                    transport.HttpClient,
                    transport.BaseUri,
                    string.Empty,
                    restTimeout: transport.RestTimeout,
                    writeTimeout: transport.WriteTimeout,
                    webSocketHandshakeTimeout: transport.WebSocketHandshakeTimeout,
                    webSocketFactory: transport.CreateWebSocket,
                    webSocketUriBuilder: transport.BuildWebSocketUri);
                using JsonDocument version = await api.GetVersionAsync(cancellationToken).ConfigureAwait(false);
                EndpointHandshakeResult handshake = EndpointHandshakeValidator.Validate(version);
                if (!handshake.IsCompatible)
                {
                    throw new EndpointSessionConnectException(
                        handshake.State,
                        isTransient: false,
                        handshake.ErrorMessage ?? "远程 Controller 不兼容。");
                }

                EndpointSession session = new(
                    transport,
                    handshake.Capabilities,
                    generation,
                    selectionRevision,
                    handshake);
                transport = null;
                return session;
            }
            catch (EndpointSessionConnectException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (JsonException exception)
            {
                throw new EndpointSessionConnectException(
                    EndpointSessionState.Incompatible,
                    isTransient: false,
                    "远程 Controller 的 /version 响应不是有效 JSON。",
                    exception);
            }
            catch (HttpRequestException exception) when (
                exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new EndpointSessionConnectException(
                    EndpointSessionState.AuthenticationFailed,
                    isTransient: false,
                    "远程 Controller 认证失败，请检查 secret。",
                    exception);
            }
            catch (HttpRequestException exception) when (
                exception.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            {
                throw new EndpointSessionConnectException(
                    EndpointSessionState.Incompatible,
                    isTransient: false,
                    "远程 Controller 不支持所需的 /version 接口。",
                    exception);
            }
            catch (Exception exception) when (exception is AuthenticationException or CryptographicException)
            {
                throw new EndpointSessionConnectException(
                    EndpointSessionState.CertificateFailed,
                    isTransient: false,
                    "远程端点证书验证失败。",
                    exception);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                throw CreateSetupFailure(endpoint, "远程端点传输配置无效。", exception);
            }
            finally
            {
                transport?.Dispose();
            }
        }
    }

    private static EndpointSessionConnectException CreateSetupFailure(
        EndpointDescriptor endpoint,
        string message,
        Exception exception) =>
        new(
            endpoint.Security == EndpointTransportSecurity.HttpsCustomCertificate
                ? EndpointSessionState.CertificateFailed
                : EndpointSessionState.Failed,
            isTransient: false,
            message,
            exception);
}
