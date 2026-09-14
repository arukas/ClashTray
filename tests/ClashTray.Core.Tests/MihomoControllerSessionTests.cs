using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class MihomoControllerSessionTests
{
    [TestMethod]
    public void LocalEndpointFactoryCreatesLoopbackOnlyDescriptor()
    {
        EndpointDescriptor endpoint = ControllerEndpointFactory.CreateLocal(9191);

        Assert.AreEqual(EndpointId.Local, endpoint.Id);
        Assert.AreEqual(EndpointKind.Local, endpoint.Kind);
        Assert.AreEqual("http://127.0.0.1:9191/", endpoint.BaseUri.AbsoluteUri);
        Assert.AreEqual(EndpointTransportSecurity.Loopback, endpoint.Security);
    }

    [TestMethod]
    public void AttachPublishesSessionAndReplacementInvalidatesPreviousGeneration()
    {
        using HttpClient firstClient = new HttpClient();
        using HttpClient secondClient = new HttpClient();
        MihomoApiClient firstApi = new MihomoApiClient(
            firstClient,
            new Uri("http://127.0.0.1:9191/"),
            string.Empty);
        MihomoApiClient secondApi = new MihomoApiClient(
            secondClient,
            new Uri("http://127.0.0.1:9292/"),
            string.Empty);
        MihomoControllerSessionRegistry registry = new MihomoControllerSessionRegistry();

        MihomoControllerSession first = registry.Attach(
            firstApi,
            ControllerEndpointFactory.CreateLocal(9191),
            EndpointCapabilityDefaults.Local);
        MihomoControllerSession second = registry.Attach(
            secondApi,
            ControllerEndpointFactory.CreateLocal(9292),
            EndpointCapabilityDefaults.Local);

        Assert.AreSame(second, registry.Current);
        Assert.AreEqual(second.Generation, registry.Generation);
        Assert.IsTrue(registry.IsCurrent(secondApi, second.Generation));
        Assert.IsFalse(registry.IsCurrent(firstApi, first.Generation));
        Assert.AreEqual(EndpointCapabilityDefaults.Local, second.Capabilities);
        Assert.AreNotEqual(first.Generation, second.Generation);
    }

    [TestMethod]
    public void DetachRemovesCurrentSessionAndInvalidatesCapturedGeneration()
    {
        using HttpClient client = new HttpClient();
        MihomoApiClient api = new MihomoApiClient(
            client,
            new Uri("http://127.0.0.1:9191/"),
            string.Empty);
        MihomoControllerSessionRegistry registry = new MihomoControllerSessionRegistry();
        MihomoControllerSession session = registry.Attach(
            api,
            ControllerEndpointFactory.CreateLocal(9191),
            EndpointCapabilityDefaults.Local);
        long generationBeforeDetach = registry.Generation;

        registry.Detach();

        Assert.IsNull(registry.Current);
        Assert.IsTrue(registry.Generation > generationBeforeDetach);
        Assert.IsFalse(registry.IsCurrent(api, session.Generation));
        Assert.ThrowsExactly<InvalidOperationException>(() => registry.Capture());
    }

    [TestMethod]
    public void LocalSessionRejectsNonLoopbackEndpoint()
    {
        using HttpClient client = new HttpClient();
        MihomoApiClient api = new MihomoApiClient(
            client,
            new Uri("https://controller.example/"),
            string.Empty);
        EndpointDescriptor endpoint = new EndpointDescriptor(
            EndpointId.Local,
            EndpointKind.Local,
            "Local",
            new Uri("https://controller.example/"),
            EndpointTransportSecurity.HttpsSystemTrust);
        MihomoControllerSessionRegistry registry = new MihomoControllerSessionRegistry();

        Assert.ThrowsExactly<ArgumentException>(() => registry.Attach(
            api,
            endpoint,
            EndpointCapabilityDefaults.Local));
        Assert.IsNull(registry.Current);
    }

    [TestMethod]
    public void DisabledEndpointCannotBecomeActiveSession()
    {
        using HttpClient client = new HttpClient();
        MihomoApiClient api = new MihomoApiClient(
            client,
            new Uri("https://controller.example/"),
            string.Empty);
        EndpointDescriptor endpoint = new EndpointDescriptor(
            new EndpointId("remote"),
            EndpointKind.Remote,
            "Remote",
            new Uri("https://controller.example/"),
            EndpointTransportSecurity.HttpsSystemTrust,
            IsEnabled: false);
        MihomoControllerSessionRegistry registry = new MihomoControllerSessionRegistry();

        Assert.ThrowsExactly<ArgumentException>(() => registry.Attach(
            api,
            endpoint,
            EndpointCapabilityDefaults.Remote));
        Assert.IsNull(registry.Current);
    }

    [TestMethod]
    public void RemoteCommandGateDeniesLocalCommandBeforeAnyControllerRequest()
    {
        using HttpClient client = new HttpClient();
        MihomoApiClient api = new MihomoApiClient(
            client,
            new Uri("https://controller.example/"),
            string.Empty);
        EndpointDescriptor endpoint = new EndpointDescriptor(
            new EndpointId("remote"),
            EndpointKind.Remote,
            "Remote",
            new Uri("https://controller.example/"),
            EndpointTransportSecurity.HttpsSystemTrust);
        MihomoControllerSessionRegistry registry = new MihomoControllerSessionRegistry();
        MihomoControllerSession session = registry.Attach(
            api,
            endpoint,
            EndpointCapabilityDefaults.Remote);

        EndpointCommandDeniedException exception = Assert.ThrowsExactly<EndpointCommandDeniedException>(() =>
            registry.EnsureCommandAllowed(
                api,
                session.Generation,
                EndpointCommand.ControlTun,
                "会话已切换，请重试。"));

        Assert.AreEqual(ErrorCode.EndpointCommandDenied, exception.ErrorCode);
        Assert.AreEqual(EndpointKind.Remote, exception.EndpointKind);
        Assert.AreEqual(EndpointCommand.ControlTun, exception.Command);
    }

    [TestMethod]
    public void TargetCommandGateChecksEndpointIdentityAndGeneration()
    {
        using HttpClient client = new HttpClient();
        MihomoApiClient api = new MihomoApiClient(
            client,
            new Uri("https://controller.example/"),
            string.Empty);
        EndpointDescriptor endpoint = new EndpointDescriptor(
            new EndpointId("remote"),
            EndpointKind.Remote,
            "Remote",
            new Uri("https://controller.example/"),
            EndpointTransportSecurity.HttpsSystemTrust);
        MihomoControllerSessionRegistry registry = new MihomoControllerSessionRegistry();
        MihomoControllerSession session = registry.Attach(
            api,
            endpoint,
            EndpointCapabilityDefaults.Remote);

        TargetCommand valid = new(endpoint.Id, session.Generation, EndpointCommand.ObserveStatus);
        registry.EnsureTargetCommandAllowed(api, valid, "会话已切换，请重试。");

        TargetCommand wrongEndpoint = new(new EndpointId("other"), session.Generation, EndpointCommand.ObserveStatus);
        Assert.ThrowsExactly<InvalidOperationException>(() => registry.EnsureTargetCommandAllowed(
            api,
            wrongEndpoint,
            "会话已切换，请重试。"));

        TargetCommand wrongGeneration = new(endpoint.Id, session.Generation + 1, EndpointCommand.ObserveStatus);
        Assert.ThrowsExactly<InvalidOperationException>(() => registry.EnsureTargetCommandAllowed(
            api,
            wrongGeneration,
            "会话已切换，请重试。"));

        TargetCommand localOnly = new(endpoint.Id, session.Generation, EndpointCommand.ControlTun);
        EndpointCommandDeniedException denied = Assert.ThrowsExactly<EndpointCommandDeniedException>(() =>
            registry.EnsureTargetCommandAllowed(api, localOnly, "会话已切换，请重试。"));
        Assert.AreEqual(ErrorCode.EndpointCommandDenied, denied.ErrorCode);
    }
}
