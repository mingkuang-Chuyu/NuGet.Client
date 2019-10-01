// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;

namespace NuGet.Common
{
    public class NullProtocolDiagnostics : IProtocolDiagnostics
    {
        public static NullProtocolDiagnostics Instance { get; } = new NullProtocolDiagnostics();

        public void OnEvent(
            string source,
            string url,
            TimeSpan? headerDuration,
            TimeSpan requestDuration,
            int? httpStatusCode,
            long bodyBytes,
            bool isSuccess,
            bool isRetry,
            bool isCancelled)
        {
        }
    }
}
