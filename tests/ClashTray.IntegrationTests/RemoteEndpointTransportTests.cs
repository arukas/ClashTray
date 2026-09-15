using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using ClashTray.Contracts;
using ClashTray.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace ClashTray.IntegrationTests;

[TestClass]
public sealed class RemoteEndpointTransportTests
{
    [TestMethod]
    public async Task CustomCaTransportUsesTheSameAuthorizationForRestAndWss()
    {
        await using LocalControllerFixture fixture = await LocalControllerFixture.StartAsync();
        EndpointDescriptor endpoint = EndpointUriNormalizer.CreateRemoteDescriptor(
            new EndpointId("loopback"),
            "Loopback controller",
            fixture.BaseUri) with
        {
            Security = EndpointTransportSecurity.HttpsCustomCertificate
        };

        using EndpointTransport transport = EndpointTransportFactory.Create(
            endpoint,
            new EndpointTransportOptions("loopback-secret", fixture.CertificateAuthority));
        MihomoApiClient api = new(
            transport.HttpClient,
            transport.BaseUri,
            string.Empty,
            webSocketFactory: transport.CreateWebSocket,
            webSocketUriBuilder: transport.BuildWebSocketUri);

        using JsonDocument version = await api.GetVersionAsync();
        Assert.AreEqual("v1.19.30-test", version.RootElement.GetProperty("version").GetString());

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        using ClientWebSocket socket = await api.ConnectWebSocketAsync("/logs", timeout.Token);
        byte[] buffer = new byte[1024];
        WebSocketReceiveResult received = await socket.ReceiveAsync(buffer, timeout.Token);
        string message = Encoding.UTF8.GetString(buffer, 0, received.Count);

        Assert.AreEqual(WebSocketMessageType.Text, received.MessageType);
        StringAssert.Contains(message, "loopback-log", StringComparison.Ordinal);
        Assert.AreEqual("Bearer loopback-secret", fixture.RestAuthorization);
        Assert.AreEqual("Bearer loopback-secret", fixture.WebSocketAuthorization);
    }

    [TestMethod]
    public async Task WrongCustomCaIsRejectedByTheLiveRestConnection()
    {
        await using LocalControllerFixture fixture = await LocalControllerFixture.StartAsync();
        using X509Certificate2 wrongCa = LocalControllerFixture.CreateCertificateAuthority("Wrong ClashTray Test CA");
        EndpointDescriptor endpoint = EndpointUriNormalizer.CreateRemoteDescriptor(
            new EndpointId("loopback"),
            "Loopback controller",
            fixture.BaseUri) with
        {
            Security = EndpointTransportSecurity.HttpsCustomCertificate
        };

        using EndpointTransport transport = EndpointTransportFactory.Create(
            endpoint,
            new EndpointTransportOptions("loopback-secret", wrongCa));
        MihomoApiClient api = new(
            transport.HttpClient,
            transport.BaseUri,
            string.Empty,
            webSocketFactory: transport.CreateWebSocket,
            webSocketUriBuilder: transport.BuildWebSocketUri);

        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => api.GetVersionAsync());
    }

    private sealed class LocalControllerFixture : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly X509Certificate2 _serverCertificate;

        private LocalControllerFixture(
            WebApplication app,
            X509Certificate2 certificateAuthority,
            X509Certificate2 serverCertificate)
        {
            _app = app;
            CertificateAuthority = certificateAuthority;
            _serverCertificate = serverCertificate;
            BaseUri = new Uri(app.Urls.Single());
        }

        public Uri BaseUri { get; }

        public X509Certificate2 CertificateAuthority { get; }

        public string? RestAuthorization { get; private set; }

        public string? WebSocketAuthorization { get; private set; }

        public static async Task<LocalControllerFixture> StartAsync()
        {
            X509Certificate2 certificateAuthority = CreateCertificateAuthority("ClashTray Integration CA");
            X509Certificate2 serverCertificate = CreateServerCertificate(certificateAuthority);
            WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(LocalControllerFixture).Assembly.GetName().Name,
                EnvironmentName = "Testing"
            });
            builder.WebHost.ConfigureKestrel(options =>
                options.Listen(IPAddress.Loopback, 0, listenOptions => listenOptions.UseHttps(serverCertificate)));

            WebApplication app = builder.Build();
            LocalControllerFixture? fixture = null;
            app.MapGet(
                "/version",
                (HttpContext context) =>
                {
                    fixture!.RestAuthorization = context.Request.Headers.Authorization.ToString();
                    return Results.Json(new { version = "v1.19.30-test" });
                });
            app.UseWebSockets();
            app.Map(
                "/logs",
                async context =>
                {
                    fixture!.WebSocketAuthorization = context.Request.Headers.Authorization.ToString();
                    if (!context.WebSockets.IsWebSocketRequest)
                    {
                        context.Response.StatusCode = StatusCodes.Status400BadRequest;
                        return;
                    }

                    using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
                    byte[] payload = Encoding.UTF8.GetBytes("{\"message\":\"loopback-log\"}\n");
                    await socket.SendAsync(
                        payload,
                        WebSocketMessageType.Text,
                        endOfMessage: true,
                        context.RequestAborted);
                });

            await app.StartAsync();
            fixture = new LocalControllerFixture(app, certificateAuthority, serverCertificate);
            return fixture;
        }

        public static X509Certificate2 CreateCertificateAuthority(string subject)
        {
            using RSA key = RSA.Create(2048);
            CertificateRequest request = new(
                $"CN={subject}",
                key,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 1, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
                critical: true));
            return request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-5),
                DateTimeOffset.UtcNow.AddHours(1));
        }

        private static X509Certificate2 CreateServerCertificate(X509Certificate2 certificateAuthority)
        {
            using RSA key = RSA.Create(2048);
            CertificateRequest request = new(
                "CN=localhost",
                key,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            SubjectAlternativeNameBuilder names = new();
            names.AddDnsName("localhost");
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                critical: true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                [new Oid("1.3.6.1.5.5.7.3.1")],
                critical: true));
            using X509Certificate2 certificate = request.Create(
                certificateAuthority,
                DateTimeOffset.UtcNow.AddMinutes(-5),
                DateTimeOffset.UtcNow.AddHours(1),
                RandomNumberGenerator.GetBytes(16));
            using X509Certificate2 certificateWithKey = certificate.CopyWithPrivateKey(key);
            return X509CertificateLoader.LoadPkcs12(
                certificateWithKey.Export(X509ContentType.Pfx),
                string.Empty,
                X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet);
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            _serverCertificate.Dispose();
            CertificateAuthority.Dispose();
        }
    }
}
