using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Newtonsoft.Json;
using Task = Microsoft.Build.Utilities.Task;

namespace NuGet.Build.Tasks
{
    public class RestoreTaskEx : Task, ICancelableTask, IDisposable
    {
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

        public bool DisableParallel { get; set; } = Environment.ProcessorCount == 1;

        public bool Force { get; set; }

        public bool ForceEvaluate { get; set; }

        public bool HideWarningsAndErrors { get; set; }

        public bool IgnoreFailedSources { get; set; }

        public bool Interactive { get; set; }

        [Required]
        public string MSBuildBinPath { get; set; }

        public bool NoCache { get; set; }

        [Required]
        public string ProjectFullPath { get; set; }

        public bool Recursive { get; set; }

        public string SolutionPath { get; set; }

        public void Cancel() => _cancellationTokenSource.Cancel();

        public void Dispose()
        {
            _cancellationTokenSource.Dispose();
        }

        public override bool Execute()
        {
            var globalProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ExcludeRestorePackageImports"] = "true"
            };

            var properties = new Dictionary<string, string>(8, StringComparer.OrdinalIgnoreCase);

            AddPropertyIfTrue(properties, nameof(DisableParallel), DisableParallel);
            AddPropertyIfTrue(properties, nameof(Force), Force);
            AddPropertyIfTrue(properties, nameof(ForceEvaluate), ForceEvaluate);
            AddPropertyIfTrue(properties, nameof(HideWarningsAndErrors), HideWarningsAndErrors);
            AddPropertyIfTrue(properties, nameof(IgnoreFailedSources), IgnoreFailedSources);
            AddPropertyIfTrue(properties, nameof(Interactive), Interactive);
            AddPropertyIfTrue(properties, nameof(NoCache), NoCache);
            AddPropertyIfTrue(properties, nameof(Recursive), Recursive);

            string entryProjectFile =
                !string.IsNullOrWhiteSpace(SolutionPath) && !string.Equals(SolutionPath, "*Undefined*", StringComparison.OrdinalIgnoreCase)
                    ? SolutionPath
                    : ProjectFullPath;
            try
            {
                var thisAssembly = new FileInfo(Assembly.GetExecutingAssembly().Location);

                var startInfo = new ProcessStartInfo
                {
#if !NETFRAMEWORK
                    Arguments = $"\"{Path.Combine(thisAssembly.DirectoryName, Path.ChangeExtension(thisAssembly.Name, ".Console.dll"))}\" \"{string.Join(";", properties.Select(i => $"{i.Key}={i.Value}"))}\" \"{Path.Combine(MSBuildBinPath, "MSBuild.dll")}\" \"{string.Join(";", entryProjectFile)}\" \"{string.Join(";", globalProperties.Select(i => $"{i.Key}={i.Value}"))}\"",
#else
                    Arguments = $"\"{string.Join(";", properties.Select(i => $"{i.Key}={i.Value}"))}\" \"{Path.Combine(MSBuildBinPath, "MSBuild.exe")}\" \"{string.Join(";", entryProjectFile)}\" \"{string.Join(";", globalProperties.Select(i => $"{i.Key}={i.Value}"))}\"",
#endif
                    CreateNoWindow = true,
#if !NETFRAMEWORK
                    FileName = Path.GetFullPath(Path.Combine(MSBuildBinPath, @"..", "..", "dotnet")),
#else
                    FileName = Path.Combine(thisAssembly.DirectoryName, Path.ChangeExtension(thisAssembly.Name, ".Console.exe")),
#endif
                    RedirectStandardError = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    WorkingDirectory = Environment.CurrentDirectory,
                };

                Log.LogMessage(MessageImportance.Low, "\"{0}\" {1}", startInfo.FileName, startInfo.Arguments);

                using (var semaphore = new SemaphoreSlim(0, 1))
                using (var loggingQueue = new TaskLoggingHelperQueue(Log))
                using (var process = new Process())
                {
                    process.EnableRaisingEvents = true;
                    process.StartInfo = startInfo;

                    process.OutputDataReceived += (sender, args) => { loggingQueue.Enqueue(args?.Data); };

                    process.Exited += (sender, args) => semaphore.Release();

                    try
                    {
                        process.Start();

                        process.StandardInput.Close();

                        process.BeginOutputReadLine();

                        semaphore.Wait(_cancellationTokenSource.Token);

                        if (!process.HasExited)
                        {
                            process.Kill();
                        }
                    }
                    catch (Exception e) when (
                        e is TaskCanceledException
                        || e is OperationCanceledException
                        || (e is AggregateException aggregateException && aggregateException.InnerException is TaskCanceledException))
                    {
                    }
                }
            }
            catch (Exception e)
            {
                Log.LogErrorFromException(e);
            }

            return !Log.HasLoggedErrors;
        }

        private void AddPropertyIfTrue(Dictionary<string, string> properties, string name, bool value)
        {
            if (value)
            {
                properties[name] = "true";
            }
        }

        internal class TaskLoggingHelperQueue : LoggingQueue<string>
        {
            private readonly TaskLoggingHelper _log;

            public TaskLoggingHelperQueue(TaskLoggingHelper log)
            {
                _log = log ?? throw new ArgumentNullException(nameof(log));
            }

            protected override void Process(string message)
            {
                if (string.IsNullOrWhiteSpace(message))
                {
                    return;
                }

                if (message.Length >= 2 && message[0] == '{' && message[message.Length - 1] == '}')
                {
                    ConsoleOutLogMessage consoleOutLogMessage;

                    try
                    {
                        consoleOutLogMessage = JsonConvert.DeserializeObject<ConsoleOutLogMessage>(message);
                    }
                    catch
                    {
                        _log.LogMessage(null, null, null, 0, 0, 0, 0, message, null, null, MessageImportance.Low);

                        return;
                    }

                    switch (consoleOutLogMessage.MessageType)
                    {
                        case ConsoleOutLogMessageType.Error:
                            _log.LogError(
                                subcategory: consoleOutLogMessage.Subcategory,
                                errorCode: consoleOutLogMessage.Code,
                                helpKeyword: consoleOutLogMessage.HelpKeyword,
                                file: consoleOutLogMessage.File,
                                lineNumber: consoleOutLogMessage.LineNumber,
                                columnNumber: consoleOutLogMessage.ColumnNumber,
                                endLineNumber: consoleOutLogMessage.EndLineNumber,
                                endColumnNumber: consoleOutLogMessage.EndColumnNumber,
                                message: consoleOutLogMessage.Message);
                            return;

                        case ConsoleOutLogMessageType.Warning:
                            _log.LogWarning(
                                subcategory: consoleOutLogMessage.Subcategory,
                                warningCode: consoleOutLogMessage.Code,
                                helpKeyword: consoleOutLogMessage.HelpKeyword,
                                file: consoleOutLogMessage.File,
                                lineNumber: consoleOutLogMessage.LineNumber,
                                columnNumber: consoleOutLogMessage.ColumnNumber,
                                endLineNumber: consoleOutLogMessage.EndLineNumber,
                                endColumnNumber: consoleOutLogMessage.EndColumnNumber,
                                message: consoleOutLogMessage.Message);
                            return;

                        case ConsoleOutLogMessageType.Message:
                            _log.LogMessageFromText(consoleOutLogMessage.Message, consoleOutLogMessage.Importance);
                            return;

                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }
            }
        }
    }
}
