﻿//*********************************************************//
//    Copyright (c) Microsoft. All rights reserved.
//    
//    Apache 2.0 License
//    
//    You may obtain a copy of the License at
//    http://www.apache.org/licenses/LICENSE-2.0
//    
//    Unless required by applicable law or agreed to in writing, software 
//    distributed under the License is distributed on an "AS IS" BASIS, 
//    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or 
//    implied. See the License for the specific language governing 
//    permissions and limitations under the License.
//
//*********************************************************//

using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudioTools.Project;
using Microsoft.NodejsTools.Npm;

using SR = Microsoft.NodejsTools.Project.SR;

namespace Microsoft.NodejsTools {
    internal class TypingsAcquisition {
        private const string TypingsTool = "typings";
        private const string TypingsToolVersion = "1.0.5"; // Lock to stable version of the 'typings' tool with known behavior.
        private const string TypingsToolExe = TypingsTool + ".cmd";
        private const string TypingsDirectoryName = "typings";

        private static SemaphoreSlim typingsToolGlobalWorkSemaphore = new SemaphoreSlim(1);

        /// <summary>
        /// Path the the private package where the typings acquisition tool is installed.
        /// </summary>
        private static string NtvsExternalToolsPath {
            get {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft",
                    "Node.js Tools",
                    "ExternalTools");
            }
        }

        /// <summary>
        /// Full path to the typings acquisition tool.
        /// </summary>
        private static string TypingsToolPath {
            get {
                return Path.Combine(
                    NtvsExternalToolsPath,
                    "node_modules",
                    ".bin",
                    TypingsToolExe);
            }
        }

        private readonly INpmController _npmController;
        private readonly string _pathToRootProjectDirectory;

        private readonly Lazy<HashSet<string>> _acquiredTypingsPackageNames;
        private bool _didTryToInstallTypingsTool;

        public TypingsAcquisition(INpmController controller) {
            _npmController = controller;
            _pathToRootProjectDirectory = controller.RootPackage.Path;

            _acquiredTypingsPackageNames = new Lazy<HashSet<string>>(() => {
                return new HashSet<string>(CurrentTypingsPackages(_pathToRootProjectDirectory));
            });
        }

        public Task<bool> AcquireTypings(IEnumerable<string> packages, Redirector redirector) {
            return typingsToolGlobalWorkSemaphore.WaitAsync().ContinueWith(async _ => {
                var typingsToAquire = GetNewTypingsToAcquire(packages);
                var success = await DownloadTypings(typingsToAquire, redirector);
                if (success) {
                    _acquiredTypingsPackageNames.Value.UnionWith(typingsToAquire);
                }
                typingsToolGlobalWorkSemaphore.Release();
                return success;
            }).Unwrap();
        }

        private IEnumerable<string> GetNewTypingsToAcquire(IEnumerable<string> packages) {
            var currentTypings = _acquiredTypingsPackageNames.Value;
            return packages.Where(package => !currentTypings.Contains(package));
        }

        private async Task<bool> DownloadTypings(IEnumerable<string> packages, Redirector redirector) {
            if (!packages.Any()) {
                return true;
            }

            string typingsTool = await EnsureTypingsToolInstalled();
            if (string.IsNullOrEmpty(typingsTool)) {
                if (redirector != null) {
                    redirector.WriteErrorLine(SR.GetString(SR.TypingsToolNotInstalledError));
                }
                return false;
            }

            using (var process = ProcessOutput.Run(
                typingsTool,
                GetTypingsToolInstallArguments(packages),
                _pathToRootProjectDirectory,
                null,
                false,
                redirector,
                quoteArgs: true)) {
                if (!process.IsStarted) {
                    // Process failed to start, and any exception message has
                    // already been sent through the redirector
                    if (redirector != null) {
                        redirector.WriteErrorLine("could not start 'typings'");
                    }
                    return false;
                }
                var i = await process;
                if (i == 0) {
                    if (redirector != null) {
                        redirector.WriteLine(SR.GetString(SR.TypingsToolInstallCompleted));
                    }
                    return true;
                } else {
                    process.Kill();
                    if (redirector != null) {
                        redirector.WriteErrorLine(SR.GetString(SR.TypingsToolInstallErrorOccurred));
                    }
                    return false;
                }
            }
        }

        private async Task<string> EnsureTypingsToolInstalled() {
            if (File.Exists(TypingsToolPath)) {
                return TypingsToolPath;
            }

            if (_didTryToInstallTypingsTool) {
                return null;
            } 
            if (!await InstallTypingsTool()) {
                return null;
            }
            return await EnsureTypingsToolInstalled();
        }

        private async Task<bool> InstallTypingsTool() {
            _didTryToInstallTypingsTool = true;

            Directory.CreateDirectory(NtvsExternalToolsPath);

            // install typings
            using (var commander = _npmController.CreateNpmCommander()) {
                return await commander.InstallPackageToFolderByVersionAsync(NtvsExternalToolsPath, TypingsTool, TypingsToolVersion, false);
            }
        }

        private static IEnumerable<string> GetTypingsToolInstallArguments(IEnumerable<string> packages) {
            var arguments = new[] { "install" }.Concat(packages.Select(name => string.Format("dt~{0}", name)));
            if (NodejsPackage.Instance.IntellisenseOptionsPage.SaveChangesToConfigFile) {
                arguments = arguments.Concat(new[] { "--save" });
            }
            return arguments.Concat(new[] { "--global" });
        }

        private static IEnumerable<string> CurrentTypingsPackages(string pathToRootProjectDirectory) {
            var packages = new List<string>();
            var typingsDirectoryPath = Path.Combine(pathToRootProjectDirectory, TypingsDirectoryName);
            if (!Directory.Exists(typingsDirectoryPath)) {
                return packages;
            }
            try {
                foreach (var file in Directory.EnumerateFiles(typingsDirectoryPath, "*.d.ts", SearchOption.AllDirectories)) {
                    var directory = Directory.GetParent(file);
                    if (directory.FullName != typingsDirectoryPath && Path.GetFullPath(directory.FullName).StartsWith(typingsDirectoryPath)) {
                        packages.Add(directory.Name);
                    }
                }
            } catch (IOException) {
                // noop
            } catch (SecurityException) {
                // noop
            } catch (UnauthorizedAccessException) {
                // noop
            }
            return packages;
        }
    }
}
