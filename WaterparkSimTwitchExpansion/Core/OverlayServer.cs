using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using BepInEx.Logging;

namespace WaterparkSimTwitchExpansion.Core
{
    /// <summary>
    /// Serves a small local web page for OBS's Browser Source (or any other overlay tool) showing
    /// who caused each chaos redemption, instead of relying on the in-game OnGUI text
    /// (OnScreenNotifier) which only appears on the streamer's own screen and isn't guaranteed to
    /// composite the way a capture method expects. Plain System.Net.HttpListener - no ASP.NET/
    /// Kestrel needed, and no new NuGet dependency (it ships in the net6.0 shared framework).
    ///
    /// Point an OBS Browser Source at http://localhost:&lt;port&gt;/overlay.html (port from
    /// config's Overlay.Port). Binding specifically to "localhost" (rather than "+"/"*"/a real
    /// hostname) means this doesn't need admin/elevation or a "netsh http add urlacl" reservation
    /// - Windows special-cases that prefix.
    /// </summary>
    public sealed class OverlayServer : IDisposable
    {
        private readonly ManualLogSource _log;
        private readonly HttpListener _listener;
        private readonly List<HttpListenerResponse> _sseClients = new List<HttpListenerResponse>();
        private readonly object _sseLock = new object();
        private volatile bool _running;
        private Thread _listenerThread;

        /// <summary>Whether the listener is currently accepting connections - lets Core.ModMenu
        /// (the in-game F9 settings panel) show/toggle overlay state without tracking it separately.</summary>
        public bool IsRunning => _running;

        public OverlayServer(ManualLogSource log, int port)
        {
            _log = log;
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{port}/");
        }

        public void Start()
        {
            if (_running)
            {
                return;
            }

            try
            {
                _listener.Start();
            }
            catch (Exception e)
            {
                _log.LogError($"OverlayServer: failed to start - is another program already using this port? Change Overlay.Port in the config if so. ({e.Message})");
                return;
            }

            _running = true;
            _listenerThread = new Thread(Listen) { IsBackground = true, Name = "WaterparkTwitchOverlay" };
            _listenerThread.Start();
            _log.LogInfo($"OverlayServer: listening at {_listener.Prefixes.First()}overlay.html - point an OBS Browser Source at it.");
        }

        private void Listen()
        {
            while (_running)
            {
                HttpListenerContext context;
                try
                {
                    context = _listener.GetContext();
                }
                catch (HttpListenerException)
                {
                    break; // Stop()/Close() called.
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                ThreadPool.QueueUserWorkItem(_ => HandleRequest(context));
            }
        }

        private void HandleRequest(HttpListenerContext context)
        {
            try
            {
                if (context.Request.Url.AbsolutePath == "/events")
                {
                    HandleEventsStream(context);
                }
                else
                {
                    ServeOverlayPage(context);
                }
            }
            catch (Exception e)
            {
                _log.LogWarning($"OverlayServer: request handling failed: {e.Message}");
                try { context.Response.Close(); } catch { /* client already gone */ }
            }
        }

        private static void ServeOverlayPage(HttpListenerContext context)
        {
            var bytes = Encoding.UTF8.GetBytes(OverlayHtml.Page);
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            context.Response.OutputStream.Close();
        }

        private void HandleEventsStream(HttpListenerContext context)
        {
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.Add("Cache-Control", "no-cache");
            context.Response.SendChunked = true;

            lock (_sseLock)
            {
                _sseClients.Add(context.Response);
            }

            // The response deliberately stays open - Broadcast() writes to it later, until the
            // browser disconnects (caught there as a write failure) or Dispose() closes it.
        }

        /// <summary>Pushes a "redemption" Server-Sent Event with the given JSON payload to every connected overlay page.</summary>
        public void Broadcast(string eventName, string jsonPayload)
        {
            var message = Encoding.UTF8.GetBytes($"event: {eventName}\ndata: {jsonPayload}\n\n");

            lock (_sseLock)
            {
                for (var i = _sseClients.Count - 1; i >= 0; i--)
                {
                    var client = _sseClients[i];
                    try
                    {
                        client.OutputStream.Write(message, 0, message.Length);
                        client.OutputStream.Flush();
                    }
                    catch
                    {
                        _sseClients.RemoveAt(i);
                        try { client.Close(); } catch { /* already gone */ }
                    }
                }
            }
        }

        /// <summary>Stops accepting connections but - unlike Dispose() - leaves the underlying
        /// HttpListener usable, so Start() can bring it back later (e.g. Core.ModMenu's overlay
        /// toggle). Existing SSE clients are dropped either way since the overlay page itself would
        /// no longer be reachable while stopped.</summary>
        public void Stop()
        {
            if (!_running)
            {
                return;
            }

            _running = false;
            try { _listener.Stop(); } catch { /* already stopped */ }

            lock (_sseLock)
            {
                foreach (var client in _sseClients)
                {
                    try { client.Close(); } catch { /* already gone */ }
                }
                _sseClients.Clear();
            }
        }

        public void Dispose()
        {
            Stop();
            try { _listener.Close(); } catch { /* already closed */ }
        }
    }
}
