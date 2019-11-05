// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

namespace NuGet.Build.Tasks
{
    /// <summary>
    /// Represents the type of a console out log message.
    /// </summary>
    internal enum ConsoleOutLogMessageType
    {
        /// <summary>
        /// The type was not specified.
        /// </summary>
        None = 0,

        /// <summary>
        /// The message is an error.
        /// </summary>
        Error,

        /// <summary>
        /// The message is a warning.
        /// </summary>
        Warning,

        /// <summary>
        /// The message is a message.
        /// </summary>
        Message,
    }
}
