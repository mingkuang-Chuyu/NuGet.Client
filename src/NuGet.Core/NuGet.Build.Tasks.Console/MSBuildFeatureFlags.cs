// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;

namespace NuGet.Build.Tasks.Console
{
    /// <summary>
    /// Represents a class for enabling MSBuild feature flags.
    /// </summary>
    internal static class MSBuildFeatureFlags
    {
        /// <summary>
        /// Represents the default set of regular expressions that MSBuild uses in order to skip the evaluation of item groups.
        /// This can be handy if you don't want evaluations to be slowed by a run away wildcard like:
        ///   $(MyProperty)\**
        ///
        /// In some cases, $(MyProperty) won't be set and would evaluation to D:\** which would cause MSBuild to evaluation every
        /// file on disk.
        /// </summary>
        public static readonly string[] DefaultWildcardMatchesToSkip =
        {
            // this is an ends with **\* without any *s.
            @"^[^\*]*\*\*\\\*$",

            // file spec ends with **\*.* without any other *s
            @"^[^\*]*\*\*\\\*\.\*$",

            // file spec ends with ** without any other *s
            @"^[^\*]*\*\*$",

            // file spec ends with * with no other wildcards
            @"^[^\*]*\*$",

            // file spec has a **\* but has no other directories after the **\* and no other wildcards.
            @"^[^\*]*\*\*\\\*[^\\|\*]*$",

            // file spec has *.* but no other star in it
            @"^[^\*]*\\\*\.\*$",
        };

        /// <summary>
        /// Gets or sets a value indicating if wildcard expansions for the entire process should be cached.
        /// </summary>
        /// <remarks>
        /// More info here: https://github.com/microsoft/msbuild/blob/master/src/Shared/Traits.cs#L55
        /// </remarks>
        public static bool EnableCacheFileEnumerations
        {
            get => string.Equals(Environment.GetEnvironmentVariable("MSBuildCacheFileEnumerations"), "1");
            set => Environment.SetEnvironmentVariable("MSBuildCacheFileEnumerations", value ? "1" : null);
        }

        /// <summary>
        /// Gets or sets a value indicating if all projects should be treated as read-only which enables an optimized way of
        /// reading them.
        /// </summary>
        /// <remarks>
        /// More info here: https://github.com/microsoft/msbuild/blob/master/src/Build/ElementLocation/XmlDocumentWithLocation.cs#L392
        /// </remarks>
        public static bool LoadAllFilesAsReadonly
        {
            get => string.Equals(Environment.GetEnvironmentVariable("MSBuildLoadAllFilesAsReadonly"), "1");
            set => Environment.SetEnvironmentVariable("MSBuildLoadAllFilesAsReadonly", value ? "1" : null);
        }

        /// <summary>
        /// Gets or sets the full path to MSBuild that should be used to evaluate projects.
        /// </summary>
        /// <remarks>
        /// MSBuild is not installed globally anymore as of version 15.0.  Processes doing evaluations must set this environment variable for the toolsets
        /// to be found by MSBuild (stuff like $(MSBuildExtensionsPath).
        /// More info here: https://github.com/microsoft/msbuild/blob/master/src/Shared/BuildEnvironmentHelper.cs#L125
        /// </remarks>
        public static string MSBuildExePath
        {
            get => Environment.GetEnvironmentVariable("MSBUILD_EXE_PATH");
            set => Environment.SetEnvironmentVariable("MSBUILD_EXE_PATH", value);
        }

        /// <summary>
        /// Gets or sets a value indicating if eager wildcards like **\* should have their evaluations skipped.
        /// </summary>
        /// <remarks>
        /// More info here: https://github.com/microsoft/msbuild/blob/master/src/Build/Utilities/EngineFileUtilities.cs#L221
        /// </remarks>
        public static bool SkipEagerWildcardEvaluations
        {
            get => !string.Equals(Environment.GetEnvironmentVariable("MSBuildSkipEagerWildCardEvaluationRegexes"), null);
            set => Environment.SetEnvironmentVariable("MSBuildSkipEagerWildCardEvaluationRegexes", string.Join(";", DefaultWildcardMatchesToSkip));
        }
    }
}
