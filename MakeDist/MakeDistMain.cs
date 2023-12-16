﻿/*
 * Copyright 2023 faddenSoft
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */
using System;

using AppCommon;
using CommonUtil;

namespace MakeDist {
    /// <summary>
    /// Command-line entry point.
    /// </summary>
    static class MakeDistMain {
        private const string APP_NAME = "MakeDist";

        /// <summary>
        /// Runtime IDs we want to build for.
        /// </summary>
        private static List<string> sStdRIDs = new List<string>() {
            "win-x86",          // 32-bit, non-version-specific Windows
            "win-x64",          // 64-bit, non-version-specific Windows
            "linux-x64",        // 64-bit, most Linux desktop distributions
            "osx-x64",          // 64-bit, non-version-specific Mac OS (min version 10.12 Sierra)
            //"osx-arm64",        // 64-bit ARM, non-version-specific Mac OS
        };

        /// <summary>
        /// OS entry point.
        /// </summary>
        internal static void Main(string[] args) {
            Environment.ExitCode = 2;       // use code 2 for usage problems
            bool isDebugBuild = false;

            if (args.Length == 0) {
                Usage();
                return;
            }
            // First arg is command.
            string cmdName = args[0];

            // Process long options.
            int argStart = 1;
            while (argStart < args.Length && args[argStart].StartsWith("--")) {
                string arg = args[argStart++];
                switch (arg) {
                    case "--debug":
                        isDebugBuild = true;
                        break;
                    case "--release":
                        isDebugBuild = false;
                        break;
                    default:
                        Usage();
                        return;
                }
            }

            // Copy remaining args to new array.
            string[] cmdArgs = new string[args.Length - argStart];
            for (int i = 0; i < cmdArgs.Length; i++) {
                cmdArgs[i] = args[argStart + i];
            }

            switch (cmdName) {
                case "build": {
                        if (cmdArgs.Length != 0) {
                            Usage();
                            break;
                        }
                        string versionTag = GlobalAppVersion.AppVersion.ToString();//.GetBuildTag();

                        // TODO: take RIDs as command-line arg list, with "std" doing default set
                        bool result = Build.ExecBuild(versionTag, sStdRIDs, isDebugBuild);
                        Environment.ExitCode = result ? 0 : 1;
                    }
                    break;
                case "set-exec": {
                        if (cmdArgs.Length < 2) {
                            Usage();
                            break;
                        }
                        bool result = SetExec.HandleSetExec(cmdArgs, true);
                        Environment.ExitCode = result ? 0 : 1;
                    }
                    break;
                case "clobber":
                    if (cmdArgs.Length != 0) {
                        Usage();
                        break;
                    }
                    Clobber();
                    Environment.ExitCode = 0;
                    break;
                default:
                    Usage();
                    break;
            }
            if (Environment.ExitCode == 1) {
                Console.Error.WriteLine("Failed");
            }
        }

        /// <summary>
        /// Prints general usage summary.
        /// </summary>
        private static void Usage() {
            Console.WriteLine("Usage: " + APP_NAME + " build [--debug|--release]");
            Console.WriteLine("       " + APP_NAME + " set-exec <file.zip> <entry-in-archive...>");
            Console.WriteLine("       " + APP_NAME + " clobber");
        }

        #region Clobber

        private const string PROJ_EXT = ".csproj";
        private const string OBJ_DIR = "obj";
        private const string BIN_DIR = "bin";

        /// <summary>
        /// Performs the "clobber" operation.
        /// </summary>
        private static void Clobber() {
            string curDir = Environment.CurrentDirectory;
            Console.WriteLine("Clobbering in '" + curDir + "'...");
            List<string> scrubPaths = new List<string>();

            ScanClobberables(curDir, scrubPaths);

            Console.WriteLine("Paths to scrub:");
            foreach (string path in scrubPaths) {
                Console.WriteLine("  " + path);
            }
            Console.Write("Proceed (y/N)? ");
            string? response = Console.ReadLine();
            if (!string.IsNullOrEmpty(response) && char.ToLower(response[0]) == 'y') {
                Console.WriteLine("Scrubbing...");
                DeletePaths(scrubPaths);
            } else {
                Console.WriteLine("Cancelled");
            }
        }

        /// <summary>
        /// Recursively scans for things to clobber.
        /// </summary>
        /// <param name="directory">Directory to scan.</param>
        /// <param name="scrubPaths">Accumulated list of scrub targets.</param>
        private static void ScanClobberables(string directory, List<string> scrubPaths) {
            foreach (string path in Directory.EnumerateFileSystemEntries(directory)) {
                // Descend into subdirectories (but not "bin" or "obj").
                string fileName = Path.GetFileName(path);
                if (fileName != OBJ_DIR && fileName != BIN_DIR && Directory.Exists(path)) {
                    ScanClobberables(path, scrubPaths);
                }

                if (path.ToLowerInvariant().EndsWith(PROJ_EXT)) {
                    // Found a project file, scrub "obj" and "bin" here.
                    string objPath = Path.Combine(directory, OBJ_DIR);
                    if (Directory.Exists(objPath)) {
                        scrubPaths.Add(objPath);
                    }
                    string binPath = Path.Combine(directory, BIN_DIR);
                    // Attempting to delete MakeDist/bin will likely fail because it's running.
                    if (Directory.Exists(binPath) && Path.GetFileName(directory) != APP_NAME) {
                        scrubPaths.Add(binPath);
                    }
                }
            }
        }

        /// <summary>
        /// Recursively deletes a list of directories.
        /// </summary>
        /// <param name="paths">List of paths to remove.</param>
        private static void DeletePaths(List<string> paths) {
            foreach (string path in paths) {
                FileUtil.DeleteDirContents(path);
                Directory.Delete(path);
            }
        }

        #endregion Clobber
    }
}
