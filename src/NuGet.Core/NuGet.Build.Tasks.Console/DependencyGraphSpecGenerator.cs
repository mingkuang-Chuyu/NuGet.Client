// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

#if IS_CORECLR
using System.Runtime.InteropServices;
#endif

using System.Threading;
using System.Threading.Tasks;
using Microsoft.Build.Construction;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Evaluation.Context;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Graph;
using NuGet.Commands;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Credentials;
using NuGet.Frameworks;
using NuGet.LibraryModel;
using NuGet.Packaging;
using NuGet.ProjectModel;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.RuntimeModel;
using NuGet.Versioning;
using ILogger = Microsoft.Build.Framework.ILogger;

namespace NuGet.Build.Tasks.Console
{
    internal class DependencyGraphSpecGenerator
    {
        private static readonly NuGetVersion DefaultNuGetVersion = new NuGetVersion(1, 0, 0);

        private static readonly Lazy<IMachineWideSettings> MachineWideSettingsLazy = new Lazy<IMachineWideSettings>(() => new XPlatMachineWideSetting());

        private static readonly string[] TargetsToBuild =
        {
            "CollectPackageReferences",
            "CollectPackageDownloads",
            "CollectFrameworkReferences"
        };

        private readonly string _entryProjectPath;

        private readonly Dictionary<string, string> _globalProperties;

        private readonly int _loggerVerbosity;

        public DependencyGraphSpecGenerator(string entryProjectPath, Dictionary<string, string> globalProperties)
        {
            _entryProjectPath = entryProjectPath;
            _globalProperties = globalProperties;
            // TODO: Pass verbosity from main process
            _loggerVerbosity = (int)LoggerVerbosity.Normal;
        }

        public bool Debug { get; set; }

        public async Task<bool> RestoreAsync(Dictionary<string, string> properties)
        {
            using (var loggingQueue = new ConsoleLoggingQueue((LoggerVerbosity)_loggerVerbosity))
            {
                var dependencyGraphSpec = GetDependencyGraphSpec(loggingQueue);

                if (dependencyGraphSpec == null)
                {
                    return false;
                }

                try
                {
                    var log = new MSBuildLogger(loggingQueue.TaskLoggingHelper);

                    NetworkProtocolUtility.SetConnectionLimit();

                    // Set user agent string used for network calls
#if IS_CORECLR
            UserAgent.SetUserAgentString(new UserAgentStringBuilder("NuGet .NET Core MSBuild Task")
                .WithOSDescription(RuntimeInformation.OSDescription));
#else
                    // OS description is set by default on Desktop
                    UserAgent.SetUserAgentString(new UserAgentStringBuilder("NuGet Desktop MSBuild Task"));
#endif

                    // This method has no effect on .NET Core.
                    NetworkProtocolUtility.ConfigureSupportedSslProtocols();

                    var providerCache = new RestoreCommandProvidersCache();

                    using (var cacheContext = new SourceCacheContext())
                    {
                        cacheContext.NoCache = IsPropertyTrue(nameof(RestoreTaskEx.NoCache), properties);
                        cacheContext.IgnoreFailedSources = IsPropertyTrue(nameof(RestoreTaskEx.IgnoreFailedSources), properties);

                        // Pre-loaded request provider containing the graph file
                        var providers = new List<IPreLoadedRestoreRequestProvider>();

                        if (dependencyGraphSpec.Restore.Count == 0)
                        {
                            // Restore will fail if given no inputs, but here we should skip it and provide a friendly message.
                            log.LogMinimal(Strings.NoProjectsToRestore);
                            return true;
                        }

                        // Add all child projects
                        if (IsPropertyTrue(nameof(RestoreTaskEx.Recursive), properties))
                        {
                            BuildTasksUtility.AddAllProjectsForRestore(dependencyGraphSpec);
                        }

                        providers.Add(new DependencyGraphSpecRequestProvider(providerCache, dependencyGraphSpec));

                        var restoreContext = new RestoreArgs
                        {
                            CacheContext = cacheContext,
                            LockFileVersion = LockFileFormat.Version,
                            DisableParallel = IsPropertyTrue(nameof(RestoreTaskEx.DisableParallel), properties),
                            Log = log,
                            MachineWideSettings = new XPlatMachineWideSetting(),
                            PreLoadedRequestProviders = providers,
                            AllowNoOp = !IsPropertyTrue(nameof(RestoreTaskEx.Force), properties),
                            HideWarningsAndErrors = IsPropertyTrue(nameof(RestoreTaskEx.HideWarningsAndErrors), properties),
                            RestoreForceEvaluate = IsPropertyTrue(nameof(RestoreTaskEx.ForceEvaluate), properties)
                        };

                        // 'dotnet restore' fails on slow machines (https://github.com/NuGet/Home/issues/6742)
                        // The workaround is to pass the '--disable-parallel' option.
                        // We apply the workaround by default when the system has 1 cpu.
                        // This will fix restore failures on VMs with 1 CPU and containers with less or equal to 1 CPU assigned.
                        if (Environment.ProcessorCount == 1)
                        {
                            restoreContext.DisableParallel = true;
                        }

                        if (restoreContext.DisableParallel)
                        {
                            HttpSourceResourceProvider.Throttle = SemaphoreSlimThrottle.CreateBinarySemaphore();
                        }

                        DefaultCredentialServiceUtility.SetupDefaultCredentialService(log, !IsPropertyTrue("Interactive", properties));

                        var restoreSummaries = await RestoreRunner.RunAsync(restoreContext, CancellationToken.None);

                        // Summary
                        RestoreSummary.Log(log, restoreSummaries);

                        return restoreSummaries.All(x => x.Success);
                    }
                }
                catch (Exception e)
                {
                    loggingQueue.TaskLoggingHelper.LogErrorFromException(e, showStackTrace: true);

                    return false;
                }
            }
        }

        private static string GetAbsolutePath(string rootDirectory, string path)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                return UriUtility.GetAbsolutePath(rootDirectory, path);
            }

            return path;
        }

        private static IEnumerable<string> GetFallbackFolders(ProjectInstance projectInstance, IReadOnlyCollection<ProjectInstance> innerNodes, string startupDirectory, ISettings settings)
        {
            var restoreFallbackFolders = projectInstance.SplitPropertyValueOrNull("RestoreFallbackFolders");

            var currentFallbackFolders = RestoreSettingsUtils.GetValue(
                () => projectInstance.SplitPropertyValueOrNull("RestoreFallbackFoldersOverride")?.Select(i => GetAbsolutePath(startupDirectory, i)),
                () => MSBuildRestoreUtility.ContainsClearKeyword(restoreFallbackFolders) ? Array.Empty<string>() : null,
                () => restoreFallbackFolders?.Select(e => UriUtility.GetAbsolutePathFromFile(projectInstance.FullPath, e)),
                () => SettingsUtility.GetFallbackPackageFolders(settings).ToArray());

            // Append additional fallback folders after removing excluded folders
            var additionalProjectFallbackFolders = MSBuildRestoreUtility.AggregateSources(
                    values: innerNodes.SelectMany(i => MSBuildStringUtility.Split(i.GetPropertyValue("RestoreAdditionalProjectFallbackFolders"))),
                    excludeValues: innerNodes.SelectMany(i => MSBuildStringUtility.Split(i.GetPropertyValue("RestoreAdditionalProjectFallbackFoldersExcludes"))))
                .Select(i => UriUtility.GetAbsolutePathFromFile(projectInstance.FullPath, i));

            return currentFallbackFolders
                .Concat(additionalProjectFallbackFolders)
                .Where(i => !string.IsNullOrWhiteSpace(i));
        }

        private static List<FrameworkDependency> GetFrameworkReferences(ProjectInstance projectInstance)
        {
            var frameworkReferenceItems = projectInstance.GetItems("FrameworkReference").Distinct(ProjectItemInstanceEvaluatedIncludeComparer.Instance).ToList();

            var frameworkDependencies = new List<FrameworkDependency>(frameworkReferenceItems.Count);

            foreach (var frameworkReferenceItem in frameworkReferenceItems)
            {
                var privateAssets = MSBuildStringUtility.Split(frameworkReferenceItem.GetMetadataValue("PrivateAssets"));

                frameworkDependencies.Add(new FrameworkDependency(frameworkReferenceItem.EvaluatedInclude, FrameworkDependencyFlagsUtils.GetFlags(privateAssets)));
            }

            return frameworkDependencies;
        }

        private static LibraryIncludeFlags GetLibraryIncludeFlags(string value, LibraryIncludeFlags defaultValue)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return defaultValue;
            }

            string[] parts = MSBuildStringUtility.Split(value);

            return parts.Length > 0 ? LibraryIncludeFlagUtils.GetFlags(parts) : defaultValue;
        }

        private static List<string> GetOriginalTargetFrameworks(IReadOnlyCollection<ProjectInstance> projectInnerNodes)
        {
            var targetFrameworks = new List<string>(projectInnerNodes.Count);

            foreach (var projectInnerNode in projectInnerNodes)
            {
                targetFrameworks.Add(GetTargetFrameworkName(projectInnerNode));
            }

            return targetFrameworks;
        }

        private static IEnumerable<DownloadDependency> GetPackageDownloads(ProjectInstance projectInstance)
        {
            foreach (var projectItemInstance in projectInstance.GetItems("PackageDownload").Distinct(ProjectItemInstanceEvaluatedIncludeComparer.Instance))
            {
                string id = projectItemInstance.EvaluatedInclude;

                foreach (var version in MSBuildStringUtility.Split(projectItemInstance.GetMetadataValue("Version")))
                {
                    VersionRange versionRange = !string.IsNullOrWhiteSpace(version) ? VersionRange.Parse(version) : VersionRange.All;

                    if (!(versionRange.HasLowerAndUpperBounds && versionRange.MinVersion.Equals(versionRange.MaxVersion)))
                    {
                        throw new ArgumentException(string.Format(CultureInfo.CurrentCulture, "'{0}' is not an exact version like '[1.0.0]'. Only exact versions are allowed with PackageDownload.", versionRange.OriginalString));
                    }

                    yield return new DownloadDependency(id, versionRange);
                }
            }
        }

        private static List<LibraryDependency> GetPackageReferences(ProjectInstance projectInstance)
        {
            var packageReferenceItems = projectInstance.GetItems("PackageReference").Distinct(ProjectItemInstanceEvaluatedIncludeComparer.Instance).ToList();

            var libraryDependencies = new List<LibraryDependency>(packageReferenceItems.Count);

            foreach (var packageReferenceItem in packageReferenceItems)
            {
                string version = packageReferenceItem.GetMetadataValue("Version");

                libraryDependencies.Add(new LibraryDependency
                {
                    AutoReferenced = packageReferenceItem.IsMetadataTrue("IsImplicitlyDefined"),
                    GeneratePathProperty = packageReferenceItem.IsMetadataTrue("GeneratePathProperty"),
                    IncludeType = GetLibraryIncludeFlags(packageReferenceItem.GetMetadataValue("IncludeAssets"), LibraryIncludeFlags.All) & ~GetLibraryIncludeFlags(packageReferenceItem.GetMetadataValue("ExcludeAssets"), LibraryIncludeFlags.None),
                    LibraryRange = new LibraryRange(
                        packageReferenceItem.EvaluatedInclude,
                        !string.IsNullOrWhiteSpace(version) ? VersionRange.Parse(version) : VersionRange.All,
                        LibraryDependencyTarget.Package),
                    NoWarn = MSBuildStringUtility.GetNuGetLogCodes(packageReferenceItem.GetMetadataValue("NoWarn")).ToList(),
                    SuppressParent = GetLibraryIncludeFlags(packageReferenceItem.GetMetadataValue("PrivateAssets"), LibraryIncludeFlagUtils.DefaultSuppressParent)
                });
            }

            return libraryDependencies;
        }

        private static string GetPackagesPath(ProjectInstance projectInstance, string startupDirectory, ISettings settings)
        {
            return RestoreSettingsUtils.GetValue(
                () => GetAbsolutePath(startupDirectory, projectInstance.GetPropertyValueOrNull("RestorePackagesPathOverride")),
                () => UriUtility.GetAbsolutePath(projectInstance.Directory, projectInstance.GetPropertyValueOrNull("RestorePackagesPath")),
                () => SettingsUtility.GetGlobalPackagesFolder(settings));
        }

        private static PackageSpec GetPackageSpec(ProjectInstance projectInstance, IReadOnlyCollection<ProjectInstance> projectInnerNodes, ISettings settings, IList<string> configFilePaths)
        {
            var startupDirectory = projectInstance.GetPropertyValue("MSBuildStartupDirectory");

            var targetFrameworks = GetTargetFrameworks(projectInstance, projectInnerNodes);

            ProjectStyle projectStyle = GetProjectStyle(projectInstance, targetFrameworks);

            string projectName = GetProjectName(projectInstance);

            string outputPath = GetRestoreOutputPath(projectInstance);

            var packageSpec = new PackageSpec(targetFrameworks)
            {
                FilePath = projectInstance.FullPath,
                Name = projectName,
                RestoreMetadata = new ProjectRestoreMetadata
                {
                    CacheFilePath = NoOpRestoreUtilities.GetProjectCacheFilePath(outputPath),
                    ConfigFilePaths = configFilePaths,
                    Settings = settings,
                    CrossTargeting = IsCrossTargeting(projectInstance, projectStyle),
                    FallbackFolders = GetFallbackFolders(projectInstance, projectInnerNodes, startupDirectory, settings).ToList(),
                    OriginalTargetFrameworks = GetOriginalTargetFrameworks(projectInnerNodes),
                    OutputPath = outputPath,
                    PackagesPath = GetPackagesPath(projectInstance, startupDirectory, settings),
                    ProjectName = projectName,
                    ProjectPath = projectInstance.FullPath,
                    ProjectStyle = projectStyle,
                    ProjectUniqueName = projectInstance.FullPath,
                    ProjectWideWarningProperties = WarningProperties.GetWarningProperties(projectInstance.GetPropertyValue("TreatWarningsAsErrors"), projectInstance.GetPropertyValue("WarningsAsErrors"), projectInstance.GetPropertyValue("NoWarn")),
                    RestoreLockProperties = new RestoreLockProperties(projectInstance.GetPropertyValue("RestorePackagesWithLockFile"), projectInstance.GetPropertyValue("NuGetLockFilePath"), projectInstance.IsPropertyTrue("RestoreLockedMode")),
                    SkipContentFileWrite = GetSkipContentFileWrite(projectInstance),
                    Sources = GetSources(projectInstance, projectInnerNodes, startupDirectory, settings),
                    TargetFrameworks = GetProjectRestoreMetadataFrameworkInfos(targetFrameworks, projectInnerNodes),
                    ValidateRuntimeAssets = projectInstance.IsPropertyTrue("ValidateRuntimeAssets"),
                },
                RuntimeGraph = new RuntimeGraph(
                    MSBuildStringUtility.Split($"{projectInstance.GetPropertyValue("RuntimeIdentifiers")};{projectInstance.GetPropertyValue("RuntimeIdentifier")}")
                        .Concat(projectInnerNodes.SelectMany(i => MSBuildStringUtility.Split($"{i.GetPropertyValue("RuntimeIdentifiers")};{i.GetPropertyValue("RuntimeIdentifier")}")))
                        .Distinct(StringComparer.Ordinal)
                        .Select(rid => new RuntimeDescription(rid))
                        .ToList(),
                    MSBuildStringUtility.Split(projectInstance.GetPropertyValue("RuntimeSupports"))
                        .Distinct(StringComparer.Ordinal)
                        .Select(s => new CompatibilityProfile(s))
                        .ToList()
                    ),
                Version = GetProjectVersion(projectInstance)
            };

            return packageSpec;
        }

        private static List<ProjectGraphEntryPoint> GetProjectGraphEntryPoints(string entryProjectPath, Dictionary<string, string> globalProperties)
        {
            if (string.Equals(Path.GetExtension(entryProjectPath), ".sln", StringComparison.OrdinalIgnoreCase))
            {
                var solutionFile = SolutionFile.Parse(entryProjectPath);

                return solutionFile.ProjectsInOrder.Where(i => i.ProjectType == SolutionProjectType.KnownToBeMSBuildFormat).Select(i => new ProjectGraphEntryPoint(i.AbsolutePath, globalProperties)).ToList();
            }

            return new List<ProjectGraphEntryPoint>
            {
                new ProjectGraphEntryPoint(entryProjectPath, globalProperties),
            };
        }

        private static string GetProjectName(ProjectInstance projectInstance)
        {
            string packageId = projectInstance.GetPropertyValue("PackageId");

            if (!string.IsNullOrWhiteSpace(packageId))
            {
                return packageId;
            }

            string assemblyName = projectInstance.GetPropertyValue("AssemblyName");

            if (!string.IsNullOrWhiteSpace(assemblyName))
            {
                return assemblyName;
            }

            return projectInstance.GetPropertyValue("MSBuildProjectName");
        }

        private static List<ProjectRestoreReference> GetProjectReferences(ProjectInstance projectInstance)
        {
            var projectReferenceItems = projectInstance.GetItems("ProjectReference")
                .Where(i => i.IsMetadataTrue("ReferenceOutputAssembly", defaultValue: true))
                .Distinct(ProjectItemInstanceEvaluatedIncludeComparer.Instance)
                .ToList();

            var projectReferences = new List<ProjectRestoreReference>(projectReferenceItems.Count);

            foreach (var projectReferenceItem in projectReferenceItems)
            {
                string fullPath = projectReferenceItem.GetMetadataValue("FullPath");

                projectReferences.Add(new ProjectRestoreReference
                {
                    ExcludeAssets = GetLibraryIncludeFlags(projectReferenceItem.GetMetadataValue("ExcludeAssets"), LibraryIncludeFlags.None),
                    IncludeAssets = GetLibraryIncludeFlags(projectReferenceItem.GetMetadataValue("IncludeAssets"), LibraryIncludeFlags.All),
                    PrivateAssets = GetLibraryIncludeFlags(projectReferenceItem.GetMetadataValue("PrivateAssets"), LibraryIncludeFlagUtils.DefaultSuppressParent),
                    ProjectPath = fullPath,
                    ProjectUniqueName = fullPath
                });
            }

            return projectReferences;
        }

        private static List<ProjectRestoreMetadataFrameworkInfo> GetProjectRestoreMetadataFrameworkInfos(List<TargetFrameworkInformation> targetFrameworks, IReadOnlyCollection<ProjectInstance> projects)
        {
            var projectRestoreMetadataFrameworkInfos = new List<ProjectRestoreMetadataFrameworkInfo>(projects.Count);

            int i = 0;

            foreach (var projectInstance in projects)
            {
                var targetFramework = targetFrameworks[i++];

                projectRestoreMetadataFrameworkInfos.Add(new ProjectRestoreMetadataFrameworkInfo(targetFramework.FrameworkName)
                {
                    ProjectReferences = GetProjectReferences(projectInstance)
                });
            }

            return projectRestoreMetadataFrameworkInfos;
        }

        private static ProjectStyle GetProjectStyle(ProjectInstance projectInstance, List<TargetFrameworkInformation> targetFrameworkInfos)
        {
            string projectStyle = projectInstance.GetPropertyValue("RestoreProjectStyle");

            if (!string.IsNullOrWhiteSpace(projectStyle) && Enum.TryParse(projectStyle, out ProjectStyle style))
            {
                return style;
            }

            if (targetFrameworkInfos.Any(i => i.Dependencies.Any()))
            {
                return ProjectStyle.PackageReference;
            }

            if (File.Exists(Path.Combine(projectInstance.Directory, "packages.config")))
            {
                return ProjectStyle.PackagesConfig;
            }

            return ProjectStyle.Unknown;
        }

        private static NuGetVersion GetProjectVersion(ProjectInstance projectInstance)
        {
            string version = projectInstance.GetPropertyValue("PackageVersion");

            if (string.IsNullOrWhiteSpace(version))
            {
                version = projectInstance.GetPropertyValue("Version");

                if (string.IsNullOrWhiteSpace(version))
                {
                    return DefaultNuGetVersion;
                }
            }

            return NuGetVersion.Parse(version);
        }

        private static string GetRestoreOutputPath(ProjectInstance projectInstance)
        {
            string outputPath = projectInstance.GetPropertyValue("RestoreOutputPath");

            if (string.IsNullOrWhiteSpace(outputPath))
            {
                outputPath = projectInstance.GetPropertyValue("MSBuildProjectExtensionsPath");
            }

            return Path.GetFullPath(Path.Combine(projectInstance.Directory, outputPath));
        }

        private static bool GetSkipContentFileWrite(ProjectInstance projectInstance)
        {
            return string.IsNullOrWhiteSpace(projectInstance.GetPropertyValue("TargetFramework")) && string.IsNullOrWhiteSpace(projectInstance.GetPropertyValue("TargetFrameworks"));
        }

        private static List<PackageSource> GetSources(ProjectInstance projectInstance, IReadOnlyCollection<ProjectInstance> innerNodes, string startupDirectory, ISettings settings)
        {
            var restoreSources = projectInstance.SplitPropertyValueOrNull("RestoreSources");

            var currentSources = RestoreSettingsUtils.GetValue(
                () => projectInstance.SplitPropertyValueOrNull("RestoreSourcesOverride")?.Select(MSBuildRestoreUtility.FixSourcePath).Select(e => GetAbsolutePath(startupDirectory, e)),
                () => MSBuildRestoreUtility.ContainsClearKeyword(restoreSources) ? Enumerable.Empty<string>() : null,
                () => restoreSources?.Select(MSBuildRestoreUtility.FixSourcePath).Select(e => UriUtility.GetAbsolutePathFromFile(projectInstance.FullPath, e)),
                () => (PackageSourceProvider.LoadPackageSources(settings)).Where(e => e.IsEnabled).Select(e => e.Source));

            var additionalProjectSources = MSBuildRestoreUtility.AggregateSources(
                    values: innerNodes.SelectMany(i => MSBuildStringUtility.Split(i.GetPropertyValue("RestoreAdditionalProjectSources"))),
                    excludeValues: Enumerable.Empty<string>())
                .Select(MSBuildRestoreUtility.FixSourcePath)
                .Select(i => UriUtility.GetAbsolutePathFromFile(projectInstance.FullPath, i))
                .ToArray();

            return currentSources
                .Concat(additionalProjectSources)
                .Where(i => !string.IsNullOrWhiteSpace(i))
                .Select(i => new PackageSource(i)).ToList();
        }

        private static TargetFrameworkInformation GetTargetFrameworkInformation(string name, ProjectInstance projectInstance)
        {
            string runtimeIdentifierGraphPath = projectInstance.GetPropertyValue(nameof(TargetFrameworkInformation.RuntimeIdentifierGraphPath));

            var targetFrameworkInformation = new TargetFrameworkInformation
            {
                FrameworkName = NuGetFramework.Parse(name),
                RuntimeIdentifierGraphPath = string.IsNullOrWhiteSpace(runtimeIdentifierGraphPath) ? null : runtimeIdentifierGraphPath
            };

            var packageTargetFallback = MSBuildStringUtility.Split(projectInstance.GetPropertyValue("PackageTargetFallback")).Select(NuGetFramework.Parse).ToList();

            var assetTargetFallback = MSBuildStringUtility.Split(projectInstance.GetPropertyValue("AssetTargetFallback")).Select(NuGetFramework.Parse).ToList();

            AssetTargetFallbackUtility.EnsureValidFallback(packageTargetFallback, assetTargetFallback, projectInstance.FullPath);

            AssetTargetFallbackUtility.ApplyFramework(targetFrameworkInformation, packageTargetFallback, assetTargetFallback);

            targetFrameworkInformation.Dependencies.AddRange(GetPackageReferences(projectInstance));

            targetFrameworkInformation.DownloadDependencies.AddRange(GetPackageDownloads(projectInstance));

            targetFrameworkInformation.FrameworkReferences.AddRange(GetFrameworkReferences(projectInstance));

            return targetFrameworkInformation;
        }

        private static string GetTargetFrameworkName(ProjectInstance projectInstance)
        {
            if (projectInstance.GlobalProperties.TryGetValue("TargetFramework", out string targetFrameworkName))
            {
                return targetFrameworkName;
            }

            targetFrameworkName = projectInstance.GetPropertyValue("TargetFramework");

            if (!string.IsNullOrWhiteSpace(targetFrameworkName))
            {
                return targetFrameworkName;
            }

            targetFrameworkName = MSBuildProjectFrameworkUtility.GetProjectFrameworkStrings(
                projectInstance.FullPath,
                projectInstance.GetPropertyValue("TargetFrameworks"),
                projectInstance.GetPropertyValue("TargetFramework"),
                projectInstance.GetPropertyValue("TargetFrameworkMoniker"),
                projectInstance.GetPropertyValue("TargetPlatformIdentifier"),
                projectInstance.GetPropertyValue("TargetPlatformVersion"),
                projectInstance.GetPropertyValue("TargetPlatformMinVersion")).FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(targetFrameworkName))
            {
                return targetFrameworkName;
            }

            return null;
        }

        private static List<TargetFrameworkInformation> GetTargetFrameworks(ProjectInstance projectInstance, IReadOnlyCollection<ProjectInstance> projectInnerNodes)
        {
            var targetFrameworkInfos = new List<TargetFrameworkInformation>(projectInnerNodes.Count);

            if (projectInnerNodes.Count == 1)
            {
                string targetFrameworkName = GetTargetFrameworkName(projectInstance);

                if (!string.IsNullOrWhiteSpace(targetFrameworkName))
                {
                    targetFrameworkInfos.Add(GetTargetFrameworkInformation(targetFrameworkName, projectInstance));
                }
            }
            else
            {
                foreach (var innerNode in projectInnerNodes)
                {
                    string targetFrameworkName = GetTargetFrameworkName(innerNode);

                    targetFrameworkInfos.Add(GetTargetFrameworkInformation(targetFrameworkName, innerNode));
                }
            }

            return targetFrameworkInfos;
        }

        private static bool IsCrossTargeting(ProjectInstance projectInstance, ProjectStyle projectStyle)
        {
            return (projectStyle == ProjectStyle.PackageReference || projectStyle == ProjectStyle.DotnetToolReference) && !string.IsNullOrWhiteSpace(projectInstance.GetPropertyValue("TargetFrameworks"));
        }

        private DependencyGraphSpec GetDependencyGraphSpec(ConsoleLoggingQueue loggingQueue)
        {
            try
            {
                loggingQueue.TaskLoggingHelper.LogMessage(MessageImportance.High, "Determining projects to restore...");

                var entryProjects = GetProjectGraphEntryPoints(_entryProjectPath, _globalProperties);

                var projects = LoadProjects(entryProjects, loggingQueue)?.ToArray();

                if (projects == null || projects.Length == 0)
                {
                    return null;
                }

                var sw = Stopwatch.StartNew();

                var dependencyGraphSpec = new DependencyGraphSpec(isReadOnly: true);

                var firstProject = projects.First().OuterNode;

                var settings = RestoreSettingsUtils.ReadSettings(
                    firstProject.GetPropertyValue("RestoreSolutionDirectory"),
                    firstProject.GetPropertyValueOrNull("RestoreRootConfigDirectory") ?? firstProject.Directory,
                    GetAbsolutePath(firstProject.GetPropertyValue("MSBuildStartupDirectory"), firstProject.GetPropertyValueOrNull("RestoreConfigFile")),
                    MachineWideSettingsLazy);

                var configFilePaths = settings.GetConfigFilePaths();

                Parallel.ForEach(projects, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, project =>
                {
                    var packageSpec = GetPackageSpec(project.OuterNode, project.InnerNodes, settings, configFilePaths);

                    lock (dependencyGraphSpec)
                    {
                        dependencyGraphSpec.AddProject(packageSpec);
                    }
                });

                foreach (var entryPoint in entryProjects)
                {
                    // TODO: Filter out projects that don't support restore
                    dependencyGraphSpec.AddRestore(entryPoint.ProjectFile);
                }

                sw.Stop();

                loggingQueue.TaskLoggingHelper.LogMessage(MessageImportance.Normal, "Created DependencyGraphSpec in {0:D2}ms.", sw.ElapsedMilliseconds);

                return dependencyGraphSpec;
            }
            catch (Exception e)
            {
                loggingQueue.TaskLoggingHelper.LogErrorFromException(e, showStackTrace: true);
            }

            return null;
        }

        private bool IsPropertyTrue(string name, Dictionary<string, string> properties)
        {
            return properties.TryGetValue(name, out string value) && StringComparer.OrdinalIgnoreCase.Equals(value, "true");
        }

        private ICollection<OuterInnerNodes> LoadProjects(IEnumerable<ProjectGraphEntryPoint> entryProjects, ConsoleLoggingQueue loggingQueue)
        {
            var projects = new ConcurrentDictionary<string, OuterInnerNodes>(StringComparer.OrdinalIgnoreCase);

            var projectCollection = new ProjectCollection(
                globalProperties: null,
                loggers: null,
                remoteLoggers: null,
                toolsetDefinitionLocations: ToolsetDefinitionLocations.Default,
                maxNodeCount: 1,
                onlyLogCriticalEvents: !Debug,
                loadProjectsReadOnly: true);

            var failedBuildSubmissions = new ConcurrentBag<BuildSubmission>();

            try
            {
                var sw = Stopwatch.StartNew();

                var evaluationContext = EvaluationContext.Create(EvaluationContext.SharingPolicy.Shared);

                ProjectGraph projectGraph;

                int buildCount = 0;

                try
                {
                    var buildParameters = new BuildParameters(projectCollection)
                    {
                        Loggers = new List<ILogger>
                        {
                            loggingQueue
                        },
                        OnlyLogCriticalEvents = false
                    };

                    BuildManager.DefaultBuildManager.BeginBuild(buildParameters);

                    projectGraph = new ProjectGraph(entryProjects, projectCollection, (path, properties, collection) =>
                    {
                        var projectOptions = new ProjectOptions
                        {
                            EvaluationContext = evaluationContext,
                            GlobalProperties = properties,
                            LoadSettings = ProjectLoadSettings.IgnoreEmptyImports | ProjectLoadSettings.IgnoreInvalidImports | ProjectLoadSettings.DoNotEvaluateElementsWithFalseCondition | ProjectLoadSettings.IgnoreMissingImports,
                            ProjectCollection = collection,
                        };

                        var project = Project.FromFile(path, projectOptions);

                        var projectInstance = project.CreateProjectInstance(ProjectInstanceSettings.None, evaluationContext);

                        if (properties.ContainsKey("TargetFramework") || string.IsNullOrWhiteSpace(projectInstance.GetPropertyValue("TargetFrameworks")))
                        {
                            BuildManager.DefaultBuildManager
                                .PendBuildRequest(
                                    new BuildRequestData(
                                        projectInstance,
                                        TargetsToBuild,
                                        hostServices: null,
                                        BuildRequestDataFlags.SkipNonexistentTargets))
                                .ExecuteAsync(
                                    callback: buildSubmission =>
                                    {
                                        if (buildSubmission.BuildResult.OverallResult == BuildResultCode.Failure)
                                        {
                                            failedBuildSubmissions.Add(buildSubmission);
                                        }
                                    },
                                    context: null);

                            Interlocked.Increment(ref buildCount);
                        }

                        projects.AddOrUpdate(
                            path,
                            key => new OuterInnerNodes(projectInstance),
                            (s, nodes) => nodes.Add(projectInstance));

                        return projectInstance;
                    });
                }
                finally
                {
                    BuildManager.DefaultBuildManager.EndBuild();
                }

                sw.Stop();

                loggingQueue.TaskLoggingHelper.LogMessage(MessageImportance.Normal, "Evaluated {0} project(s) in {1:D2}ms ({2} builds, {3} failures).", projectGraph.ProjectNodes.Count, sw.ElapsedMilliseconds, buildCount, failedBuildSubmissions.Count);

                if (failedBuildSubmissions.Any())
                {
                    return null;
                }
            }
            catch (Exception e)
            {
                loggingQueue.TaskLoggingHelper.LogErrorFromException(e, showStackTrace: true);

                return null;
            }
            finally
            {
                projectCollection.Dispose();
            }

            return projects.Values;
        }

        internal class OuterInnerNodes : ConcurrentBag<ProjectInstance>
        {
            public OuterInnerNodes(ProjectInstance outerNode)
            {
                OuterNode = outerNode;
            }

            public IReadOnlyCollection<ProjectInstance> InnerNodes => Count == 0 ? (IReadOnlyCollection<ProjectInstance>)new[] { OuterNode } : this;

            public ProjectInstance OuterNode { get; }

            public new OuterInnerNodes Add(ProjectInstance projectInstance)
            {
                base.Add(projectInstance);

                return this;
            }
        }

        internal sealed class ProjectItemInstanceEvaluatedIncludeComparer : IEqualityComparer<ProjectItemInstance>
        {
            public static readonly ProjectItemInstanceEvaluatedIncludeComparer Instance = new ProjectItemInstanceEvaluatedIncludeComparer();

            public bool Equals(ProjectItemInstance x, ProjectItemInstance y)
            {
                return string.Equals(x?.EvaluatedInclude, y?.EvaluatedInclude, StringComparison.OrdinalIgnoreCase);
            }

            public int GetHashCode(ProjectItemInstance obj) => StringComparer.OrdinalIgnoreCase.GetHashCode(obj.EvaluatedInclude);
        }
    }
}
