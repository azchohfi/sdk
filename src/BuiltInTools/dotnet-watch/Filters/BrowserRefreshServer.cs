// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Tools.Internal;

namespace Microsoft.DotNet.Watcher.Tools
{
    public class BrowserRefreshServer : IAsyncDisposable
    {
        private readonly byte[] ReloadMessage = Encoding.UTF8.GetBytes("Reload");
        private readonly byte[] WaitMessage = Encoding.UTF8.GetBytes("Wait");
        private readonly JsonSerializerOptions _jsonSerializerOptions = new(JsonSerializerDefaults.Web);
        private readonly List<WebSocket> _clientSockets = new();
        private readonly IReporter _reporter;
        private readonly TaskCompletionSource _taskCompletionSource;
        private IHost _refreshServer;

        public BrowserRefreshServer(IReporter reporter)
        {
            _reporter = reporter;
            _taskCompletionSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public async ValueTask<string> StartAsync(CancellationToken cancellationToken)
        {
            var envHostName = Environment.GetEnvironmentVariable("DOTNET_WATCH_AUTO_RELOAD_WS_HOSTNAME");
            var hostName = envHostName ?? "127.0.0.1";

            var useTls = await ShouldUseHttps();

            _refreshServer = new HostBuilder()
                .ConfigureWebHost(builder =>
                {
                    builder.UseKestrel();
                    builder.UseUrls(useTls ? $"https://{hostName}:0" : $"http://{hostName}:0");

                    builder.Configure(app =>
                    {
                        app.UseWebSockets();
                        app.Run(WebSocketRequest);
                    });
                })
                .Build();

            await _refreshServer.StartAsync(cancellationToken);

            var serverUrl = _refreshServer.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()
                .Addresses
                .First();

            if (envHostName is null)
            {
                return useTls ?
                    serverUrl.Replace("https://127.0.0.1", "wss://localhost", StringComparison.Ordinal) :
                    serverUrl.Replace("http://127.0.0.1", "ws://localhost", StringComparison.Ordinal);
            }

            return serverUrl
                .Replace("https://", "wss://", StringComparison.Ordinal)
                .Replace("http://", "ws://", StringComparison.Ordinal);
        }

        private async Task WebSocketRequest(HttpContext context)
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            _clientSockets.Add(await context.WebSockets.AcceptWebSocketAsync());
            await _taskCompletionSource.Task;
        }

        public ValueTask SendJsonSerlialized<TValue>(TValue value, CancellationToken cancellationToken = default)
        {
            var jsonSerialized = JsonSerializer.SerializeToUtf8Bytes(value, _jsonSerializerOptions);
            return SendMessage(jsonSerialized, cancellationToken);
        }

        public async ValueTask SendMessage(ReadOnlyMemory<byte> messageBytes, CancellationToken cancellationToken = default)
        {
            try
            {
                for (var i = 0; i < _clientSockets.Count; i++)
                {
                    var clientSocket = _clientSockets[i];
                    if (clientSocket.State is not WebSocketState.Open)
                    {
                        continue;
                    }
                    await clientSocket.SendAsync(messageBytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
                }
            }
            catch (TaskCanceledException)
            {
                _reporter.Verbose("WebSocket connection has been terminated.");
            }
            catch (Exception ex)
            {
                _reporter.Verbose($"Refresh server error: {ex}");
            }
        }

        public async ValueTask DisposeAsync()
        {
            for (var i = 0; i < _clientSockets.Count; i++)
            {
                var clientSocket = _clientSockets[i];
                await clientSocket.CloseOutputAsync(WebSocketCloseStatus.Empty, null, default);
                clientSocket.Dispose();
            }

            if (_refreshServer != null)
            {
                _refreshServer.Dispose();
            }

            _taskCompletionSource.TrySetResult();
        }

        public async ValueTask<ValueWebSocketReceiveResult?> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            for (int i = 0; i < _clientSockets.Count; i++)
            {
                var clientSocket = _clientSockets[i];

                if (clientSocket.State is not WebSocketState.Open)
                {
                    continue;
                }

                try
                {
                    return await clientSocket.ReceiveAsync(buffer, cancellationToken);
                }
                catch (Exception ex)
                {
                    _reporter.Verbose($"Refresh server error: {ex}");
                }
            }

            return default;
        }

        public ValueTask ReloadAsync(CancellationToken cancellationToken) => SendMessage(ReloadMessage, cancellationToken);

        public ValueTask SendWaitMessageAsync(CancellationToken cancellationToken) => SendMessage(WaitMessage, cancellationToken);

        private static async Task<bool> ShouldUseHttps()
        {
            try
            {
                using var process = Process.Start(DotnetMuxer.MuxerPath, "dev-certs https -c");
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
