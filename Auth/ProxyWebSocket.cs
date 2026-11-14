using System;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Jellyfin.Profiles.Auth
{
    /// <summary>
    /// Accepts the live connection of an app on the picker, so <see cref="ProxySwitcher"/>
    /// can close it on a switch and rewrite what the server sends down it.
    ///
    /// <para><b>Why our own handshake.</b> ASP.NET's <c>WebSocketMiddleware</c> installs its
    /// <see cref="IHttpWebSocketFeature"/> only when none is set, and it runs after us (we are at
    /// the front of the pipeline, see <c>ProfilesStartupFilter</c>). The only way to hold the
    /// socket Jellyfin will use is to be the feature that makes it. The handshake is RFC 6455
    /// section 4.2.2 only, with no compression, which Jellyfin does not ask for.</para>
    /// </summary>
    internal sealed class ProxyWebSocketFeature : IHttpWebSocketFeature
    {
        private const string Rfc6455Guid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        private readonly HttpContext _context;
        private readonly IHttpUpgradeFeature _upgrade;
        private readonly Func<WebSocket, WebSocket> _wrap;

        internal ProxyWebSocketFeature(HttpContext context, IHttpUpgradeFeature upgrade, Func<WebSocket, WebSocket> wrap)
        {
            _context = context;
            _upgrade = upgrade;
            _wrap = wrap;
        }

        /// <inheritdoc />
        public bool IsWebSocketRequest
        {
            get
            {
                var request = _context.Request;
                if (!_upgrade.IsUpgradableRequest || !HttpMethods.IsGet(request.Method)) return false;

                return request.Headers["Upgrade"].ToString().Contains("websocket", StringComparison.OrdinalIgnoreCase)
                    && request.Headers["Sec-WebSocket-Version"].ToString().Trim() == "13"
                    && IsValidKey(request.Headers["Sec-WebSocket-Key"].ToString());
            }
        }

        /// <summary>RFC 6455 section 4.1: the key is the base64 of exactly 16 bytes.</summary>
        private static bool IsValidKey(string key)
        {
            var bytes = new byte[24];
            return Convert.TryFromBase64String(key.Trim(), bytes, out var written) && written == 16;
        }

        /// <inheritdoc />
        public async Task<WebSocket> AcceptAsync(WebSocketAcceptContext acceptContext)
        {
            if (!IsWebSocketRequest) throw new InvalidOperationException("Not a WebSocket request.");

            var key = _context.Request.Headers["Sec-WebSocket-Key"].ToString().Trim();
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + Rfc6455Guid)));

            var headers = _context.Response.Headers;
            headers["Connection"] = "Upgrade";
            headers["Upgrade"] = "websocket";
            headers["Sec-WebSocket-Accept"] = accept;
            if (!string.IsNullOrEmpty(acceptContext?.SubProtocol))
            {
                headers["Sec-WebSocket-Protocol"] = acceptContext.SubProtocol;
            }

            var stream = await _upgrade.UpgradeAsync().ConfigureAwait(false);

            var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
            {
                IsServer = true,
                SubProtocol = acceptContext?.SubProtocol,
                KeepAliveInterval = acceptContext?.KeepAliveInterval ?? TimeSpan.FromMinutes(2),
            });

            return _wrap(socket);
        }
    }

    /// <summary>
    /// A server socket that passes everything through, except that each text message it sends
    /// goes through <see cref="_rewrite"/> first. Shows the app its main account's id where the
    /// server wrote the profile's (<see cref="ProxySwitcher"/>).
    /// </summary>
    internal sealed class RewritingWebSocket : WebSocket
    {
        private readonly WebSocket _inner;
        private readonly Func<string, string> _rewrite;
        private readonly Action<RewritingWebSocket> _onDispose;
        private readonly SemaphoreSlim _send = new(1, 1);
        private MemoryStream? _pending;

        internal RewritingWebSocket(WebSocket inner, Func<string, string> rewrite, Action<RewritingWebSocket> onDispose)
        {
            _inner = inner;
            _rewrite = rewrite;
            _onDispose = onDispose;
        }

        public override WebSocketCloseStatus? CloseStatus => _inner.CloseStatus;

        public override string? CloseStatusDescription => _inner.CloseStatusDescription;

        public override WebSocketState State => _inner.State;

        public override string? SubProtocol => _inner.SubProtocol;

        public override void Abort() => _inner.Abort();

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => _inner.CloseAsync(closeStatus, statusDescription, cancellationToken);

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => _inner.CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

        public override void Dispose()
        {
            _onDispose(this);
            _inner.Dispose();
            _send.Dispose();
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
            => _inner.ReceiveAsync(buffer, cancellationToken);

        public override ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
            => _inner.ReceiveAsync(buffer, cancellationToken);

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            => SendCoreAsync(buffer.AsMemory(), messageType, endOfMessage, cancellationToken).AsTask();

        public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            => SendCoreAsync(buffer, messageType, endOfMessage, cancellationToken);

        public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, WebSocketMessageFlags messageFlags, CancellationToken cancellationToken)
            => SendCoreAsync(buffer, messageType, (messageFlags & WebSocketMessageFlags.EndOfMessage) != 0, cancellationToken);

        private async ValueTask SendCoreAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            // Every send takes the lock, binary included: the framework allows one send in
            // flight and throws on a second, and a binary frame sent during a text rewrite was
            // that second one (r2-05-04).
            await _send.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (messageType != WebSocketMessageType.Text)
                {
                    await _inner.SendAsync(buffer, messageType, endOfMessage, cancellationToken).ConfigureAwait(false);
                    return;
                }

                // A message sent in pieces is rewritten whole: an id can straddle two pieces.
                if (!endOfMessage)
                {
                    _pending ??= new MemoryStream();
                    _pending.Write(buffer.Span);
                    return;
                }

                string text;
                if (_pending != null)
                {
                    _pending.Write(buffer.Span);
                    text = Encoding.UTF8.GetString(_pending.GetBuffer(), 0, (int)_pending.Length);
                    _pending = null;
                }
                else
                {
                    text = Encoding.UTF8.GetString(buffer.Span);
                }

                var bytes = Encoding.UTF8.GetBytes(_rewrite(text));
                await _inner.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _send.Release();
            }
        }
    }
}
