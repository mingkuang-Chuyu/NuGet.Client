using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Build.Execution;
using NuGet.Common;

namespace NuGet.Build.Tasks.Console
{
    internal static class ExtensionMethods
    {
        public static string GetPropertyValueOrNull(this ProjectInstance projectInstance, string name)
        {
            string value = projectInstance.GetPropertyValue(name);

            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        public static string[] SplitPropertyValueOrNull(this ProjectInstance projectInstance, string name)
        {
            string value = projectInstance.GetPropertyValue(name);

            return string.IsNullOrWhiteSpace(value) ? null : MSBuildStringUtility.Split(value);
        }

        public static bool IsMetadataTrue(this ProjectItemInstance item, string name, bool defaultValue = false)
        {
            return IsValueTrue(item.GetMetadataValue(name), defaultValue);
        }

        public static bool IsPropertyTrue(this ProjectInstance projectInstance, string name, bool defaultValue = false)
        {
            return IsValueTrue(projectInstance.GetPropertyValue(name), defaultValue);
        }

        private static bool IsValueTrue(string value, bool defaultValue = false)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return defaultValue;
            }

            return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }
    }
}
