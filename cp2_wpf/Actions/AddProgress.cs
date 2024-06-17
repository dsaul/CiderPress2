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
using System.ComponentModel;
using System.Diagnostics;

using AppCommon;
using CommonUtil;
using cp2_wpf.WPFCommon;
using DiskArc;
using FileConv;

namespace cp2_wpf.Actions {
    /// <summary>
    /// This runs an AddFileWorker inside a WorkProgress dialog.
    /// </summary>
    internal class AddProgress : WorkProgress.IWorker {
        private object mArchiveOrFileSystem;
        private DiskArcNode mLeafNode;
        private AddFileSet mAddFileSet;
        private IFileEntry mTargetDir;
        private AppHook mAppHook;

        public bool DoCompress { get; set; }
        public bool EnableMacOSZip { get; set; }
        public bool StripPaths { get; set; }
        public bool RawMode { get; set; }


        public AddProgress(object archiveOrFileSystem, DiskArcNode leafNode, AddFileSet addSet,
                IFileEntry targetDir, AppHook appHook) {
            mArchiveOrFileSystem = archiveOrFileSystem;
            mLeafNode = leafNode;
            mAddFileSet = addSet;
            mTargetDir = targetDir;
            mAppHook = appHook;
        }

        /// <summary>
        /// Performs the operation.
        /// </summary>
        /// <remarks>
        /// THIS RUNS ON THE WORKER THREAD.  Do not try to access GUI objects.
        /// </remarks>
        /// <param name="bkWorker">Background worker object.</param>
        /// <returns>Operation results.</returns>
        public object DoWork(BackgroundWorker bkWorker) {
            string curDir = Environment.CurrentDirectory;

            ProgressUtil.PersistentChoices choices = new ProgressUtil.PersistentChoices();
            AddFileWorker.CallbackFunc cbFunc = delegate (CallbackFacts what) {
                return ProgressUtil.HandleCallback(what, "add", choices, mLeafNode, bkWorker);
            };
            AddFileWorker addWorker = new AddFileWorker(mAddFileSet, cbFunc,
                doCompress: DoCompress, macZip: EnableMacOSZip, stripPaths: StripPaths,
                rawMode: RawMode, mAppHook);

            if (mArchiveOrFileSystem is IArchive) {
                IArchive arc = (IArchive)mArchiveOrFileSystem;
                try {
                    arc.StartTransaction();
                    addWorker.AddFilesToArchive(arc, out bool isCancelled);
                    if (isCancelled) {
                        //ProgressUtil.ShowCancelled(bkWorker);
                        return false;
                    }
                    bkWorker.ReportProgress(100, ProgressUtil.FINISHING_MSG);
                    mLeafNode.SaveUpdates(DoCompress);
                } catch (AppCommon.CancelException) {
                    Debug.Assert(bkWorker.CancellationPending);
                    return false;
                } catch (ConversionException ex) {
                    ProgressUtil.ShowMessage("Import error: " + ex.Message, true, bkWorker);
                    return false;
                } catch (Exception ex) {
                    ProgressUtil.ShowMessage("Error: " + ex.Message, true, bkWorker);
                    return false;
                } finally {
                    arc.CancelTransaction();    // no effect if transaction isn't open
                }
            } else if (mArchiveOrFileSystem is IFileSystem) {
                IFileSystem fs = (IFileSystem)mArchiveOrFileSystem;
                bool success = true;
                string? failMsg = null;
                try {
                    addWorker.AddFilesToDisk(fs, mTargetDir, out bool isCancelled);
                    if (isCancelled) {
                        //ProgressUtil.ShowCancelled(bkWorker);
                        // continue; some changes may have been made
                        success = false;
                    }
                } catch (ConversionException ex) {
                    failMsg = "Import error: " + ex.Message;
                    success = false;
                } catch (Exception ex) {
                    failMsg = "Error: " + ex.Message;
                    success = false;
                }
                // Finish writing changes, whether or not the operation succeeded.
                try {
                    // If we're failing, leave the problematic filename on screen.
                    if (failMsg == null) {
                        bkWorker.ReportProgress(100, ProgressUtil.FINISHING_MSG);
                    }
                    mLeafNode.SaveUpdates(DoCompress);
                } catch (Exception ex) {
                    ProgressUtil.ShowMessage("Error: update failed: " + ex.Message, true, bkWorker);
                    return false;
                }
                if (failMsg != null) {
                    // We want to defer reporting of failures until after SaveUpdates() runs,
                    // because otherwise we're pausing for an indefinite period of time with the
                    // disk image in an inconsistent state.  In particular, if we get a Disk Full
                    // error and CP2 is killed while the dialog is up, the disk image can have
                    // blocks marked in-use but no matching file entry (probably because of
                    // buffering in the IDiskImage stream... flushing the disk image object seems
                    // to fix things).  Flushing the filesystem object doesn't help.
                    //
                    // Update: the new DiskArcNode.FlushStreams() call fixes this too.  I'm keeping
                    // the failure message at the end anyway to ensure that the full update
                    // process has completed before we stop and show an error.
                    ProgressUtil.ShowMessage(failMsg, true, bkWorker);
                }
                if (!success) {
                    return false;
                }
            } else {
                Debug.Assert(false);
            }
            return true;
        }

        public bool RunWorkerCompleted(object? results) {
            bool success = (results is true);
            Debug.WriteLine("Operation completed, success=" + success);
            return success;
        }
    }
}
