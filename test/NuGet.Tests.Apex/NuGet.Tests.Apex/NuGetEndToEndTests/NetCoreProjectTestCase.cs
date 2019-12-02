using System.Collections.Generic;
using System.IO;
using Microsoft.Test.Apex.VisualStudio.Solution;
using NuGet.StaFact;
using Xunit;
using Xunit.Abstractions;

namespace NuGet.Tests.Apex
{
    public class NetCoreProjectTestCase : SharedVisualStudioHostTestClass, IClassFixture<VisualStudioHostFixtureFactory>
    {
        public NetCoreProjectTestCase(VisualStudioHostFixtureFactory visualStudioHostFixtureFactory, ITestOutputHelper output)
            : base(visualStudioHostFixtureFactory, output)
        {
        }

        // basic create for .net core template
        [NuGetWpfTheory]
        [MemberData(nameof(GetNetCoreTemplates))]
        public void CreateNetCoreProject_RestoresNewProject(ProjectTemplate projectTemplate)
        {
            // Arrange
            EnsureVisualStudioHost();

            using (var testContext = new ApexTestContext(VisualStudio, projectTemplate, XunitLogger))
            {
                CopyNetStandardLibraryToSource(testContext.UserPackagesFolder);

                VisualStudio.AssertNoErrors();
            }
        }

        // basic create for .net core template
        [NuGetWpfTheory]
        [MemberData(nameof(GetNetCoreTemplates))]
        public void CreateNetCoreProject_AddProjectReference(ProjectTemplate projectTemplate)
        {
            // Arrange
            EnsureVisualStudioHost();

            using (var testContext = new ApexTestContext(VisualStudio, projectTemplate, XunitLogger))
            {
                var project2 = testContext.SolutionService.AddProject(ProjectLanguage.CSharp, projectTemplate, ProjectTargetFramework.V46, "TestProject2");
                project2.Build();

                testContext.Project.References.Dte.AddProjectReference(project2);
                testContext.SolutionService.SaveAll();

                testContext.SolutionService.Build();
                testContext.NuGetApexTestService.WaitForAutoRestore();

                CommonUtility.AssertPackageInAssetsFile(VisualStudio, testContext.Project, "TestProject2", "1.0.0", XunitLogger);
            }
        }

        // There  is a bug with VS or Apex where NetCoreConsoleApp and NetCoreClassLib create netcore 2.1 projects that are not supported by the sdk
        // Commenting out any NetCoreConsoleApp or NetCoreClassLib template and swapping it for NetStandardClassLib as both are package ref.

        public static IEnumerable<object[]> GetNetCoreTemplates()
        {
            yield return new object[] { ProjectTemplate.NetStandardClassLib };
        }

        private bool CopyNetStandardLibraryToSource(string globalPackageDir)
        {
            // Arrange
            EnsureVisualStudioHost();

            using (var testContext = new ApexTestContext(VisualStudio, ProjectTemplate.NetStandardClassLib, XunitLogger, noAutoRestore: false, addNuGetOrgFeed: true))
            {
                string netstandardLib = Path.Combine(testContext.UserPackagesFolder, "netstandard.library");

                //if netstandard.library doesn't exist, it means NuGetFallBackFolder works. Then no need to copy to global.package folder
                if (!Directory.Exists(netstandardLib))
                {
                    return false;
                }

                //if netstandard.library exists, then we need to copy to global.package folder
                foreach (var directory in Directory.GetDirectories(netstandardLib, "*", SearchOption.AllDirectories))
                {
                    var destDir = globalPackageDir + directory.Substring(netstandardLib.Length);
                    if (!Directory.Exists(destDir))
                    {
                        Directory.CreateDirectory(destDir);
                    }
                }

                //Copy files recursively to destination directories
                foreach (var fileName in Directory.GetFiles(netstandardLib, "*", SearchOption.AllDirectories))
                {
                    var destFileName = globalPackageDir + fileName.Substring(netstandardLib.Length);
                    File.Copy(fileName, destFileName);
                }
                return true;
            }
        }
    }
}
