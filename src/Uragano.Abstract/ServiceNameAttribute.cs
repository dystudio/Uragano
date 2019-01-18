﻿using System;

namespace Uragano.Abstractions
{
	[AttributeUsage(AttributeTargets.Interface)]
	public class ServiceNameAttribute : Attribute
	{
		public ServiceNameAttribute(string serviceName)
		{
			if (string.IsNullOrWhiteSpace(serviceName))
				throw new ArgumentNullException(nameof(serviceName));
			Name = serviceName;
		}

		public string Name { get; set; }
	}
}
