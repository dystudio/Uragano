﻿using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Transport.Channels;
using Microsoft.Extensions.Logging;
using Uragano.Abstractions;

namespace Uragano.Remoting
{
    public class Client : IClient
    {
        private IChannel Channel { get; }
        private readonly ConcurrentDictionary<string, TaskCompletionSource<IServiceResult>> _resultCallbackTask =
            new ConcurrentDictionary<string, TaskCompletionSource<IServiceResult>>();

        private IMessageListener MessageListener { get; }

        private IEventLoopGroup EventLoopGroup { get; }

        private ILogger Logger { get; }

        private ICodec Codec { get; }

        private string Node { get; }

        public Client(IChannel channel, IEventLoopGroup eventLoopGroup, IMessageListener messageListener, ILogger logger, ICodec codec, string node)
        {
            Channel = channel;
            MessageListener = messageListener;
            MessageListener.OnReceived += MessageListener_OnReceived;
            EventLoopGroup = eventLoopGroup;
            Logger = logger;
            Codec = codec;
            Node = node;
        }

        private void MessageListener_OnReceived(TransportMessage<IServiceResult> message)
        {
            if (_resultCallbackTask.TryGetValue(message.Id, out var task))
            {
                task.TrySetResult(message.Body);
            }
            else
                Logger.LogWarning($"\nThe message callback wait task was not found, probably because the timeout has been canceled waiting.\n[message id:{message.Id}]");
        }

        public async Task<IServiceResult> SendAsync(IInvokeMessage message)
        {
            var transportMessage = new TransportMessage<IInvokeMessage>
            {
                Id = Guid.NewGuid().ToString("N"),
                Body = message
            };
            if (Logger.IsEnabled(LogLevel.Trace))
                Logger.LogTrace($"Sending message to node {Node}:\nMessage id:{transportMessage.Id}\nArgs:{Codec.ToJson(message.Args)}\n\n");
            var tcs = new TaskCompletionSource<IServiceResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var ct = new CancellationTokenSource(UraganoOptions.Remoting_Invoke_CancellationTokenSource_Timeout.Value))
            {
                ct.Token.Register(() =>
                {
                    tcs.TrySetResult(new ServiceResult("Remoting invoke timeout!", RemotingStatus.Timeout));
                    Logger.LogWarning("Remoting invoke timeout,You can set the wait time with the Remoting_Invoke_CancellationTokenSource_Timeout option.\nSend to node:{1}\nMessage id:{0}\n\n", transportMessage.Id, Node);
                }, false);

                if (!_resultCallbackTask.TryAdd(transportMessage.Id, tcs)) throw new Exception("Failed to send.");
                try
                {
                    await Channel.WriteAndFlushAsync(transportMessage);
                    if (Logger.IsEnabled(LogLevel.Trace))
                        Logger.LogTrace($"Send completed, waiting for node {Node} to return results:\nMessage id:{transportMessage.Id}\n\n");
                    var result = await tcs.Task;
                    if (Logger.IsEnabled(LogLevel.Trace))
                        Logger.LogTrace($"The client received the return result of node {Node}:\nMessage id:{transportMessage.Id}\nBody:{Codec.ToJson(result)}\n\n");
                    return result;
                }
                finally
                {
                    _resultCallbackTask.TryRemove(transportMessage.Id, out var t);
                    t?.TrySetCanceled();
                }
            }
        }

        public async Task DisconnectAsync()
        {
            Logger.LogTrace($"Stopping client.[{Channel.LocalAddress}]");
            foreach (var task in _resultCallbackTask.Values)
            {
                task.TrySetCanceled();
            }

            _resultCallbackTask.Clear();
            if (Channel.Open)
            {
                await Channel.CloseAsync();
                await EventLoopGroup.ShutdownGracefullyAsync(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1));
            }

            Logger.LogTrace($"The client[{Channel.LocalAddress}] has stopped.");
        }
    }
}
