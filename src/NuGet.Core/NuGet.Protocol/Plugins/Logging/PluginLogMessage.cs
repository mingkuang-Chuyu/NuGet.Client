// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace NuGet.Protocol.Plugins
{
    internal abstract class PluginLogMessage : IPluginLogMessage
    {
        private static readonly StringEnumConverter _enumConverter = new StringEnumConverter();

        private readonly DateTime _now;
        private readonly int _aWorkerThreads;
        private readonly int _aComplPortThreads;

        protected PluginLogMessage(DateTimeOffset now)
        {
            _now = now.UtcDateTime;
            ThreadPool.GetAvailableThreads(out _aWorkerThreads, out _aComplPortThreads);
        }

        protected string ToString(string type, JObject message)
        {
            if (string.IsNullOrEmpty(type))
            {
                throw new ArgumentException(Strings.ArgumentCannotBeNullOrEmpty, nameof(type));
            }

            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }
            
            var outerMessage = new JObject(
                new JProperty("now", _now.ToString("O")), // round-trip format
                new JProperty("managedThreadId", Thread.CurrentThread.ManagedThreadId),
                new JProperty("awt", _aWorkerThreads),
                new JProperty("acpt", _aComplPortThreads),
                new JProperty("type", type),
                new JProperty("message", message));

            return outerMessage.ToString(Formatting.None, _enumConverter);
        }
    }
}
