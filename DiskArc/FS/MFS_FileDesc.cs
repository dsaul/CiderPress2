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

using CommonUtil;
using static DiskArc.Defs;
using static DiskArc.IFileSystem;

namespace DiskArc.FS {
    public class MFS_FileDesc : DiskFileStream {
        // Stream
        public override bool CanRead { get { return FileEntry != null; } }
        public override bool CanSeek { get { return FileEntry != null; } }
        public override bool CanWrite { get { return FileEntry != null && !mIsReadOnly; } }

        public override long Length => mEOF;

        public override long Position {
            get { return mMark; }
            set { Seek(value, SeekOrigin.Begin); }
        }

        // DiskFileStream
        public override FilePart Part { get; }

        internal MFS FileSystem { get; private set; }
        internal MFS_FileEntry FileEntry { get; private set; }

        private string mDebugPathName { get; set; }
        private bool mIsReadOnly;                       // is writing disallowed?
        private bool mInternalOpen;                     // skip informing filesystem of close?

        /// <summary>
        /// Current file length.
        /// The value may change if the file is modified.
        /// </summary>
        private int mEOF;

        /// <summary>
        /// Current file position.  May extend past EOF.
        /// </summary>
        private int mMark;

        /// <summary>
        /// Private constructor.
        /// </summary>
        private MFS_FileDesc(MFS_FileEntry entry, FileAccessMode mode, FilePart part,
                bool internalOpen) {
            FileSystem = entry.FileSystem;
            FileEntry = entry;
            Part = part;
            mInternalOpen = internalOpen;
            mIsReadOnly = (mode == FileAccessMode.ReadOnly);

            mDebugPathName = entry.FullPathName;        // latch name when file opened
        }

        internal static MFS_FileDesc CreateFD(Pascal_FileEntry entry, FileAccessMode mode,
                FilePart part, bool internalOpen) {
            throw new NotImplementedException();
        }

        // Stream
        public override int Read(byte[] buffer, int offset, int count) {
            throw new NotImplementedException();
        }

        // Stream
        public override void Write(byte[] buffer, int offset, int count) {
            throw new NotImplementedException();
        }

        // Stream
        public override long Seek(long offset, SeekOrigin origin) {
            throw new NotImplementedException();
        }

        // Stream
        public override void SetLength(long value) {
            throw new NotImplementedException();
        }

        // Stream
        public override void Flush() {
            throw new NotImplementedException();
        }

        // IDisposable generic finalizer.
        ~MFS_FileDesc() {
            Dispose(false);
        }
        protected override void Dispose(bool disposing) {
            if (disposing) {
                if (!mInternalOpen) {
                    try {
                        // Tell the OS to forget about us.
                        FileSystem.CloseFile(this);
                    } catch (Exception ex) {
                        Debug.WriteLine("FileSystem.CloseFile from fd close failed: " + ex.Message);
                    }
                }
            } else {
                // Finalizer dispose.
                if (FileEntry == null) {
                    Debug.Assert(false, "Finalization didn't get suppressed?");
                } else {
                    Debug.WriteLine("NOTE: GC disposing of file desc object " + this);
                }
            }

            // Mark the fd invalid so future calls will crash or do nothing.
            Invalidate();
        }

        /// <summary>
        /// Marks the file descriptor invalid, clearing various fields to ensure something bad
        /// will happen if we try to use it.
        /// </summary>
        private void Invalidate() {
#pragma warning disable CS8625
            FileSystem = null;
            FileEntry = null;
#pragma warning restore CS8625
        }

        /// <summary>
        /// Throws an exception if the file descriptor has been invalidated.
        /// </summary>
        private void CheckValid() {
            if (FileEntry == null || !FileEntry.IsValid) {
                throw new ObjectDisposedException("File descriptor has been closed (" +
                    mDebugPathName + ")");
            }
        }

        // DiskFileStream
        public override bool DebugValidate(IFileSystem fs, IFileEntry entry) {
            Debug.Assert(entry != null && entry != IFileEntry.NO_ENTRY);
            if (FileSystem == null || FileEntry == null) {
                return false;       // we're invalid
            }
            return (fs == FileSystem && entry == FileEntry);
        }

        public override string ToString() {
            return "[MFS_FileDesc '" + mDebugPathName +
                (FileEntry == null ? " *INVALID*" : "") + "']";
        }
    }
}
