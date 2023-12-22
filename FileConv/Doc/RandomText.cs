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
using DiskArc.FS;
using FileConv.Generic;

namespace FileConv.Doc {
    /// <summary>
    /// DOS/ProDOS random-access text file converter.  The records are output as a CellGrid.
    /// </summary>
    /// <remarks>
    /// <para>This is not a general-purpose converter for files with fixed-length records.  This
    /// is for random-access text files created by BASIC programs.  Every record is a row, and
    /// within a row there can be multiple CR-terminated columns.  The character set is always
    /// low/high ASCII.</para>
    /// </remarks>
    public class RandomText : Converter {
        public const string TAG = "rtext";
        public const string LABEL = "Random-Access Text";
        public const string DESCRIPTION =
            "Converts a DOS or ProDOS random-access text file to spreadsheet form.  Each " +
            "record is output as a row.  Multiple entries within a single record (separated " +
            "by carriage returns) are output as columns within that row. The length option " +
            "may be set to a positive integer value, or left blank to use the file's " +
            "auxiliary type.";
        public const string DISCRIMINATOR = "ProDOS TXT with nonzero auxtype, DOS T (raw mode).";
        public override string Tag => TAG;
        public override string Label => LABEL;
        public override string Description => DESCRIPTION;
        public override string Discriminator => DISCRIMINATOR;

        public const string OPT_LEN = "len";

        public override List<OptionDefinition> OptionDefs { get; protected set; } =
            new List<OptionDefinition>() {
                new OptionDefinition(OPT_LEN, "Record length",
                    OptionDefinition.OptType.IntValue, string.Empty),
            };


        private RandomText() { }

        public RandomText(FileAttribs attrs, Stream? dataStream, Stream? rsrcStream,
                ResourceMgr? resMgr, ConvFlags convFlags, AppHook appHook)
                : base(attrs, dataStream, rsrcStream, resMgr, convFlags, appHook) {
            Applic = TestApplicability();
        }

        protected override Applicability TestApplicability() {
            if (DataStream == null) {
                return Applicability.Not;
            }
            if (FileAttrs.FileType != FileAttribs.FILE_TYPE_TXT) {
                return Applicability.Not;
            }
            if (DataStream is DOS_FileDesc && !IsRawDOS) {
                // File is on a DOS filesystem, but they're not in raw mode.  Allow but put it
                // way down the list.
                return Applicability.ProbablyNot;
            }
            if (DataStream is not DOS_FileDesc && FileAttrs.AuxType == 0) {
                // File is on ProDOS or in a file archive and has a zero auxtype.  This is
                // almost certainly not random-access text, but we'll make it available
                // for manual selection.
                return Applicability.ProbablyNot;
            }

            // Merlin ProDOS source code is TXT with nonzero aux type, so we really want to
            // see a zero byte.
            bool hasZero = HasEarlyZero(DataStream);
            if (hasZero) {
                return Applicability.Probably;
            } else if (FileAttrs.AuxType != 0) {
                return Applicability.Maybe;
            } else {
                return Applicability.ProbablyNot;
            }
        }

        public override Type GetExpectedType(Dictionary<string, string> options) {
            // Match behavior of ConvertFile().
            int len = FileAttrs.AuxType;
            len = GetIntOption(options, OPT_LEN, len);
            if (len <= 0) {
                return typeof(SimpleText);
            } else {
                return typeof(CellGrid);
            }
        }

        public override IConvOutput ConvertFile(Dictionary<string, string> options) {
            if (Applic <= Applicability.Not) {
                Debug.Assert(false);
                return new ErrorText("ERROR");
            }
            Debug.Assert(DataStream != null);

            // Use aux type as record length.  Override with option if present.
            int len = FileAttrs.AuxType;
            len = GetIntOption(options, OPT_LEN, len);

            if (len <= 0) {
                // This is intrusive, but I want it to be obvious.  Anybody who doesn't want
                // it should be using "text" instead of "rtext".
                SimpleText output = new SimpleText();
                output.Text.AppendLine("[ record length is zero, formatting as plain text ]");
                output.Text.AppendLine();
                PlainText.ConvertStream(DataStream, ConvUtil.ExportCharSrc.HighASCII,
                    true, -1, output.Text);
                AddDOSRawNote(DataStream, output);
                return output;
            } else {
                return ConvertStream(DataStream, len);
            }
        }

        private static IConvOutput ConvertStream(Stream dataStream, int length) {
            CellGrid output = new CellGrid();
            dataStream.Position = 0;
            byte[] dataBuf = new byte[length];
            StringBuilder sb = new StringBuilder(length);
            string EOL = Environment.NewLine;

            AddDOSRawNote(dataStream, output);

            int row = 0;
            int remain = (int)dataStream.Length;
            while (remain != 0) {
                int toRead = Math.Min(dataBuf.Length, remain);
                dataStream.ReadExactly(dataBuf, 0, toRead);
                int recLen = RawData.FirstZero(dataBuf, 0, toRead);
                if (recLen < 0) {
                    // Didn't find a zero before we ran out of data.
                    recLen = toRead;
                }
                if (recLen != 0) {
                    // Found record with data in it.
                    sb.Clear();
                    int col = 0;
                    for (int i = 0; i < recLen; i++) {
                        // CR acts as a sub-record separator.
                        if ((dataBuf[i] & 0x7f) == '\r') {
                            if (sb.Length > 0) {
                                output.SetCellValue(col, row, sb.ToString());
                                sb.Clear();
                            }
                            // Advance the column whether it was empty or not.
                            col++;
                        } else {
                            // No need to filter out control chars.
                            char ch = (char)(dataBuf[i] & 0x7f);
                            sb.Append(ch);
                        }
                    }
                    if (sb.Length > 0) {
                        output.SetCellValue(col, row, sb.ToString());
                    }
                }

                remain -= toRead;
                row++;
            }
            return output;
        }

        /// <summary>
        /// Adds a note to the output if the input is a DOS file and we're not in "raw" mode.
        /// </summary>
        /// <param name="dataStream">Data fork input stream.</param>
        /// <param name="output">Converter output object.</param>
        private static void AddDOSRawNote(Stream dataStream, IConvOutput output) {
            if (dataStream is DOS_FileDesc &&
                    ((DOS_FileDesc)dataStream).Part != Defs.FilePart.RawData) {
                output.Notes.AddI("DOS random-access text files should be opened in 'raw' mode.");
            }
        }

        /// <summary>
        /// Checks to see if a file has a $00 byte followed by a nonzero byte.  If this is being
        /// read from a DOS disk (in raw mode), we want to ignore the last sector.
        /// </summary>
        private static bool HasEarlyZero(Stream stream) {
            long len = stream.Length;
            if (stream is DOS_FileDesc) {
                len = (len - 255) & ~0xff;
            }
            if (len <= 0) {
                return false;
            }

            byte[] buf = new byte[8192];
            stream.Position = 0;
            bool foundZero = false;
            while (true) {
                int actual = stream.Read(buf, 0, buf.Length);
                if (actual == 0) {
                    return false;       // EOF reached
                }
                for (int i = 0; i < actual; i++) {
                    if (buf[i] == 0x00) {
                        foundZero = true;
                    } else if (foundZero) {
                        return true;
                    }
                }
            }
        }
    }
}
