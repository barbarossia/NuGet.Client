// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Build.Evaluation;
using Microsoft.VisualStudio.Setup.Configuration;
using NuGet.Commands;
using NuGet.Common;

namespace NuGet.CommandLine
{
    public static class MsBuildUtility
    {
        internal const int MsBuildWaitTime = 2 * 60 * 1000; // 2 minutes in milliseconds

        private const string GetProjectReferencesTarget =
            "NuGet.CommandLine.GetProjectsReferencingProjectJsonFiles.targets";

        private const string GetProjectReferencesEntryPointTarget =
            "NuGet.CommandLine.GetProjectsReferencingProjectJsonFilesEntryPoint.targets";

        private static readonly HashSet<string> _msbuildExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".csproj",
            ".vbproj",
            ".fsproj",
            ".xproj",
            ".nuproj"
        };

        public static bool IsMsBuildBasedProject(string projectFullPath)
        {
            return _msbuildExtensions.Contains(Path.GetExtension(projectFullPath));
        }

        public static int Build(string msbuildDirectory,
                                    string args)
        {
            string msbuildPath = Path.Combine(msbuildDirectory, "msbuild.exe");

            if (!File.Exists(msbuildPath))
            {
                throw new CommandLineException(
                    string.Format(
                        CultureInfo.CurrentCulture,
                        LocalizedResourceManager.GetString(nameof(NuGetResources.MsBuildDoesNotExistAtPath)),
                        msbuildPath));
            }

            var processStartInfo = new ProcessStartInfo
            {
                UseShellExecute = false,
                FileName = msbuildPath,
                Arguments = args,
                RedirectStandardOutput = false,
                RedirectStandardError = false
            };

            using (var process = Process.Start(processStartInfo))
            {
                process.WaitForExit();

                return process.ExitCode;
            }
        }

        /// <summary>
        /// Returns the closure of project references for projects specified in <paramref name="projectPaths"/>.
        /// </summary>
        public static MSBuildProjectReferenceProvider GetProjectReferences(
            string msbuildDirectory,
            string[] projectPaths,
            int timeOut)
        {
            string msbuildPath = Path.Combine(msbuildDirectory, "msbuild.exe");

            if (!File.Exists(msbuildPath))
            {
                throw new CommandLineException(
                    string.Format(
                        CultureInfo.CurrentCulture,
                        LocalizedResourceManager.GetString(nameof(NuGetResources.MsBuildDoesNotExistAtPath)),
                        msbuildPath));
            }

            var nugetExePath = Assembly.GetEntryAssembly().Location;

            using (var entryPointTargetPath = new TempFile(".targets"))
            using (var customAfterBuildTargetPath = new TempFile(".targets"))
            using (var resultsPath = new TempFile(".result"))
            {
                ExtractResource(GetProjectReferencesEntryPointTarget, entryPointTargetPath);
                ExtractResource(GetProjectReferencesTarget, customAfterBuildTargetPath);

                var argumentBuilder = new StringBuilder(
                    "/t:NuGet_GetProjectsReferencingProjectJson " +
                    "/nologo /nr:false /v:q " +
                    "/p:BuildProjectReferences=false");

                argumentBuilder.Append(" /p:NuGetTasksAssemblyPath=");
                AppendQuoted(argumentBuilder, nugetExePath);

                argumentBuilder.Append(" /p:NuGetCustomAfterBuildTargetPath=");
                AppendQuoted(argumentBuilder, customAfterBuildTargetPath);

                argumentBuilder.Append(" /p:ResultsFile=");
                AppendQuoted(argumentBuilder, resultsPath);

                argumentBuilder.Append(" /p:NuGet_ProjectReferenceToResolve=\"");
                for (var i = 0; i < projectPaths.Length; i++)
                {
                    argumentBuilder.Append(projectPaths[i])
                        .Append(";");
                }

                argumentBuilder.Append("\" ");
                AppendQuoted(argumentBuilder, entryPointTargetPath);

                var processStartInfo = new ProcessStartInfo
                {
                    UseShellExecute = false,
                    FileName = msbuildPath,
                    Arguments = argumentBuilder.ToString(),
                    RedirectStandardError = true
                };

                using (var process = Process.Start(processStartInfo))
                {
                    var finished = process.WaitForExit(timeOut);

                    if (!finished)
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch (Exception ex)
                        {
                            throw new CommandLineException(
                                LocalizedResourceManager.GetString(nameof(NuGetResources.Error_CannotKillMsBuild)) + " : " +
                                ex.Message,
                                ex);
                        }

                        throw new CommandLineException(
                            LocalizedResourceManager.GetString(nameof(NuGetResources.Error_MsBuildTimedOut)));
                    }

                    if (process.ExitCode != 0)
                    {
                        throw new CommandLineException(process.StandardError.ReadToEnd());
                    }
                }

                var lines = new string[0];

                if (File.Exists(resultsPath))
                {
                    lines = File.ReadAllLines(resultsPath);
                }

                return new MSBuildProjectReferenceProvider(lines);
            }
        }

        /// <summary>
        /// Gets the list of project files in a solution, using XBuild's solution parser.
        /// </summary>
        /// <param name="solutionFile">The solution file. </param>
        /// <returns>The list of project files (in full path) in the solution.</returns>
        public static IEnumerable<string> GetAllProjectFileNamesWithXBuild(string solutionFile)
        {
            try
            {
                var assembly = Assembly.Load(
                    "Microsoft.Build.Engine, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a");
                var solutionParserType = assembly.GetType("Mono.XBuild.CommandLine.SolutionParser");
                if (solutionParserType == null)
                {
                    throw new CommandLineException(
                        LocalizedResourceManager.GetString("Error_CannotGetXBuildSolutionParser"));
                }

                var getAllProjectFileNamesMethod = solutionParserType.GetMethod(
                    "GetAllProjectFileNames",
                    new Type[] { typeof(string) });
                if (getAllProjectFileNamesMethod == null)
                {
                    throw new CommandLineException(
                        LocalizedResourceManager.GetString("Error_CannotGetGetAllProjectFileNamesMethod"));
                }

                var names = (IEnumerable<string>)getAllProjectFileNamesMethod.Invoke(
                    null, new object[] { solutionFile });
                return names;
            }
            catch (Exception ex)
            {
                var message = string.Format(
                    CultureInfo.CurrentCulture,
                    LocalizedResourceManager.GetString("Error_SolutionFileParseError"),
                    solutionFile,
                    ex.Message);

                throw new CommandLineException(message);
            }
        }

        /// <summary>
        /// Gets the list of project files in a solution, using MSBuild API.
        /// </summary>
        /// <param name="solutionFile">The solution file. </param>
        /// <param name="msbuildPath">The directory that contains msbuild.</param>
        /// <returns>The list of project files (in full path) in the solution.</returns>
        public static IEnumerable<string> GetAllProjectFileNamesWithMsbuild(
            string solutionFile,
            string msbuildPath)
        {
            try
            {
                var solution = new Solution(solutionFile, msbuildPath);
                var solutionDirectory = Path.GetDirectoryName(solutionFile);
                return solution.Projects.Where(project => !project.IsSolutionFolder)
                    .Select(project => Path.Combine(solutionDirectory, project.RelativePath));
            }
            catch (Exception ex)
            {
                var message = string.Format(
                    CultureInfo.CurrentCulture,
                    LocalizedResourceManager.GetString("Error_SolutionFileParseError"),
                    solutionFile,
                    ex.Message);

                throw new CommandLineException(message);
            }
        }

        public static IEnumerable<string> GetAllProjectFileNames(
            string solutionFile,
            string msbuildPath)
        {
            if (EnvironmentUtility.IsMonoRuntime)
            {
                return GetAllProjectFileNamesWithXBuild(solutionFile);
            }
            else
            {
                return GetAllProjectFileNamesWithMsbuild(solutionFile, msbuildPath);
            }
        }

        /// <summary>
        /// Gets the version of MSBuild in PATH.
        /// </summary>
        /// <returns>The version of MSBuild in PATH. Returns null if MSBuild does not exist in PATH.</returns>
        private static Version GetMSBuildVersionInPath()
        {
            // run msbuild to get the version
            var processStartInfo = new ProcessStartInfo
            {
                UseShellExecute = false,
                FileName = "msbuild.exe",
                Arguments = "/version /nologo",
                RedirectStandardOutput = true
            };

            try
            {
                using (var process = Process.Start(processStartInfo))
                {
                    process.WaitForExit(MsBuildWaitTime);

                    if (process.ExitCode == 0)
                    {
                        var output = process.StandardOutput.ReadToEnd();

                        // The output of msbuid /version /nologo with MSBuild 12 & 14 is something like:
                        // 14.0.23107.0
                        var lines = output.Split(
                            new[] { Environment.NewLine },
                            StringSplitOptions.RemoveEmptyEntries);

                        var versionString = lines.LastOrDefault(
                            line => !string.IsNullOrWhiteSpace(line));

                        Version version;
                        if (Version.TryParse(versionString, out version))
                        {
                            return version;
                        }
                    }
                }
            }
            catch
            {
                // ignore errors
            }

            return null;
        }

        /// <summary>
        /// Gets the msbuild toolset that matches the given <paramref name="msbuildVersion"/>.
        /// </summary>
        /// <param name="msbuildVersion">The msbuild version. Can be null.</param>
        /// <param name="installedToolsets">List of installed toolsets,
        /// ordered by ToolsVersion, from highest to lowest.</param>
        /// <returns>The matching toolset.</returns>
        /// <remarks>This method is not intended to be called directly. It's marked public so that it
        /// can be called by unit tests.</remarks>
        public static Toolset SelectMsbuildToolset(
            Version msbuildVersion,
            IEnumerable<Toolset> installedToolsets)
        {
            Toolset selectedToolset;
            if (msbuildVersion == null)
            {
                // MSBuild does not exist in PATH. In this case, the highest installed version is used
                selectedToolset = installedToolsets.FirstOrDefault();
            }
            else
            {
                // Search by major & minor version
                selectedToolset = installedToolsets.FirstOrDefault(
                    toolset =>
                    {
                        var v = SafeParseVersion(toolset.ToolsVersion);
                        return v.Major == msbuildVersion.Major && v.Minor == v.Minor;
                    });

                if (selectedToolset == null)
                {
                    // no match found. Now search by major only
                    selectedToolset = installedToolsets.FirstOrDefault(
                        toolset =>
                        {
                            var v = SafeParseVersion(toolset.ToolsVersion);
                            return v.Major == msbuildVersion.Major;
                        });
                }

                if (selectedToolset == null)
                {
                    // still no match. Use the highest installed version in this case
                    selectedToolset = installedToolsets.FirstOrDefault();
                }
            }

            if (selectedToolset == null)
            {
                throw new CommandLineException(
                    LocalizedResourceManager.GetString(
                            nameof(NuGetResources.Error_MSBuildNotInstalled)));
            }

            return selectedToolset;
        }

        /// <summary>
        /// Returns the msbuild directory. If <paramref name="userVersion"/> is null, then the directory containing
        /// the highest installed msbuild version is returned. Otherwise, the directory containing msbuild
        /// whose version matches <paramref name="userVersion"/> is returned. If no match is found,
        /// an exception will be thrown.
        /// </summary>
        /// <param name="userVersion">The user specified version. Can be null</param>
        /// <param name="console">The console used to output messages.</param>
        /// <returns>The msbuild directory.</returns>
        public static string GetMsbuildDirectory(string userVersion, IConsole console)
        {
            List<Toolset> installedToolsets;
            using (var projectCollection = new ProjectCollection())
            {
                installedToolsets = projectCollection.Toolsets.OrderByDescending(
                    toolset => SafeParseVersion(toolset.ToolsVersion)).ToList();
            }

            return GetMsbuildDirectoryInternal(userVersion, console, installedToolsets);
        }

        // This method is called by GetMsbuildDirectory(). This method is not intended to be called directly.
        // It's marked public so that it can be called by unit tests.
        public static string GetMsbuildDirectoryInternal(
            string userVersion,
            IConsole console,
            IEnumerable<Toolset> installedToolsets)
        {
            if (string.IsNullOrEmpty(userVersion))
            {
                var msbuildVersion = GetMSBuildVersionInPath();
                var toolset = SelectMsbuildToolset(msbuildVersion, installedToolsets);

                if (console != null)
                {
                    if (console.Verbosity == Verbosity.Detailed)
                    {
                        console.WriteLine(
                            LocalizedResourceManager.GetString(
                                nameof(NuGetResources.MSBuildAutoDetection_Verbose)),
                            toolset.ToolsVersion,
                            toolset.ToolsPath);
                    }
                    else
                    {
                        console.WriteLine(
                            LocalizedResourceManager.GetString(
                                nameof(NuGetResources.MSBuildAutoDetection)),
                            toolset.ToolsVersion,
                            toolset.ToolsPath);
                    }
                }

                return toolset.ToolsPath;
            }
            else
            {
                // Force version string to 1 decimal place
                string userVersionString = userVersion;
                decimal parsedVersion = 0;
                if (decimal.TryParse(userVersion, out parsedVersion))
                {
                    decimal adjustedVersion = (decimal)(((int)(parsedVersion * 10)) / 10F);
                    userVersionString = adjustedVersion.ToString("F1");
                }

                // MSBuild versions >15 require an alternative lookup
                if (parsedVersion > 15)
                {
                    return FindMSBuildInstance();
                }

                Version ver;
                bool hasNumericVersion = Version.TryParse(userVersionString, out ver);

                var selectedToolset = installedToolsets.FirstOrDefault(
                toolset =>
                {
                    // first match by string comparison
                    if (string.Equals(userVersionString, toolset.ToolsVersion, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    // then match by Major & Minor version numbers.
                    Version toolsVersion;
                    if (hasNumericVersion && Version.TryParse(toolset.ToolsVersion, out toolsVersion))
                    {
                        return (toolsVersion.Major == ver.Major &&
                            toolsVersion.Minor == ver.Minor);
                    }

                    return false;
                });

                if (selectedToolset == null)
                {
                    var message = string.Format(
                        CultureInfo.CurrentCulture,
                        LocalizedResourceManager.GetString(
                            nameof(NuGetResources.Error_CannotFindMsbuild)),
                        userVersion);

                    throw new CommandLineException(message);
                }

                return selectedToolset.ToolsPath;
            }
        }

        private static void AppendQuoted(StringBuilder builder, string targetPath)
        {
            builder
                .Append('"')
                .Append(targetPath)
                .Append('"');
        }

        private static void ExtractResource(string resourceName, string targetPath)
        {
            using (var input = typeof(MsBuildUtility).Assembly.GetManifestResourceStream(resourceName))
            {
                using (var output = File.OpenWrite(targetPath))
                {
                    input.CopyTo(output);
                }
            }
        }

        // We sort the none offical version to be first so they don't get automatically picked up
        private static Version SafeParseVersion(string version)
        {
            Version result;

            if (Version.TryParse(version, out result))
            {
                return result;
            }
            else
            {
                return new Version(0, 0);
            }
        }

        /// <summary>
        /// This class is used to create a temp file, which is deleted in Dispose().
        /// </summary>
        private class TempFile : IDisposable
        {
            private readonly string _filePath;

            /// <summary>
            /// Constructor. It creates an empty temp file under the temp directory / NuGet, with
            /// extension <paramref name="extension"/>.
            /// </summary>
            /// <param name="extension">The extension of the temp file.</param>
            public TempFile(string extension)
            {
                if (string.IsNullOrEmpty(extension))
                {
                    throw new ArgumentNullException(nameof(extension));
                }

                var tempDirectory = Path.Combine(Path.GetTempPath(), "NuGet-Scratch");

                Directory.CreateDirectory(tempDirectory);

                int count = 0;
                do
                {
                    _filePath = Path.Combine(tempDirectory, Path.GetRandomFileName() + extension);

                    if (!File.Exists(_filePath))
                    {
                        try
                        {
                            // create an empty file
                            using (var filestream = File.Open(_filePath, FileMode.CreateNew))
                            {
                            }

                            // file is created successfully.
                            return;
                        }
                        catch
                        {
                        }
                    }

                    count++;
                }
                while (count < 3);

                throw new InvalidOperationException(
                    LocalizedResourceManager.GetString(nameof(NuGetResources.Error_FailedToCreateRandomFileForP2P)));
            }

            public static implicit operator string(TempFile f)
            {
                return f._filePath;
            }

            public void Dispose()
            {
                if (File.Exists(_filePath))
                {
                    try
                    {
                        File.Delete(_filePath);
                    }
                    catch
                    {
                    }
                }
            }
        }

        /// <summary>
        /// Finds instances of MSBuild installed for versions after VS14
        /// The rules for instance discovery are:
        /// 1. If there is an MSBuild instance on the PATH, take the first one discovered
        /// 2. Otherwise, take the most recent one installed
        /// </summary>
        /// <returns>Directory of instance we will use; null on fail (silent)</returns>
        public static string FindMSBuildInstance()
        {
            //System.Diagnostics.Debugger.Launch();

            var instances = GetInstalledInstances();
            if (instances == null)
            {
                return null;
            }

            // Find first item in the MSBUILD_EXE_PATH to match an instance
            var searchPath = Environment.GetEnvironmentVariable("MSBUILD_EXE_PATH");
            if (!string.IsNullOrEmpty(searchPath))
            {
                var searchPathItems = new List<string>(searchPath.Split(new char[] { ';' }));
                var matchedItem = searchPathItems.FirstOrDefault(searchPathItem =>
                {
                    var matchedInstance = instances.FirstOrDefault(instance =>
                    {
                        var installationPath = instance.GetInstallationPath();
                        if (string.IsNullOrEmpty(installationPath))
                        {
                            return false;
                        }

                        var testMSBuildPath = Path.Combine(installationPath, "MSBuild");
                        return testMSBuildPath.Equals(searchPathItem, StringComparison.InvariantCultureIgnoreCase);
                    });

                    return matchedInstance != null;
                });

                if (!string.IsNullOrEmpty(matchedItem))
                {
                    return matchedItem;
                }
            }

            // PATH search failed - return latest install
            string msBuildPath = string.Empty;
            var latestInstance = instances.OrderByDescending(instance => ConvertFILETIMEToDateTime(instance.GetInstallDate()))
                .FirstOrDefault(instance =>
                {
                    // Ensure an msbuild.exe in the VS install
                    msBuildPath = GetMSBuildPathFromVsPath(instance.GetInstallationPath());
                    return !string.IsNullOrEmpty(msBuildPath);
                });

            if (string.IsNullOrEmpty(msBuildPath))
            {
                return null;
            }

            return msBuildPath;
        }

        private static IEnumerable<ISetupInstance> GetInstalledInstances()
        {
            ISetupConfiguration configuration;
            try
            {
                configuration = new SetupConfiguration() as ISetupConfiguration2;
            }
            catch (Exception)
            {
                return null; // No COM class
            }

            if (configuration == null)
            {
                return null;
            }

            var enumerator = configuration.EnumInstances();
            if (enumerator == null)
            {
                return null;
            }

            var setupInstances = new List<ISetupInstance>();
            while (true)
            {
                var fetchedInstances = new ISetupInstance[3];
                int fetched;
                enumerator.Next(fetchedInstances.Length, fetchedInstances, out fetched);
                if (fetched == 0)
                {
                    break;
                }

                // fetched will return 3 even if only one instance returned
                int index = 0;
                while (fetched > 0)
                {
                    setupInstances.Add(fetchedInstances[index++]);
                    fetched--;
                }
            }

            if (setupInstances.Count == 0)
            {
                return null;
            }

            return setupInstances;
        }

        private static DateTime ConvertFILETIMEToDateTime(FILETIME time)
        {
            long highBits = time.dwHighDateTime;
            highBits = highBits << 32;
            return DateTime.FromFileTimeUtc(highBits | (long)(uint)time.dwLowDateTime);
        }

        private static string GetMSBuildPathFromVsPath(string vsPath)
        {
            if (string.IsNullOrEmpty(vsPath))
            {
                return null;
            }

            string msBuildRoot = Path.Combine(vsPath, "MSBuild");
            if (!Directory.Exists(msBuildRoot))
            {
                return null;
            }

            // Enumerate all versions of MSBuild present, take the highest
            string msBuildPath = string.Empty;
            var highestVersionRoot = Directory.EnumerateDirectories(msBuildRoot).OrderByDescending(dir =>
            {
                var dirName = new DirectoryInfo(dir).Name;
                float dirValue;
                if (float.TryParse(dirName, out dirValue))
                {
                    return dirValue;
                }

                return 0F;
            })
            .FirstOrDefault(dir =>
            {
                msBuildPath = Path.Combine(dir, "bin");
                return File.Exists(Path.Combine(msBuildPath, "msbuild.exe"));
            });

            return msBuildPath;
        }
    }
}