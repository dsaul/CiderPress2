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
using System.Diagnostics;
using System.Text;

using CommonUtil;
using DiskArc;
using DiskArc.Arc;
using static DiskArc.Defs;
using static DiskArc.IFileSystem;


// Regarding storing multiple files with the same name in an IArchive...
//
// Most archive formats allow multiple files to be stored with the same filename.  This is
// undesirable for a variety of reasons, so we want to prevent it at the application level.
// We should be okay comparing entries against a hash table (to avoid O(N^2) behavior required
// to compare everything against everything else), so long as we handle case sensitivity
// appropriately.
//
// Most archive format specifications don't specify case sensitivity, partly because it doesn't
// matter if you allow duplicate entries, but also because what really matters is the
// case-sensitive behavior of the filesystem onto which the files are ultimately extracted.  If
// the format could reasonably be used to store and extract files on a UNIX system, case-sensitive
// comparisons should be used.
//
// Ultimately the choice isn't crucial for correctness.  The important thing is to provide
// consistent behavior, ideally matching what other common archivers (such as Info-ZIP) do.

namespace AppCommon {
    /// <summary>
    /// Given a set of files to add from the host filesystem (in an AddFileSet), perform the
    /// steps required to add the files to an IArchive or IFileSystem object.  Uses callbacks
    /// to display progress and warning messages, and to query for handling of conflicts.
    /// </summary>
    public class AddFileWorker {
        /// <summary>
        /// Callback function interface definition.
        /// </summary>
        public delegate CallbackFacts.Results CallbackFunc(CallbackFacts what);

        /// <summary>
        /// If set, files added to archives with compression features will be compressed using
        /// the default compression format.
        /// </summary>
        public bool DoCompress { get; set; } = true;

        /// <summary>
        /// If set, files added to a ZIP archive that have resource forks or HFS types will
        /// be stored as AppleDouble with a "__MACOSX" prefix.
        /// </summary>
        public bool EnableMacOSZip { get; set; } = false;

        /// <summary>
        /// If set, strip pathnames off of files before adding them.  For a filesystem, all
        /// files will be added to the target directory.
        /// </summary>
        public bool StripPaths { get; set; } = false;

        /// <summary>
        /// If set, use raw mode when adding files to filesystems (notably DOS 3.x).
        /// </summary>
        public bool RawMode { get; set; } = false;

        /// <summary>
        /// Set of files to add.  Not sorted.
        /// </summary>
        private AddFileSet mFileSet;

        /// <summary>
        /// Callback function, for progress updates, warnings, and problem resolution.
        /// </summary>
        private CallbackFunc mFunc;

        private AppHook mAppHook;


        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="fileSet">Set of files to add.</param>
        public AddFileWorker(AddFileSet fileSet, CallbackFunc func, bool doCompress, bool macZip,
                bool stripPaths, bool rawMode, AppHook appHook) {
            mFileSet = fileSet;
            mFunc = func;
            DoCompress = doCompress;
            EnableMacOSZip = macZip;
            StripPaths = stripPaths;
            RawMode = rawMode;
            mAppHook = appHook;
        }

        #region IArchive

        /// <summary>
        /// Adds files to an archive.  Queues up all operations but does not commit the
        /// transaction.
        /// </summary>
        /// <param name="archive">Archive to add files to.  Open transaction before calling.</param>
        /// <param name="isCancelled">Result: true if operation was cancelled.</param>
        /// <exception cref="IOException">File I/O error occurred.</exception>
        /// <exception cref="FileConv.ConversionException">Error in import conversion.</exception>
        public void AddFilesToArchive(IArchive archive, out bool isCancelled) {
            if (archive.Characteristics.HasSingleEntry) {
                throw new Exception("can't add files to single-entry archive");
            }
            bool canRsrcFork = archive.Characteristics.HasResourceForks ||
                (archive is Zip && EnableMacOSZip);
            bool doStripPaths = StripPaths ||
                archive.Characteristics.DefaultDirSep == IFileEntry.NO_DIR_SEP;

            if (archive.IsDubious) {
                throw new Exception("target archive is read-only (damage)");
            }

            // Generate a list of archive contents, used when checking for duplicate entries.  We
            // want to do case-insensitive comparisons for general consistency.
            Dictionary<string, IFileEntry> dupCheck =
                new Dictionary<string, IFileEntry>(StringComparer.InvariantCultureIgnoreCase);
            foreach (IFileEntry entry in archive) {
                // Archive might already have duplicates, so don't call Add().
                dupCheck[entry.FileName] = entry;
            }

            AddFileEntry[] addEntries = GenerateSortedArray(mFileSet);

            for (int idx = 0; idx < addEntries.Length; idx++) {
                AddFileEntry addEnt = addEntries[idx];
                if (addEnt.IsDirectory) {
                    // This will cause a bump in the progress meter.
                    continue;
                }

                if (!addEnt.HasDataFork && addEnt.HasRsrcFork && !canRsrcFork) {
                    // Nothing but a resource fork, and we can't store those.  Report that
                    // we're skipping it, and move on.  (In practice this is rare, since most
                    // things always have a data fork, but an AppleSingle file could do this.)
                    CallbackFacts facts = new CallbackFacts(
                        CallbackFacts.Reasons.ResourceForkIgnored, addEnt.FullRsrcPath,
                        Path.DirectorySeparatorChar);
                    facts.Part = FilePart.RsrcFork;
                    mFunc(facts);
                    continue;
                }

                string storageDir = doStripPaths ? string.Empty : addEnt.StorageDir;
                string storageName = addEnt.StorageName;

                // Different formats will have different constraints on the lengths of filenames
                // and the total length of a partial path, as well as the set of characters used.
                // We need to fix each individual component, assemble them, and test the length.
                // If the result is the same as an existing file, we give the user the option to
                // skip it, overwrite existing, or cancel entirely.
                //
                // We could offer the possibility to rename, though that gets a bit more
                // involved.
                string? adjPath =
                    AdjustArchivePath(archive, storageDir, addEnt.StorageDirSep, storageName);
                if (adjPath == null) {
                    // TODO(maybe): allow the user to rename it be shorter... may not make
                    //   sense unless we allow rename of the full path
                    CallbackFacts facts = new CallbackFacts(CallbackFacts.Reasons.PathTooLong,
                        storageDir + Path.DirectorySeparatorChar + storageName,
                        Path.DirectorySeparatorChar);
                    CallbackFacts.Results result = mFunc(facts);
                    if (result == CallbackFacts.Results.Skip) {
                        continue;
                    } else {
                        isCancelled = true;
                        return;
                    }
                }

                // Check for a duplicate.  If it exists, ask the user if they want to overwrite
                // or skip.
                if (dupCheck.TryGetValue(adjPath, out IFileEntry? dupEntry)) {
                    CallbackFacts facts = new CallbackFacts(CallbackFacts.Reasons.FileNameExists,
                        adjPath, addEnt.StorageDirSep);
                    CallbackFacts.Results result = mFunc(facts);
                    switch (result) {
                        case CallbackFacts.Results.Cancel:
                            isCancelled = true;
                            return;
                        case CallbackFacts.Results.Skip:
                            adjPath = null;
                            break;
                        case CallbackFacts.Results.Overwrite:
                            break;
                        default:
                            Debug.Assert(false);
                            break;
                    }
                }
                if (adjPath == null) {
                    continue;
                }
                if (dupEntry != null) {
                    archive.DeleteRecord(dupEntry);
                }

                // Create new record.
                IFileEntry newEntry = archive.CreateRecord();
                newEntry.FileName = adjPath;
                //newEntry.FileNameSeparator = archive.Characteristics.DefaultDirSep;
                CopyAttributes(addEnt, newEntry);

                CompressionFormat fmt =
                    DoCompress ? CompressionFormat.Default : CompressionFormat.Uncompressed;
                int progressPerc = (100 * idx) / addEntries.Length;
                if (addEnt.HasDataFork) {
                    string storageDisplayName;
                    if (string.IsNullOrEmpty(storageDir)) {
                        storageDisplayName = addEnt.StorageName;
                    } else {
                        storageDisplayName = storageDir + addEnt.StorageDirSep +
                            addEnt.StorageName;
                    }
                    AddFileSource afSource = new AddFileSource(addEnt.FullDataPath,
                        addEnt.DataSource, FilePart.DataFork, mFunc, progressPerc,
                        storageDisplayName, addEnt.StorageDirSep,
                        mFileSet.Importer, mFileSet.ImportOptions, mAppHook);
                    archive.AddPart(newEntry, FilePart.DataFork, afSource, fmt);
                }

                // Treat resource forks as a half-step.
                progressPerc = (int)((100 * (idx + 0.5)) / addEntries.Length);
                if (archive is Zip && EnableMacOSZip) {
                    if (!addEnt.HasDataFork) {
                        // We want to keep the data fork part, since AppleDouble requires both.
                        // We didn't give it a data part earlier, so do it now.
                        SimplePartSource zeroSource =
                            new SimplePartSource(new MemoryStream(new byte[0]));
                        archive.AddPart(newEntry, FilePart.DataFork, zeroSource, fmt);
                    }
                    // Create the "header" file if there's a resource fork, or if we just need
                    // to preserve the file type info.
                    if (addEnt.HasRsrcFork || addEnt.HasNonZeroTypes) {
                        AddMacZipRecord(archive, addEnt, adjPath, progressPerc, fmt);
                    }
                } else if (addEnt.HasRsrcFork) {
                    if (!canRsrcFork) {
                        // Nowhere to put this resource fork.  Report it and move on.
                        Debug.Assert(addEnt.HasDataFork);
                        CallbackFacts facts = new CallbackFacts(
                            CallbackFacts.Reasons.ResourceForkIgnored, addEnt.FullRsrcPath,
                            Path.DirectorySeparatorChar);
                        facts.Part = FilePart.RsrcFork;
                        mFunc(facts);
                    } else {
                        // Add resource fork to record created earlier.
                        string storageDisplayName;
                        if (string.IsNullOrEmpty(storageDir)) {
                            storageDisplayName = addEnt.StorageName;
                        } else {
                            storageDisplayName = storageDir + addEnt.StorageDirSep +
                                addEnt.StorageName;
                        }
                        AddFileSource afSource = new AddFileSource(addEnt.FullRsrcPath,
                            addEnt.RsrcSource, FilePart.RsrcFork, mFunc, progressPerc,
                            storageDisplayName, addEnt.StorageDirSep, mFileSet.Importer,
                            mFileSet.ImportOptions, mAppHook);
                        archive.AddPart(newEntry, FilePart.RsrcFork, afSource, fmt);
                    }
                }
            }
            isCancelled = false;
        }

        /// <summary>
        /// Adjusts the storage name to be compatible with the archive's requirements.
        /// </summary>
        /// <param name="archive">IArchive instance.</param>
        /// <param name="storageDir">Partial path without filename, with components divided by
        ///   host path separators.  May be empty.</param>
        /// <param name="storageName">File name.  May not conform to host filesystem
        ///   conventions.</param>
        /// <returns>Adjusted partial path, or null if the path is longer than the archive format
        ///   supports.</returns>
        internal static string? AdjustArchivePath(IArchive archive, string storageDir, char dirSep,
                string storageName) {
            char sepChar = archive.Characteristics.DefaultDirSep;
            StringBuilder sb = new StringBuilder(storageDir.Length + storageName.Length + 1);
            string[] parts = storageDir.Split(dirSep, StringSplitOptions.RemoveEmptyEntries);
            foreach (string part in parts) {
                string adjPart = archive.AdjustFileName(part);
                sb.Append(adjPart);
                sb.Append(sepChar);
            }
            string adjName = archive.AdjustFileName(storageName);
            sb.Append(adjName);

            string result = sb.ToString();
            if (!archive.CheckStorageName(result)) {
                return null;
            }
            return result;
        }

        /// <summary>
        /// Adds a special MacOSX ZIP record that will hold file type data and/or resource fork.
        /// </summary>
        private void AddMacZipRecord(IArchive archive, AddFileEntry ent, string adjPath,
                int progressPercent, CompressionFormat fmt) {
            Debug.Assert(archive is Zip);
            Debug.Assert(ent.HasRsrcFork || ent.HasNonZeroTypes);

            // Create a new record.
            IFileEntry rsrcEntry = archive.CreateRecord();

            // Form __MACOSX/path/._filename string.
            StringBuilder sb = new StringBuilder();
            sb.Append(Zip.MAC_ZIP_RSRC_PREFIX);
            sb.Append(archive.Characteristics.DefaultDirSep);
            int fileNameSplit =
                adjPath.LastIndexOf(archive.Characteristics.DefaultDirSep);
            if (fileNameSplit < 0) {
                // Just the filename.
                sb.Append(AppleSingle.ADF_PREFIX);
                sb.Append(adjPath);
            } else {
                sb.Append(adjPath.Substring(0, fileNameSplit + 1));
                sb.Append(AppleSingle.ADF_PREFIX);
                sb.Append(adjPath.Substring(fileNameSplit + 1));
            }

            rsrcEntry.FileName = sb.ToString();
            rsrcEntry.DirectorySeparatorChar = archive.Characteristics.DefaultDirSep;
            rsrcEntry.ModWhen = ent.ModWhen;        // match the modification date

            // Add the part.  This will be an AppleDouble "header" file that has the file
            // type info and (if present) the resource fork.  This will be generated on the fly
            // so we don't have to read the input file up front and hold onto it while we work.
            AddFileSourceMZ afSource = new AddFileSourceMZ(ent, mFunc, progressPercent,
                mFileSet.Importer, mFileSet.ImportOptions, mAppHook);
            archive.AddPart(rsrcEntry, FilePart.DataFork, afSource, fmt);
        }

        #endregion IArchive

        #region IFileSystem

        /// <summary>
        /// Adds files to a filesystem.
        /// </summary>
        /// <param name="fileSystem">Filesystem to add files to.</param>
        /// <param name="targetDir">Base directory where files are added, or NO_ENTRY if none
        ///   was specified.</param>
        /// <param name="isCancelled">Result: true if operation was cancelled.</param>
        /// <exception cref="IOException">File I/O error occurred.</exception>
        /// <exception cref="FileConv.ConversionException">Error in import conversion.</exception>
        public void AddFilesToDisk(IFileSystem fileSystem, IFileEntry targetDir,
                out bool isCancelled) {
            if (targetDir != IFileEntry.NO_ENTRY) {
                Debug.Assert(targetDir.IsDirectory);
                Debug.Assert(targetDir.GetFileSystem() == fileSystem);
            }
            if (fileSystem.IsReadOnly) {
                // Should have been caught be caller.
                throw new Exception("target filesystem is read-only" +
                    (fileSystem.IsDubious ? " (damage)" : ""));
            }

            bool canRsrcFork = fileSystem.Characteristics.HasResourceForks;
            bool doStripPaths = StripPaths || !fileSystem.Characteristics.IsHierarchical;
            bool useRawMode = RawMode;

            AddFileEntry[] addEntries = GenerateSortedArray(mFileSet);

            IFileEntry targetDirEnt = (targetDir == IFileEntry.NO_ENTRY) ?
                fileSystem.GetVolDirEntry() : targetDir;

            for (int i = 0; i < addEntries.Length; i++) {
                if (IsCancelPending()) {
                    isCancelled = true;
                    return;
                }
                AddFileEntry addEnt = addEntries[i];
                if (doStripPaths && addEnt.IsDirectory) {
                    continue;
                }

                if (!addEnt.HasDataFork && addEnt.HasRsrcFork && !canRsrcFork) {
                    // Nothing but a resource fork, and we can't store those.  Report that
                    // we're skipping it, and move on.  (In practice this is rare, since most
                    // things always have a data fork, but an AppleSingle file could do this.)
                    CallbackFacts facts = new CallbackFacts(
                        CallbackFacts.Reasons.ResourceForkIgnored,
                        addEnt.FullRsrcPath, Path.DirectorySeparatorChar);
                    facts.Part = FilePart.RsrcFork;
                    mFunc(facts);
                    continue;
                }

                // Find the destination directory for this file, creating directories as
                // needed.
                // TODO: this would be more efficient for multiple consecutive adds to the same
                // directory if we remembered the path / entry from the previous iteration.
                string storageDir = doStripPaths ? string.Empty : addEnt.StorageDir;
                IFileEntry subDirEnt;
                subDirEnt = fileSystem.CreateSubdirectories(targetDirEnt, storageDir,
                    addEnt.StorageDirSep);

                // Add the new file to subDirEnt.  See if it already exists.
                string adjName = fileSystem.AdjustFileName(addEnt.StorageName);
                if (fileSystem.TryFindFileEntry(subDirEnt, adjName, out IFileEntry newEntry)) {
                    if (addEnt.IsDirectory && !newEntry.IsDirectory) {
                        throw new Exception("Cannot replace non-directory '" + newEntry.FileName +
                            "' with directory");
                    } else if (!addEnt.IsDirectory && newEntry.IsDirectory) {
                        throw new Exception("Cannot replace directory '" + newEntry.FileName +
                            "' with non-directory");
                    } else if (!addEnt.IsDirectory && !newEntry.IsDirectory) {
                        // File exists.  Skip or overwrite.
                        bool doSkip = false;
                        CallbackFacts facts =
                            new CallbackFacts(CallbackFacts.Reasons.FileNameExists,
                                newEntry.FullPathName, newEntry.DirectorySeparatorChar);
                        CallbackFacts.Results result = mFunc(facts);
                        switch (result) {
                            case CallbackFacts.Results.Cancel:
                                isCancelled = true;
                                return;
                            case CallbackFacts.Results.Skip:
                                doSkip = true;
                                break;
                            case CallbackFacts.Results.Overwrite:
                                break;
                            default:
                                Debug.Assert(false);
                                break;
                        }
                        if (doSkip) {
                            continue;
                        }

                        if (newEntry.IsDubious || newEntry.IsDamaged) {
                            throw new Exception("cannot overwrite damaged file: " +
                                newEntry.FullPathName);
                        }

                        // We can either delete the existing entry and create a new one, or merge
                        // with the current contents, which might be helpful if the file on disk is
                        // extended and we're only adding one fork.  The merge would retain the
                        // current file attributes, so that adding a data fork wouldn't change the
                        // file type.  (I think somebody asked about this once for CiderPress, but
                        // I never did anything about it.  I can't find the request.)
                        //
                        // For now, just delete and recreate the entry.
                        fileSystem.DeleteFile(newEntry);
                        newEntry = IFileEntry.NO_ENTRY;
                    } else {
                        // Adding a directory that already exists.
                        Debug.Assert(addEnt.IsDirectory && newEntry.IsDirectory);
                    }
                }

                if (newEntry == IFileEntry.NO_ENTRY) {
                    CreateMode mode = CreateMode.File;
                    if (addEnt.HasRsrcFork && canRsrcFork) {
                        mode = CreateMode.Extended;
                    } else if (addEnt.IsDirectory) {
                        mode = CreateMode.Directory;
                    }
                    // Create file and set file type, so DOS "cooked" mode works.
                    newEntry = fileSystem.CreateFile(subDirEnt, adjName, mode, addEnt.FileType);
                }

                if (addEnt.IsDirectory) {
                    continue;
                }

                // add data fork
                if (addEnt.HasDataFork) {
                    int progressPerc = (100 * i) / addEntries.Length;
                    FilePart part = useRawMode ? FilePart.RawData : FilePart.DataFork;
                    try {
                        using (DiskFileStream outStream = fileSystem.OpenFile(newEntry,
                                FileAccessMode.ReadWrite, part)) {
                            CopyFilePart(addEnt.FullDataPath, addEnt.DataSource, FilePart.DataFork,
                                progressPerc, newEntry.FullPathName,
                                fileSystem.Characteristics.DirSep, outStream);
                        }
                    } catch {
                        // Copy or conversion failed, clean up.
                        fileSystem.DeleteFile(newEntry);
                        throw;
                    }
                }

                // add rsrc fork
                if (addEnt.HasRsrcFork && canRsrcFork) {
                    int progressPerc = (int)((100 * (i + 0.5)) / addEntries.Length);
                    try {
                        using (DiskFileStream outStream = fileSystem.OpenFile(newEntry,
                                FileAccessMode.ReadWrite, FilePart.RsrcFork)) {
                            CopyFilePart(addEnt.FullRsrcPath, addEnt.RsrcSource, FilePart.RsrcFork,
                                progressPerc, newEntry.FullPathName,
                                fileSystem.Characteristics.DirSep, outStream);
                        }
                    } catch {
                        // Copy failed, clean up.
                        fileSystem.DeleteFile(newEntry);
                        throw;
                    }
                }

                // Set types, dates, and access flags.
                CopyAttributes(addEnt, newEntry);
                newEntry.SaveChanges();
            }

            isCancelled = false;
        }

        /// <summary>
        /// Copies a part (i.e fork) of a file to a disk image file stream.  The file source may
        /// be a plain or structured file.
        /// </summary>
        /// <param name="fullPath">Path to source.</param>
        /// <param name="sourceType">Source type; could be plain or ADF/AS.</param>
        /// <param name="part">Which part we're interested in.</param>
        /// <param name="progressPercent">Percent complete for progress update.</param>
        /// <param name="storageName">Name of file as it appears in the filesystem.</param>
        /// <param name="storageDirSep">Directory separator char for storageName.</param>
        /// <param name="outStream">Destination stream.</param>
        private void CopyFilePart(string fullPath, AddFileEntry.SourceType sourceType,
                FilePart part, int progressPercent, string storageName,
                char storageDirSep, DiskFileStream outStream) {
            Debug.Assert(part == FilePart.DataFork || part == FilePart.RsrcFork);
            Stream? outerStream = null;
            Stream? fileStream = null;
            AppleSingle? appleSingle = null;

            CallbackFacts facts = new CallbackFacts(CallbackFacts.Reasons.Progress);
            string curDir = Environment.CurrentDirectory;
            if (fullPath.StartsWith(curDir)) {
                facts.OrigPathName = fullPath.Substring(curDir.Length + 1);
            } else {
                facts.OrigPathName = fullPath;
            }
            facts.OrigDirSep = Path.DirectorySeparatorChar;
            facts.NewPathName = storageName;
            facts.NewDirSep = storageDirSep;
            facts.ProgressPercent = progressPercent;
            facts.Part = part;
            mFunc(facts);

            try {
                // Open the file stream.
                switch (sourceType) {
                    case AddFileEntry.SourceType.Plain:
                    case AddFileEntry.SourceType.Import:
                        fileStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read);
                        break;
                    case AddFileEntry.SourceType.AppleSingle:
                        outerStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read);
                        appleSingle = AppleSingle.OpenArchive(outerStream, mAppHook);
                        fileStream = appleSingle.OpenPart(appleSingle.GetFirstEntry(), part);
                        break;
                    case AddFileEntry.SourceType.AppleDouble:
                        outerStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read);
                        appleSingle = AppleSingle.OpenArchive(outerStream, mAppHook);
                        fileStream = appleSingle.OpenPart(appleSingle.GetFirstEntry(), part);
                        break;
                    default:
                        throw new NotImplementedException("Source type not handled: " + sourceType);
                }

                if (sourceType == AddFileEntry.SourceType.Import) {
                    // Convert the data through the importer.
                    //
                    // Doing this one stream at a time is a little awkward.  Doing it better
                    // involves temp files.
                    Debug.Assert(mFileSet.Importer != null);
                    if (part == FilePart.DataFork) {
                        mFileSet.Importer.ConvertFile(fileStream, mFileSet.ImportOptions!,
                            outStream, Stream.Null);
                    } else {
                        mFileSet.Importer.ConvertFile(fileStream, mFileSet.ImportOptions!,
                            Stream.Null, outStream);
                    }
                } else {
                    // Simply copy the data.
                    fileStream.CopyTo(outStream);
                }
            } finally {
                fileStream?.Dispose();
                appleSingle?.Dispose();
                outerStream?.Dispose();
            }
        }

        #endregion IFileSystem

        #region Utility

        /// <summary>
        /// Generates an array of AddFileEntry objects, sorted by source file path.
        /// </summary>
        /// <remarks>
        /// <para>The order in which entries appear in the AddFileSet is undefined, which may
        /// be confusing for the user if we're showing progress updates, not to mention a little
        /// weird in archives and non-sorting filesystems.</para>
        /// </remarks>
        /// <param name="fileSet"></param>
        /// <returns></returns>
        private static AddFileEntry[] GenerateSortedArray(AddFileSet fileSet) {
            int idx = 0;
            AddFileEntry[] addEntries = new AddFileEntry[fileSet.Count];
            foreach (AddFileEntry entry in fileSet) {
                addEntries[idx++] = entry;
            }
            Array.Sort(addEntries, delegate (AddFileEntry ent1, AddFileEntry ent2) {
                // Try to compare data fork source files.  Use rsrc fork if data doesn't exist.
                string str1 = ent1.HasDataFork ? ent1.FullDataPath : ent1.FullRsrcPath;
                string str2 = ent2.HasDataFork ? ent2.FullDataPath : ent2.FullRsrcPath;
                return string.Compare(str1, str2);
            });
            return addEntries;
        }

        /// <summary>
        /// Copies attributes from an AddFileEntry object to an IFileEntry.  Does not set
        /// FileName or FileNameSeparator (which must be adjusted for target).
        /// </summary>
        /// <param name="addEnt">Source add-file entry.</param>
        /// <param name="newEntry">Destination file entry.</param>
        private static void CopyAttributes(AddFileEntry addEnt, IFileEntry newEntry) {
            // This test isn't ideal, because the Has*Types properties aren't necessarily fixed
            // for all formats.  It should be correct for HFS-only formats though, and we're
            // calling here after filesystem entries are fully-formed.
            bool hasHfsTypesOnly = newEntry.HasHFSTypes && !newEntry.HasProDOSTypes;
            bool gotHfsTypes = (addEnt.HFSFileType != 0 || addEnt.HFSCreator != 0);
            bool gotProTypes = (addEnt.FileType != 0 || addEnt.AuxType != 0);

            if (hasHfsTypesOnly && !gotHfsTypes) {
                // We're adding files to an HFS disk (or other format that only has HFS types),
                // but there's no HFS file type information.
                if (gotProTypes) {
                    // Encode the ProDOS type info in the HFS type fields.
                    FileAttribs.ProDOSToHFS(addEnt.FileType, addEnt.AuxType, out uint hfsFileType,
                        out uint hfsCreator);
                    newEntry.HFSFileType = hfsFileType;
                    newEntry.HFSCreator = hfsCreator;
                } else {
                    // Supply a generic default type.
                    newEntry.HFSFileType = FileAttribs.TYPE_BINA;       // generic binary file
                    newEntry.HFSCreator = FileAttribs.CREATOR_CPII;
                }
            } else {
                // Either it has the ability to store both ProDOS and HFS, or it's HFS-only but
                // we have nonzero HFS types values.  Either way, store what we got.
                newEntry.HFSFileType = addEnt.HFSFileType;
                newEntry.HFSCreator = addEnt.HFSCreator;
            }

            if (!gotProTypes) {
                // Look for a ProDOS type in the HFS type.  This could be an actual encoding
                // (creator='pdos') or just a handy conversion (any 'TEXT' becomes TXT).
                FileAttribs.ProDOSFromHFS(newEntry.HFSFileType, newEntry.HFSCreator,
                    out byte proType, out ushort proAux);
                newEntry.FileType = proType;
                newEntry.AuxType = proAux;
            } else {
                // Nonzero values found, keep them.
                newEntry.FileType = addEnt.FileType;
                newEntry.AuxType = addEnt.AuxType;
            }

            newEntry.CreateWhen = addEnt.CreateWhen;
            newEntry.ModWhen = addEnt.ModWhen;
            newEntry.Access = addEnt.Access;
            // no Comment field in AddFileEntry
        }

        private bool IsCancelPending() {
            CallbackFacts facts = new CallbackFacts(CallbackFacts.Reasons.QueryCancel);
            return mFunc(facts) == CallbackFacts.Results.Cancel;
        }

        #endregion Utility
    }
}
