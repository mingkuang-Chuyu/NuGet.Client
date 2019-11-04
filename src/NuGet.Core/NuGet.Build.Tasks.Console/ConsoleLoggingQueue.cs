using System;
using System.Collections;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Newtonsoft.Json;

namespace NuGet.Build.Tasks.Console
{
    internal class ConsoleLoggingQueue : LoggingQueue<ConsoleOutLogMessage>, IBuildEngine, ILogger
    {
        private static readonly JsonSerializerSettings SerializerSettings = new JsonSerializerSettings
        {
            DefaultValueHandling = DefaultValueHandling.Ignore,
            NullValueHandling = NullValueHandling.Ignore,
        };

        private readonly Lazy<TaskLoggingHelper> _taskLoggingHelperLazy;

        private IEventSource _eventSource;

        private MessageImportance _minMessageImportance;

        private LoggerVerbosity _verbosity;

        public ConsoleLoggingQueue(LoggerVerbosity verbosity)
        {
            _verbosity = verbosity;

            switch (verbosity)
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
                    throw new ArgumentOutOfRangeException(nameof(verbosity), verbosity, null);
            }

            _taskLoggingHelperLazy = new Lazy<TaskLoggingHelper>(() => new TaskLoggingHelper(this, nameof(DependencyGraphSpecGenerator)));
        }

        int IBuildEngine.ColumnNumberOfTaskNode => 0;

        bool IBuildEngine.ContinueOnError => false;

        int IBuildEngine.LineNumberOfTaskNode => 0;

        string ILogger.Parameters { get; set; }

        string IBuildEngine.ProjectFileOfTaskNode => null;

        public TaskLoggingHelper TaskLoggingHelper => _taskLoggingHelperLazy.Value;

        LoggerVerbosity ILogger.Verbosity
        {
            get => _verbosity;
            set
            {
                _verbosity = value;

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

        bool IBuildEngine.BuildProjectFile(string projectFileName, string[] targetNames, IDictionary globalProperties, IDictionary targetOutputs) => throw new NotImplementedException();

        void ILogger.Initialize(IEventSource eventSource)
        {
            _eventSource = eventSource;

            _eventSource.MessageRaised += OnMessageRaised;
            _eventSource.WarningRaised += OnWarningRaised;
            _eventSource.ErrorRaised += OnErrorRaised;
        }

        void IBuildEngine.LogCustomEvent(CustomBuildEventArgs e)
        {
        }

        void IBuildEngine.LogErrorEvent(BuildErrorEventArgs e) => OnErrorRaised(this, e);

        void IBuildEngine.LogMessageEvent(BuildMessageEventArgs e) => OnMessageRaised(this, e);

        void IBuildEngine.LogWarningEvent(BuildWarningEventArgs e) => OnWarningRaised(this, e);

        void ILogger.Shutdown()
        {
            if (_eventSource != null)
            {
                _eventSource.ErrorRaised -= OnErrorRaised;
                _eventSource.WarningRaised -= OnWarningRaised;
                _eventSource.MessageRaised -= OnMessageRaised;
            }
        }

        protected override void Process(ConsoleOutLogMessage message)
        {
            System.Console.Out.WriteLine(JsonConvert.SerializeObject(message, SerializerSettings));
        }

        private void OnErrorRaised(object sender, BuildErrorEventArgs e) => Enqueue(e);

        private void OnMessageRaised(object sender, BuildMessageEventArgs e)
        {
            if (e.Importance <= _minMessageImportance)
            {
                Enqueue(e);
            }
        }

        private void OnWarningRaised(object sender, BuildWarningEventArgs e) => Enqueue(e);
    }
}
