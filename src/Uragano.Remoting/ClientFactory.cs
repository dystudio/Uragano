﻿using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Common.Utilities;
using DotNetty.Handlers.Logging;
using DotNetty.Transport.Bootstrapping;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using DotNetty.Transport.Libuv;
using Uragano.Abstractions.Remoting;
using Uragano.Codec.MessagePack;

namespace Uragano.Remoting
{
	public class ClientFactory : IClientFactory
	{

		private Bootstrap Bootstrap { get; }

		private readonly ConcurrentDictionary<string, Lazy<IClient>> _clients = new ConcurrentDictionary<string, Lazy<IClient>>();

		private static readonly AttributeKey<TransportContext> TransportContextAttributeKey = AttributeKey<TransportContext>.ValueOf(typeof(ClientFactory), nameof(TransportContext));

		private static readonly AttributeKey<IMessageListener> MessageListenerAttributeKey = AttributeKey<IMessageListener>.ValueOf(typeof(ClientFactory), nameof(IMessageListener));


		public ClientFactory()
		{

			Bootstrap = CreateBootstrap();
			Bootstrap.Handler(new ActionChannelInitializer<ISocketChannel>(channel =>
			{
				var pipeline = channel.Pipeline;
				pipeline.AddLast(new LoggingHandler("SRV-CONN"));
				pipeline.AddLast(new LengthFieldPrepender(2));
				pipeline.AddLast(new LengthFieldBasedFrameDecoder(int.MaxValue, 0, 2, 0, 2));
				pipeline.AddLast(new MessageDecoder<ResultMessage>());
				pipeline.AddLast(new MessageEncoder<InvokeMessage>());
				pipeline.AddLast(new ClientMessageHandler(this));
			}));
		}

		public void RemoveClient(string host, int port)
		{
			if (!_clients.TryRemove($"{host}:{port}", out var client)) return;
			if (client.IsValueCreated)
				client.Value.Dispose();
		}

		public IClient CreateClient(string host, int port)
		{
			var key = $"{host}:{port}";
			try
			{
				return _clients.GetOrAdd(key, new Lazy<IClient>(() =>
				{
					var bootstrap = Bootstrap;
					var channel = bootstrap.ConnectAsync(IPAddress.Parse(host), port).GetAwaiter().GetResult();
					channel.GetAttribute(TransportContextAttributeKey).Set(new TransportContext
					{
						Host = host,
						Port = port
					});
					var listener = new MessageListener();
					channel.GetAttribute(MessageListenerAttributeKey).Set(listener);
					return new Client(channel, listener);
				})).Value;
			}
			catch
			{
				_clients.TryRemove(key, out _);
				throw;
			}
		}

		public static Bootstrap CreateBootstrap()
		{
			IEventLoopGroup group;

			var bootstrap = new Bootstrap();
			if (false)
			{
				group = new EventLoopGroup();
				bootstrap.Channel<TcpChannel>();
			}
			else
			{
				group = new MultithreadEventLoopGroup();
				bootstrap.Channel<TcpSocketChannel>();
			}
			bootstrap
				.Group(group)
				.Option(ChannelOption.TcpNodelay, true)
				.Option(ChannelOption.Allocator, PooledByteBufferAllocator.Default)
				;

			return bootstrap;
		}


		public void Dispose()
		{
			foreach (var client in _clients.Values.Where(p => p.IsValueCreated))
			{
				client.Value.Dispose();
			}
		}

		internal class ClientMessageHandler : ChannelHandlerAdapter
		{

			private IClientFactory ClientFactory { get; }


			public ClientMessageHandler(IClientFactory clientFactory)
			{
				ClientFactory = clientFactory;
			}

			public override void ChannelRead(IChannelHandlerContext context, object message)
			{
				var msg = message as TransportMessage<ResultMessage>;
				var listener = context.Channel.GetAttribute(MessageListenerAttributeKey).Get();
				listener.Received(new MessageSender(), msg);
			}

			public override void ChannelInactive(IChannelHandlerContext context)
			{
				var ctx = context.Channel.GetAttribute(TransportContextAttributeKey).Get();
				ClientFactory.RemoveClient(ctx.Host, ctx.Port);
			}
		}
	}
}
