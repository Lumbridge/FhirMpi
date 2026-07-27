using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using OpenMpi.Domain;

namespace OpenMpi.Hl7v2.Host;

public sealed class MllpListenerWorker(
    IOptions<MllpHostOptions> options,
    OpenMpi.Hl7v2.MllpConnectionProcessor processor,
    ILogger<MllpListenerWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, string, string, int, Exception?> LogListening =
        LoggerMessage.Define<string, string, int>(
            LogLevel.Information,
            new EventId(2001, nameof(LogListening)),
            "MLLP listener {Listener} is bound to {Address}:{Port}");
    private static readonly Action<ILogger, string, string, Exception?> LogConnectionFailed =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(2002, nameof(LogConnectionFailed)),
            "MLLP connection on {Listener} failed with {ExceptionType}");
    private readonly List<TcpListener> _listeners = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var configured = options.Value;
        Validate(configured);
        var tasks = configured.Listeners
            .Select(listener => RunListenerAsync(listener, configured, stoppingToken))
            .ToArray();
        await Task.WhenAll(tasks);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var listener in _listeners)
        {
            listener.Stop();
        }

        return base.StopAsync(cancellationToken);
    }

    private async Task RunListenerAsync(
        MllpListenerOptions listenerOptions,
        MllpHostOptions hostOptions,
        CancellationToken stoppingToken)
    {
        var listener = new TcpListener(
            IPAddress.Parse(listenerOptions.Address),
            listenerOptions.Port);
        _listeners.Add(listener);
        listener.Start();
        using var connectionGate = new SemaphoreSlim(
            hostOptions.MaximumConcurrentConnectionsPerListener);
        LogListening(
            logger,
            listenerOptions.Name,
            listenerOptions.Address,
            listenerOptions.Port,
            null);
        while (!stoppingToken.IsCancellationRequested)
        {
            await connectionGate.WaitAsync(stoppingToken);
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                connectionGate.Release();
                break;
            }
            catch (SocketException) when (stoppingToken.IsCancellationRequested)
            {
                connectionGate.Release();
                break;
            }
            catch
            {
                connectionGate.Release();
                throw;
            }

            _ = HandleConnectionAndReleaseAsync(
                client,
                listenerOptions,
                hostOptions,
                connectionGate,
                stoppingToken);
        }
    }

    private async Task HandleConnectionAndReleaseAsync(
        TcpClient client,
        MllpListenerOptions listener,
        MllpHostOptions host,
        SemaphoreSlim connectionGate,
        CancellationToken stoppingToken)
    {
        try
        {
            await HandleConnectionAsync(client, listener, host, stoppingToken);
        }
        finally
        {
            connectionGate.Release();
        }
    }

    private async Task HandleConnectionAsync(
        TcpClient client,
        MllpListenerOptions listener,
        MllpHostOptions host,
        CancellationToken stoppingToken)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(host.IdleTimeoutSeconds));
            try
            {
                await using var stream = await CreateStreamAsync(
                    client.GetStream(),
                    listener,
                    timeout.Token);
                await processor.ProcessAsync(
                    stream,
                    new OpenMpi.Hl7v2.Hl7ListenerBinding(
                        new TenantId(listener.TenantId),
                        new SourceSystemId(listener.SourceSystem),
                        listener.ActorId),
                    timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                // Connection timeout or service shutdown.
            }
            catch (Exception exception)
            {
                LogConnectionFailed(
                    logger,
                    listener.Name,
                    exception.GetType().Name,
                    null);
            }
        }
    }

    private static async ValueTask<Stream> CreateStreamAsync(
        NetworkStream networkStream,
        MllpListenerOptions options,
        CancellationToken cancellationToken)
    {
        if (options.CertificatePath is null)
        {
            return networkStream;
        }

        var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            options.CertificatePath,
            options.CertificatePassword);
        var allowedThumbprints = options.AllowedClientCertificateThumbprints
            .Select(NormaliseThumbprint)
            .ToHashSet(StringComparer.Ordinal);
        var ssl = new SslStream(
            networkStream,
            leaveInnerStreamOpen: false,
            (_, clientCertificate, _, errors) =>
                errors == SslPolicyErrors.None &&
                clientCertificate is not null &&
                allowedThumbprints.Contains(NormaliseThumbprint(clientCertificate.GetCertHashString())));
        await ssl.AuthenticateAsServerAsync(
            new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                ClientCertificateRequired = true,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.Online
            },
            cancellationToken);
        return ssl;
    }

    private static void Validate(MllpHostOptions options)
    {
        if (options.Listeners.Count == 0)
        {
            throw new InvalidOperationException("At least one MLLP listener must be configured.");
        }

        if (options.MaximumMessageBytes is < 1024 or > 16 * 1024 * 1024)
        {
            throw new InvalidOperationException(
                "Mllp:MaximumMessageBytes must be between 1 KiB and 16 MiB.");
        }

        if (options.MaximumConcurrentConnectionsPerListener is < 1 or > 10000)
        {
            throw new InvalidOperationException(
                "Mllp:MaximumConcurrentConnectionsPerListener must be between 1 and 10000.");
        }

        foreach (var listener in options.Listeners)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(listener.Name);
            ArgumentException.ThrowIfNullOrWhiteSpace(listener.TenantId);
            ArgumentException.ThrowIfNullOrWhiteSpace(listener.SourceSystem);
            if (listener.Port is < 1 or > 65535)
            {
                throw new InvalidOperationException(
                    $"MLLP listener '{listener.Name}' has an invalid port.");
            }

            if (listener.CertificatePath is null && !listener.AllowPlaintext)
            {
                throw new InvalidOperationException(
                    $"MLLP listener '{listener.Name}' must configure TLS or explicitly opt into plaintext.");
            }

            if (listener.CertificatePath is not null &&
                listener.AllowedClientCertificateThumbprints.Count == 0)
            {
                throw new InvalidOperationException(
                    $"MLLP listener '{listener.Name}' requires at least one allowed client certificate.");
            }
        }
    }

    private static string NormaliseThumbprint(string value) =>
        value.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
}
