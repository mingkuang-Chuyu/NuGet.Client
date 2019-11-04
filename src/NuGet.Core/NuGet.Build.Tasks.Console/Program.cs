// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
#if IS_CORECLR
using System.Runtime.InteropServices;
#endif
using System.Threading.Tasks;
using NuGet.Common;

namespace NuGet.Build.Tasks.Console
{
    internal static class Program
    {
        private static readonly string[] DefaultWildcardMatchesToSkip =
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

            // file spec ends with text\optional text*extension
            @"^[^\*]*\\[^\*|\\]*\*[^\*|\\]+$",

            // just a wildcard file name or extension
            @"^\*[^\\|\*]+$",
        };

        private static readonly char[] EqualSign = { '=' };

        public static async Task<int> Main(string[] args)
        {
            var properties = ParseProperties(args[0]);
            var msbuildExePath = new FileInfo(args[1]);
            var entryProjectPath = args[2];
            var globalProperties = ParseProperties(args[3]);
            var debug = globalProperties.TryGetValue("NuGetDebug", out var debugValue) && string.Equals(debugValue, "true", StringComparison.OrdinalIgnoreCase);

            if (debug)
            {
                Debugger.Launch();
            }

            Environment.SetEnvironmentVariable("MSBUILD_EXE_PATH", msbuildExePath.FullName);
            Environment.SetEnvironmentVariable("MSBuildCacheFileEnumerations", "1");
            Environment.SetEnvironmentVariable("MSBuildLoadAllFilesAsReadonly", "1");
            Environment.SetEnvironmentVariable("MSBuildSkipEagerWildCardEvaluationRegexes", string.Join(";", DefaultWildcardMatchesToSkip));

            string msbuildDirectory = msbuildExePath.DirectoryName;

            AppDomain.CurrentDomain.AssemblyResolve += (sender, resolveArgs) =>
            {
                var assemblyName = new AssemblyName(resolveArgs.Name);

                var path = Path.Combine(msbuildDirectory, $"{assemblyName.Name}.dll");

                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            };

            var dependencyGraphSpecGenerator = new DependencyGraphSpecGenerator(entryProjectPath, globalProperties)
            {
                Debug = debug
            };

            return await dependencyGraphSpecGenerator.RestoreAsync(properties) ? 0 : 1;
        }

        private static Dictionary<string, string> ParseProperties(string value)
        {
            var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var pairString in MSBuildStringUtility.Split(value))
            {
                var pair = pairString.Split(EqualSign, 2);

                if (pair.Length == 2)
                {
                    properties[pair[0]] = pair[1];
                }
            }

            return properties;
        }
    }
}
