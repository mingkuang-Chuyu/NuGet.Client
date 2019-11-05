// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Newtonsoft.Json;

namespace NuGet.Build.Tasks.Console
{
    /// <summary>
    /// Represents a logging queue of messages that are written to <see cref="System.Console.Out" />.
    /// </summary>
    /// <remarks>
    /// This class implements <see cref="IBuildEngine" /> so that an instance of <see cref="Microsoft.Build.Utilities.TaskLoggingHelper" /> can be created.
    ///
    /// This class implements <see cref="ILogger" /> so that it can be passed to MSBuild APIs that require an ILogger to log messages.</remarks>
    internal class ConsoleLoggingQueue : LoggingQueue<ConsoleOutLogMessage>, IBuildEngine, ILogger
    {
        /// <summary>
        /// Serialization settings for messages.  These should be set to be as fast as possible and not for friendly
        /// display since no user will ever see them.
        /// </summary>
        private static readonly JsonSerializerSettings SerializerSettings = new JsonSerializerSettings
        {
            DefaultValueHandling = DefaultValueHandling.Ignore,
            NullValueHandling = NullValueHandling.Ignore,
        };

        /// <summary>
        ///
        /// </summary>
        private readonly Lazy<TaskLoggingHelper> _taskLoggingHelperLazy;

        /// <summary>
        /// Gets or sets an <see cref="IEventSource" /> object for subscribing to MSBuild logging events.
        /// </summary>
        private IEventSource _eventSource;

        /// <summary>
        /// Gets or sets the minimum <see cref="MessageImportance" /> of messages to log.
        /// </summary>
        private MessageImportance _minMessageImportance;

        /// <summary>
        /// Gets or sets the <see cref="LoggerVerbosity" /> for logging messages.
        /// </summary>
        private LoggerVerbosity _verbosity;

        /// <summary>
        /// Initializes a new instance of the <see cref="ConsoleLoggingQueue" /> class.
        /// </summary>
        /// <param name="verbosity">The <see cref="LoggerVerbosity" /> to use when logging messages.</param>
        public ConsoleLoggingQueue(LoggerVerbosity verbosity)
        {
            Verbosity = verbosity;

            _taskLoggingHelperLazy = new Lazy<TaskLoggingHelper>(() => new TaskLoggingHelper(this, nameof(DependencyGraphSpecGenerator)));
        }

        /// <inheritdoc cref="IBuildEngine.ColumnNumberOfTaskNode" />
        int IBuildEngine.ColumnNumberOfTaskNode => 0;

        /// <inheritdoc cref="IBuildEngine.ContinueOnError" />
        bool IBuildEngine.ContinueOnError => false;

        /// <inheritdoc cref="IBuildEngine.LineNumberOfTaskNode" />
        int IBuildEngine.LineNumberOfTaskNode => 0;

        /// <inheritdoc cref="ILogger.Parameters" />
        string ILogger.Parameters { get; set; }

        /// <inheritdoc cref="IBuildEngine.ProjectFileOfTaskNode" />
        string IBuildEngine.ProjectFileOfTaskNode => null;

        /// <summary>
        /// Gets a <see cref="Microsoft.Build.Utilities.TaskLoggingHelper" /> that can be used to write log messages to the current queue.
        /// </summary>
        public TaskLoggingHelper TaskLoggingHelper => _taskLoggingHelperLazy.Value;

        /// <inheritdoc cref="ILogger.Verbosity" />
        public LoggerVerbosity Verbosity
        {
            get => _verbosity;
            set
            {
                _verbosity = value;

                // Determine the minimum verbosity of messages
                switch (value)
                {
                    case LoggerVerbosity.Quiet:
                    case LoggerVerbosity.Minimal:
                        _minMessageImportance = MessageImportance.High;
                        break;

                    case LoggerVerbosity.Normal:
                        _minMessageImportance = MessageImportance.Normal;
                        break;

                    case LoggerVerbosity.Detailed:
                    case LoggerVerbosity.Diagnostic:
                        _minMessageImportance = MessageImportance.Low;
                        break;

                    default:
                        throw new ArgumentOutOfRangeException(nameof(value), value, null);
                }
            }
        }

        /// <inheritdoc cref="IBuildEngine.BuildProjectFile" />
        bool IBuildEngine.BuildProjectFile(string projectFileName, string[] targetNames, IDictionary globalProperties, IDictionary targetOutputs) => throw new NotImplementedException();

        /// <inheritdoc cref="ILogger.Initialize" />
        void ILogger.Initialize(IEventSource eventSource)
        {
            _eventSource = eventSource;

            _eventSource.MessageRaised += OnMessageRaised;
            _eventSource.WarningRaised += OnWarningRaised;
            _eventSource.ErrorRaised += OnErrorRaised;
        }

        /// <inheritdoc cref="IBuildEngine.LogCustomEvent" />
        void IBuildEngine.LogCustomEvent(CustomBuildEventArgs e) { }

        /// <inheritdoc cref="IBuildEngine.LogErrorEvent" />
        void IBuildEngine.LogErrorEvent(BuildErrorEventArgs e) => OnErrorRaised(this, e);

        /// <inheritdoc cref="IBuildEngine.LogMessageEvent" />
        void IBuildEngine.LogMessageEvent(BuildMessageEventArgs e) => OnMessageRaised(this, e);

        /// <inheritdoc cref="IBuildEngine.LogWarningEvent" />
        void IBuildEngine.LogWarningEvent(BuildWarningEventArgs e) => OnWarningRaised(this, e);

        /// <inheritdoc cref="ILogger.Shutdown" />
        void ILogger.Shutdown()
        {
            if (_eventSource != null)
            {
                _eventSource.ErrorRaised -= OnErrorRaised;
                _eventSource.WarningRaised -= OnWarningRaised;
                _eventSource.MessageRaised -= OnMessageRaised;
            }
        }

        /// <summary>
        /// Processes a logging message by serializing it as JSON and writing it to <see cref="System.Console.Out" />.
        /// </summary>
        /// <param name="message">The <see cref="ConsoleOutLogMessage" /> to log.</param>
        protected override void Process(ConsoleOutLogMessage message)
        {
            System.Console.Out.WriteLine(JsonConvert.SerializeObject(message, SerializerSettings));
        }

        /// <summary>
        /// Handles the event when an error event was logged.
        /// </summary>
        /// <param name="sender">The sender of the event.</param>
        /// <param name="e">The <see cref="BuildErrorEventArgs" /> containing the details of the error event.</param>
        private void OnErrorRaised(object sender, BuildErrorEventArgs e) => Enqueue(e);

        /// <summary>
        /// Handles the event when a message event was logged.
        /// </summary>
        /// <param name="sender">The sender of the event.</param>
        /// <param name="e">The <see cref="BuildMessageEventArgs" /> containing the details of the message event.</param>
        private void OnMessageRaised(object sender, BuildMessageEventArgs e)
        {
            // Only log the message if its importance is meets the requirements for the minimum verbosity
            if (e.Importance <= _minMessageImportance)
            {
                Enqueue(e);
            }
        }

        /// <summary>
        /// Handles the event when a message event was logged.
        /// </summary>
        /// <param name="sender">The sender of the event.</param>
        /// <param name="e">The <see cref="BuildWarningEventArgs" /> containing the details of the message event.</param>
        private void OnWarningRaised(object sender, BuildWarningEventArgs e) => Enqueue(e);
    }
}
