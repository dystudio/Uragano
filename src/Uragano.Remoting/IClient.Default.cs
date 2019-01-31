﻿using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using DotNetty.Transport.Channels;
using Uragano.Abstractions;

namespace Uragano.Remoting
{
    public class Client : IClient
    {
        private IChannel Channel { get; }
        private readonly ConcurrentDictionary<string, TaskCompletionSource<ResultMessage>> _resultCallbackTask =
            new ConcurrentDictionary<string, TaskCompletionSource<ResultMessage>>();

        private IMessageListener MessageListener { get; }

        public Client(IChannel channel, IMessageListener messageListener)
        {
            Channel = channel;
            MessageListener = messageListener;
            MessageListener.OnReceived += MessageListener_OnReceived;

        }

        private void MessageListener_OnReceived(IMessageSender sender, TransportMessage<ResultMessage> message)
        {
            if (_resultCallbackTask.TryGetValue(message.Id, out var task))
            {
                task.TrySetResult(message.Body);
            }
            else
                Console.WriteLine("Not found callback");
        }

        public async Task<ResultMessage> SendAsync(InvokeMessage message)
        {
            var transportMessage = new TransportMessage<InvokeMessage>
            {
                Id = Guid.NewGuid().ToString(),
                Body = message
            };

            var task = new TaskCompletionSource<ResultMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_resultCallbackTask.TryAdd(transportMessage.Id, task)) throw new Exception("Failed to send.");
            try
            {
                await Channel.WriteAndFlushAsync(transportMessage);
                //using (var cts = new CancellationTokenSource())
                //{
                //	if (task.Task == await Task.WhenAny(task.Task, Task.Delay(4000, cts.Token)))
                //	{
                //		cts.Cancel();
                return await task.Task;
                //}
                //else
                //	task.SetCanceled();
                //}
                //throw new TimeoutException();
            }
            finally
            {
                _resultCallbackTask.TryRemove(transportMessage.Id, out var t);
                t.TrySetCanceled();
            }
        }


        public void Dispose()
        {
            foreach (var task in _resultCallbackTask.Values)
            {
                task.TrySetCanceled();
            }

            Channel.DisconnectAsync();
            Channel.CloseAsync();
        }
    }
}
