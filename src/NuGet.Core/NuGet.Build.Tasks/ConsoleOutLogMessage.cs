// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using Microsoft.Build.Framework;

namespace NuGet.Build.Tasks
{
    internal enum ConsoleOutLogMessageType
    {
        None = 0,
        Error,
        Warning,
        Message,
    }

    internal sealed class ConsoleOutLogMessage
    {
        public string Code { get; set; }

        public int ColumnNumber { get; set; }

        public int EndColumnNumber { get; set; }

        public int EndLineNumber { get; set; }

        public string File { get; set; }

        public string HelpKeyword { get; set; }

        public MessageImportance Importance { get; set; }

        public int LineNumber { get; set; }

        public string Message { get; set; }

        public ConsoleOutLogMessageType MessageType { get; set; }

        public string ProjectFile { get; set; }

        public string SenderName { get; set; }

        public string Subcategory { get; set; }

        public static implicit operator ConsoleOutLogMessage(BuildMessageEventArgs buildMessageEventArgs)
        {
            return new ConsoleOutLogMessage
            {
                Importance = buildMessageEventArgs.Importance,
                Message = buildMessageEventArgs.Message,
                MessageType = ConsoleOutLogMessageType.Message,
            };
        }

        public static implicit operator ConsoleOutLogMessage(BuildWarningEventArgs buildWarningEventArgs)
        {
            return new ConsoleOutLogMessage
            {
                Code = buildWarningEventArgs.Code,
                ColumnNumber = buildWarningEventArgs.ColumnNumber,
                EndColumnNumber = buildWarningEventArgs.EndColumnNumber,
                EndLineNumber = buildWarningEventArgs.EndLineNumber,
                File = buildWarningEventArgs.File,
                HelpKeyword = buildWarningEventArgs.HelpKeyword,
                LineNumber = buildWarningEventArgs.LineNumber,
                Message = buildWarningEventArgs.Message,
                MessageType = ConsoleOutLogMessageType.Warning,
                ProjectFile = buildWarningEventArgs.ProjectFile,
                SenderName = buildWarningEventArgs.SenderName,
                Subcategory = buildWarningEventArgs.Subcategory
            };
        }

        public static implicit operator ConsoleOutLogMessage(BuildErrorEventArgs buildErrorEventArgs)
        {
            return new ConsoleOutLogMessage
            {
                Code = buildErrorEventArgs.Code,
                ColumnNumber = buildErrorEventArgs.ColumnNumber,
                EndColumnNumber = buildErrorEventArgs.EndColumnNumber,
                EndLineNumber = buildErrorEventArgs.EndLineNumber,
                File = buildErrorEventArgs.File,
                HelpKeyword = buildErrorEventArgs.HelpKeyword,
                LineNumber = buildErrorEventArgs.LineNumber,
                Message = buildErrorEventArgs.Message,
                MessageType = ConsoleOutLogMessageType.Error,
                ProjectFile = buildErrorEventArgs.ProjectFile,
                SenderName = buildErrorEventArgs.SenderName,
                Subcategory = buildErrorEventArgs.Subcategory,
            };
        }
    }
}
