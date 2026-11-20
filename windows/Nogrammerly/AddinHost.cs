using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Nogrammerly.Core;

namespace Nogrammerly;

/// <summary>
/// Serves the Word add-in's own files to Word over HTTPS, on this PC only, so the add-in needs no separate
/// web server. What may be served is decided by <see cref="AddinSite"/>; this class adds the sockets and TLS.
/// </summary>
internal sealed class AddinHost : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private readonly List<TcpListener> listeners = [];
    private readonly X509Certificate2 certificate;
    private readonly int port;
    private readonly string root;
    private readonly Func<string> pairingKey;

    /// <param name="root">Folder holding the add-in's <c>src</c> and <c>assets</c> folders.</param>
    public AddinHost(X509Certificate2 certificate, int port, string root, Func<string> pairingKey)
    {
        this.certificate = certificate; this.port = port; this.root = root; this.pairingKey = pairingKey;
    }

    /// <summary>False if the port is taken. "localhost" can mean either loopback address, so both are served.</summary>
    public bool Start()
    {
        foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
        {
            var listener = new TcpListener(address, port);
            try { listener.Start(); }
            catch (SocketException) when (address.Equals(IPAddress.IPv6Loopback)) { continue; } // IPv6 may be turned off.
            catch (SocketException) { return false; }
            listeners.Add(listener);
            _ = Task.Run(() => AcceptLoop(listener));
        }
        return true;
    }

    private async Task AcceptLoop(TcpListener listener)
    {
        while (true)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(); }
            catch (Exception e) when (e is SocketException or ObjectDisposedException or InvalidOperationException) { return; }
            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using var timeout = new CancellationTokenSource(Patience);
        try
        {
            using (client)
            await using (var stream = new SslStream(client.GetStream()))
            {
                await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, timeout.Token);
                var head = await ReadHead(stream, timeout.Token);
                var reply = AddinSite.Respond(head is null ? null : AddinSite.ParseHead(head), port, root, pairingKey());
                await stream.WriteAsync(AddinSite.Serialize(reply), timeout.Token);
                await stream.ShutdownAsync();
            }
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or System.Security.Authentication.AuthenticationException or ObjectDisposedException) { }
        // A caller that hangs up, stalls or doesn't speak TLS: nothing to do.
    }

    /// <summary>Reads up to the blank line that ends the request's headers; null if it is too long or cut short.</summary>
    private static async Task<string?> ReadHead(Stream stream, CancellationToken token)
    {
        var buffer = new byte[AddinSite.MaxHeadBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), token);
            if (read == 0) return null;
            length += read;
            var end = buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8);
            if (end >= 0) return Encoding.ASCII.GetString(buffer, 0, end);
        }
        return null;
    }

    public void Dispose()
    {
        foreach (var listener in listeners) listener.Stop();
        listeners.Clear();
    }
}
