﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using DotNetty.Buffers;
using DotNetty.Transport.Channels;
using Uragano.Abstractions.ServiceInvoker;
using Microsoft.Extensions.DependencyInjection;
using Uragano.Abstractions;

namespace Uragano.Remoting
{
	public class ServerMessageHandler : ChannelHandlerAdapter
	{

		private IInvokerFactory InvokerFactory { get; }
		private IProxyGenerateFactory ProxyGenerateFactory { get; }

		public ServerMessageHandler(IInvokerFactory invokerFactory, IProxyGenerateFactory proxyGenerateFactory)
		{
			InvokerFactory = invokerFactory;
			ProxyGenerateFactory = proxyGenerateFactory;
		}

		public override void ChannelRead(IChannelHandlerContext context, object message)
		{
			if (!(message is TransportMessage<InvokeMessage> transportMessage))
				throw new ArgumentNullException(nameof(message));
			try
			{
				var service = InvokerFactory.Get(transportMessage.Content.Route);
				var proxyInstance = ProxyGenerateFactory.CreateLocalProxy(service.MethodInfo.DeclaringType);
				var result = service.MethodInfo.Invoke(proxyInstance, transportMessage.Content.Args);

				context.WriteAndFlushAsync(new TransportMessage<ResultMessage>
				{
					Id = transportMessage.Id,
					Content = new ResultMessage(result)
				}).Wait();
			}
			catch (Exception e)
			{
				context.WriteAndFlushAsync(new TransportMessage<ResultMessage>
				{
					Id = transportMessage.Id,
					Content = new ResultMessage(e.Message) { Status = RemotingStatus.Error }
				}).Wait();
			}
		}

		public override void ChannelReadComplete(IChannelHandlerContext context)
		{
			context.Flush();
		}

		public override void ExceptionCaught(IChannelHandlerContext context, Exception exception)
		{
			context.CloseAsync().Wait();
		}
	}
}
