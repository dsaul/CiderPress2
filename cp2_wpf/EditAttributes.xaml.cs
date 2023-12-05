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
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using AppCommon;
using CommonUtil;
using DiskArc;
using DiskArc.Arc;
using DiskArc.FS;

namespace cp2_wpf {
    /// <summary>
    /// File entry attribute editor.
    /// </summary>
    public partial class EditAttributes : Window, INotifyPropertyChanged {
        // INotifyPropertyChanged
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string propertyName = "") {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// Set to true when input is valid.  Controls whether the OK button is enabled.
        /// </summary>
        public bool IsValid {
            get { return mIsValid; }
            set { mIsValid = value; OnPropertyChanged(); }
        }
        private bool mIsValid;

        private Brush mDefaultLabelColor = SystemColors.WindowTextBrush;
        private Brush mErrorLabelColor = Brushes.Red;

        /// <summary>
        /// File attributes after edits.  The filename will always be in FullPathName, even
        /// for disk images.
        /// </summary>
        public FileAttribs NewAttribs { get; private set; } = new FileAttribs();

        private object mArchiveOrFileSystem;
        private IFileEntry mFileEntry;
        private IFileEntry mADFEntry;
        private FileAttribs mOldAttribs;


        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="parent">Parent window.</param>
        /// <param name="archiveOrFileSystem">IArchive or IFileSystem object.</param>
        /// <param name="entry">File entry object.</param>
        /// <param name="adfEntry">For MacZip, the ADF header entry; otherwise NO_ENTRY.</param>
        /// <param name="attribs">Current file attributes, from <paramref name="entry"/> or
        ///   MacZip header contents.</param>
        public EditAttributes(Window parent, object archiveOrFileSystem, IFileEntry entry,
                IFileEntry adfEntry, FileAttribs attribs) {
            InitializeComponent();
            Owner = parent;
            DataContext = this;

            mArchiveOrFileSystem = archiveOrFileSystem;
            mFileEntry = entry;
            mADFEntry = adfEntry;
            mOldAttribs = attribs;

            if (archiveOrFileSystem is IArchive) {
                IArchive arc = (IArchive)archiveOrFileSystem;
                mIsValidFunc = arc.IsValidFileName;
                mSyntaxRulesText = "\u2022 " + arc.Characteristics.FileNameSyntaxRules;
                if (arc.Characteristics.DefaultDirSep != IFileEntry.NO_DIR_SEP) {
                    DirSepText = string.Format(DIR_SEP_CHAR_FMT, arc.Characteristics.DefaultDirSep);
                    DirSepTextVisibility = Visibility.Visible;
                } else {
                    DirSepText = string.Empty;
                    DirSepTextVisibility = Visibility.Collapsed;
                }
            } else if (archiveOrFileSystem is IFileSystem) {
                IFileSystem fs = (IFileSystem)archiveOrFileSystem;
                if (entry.IsDirectory && entry.ContainingDir == IFileEntry.NO_ENTRY) {
                    // Volume Directory.
                    mIsValidFunc = fs.IsValidVolumeName;
                    mSyntaxRulesText = "\u2022 " + fs.Characteristics.VolumeNameSyntaxRules;
                } else {
                    mIsValidFunc = fs.IsValidFileName;
                    mSyntaxRulesText = "\u2022 " + fs.Characteristics.FileNameSyntaxRules;
                }
                DirSepText = string.Empty;
                DirSepTextVisibility = Visibility.Collapsed;
            } else {
                throw new NotImplementedException("Can't edit " + archiveOrFileSystem);
            }

            NewAttribs = new FileAttribs(mOldAttribs);

            PrepareFileName();

            PrepareProTypeList();
            ProTypeDescString = FileTypes.GetDescription(attribs.FileType, attribs.AuxType);
            ProAuxString = attribs.AuxType.ToString("X4");

            PrepareHFSTypes();

            PrepareTimestamps();

            PrepareAccess();

            PrepareComment();

            UpdateControls();
        }


        private void Window_Loaded(object sender, RoutedEventArgs e) {
            Loaded_FileType();
        }

        /// <summary>
        /// When window finishes rendering, put the focus on the filename text box, with all of
        /// the text selected.
        /// </summary>
        private void Window_ContentRendered(object sender, EventArgs e) {
            fileNameTextBox.SelectAll();
            fileNameTextBox.Focus();
        }

        /// <summary>
        /// Updates the controls when a change has been made.
        /// </summary>
        private void UpdateControls() {
            // Filename.
            SyntaxRulesForeground = mIsFileNameValid ? mDefaultLabelColor : mErrorLabelColor;
            UniqueNameForeground = mIsFileNameUnique ? mDefaultLabelColor : mErrorLabelColor;

            // ProDOS file and aux type.
            // We're currently always picking the file type from a list, so it's always valid.
            ProAuxForeground = mProAuxValid ? mDefaultLabelColor : mErrorLabelColor;
            if (mProAuxValid) {
                ProTypeDescString =
                    FileTypes.GetDescription(NewAttribs.FileType, NewAttribs.AuxType);
            } else {
                ProTypeDescString = string.Empty;
            }

            // HFS file type and creator.
            HFSTypeForeground = mHFSTypeValid ? mDefaultLabelColor : mErrorLabelColor;
            HFSCreatorForeground = mHFSCreatorValid ? mDefaultLabelColor : mErrorLabelColor;

            // Timestamps.
            CreateWhenForeground = mCreateWhenValid ? mDefaultLabelColor : mErrorLabelColor;
            ModWhenForeground = mModWhenValid ? mDefaultLabelColor : mErrorLabelColor;

            IsValid = mIsFileNameValid && mIsFileNameUnique && mProAuxValid &&
                mHFSTypeValid && mHFSCreatorValid && mCreateWhenValid && mModWhenValid;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e) {
            DialogResult = true;
        }


        #region Filename

        //
        // Filename.
        //

        public string SyntaxRulesText {
            get { return mSyntaxRulesText; }
            set { mSyntaxRulesText = value; OnPropertyChanged(); }
        }
        private string mSyntaxRulesText;

        public Brush SyntaxRulesForeground {
            get { return mSyntaxRulesForeground; }
            set { mSyntaxRulesForeground = value; OnPropertyChanged(); }
        }
        private Brush mSyntaxRulesForeground = SystemColors.WindowTextBrush;

        public Brush UniqueNameForeground {
            get { return mUniqueNameForeground; }
            set { mUniqueNameForeground = value; OnPropertyChanged(); }
        }
        private Brush mUniqueNameForeground = SystemColors.WindowTextBrush;
        public Visibility UniqueTextVisibility { get; private set; } = Visibility.Visible;

        public string DirSepText { get; private set; }
        public Visibility DirSepTextVisibility { get; private set; }
        private const string DIR_SEP_CHAR_FMT = "\u2022 Directory separator character is '{0}'.";

        private delegate bool IsValidFileNameFunc(string name);
        private IsValidFileNameFunc mIsValidFunc;

        private bool mIsFileNameValid;
        private bool mIsFileNameUnique;

        /// <summary>
        /// Filename string.
        /// </summary>
        public string FileName {
            get { return NewAttribs.FullPathName; }
            set {
                NewAttribs.FullPathName = value;
                OnPropertyChanged();
                CheckFileNameValidity(out mIsFileNameValid, out mIsFileNameUnique);
                UpdateControls();
            }
        }

        private void CheckFileNameValidity(out bool isValid, out bool isUnique) {
            isValid = mIsValidFunc(NewAttribs.FullPathName);
            isUnique = true;
            if (mArchiveOrFileSystem is IArchive) {
                IArchive arc = (IArchive)mArchiveOrFileSystem;
                // Check name for uniqueness.
                if (arc.TryFindFileEntry(NewAttribs.FullPathName, out IFileEntry entry) &&
                        entry != mFileEntry) {
                    isUnique = false;
                }
            } else {
                IFileSystem fs = (IFileSystem)mArchiveOrFileSystem;
                if (mFileEntry.ContainingDir != IFileEntry.NO_ENTRY) {
                    // Not editing the volume dir attributes.  Check name for uniqueness.
                    if (fs.TryFindFileEntry(mFileEntry.ContainingDir, NewAttribs.FullPathName,
                            out IFileEntry entry) && entry != mFileEntry) {
                        isUnique = false;
                    }
                }
            }
        }

        private void PrepareFileName() {
            if (mFileEntry is DOS_FileEntry && mFileEntry.IsDirectory) {
                // The DOS volume name is formatted as "DOS-nnn", but we just want the number.
                NewAttribs.FullPathName = ((DOS)mArchiveOrFileSystem).VolumeNum.ToString("D3");
            } else if (mArchiveOrFileSystem is IArchive) {
                NewAttribs.FullPathName = mOldAttribs.FullPathName;
            } else {
                NewAttribs.FullPathName = mOldAttribs.FileNameOnly;
            }
            CheckFileNameValidity(out mIsFileNameValid, out mIsFileNameUnique);
        }

        #endregion Filename

        #region File Type

        //
        // File types.
        //

        public Visibility ProTypeVisibility { get; private set; } = Visibility.Visible;
        public Visibility HFSTypeVisibility { get; private set; } = Visibility.Visible;

        public class ProTypeListItem {
            public string Label { get; private set; }
            public byte Value { get; private set; }

            public ProTypeListItem(string label, byte value) {
                Label = label;
                Value = value;
            }
        }

        /// <summary>
        /// List of suitable types from the ProDOS type list.  This is the ItemsSource for the
        /// combo box.
        /// </summary>
        public List<ProTypeListItem> ProTypeList { get; } = new List<ProTypeListItem>();

        /// <summary>
        /// True if the ProDOS type list is enabled.  It will be visible but disabled in
        /// certain circumstances, such as when editing attributes for a directory entry.
        /// </summary>
        public bool IsProTypeListEnabled { get; private set; } = true;

        /// <summary>
        /// True if the ProDOS aux type entry field is enabled.
        /// </summary>
        public bool IsProAuxEnabled { get; private set; } = true;

        public string ProTypeDescString {
            get { return mProTypeDescString; }
            set { mProTypeDescString = value; OnPropertyChanged(); }
        }
        public string mProTypeDescString = string.Empty;

        /// <summary>
        /// Aux type input field (0-4 hex chars).  Must be a valid hex value or empty string.
        /// </summary>
        public string ProAuxString {
            get { return mProAuxString; }
            set {
                mProAuxString = value;
                mProAuxValid = true;
                OnPropertyChanged();
                if (string.IsNullOrEmpty(value)) {
                    NewAttribs.AuxType = 0;
                } else {
                    try {
                        NewAttribs.AuxType = Convert.ToUInt16(value, 16);
                    } catch (Exception) {       // ArgumentException or FormatException
                        mProAuxValid = false;
                    }
                }
                UpdateControls();
            }
        }
        private string mProAuxString = string.Empty;
        private bool mProAuxValid = true;

        // Aux type label color, set to red on error.
        public Brush ProAuxForeground {
            get { return mProAuxForeground; }
            set { mProAuxForeground = value; OnPropertyChanged(); }
        }
        private Brush mProAuxForeground = SystemColors.WindowTextBrush;

        public string HFSTypeCharsString {
            get { return mHFSTypeCharsString; }
            set {
                mHFSTypeCharsString = value;
                OnPropertyChanged();
                mHFSTypeHexString = SetHexFromChars(value, out uint newNum, out mHFSTypeValid);
                OnPropertyChanged(nameof(HFSTypeHexString));
                NewAttribs.HFSFileType = newNum;
                UpdateControls();
            }
        }
        private string mHFSTypeCharsString = string.Empty;

        public string HFSTypeHexString {
            get { return mHFSTypeHexString; }
            set {
                mHFSTypeHexString = value;
                OnPropertyChanged();
                mHFSTypeCharsString = SetCharsFromHex(value, out uint newNum, out mHFSTypeValid);
                OnPropertyChanged(nameof(HFSTypeCharsString));
                NewAttribs.HFSFileType = newNum;
                UpdateControls();
            }
        }
        private string mHFSTypeHexString = string.Empty;
        private bool mHFSTypeValid = true;

        public Brush HFSTypeForeground {
            get { return mHFSTypeForeground; }
            set { mHFSTypeForeground = value; OnPropertyChanged(); }
        }
        private Brush mHFSTypeForeground = SystemColors.WindowTextBrush;

        public string HFSCreatorCharsString {
            get { return mHFSCreatorCharsString; }
            set {
                mHFSCreatorCharsString = value;
                OnPropertyChanged();
                mHFSCreatorHexString =
                    SetHexFromChars(value, out uint newNum, out mHFSCreatorValid);
                OnPropertyChanged(nameof(HFSCreatorHexString));
                NewAttribs.HFSCreator = newNum;
                UpdateControls();
            }
        }
        private string mHFSCreatorCharsString = string.Empty;

        public string HFSCreatorHexString {
            get { return mHFSCreatorHexString; }
            set {
                mHFSCreatorHexString = value;
                OnPropertyChanged();
                mHFSCreatorCharsString =
                    SetCharsFromHex(value, out uint newNum, out mHFSCreatorValid);
                OnPropertyChanged(nameof(HFSCreatorCharsString));
                NewAttribs.HFSCreator = newNum;
                UpdateControls();
            }
        }
        private string mHFSCreatorHexString = string.Empty;
        private bool mHFSCreatorValid = true;

        public Brush HFSCreatorForeground {
            get { return mHFSCreatorForeground; }
            set { mHFSCreatorForeground = value; OnPropertyChanged(); }
        }
        private Brush mHFSCreatorForeground = SystemColors.WindowTextBrush;

        /// <summary>
        /// Computes the hexadecimal field when the 4-char field changes.
        /// </summary>
        /// <param name="charValue">New character constant.</param>
        /// <param name="newNum">Result: numeric value.</param>
        /// <param name="isValid">Result: true if string is valid.</param>
        /// <returns>Hexadecimal string.</returns>
        private static string SetHexFromChars(string charValue, out uint newNum, out bool isValid) {
            string newHexStr;
            isValid = true;
            if (string.IsNullOrEmpty(charValue)) {
                newNum = 0;
                newHexStr = string.Empty;
            } else if (charValue.Length == 4) {
                // set hex value
                newNum = MacChar.IntifyMacConstantString(charValue);
                newHexStr = newNum.ToString("X8");
            } else {
                // incomplete string, erase hex value
                newNum = 0;
                newHexStr = string.Empty;
                isValid = false;
            }
            return newHexStr;
        }

        /// <summary>
        /// Computes the 4-char field value when the hexadecimal field changes.
        /// </summary>
        /// <param name="hexStr">New hex string.</param>
        /// <param name="newNum">Result: numeric value.</param>
        /// <param name="isValid">Result: true if string is valid.</param>
        /// <returns>Character string.</returns>
        private static string SetCharsFromHex(string hexStr, out uint newNum, out bool isValid) {
            string newCharStr;
            isValid = true;
            if (string.IsNullOrEmpty(hexStr)) {
                newCharStr = string.Empty;
                newNum = 0;
            } else {
                try {
                    newNum = Convert.ToUInt32(hexStr, 16);
                    newCharStr = MacChar.StringifyMacConstant(newNum);
                } catch (Exception) {       // ArgumentException or FormatException
                    isValid = false;
                    newNum = 0;
                    newCharStr = string.Empty;
                }
            }
            return newCharStr;
        }

        private static readonly byte[] DOS_TYPES = {
            FileAttribs.FILE_TYPE_TXT,      // T
            FileAttribs.FILE_TYPE_INT,      // I
            FileAttribs.FILE_TYPE_BAS,      // A
            FileAttribs.FILE_TYPE_BIN,      // B
            FileAttribs.FILE_TYPE_F2,       // S
            FileAttribs.FILE_TYPE_REL,      // R
            FileAttribs.FILE_TYPE_F3,       // AA
            FileAttribs.FILE_TYPE_F4        // BB
        };
        private static readonly byte[] PASCAL_TYPES = {
            FileAttribs.FILE_TYPE_NON,      // untyped
            FileAttribs.FILE_TYPE_BAD,      // bad blocks
            FileAttribs.FILE_TYPE_PCD,      // code
            FileAttribs.FILE_TYPE_PTX,      // text
            FileAttribs.FILE_TYPE_F3,       // info
            FileAttribs.FILE_TYPE_PDA,      // data
            FileAttribs.FILE_TYPE_F4,       // graf
            FileAttribs.FILE_TYPE_FOT,      // foto
            FileAttribs.FILE_TYPE_F5        // securedir
        };

        /// <summary>
        /// Prepares the DOS/ProDOS type pop-up.
        /// </summary>
        private void PrepareProTypeList() {
            if (mFileEntry is DOS_FileEntry) {
                if (mFileEntry.IsDirectory) {
                    // Editing VTOC volume number.
                    IsProTypeListEnabled = false;
                    IsProAuxEnabled = false;
                    ProTypeVisibility = Visibility.Collapsed;
                } else {
                    // Editing DOS file type.  In theory we want to enable/disable the aux type
                    // field based on the current file type, but it's not essential.
                    foreach (byte type in DOS_TYPES) {
                        string abbrev = FileTypes.GetDOSTypeAbbrev(type);
                        ProTypeList.Add(new ProTypeListItem(abbrev, type));
                    }
                }
            } else if (mFileEntry is Pascal_FileEntry) {
                IsProAuxEnabled = false;
                if (mFileEntry.IsDirectory) {
                    // Editing volume name.
                    IsProTypeListEnabled = false;
                    ProTypeVisibility = Visibility.Collapsed;
                } else {
                    // Editing Pascal file type.
                    foreach (byte type in PASCAL_TYPES) {
                        string abbrev = FileTypes.GetPascalTypeName(type);
                        ProTypeList.Add(new ProTypeListItem(abbrev, type));
                    }
                }
            } else if (mFileEntry.HasProDOSTypes || mADFEntry != IFileEntry.NO_ENTRY) {
                for (int type = 0; type < 256; type++) {
                    string abbrev = FileTypes.GetFileTypeAbbrev(type);
                    if (abbrev[0] == '$') {
                        abbrev = "???";
                    }
                    string label = abbrev + " $" + type.ToString("X2");
                    ProTypeList.Add(new ProTypeListItem(label, (byte)type));
                }

                IsProTypeListEnabled = IsProAuxEnabled = (!mFileEntry.IsDirectory);
            } else {
                IsProTypeListEnabled = IsProAuxEnabled = false;
                ProTypeVisibility = Visibility.Collapsed;
            }

            if (mFileEntry.IsDirectory && mFileEntry.ContainingDir == IFileEntry.NO_ENTRY) {
                // Editing volume dir name or VTOC volume number.
                UniqueTextVisibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// Initializes the fields during construction.
        /// </summary>
        private void PrepareHFSTypes() {
            if (mADFEntry == IFileEntry.NO_ENTRY && !mFileEntry.HasHFSTypes) {
                // Not MacZip AppleDouble, doesn't have HFS types.
                HFSTypeVisibility = Visibility.Collapsed;
            }

            // Set the hex string; automatically sets the 4-char string and "valid" flag.
            if (NewAttribs.HFSFileType == 0) {
                HFSTypeHexString = string.Empty;
            } else {
                HFSTypeHexString = NewAttribs.HFSFileType.ToString("X8");
            }
            if (NewAttribs.HFSCreator == 0) {
                HFSCreatorHexString = string.Empty;
            } else {
                HFSCreatorHexString = NewAttribs.HFSCreator.ToString("X8");
            }
        }

        private void Loaded_FileType() {
            // Set the selected entry in the DOS/ProDOS type pop-up.  If ProDOS types aren't
            // relevant, the list will be empty and this won't do anything.
            for (int i = 0; i < ProTypeList.Count; i++) {
                if (ProTypeList[i].Value == NewAttribs.FileType) {
                    proTypeCombo.SelectedIndex = i;
                    break;
                }
            }
            if (ProTypeList.Count != 0 && proTypeCombo.SelectedIndex < 0) {
                // This can happen when editing the DOS volume, which is given the DIR type,
                // but shouldn't happen otherwise.
                Debug.Assert(mFileEntry is DOS_FileEntry, "no ProDOS type matched");
                proTypeCombo.SelectedIndex = 0;
            }
        }

        private void ProTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) {
            int selIndex = proTypeCombo.SelectedIndex;
            if (selIndex >= 0) {
                NewAttribs.FileType = ProTypeList[selIndex].Value;
                Debug.WriteLine("ProDOS file type: $" + NewAttribs.FileType.ToString("x2"));
            }
            UpdateControls();
        }

        #endregion File Type

        #region Timestamps

        //
        // Dates.
        //

        public Visibility TimestampVisibility { get; private set; } = Visibility.Visible;

        public DateTime TimestampStart { get; set; }
        public DateTime TimestampEnd { get; set; }

        public DateTime? CreateDate {
            get { return mCreateDate; }
            set {
                // This fires twice when a change is made (using a DatePicker control).  The
                // change is not published until the focus leaves the field, regardless of how
                // the binding is configured.
                mCreateDate = value;
                OnPropertyChanged();
                NewAttribs.CreateWhen = DateTimeUpdated(mCreateDate, mCreateTimeString,
                    out mCreateWhenValid);
                UpdateControls();
            }
        }
        private DateTime? mCreateDate;

        public string CreateTimeString {
            get { return mCreateTimeString; }
            set {
                mCreateTimeString = value;
                OnPropertyChanged();
                NewAttribs.CreateWhen = DateTimeUpdated(mCreateDate, mCreateTimeString,
                    out mCreateWhenValid);
                UpdateControls();
            }
        }
        private string mCreateTimeString = string.Empty;
        private bool mCreateWhenValid = true;

        public bool CreateWhenEnabled { get; private set; } = true;

        public DateTime? ModDate {
            get { return mModDate; }
            set {
                // This fires twice when a change is made (using a DatePicker control).  The
                // change is not published until the focus leaves the field, regardless of how
                // the binding is configured.
                mModDate = value;
                OnPropertyChanged();
                NewAttribs.ModWhen = DateTimeUpdated(mModDate, mModTimeString,
                    out mModWhenValid);
                UpdateControls();
            }
        }
        private DateTime? mModDate;

        public string ModTimeString {
            get { return mModTimeString; }
            set {
                mModTimeString = value;
                OnPropertyChanged();
                NewAttribs.ModWhen = DateTimeUpdated(mModDate, mModTimeString,
                    out mModWhenValid);
                UpdateControls();
            }
        }
        private string mModTimeString = string.Empty;
        private bool mModWhenValid = true;

        public Brush CreateWhenForeground {
            get { return mCreateWhenForeground; }
            set { mCreateWhenForeground = value; OnPropertyChanged(); }
        }
        private Brush mCreateWhenForeground = SystemColors.WindowTextBrush;

        public Brush ModWhenForeground {
            get { return mModWhenForeground; }
            set { mModWhenForeground = value; OnPropertyChanged(); }
        }
        private Brush mModWhenForeground = SystemColors.WindowTextBrush;

        /// <summary>
        /// Time pattern.  We allow "1:23", "23:45", and "34:56:78".
        /// </summary>
        private const string TIME_PATTERN = @"^(\d{1,2}):(\d\d)(?>:(\d\d))?$";
        private static Regex sTimeRegex = new Regex(TIME_PATTERN);

        /// <summary>
        /// Recomputes the full date/time and input validity.
        /// </summary>
        /// <param name="ndt">DateTime value from DatePicker.</param>
        /// <param name="timeStr">String from time input field.</param>
        /// <param name="isValid">Result: true if inputs are valid.</param>
        /// <returns>Combined date/time.</returns>
        private DateTime DateTimeUpdated(DateTime? ndt, string timeStr, out bool isValid) {
            isValid = true;
            if (ndt == null) {
                return TimeStamp.NO_DATE;
            }
            DateTime dt = (DateTime)ndt;
            DateTime newWhen;
            if (!string.IsNullOrEmpty(timeStr)) {
                MatchCollection matches = sTimeRegex.Matches(timeStr);
                if (matches.Count != 1) {
                    isValid = false;
                    return TimeStamp.NO_DATE;
                }
                int hours = int.Parse(matches[0].Groups[1].Value);
                int minutes = int.Parse(matches[0].Groups[2].Value);
                int seconds = 0;
                if (!string.IsNullOrEmpty(matches[0].Groups[3].Value)) {
                    seconds = int.Parse(matches[0].Groups[3].Value);
                }
                if (hours >= 24 || minutes >= 60 || seconds >= 60) {
                    isValid = false;
                    return TimeStamp.NO_DATE;
                }

                newWhen = new DateTime(dt.Year, dt.Month, dt.Day, hours, minutes, seconds,
                    DateTimeKind.Local);
            } else {
                DateTime newDt = new DateTime(dt.Year, dt.Month, dt.Day);
                newWhen = DateTime.SpecifyKind(newDt, DateTimeKind.Local);
            }

            isValid = newWhen >= TimestampStart && newWhen <= TimestampEnd;
            return newWhen;
        }

        /// <summary>
        /// Prepares properties during construction.
        /// </summary>
        private void PrepareTimestamps() {
            if (mArchiveOrFileSystem is IArchive) {
                if (mADFEntry == IFileEntry.NO_ENTRY) {
                    IArchive arc = (IArchive)mArchiveOrFileSystem;
                    TimestampStart = arc.Characteristics.TimeStampStart;
                    TimestampEnd = arc.Characteristics.TimeStampEnd;
                } else {
                    // MacZip AppleDouble
                    TimestampStart = AppleSingle.SCharacteristics.TimeStampStart;
                    TimestampEnd = AppleSingle.SCharacteristics.TimeStampEnd;
                }
            } else {
                IFileSystem fs = (IFileSystem)mArchiveOrFileSystem;
                TimestampStart = fs.Characteristics.TimeStampStart;
                TimestampEnd = fs.Characteristics.TimeStampEnd;
            }
            //Debug.WriteLine("Timestamp date range: " + TimestampStart + " - " + TimestampEnd);

            if (TimestampStart == TimestampEnd) {
                TimestampVisibility = Visibility.Collapsed;
            }

            // Disable creation date input for formats that don't support it.
            if ((mArchiveOrFileSystem is Zip && mADFEntry == IFileEntry.NO_ENTRY) ||
                    mArchiveOrFileSystem is GZip || mArchiveOrFileSystem is Pascal) {
                CreateWhenEnabled = false;
            }

            if (TimeStamp.IsValidDate(NewAttribs.CreateWhen)) {
                mCreateDate = NewAttribs.CreateWhen;
                mCreateTimeString = NewAttribs.CreateWhen.ToString("HH:mm:ss");
            } else {
                mCreateDate = null;
                mCreateTimeString = string.Empty;
            }
            if (TimeStamp.IsValidDate(NewAttribs.ModWhen)) {
                mModDate = NewAttribs.ModWhen;
                mModTimeString = NewAttribs.ModWhen.ToString("HH:mm:ss");
            } else {
                mModDate = null;
                mModTimeString = string.Empty;
            }
            mCreateWhenValid = mModWhenValid = true;
        }

        #endregion Timestamps

        #region Access Flags

        //
        // Access flags.
        //

        public Visibility AccessVisibility { get; private set; } = Visibility.Visible;

        public Visibility ShowLockedOnlyVisibility { get; private set; } = Visibility.Visible;
        public Visibility ShowAllFlagsVisibility { get; private set; } = Visibility.Visible;

        // Bits to modify when flipping between locked and unlocked.  All other bits are
        // left unchanged, except that we want to enable "read" access when unlocking just
        // in case they're trying to clear a file with no permissions at all.
        private const byte FILE_ACCESS_TOGGLE = (byte)
            (FileAttribs.AccessFlags.Write |
            FileAttribs.AccessFlags.Rename |
            FileAttribs.AccessFlags.Delete);

        public bool AccessLocked {
            get { return mAccessLocked; }
            set {
                mAccessLocked = value;
                OnPropertyChanged();
                if (value) {
                    NewAttribs.Access = (byte)(NewAttribs.Access & ~FILE_ACCESS_TOGGLE);
                } else {
                    NewAttribs.Access |= FILE_ACCESS_TOGGLE;
                    NewAttribs.Access |= (byte)FileAttribs.AccessFlags.Read;
                }
            }
        }
        private bool mAccessLocked;

        public bool AccessRead {
            get { return mAccessRead; }
            set {
                mAccessRead = value;
                OnPropertyChanged();
                if (value) {
                    NewAttribs.Access |= (byte)FileAttribs.AccessFlags.Read;
                } else {
                    NewAttribs.Access =
                        (byte)(NewAttribs.Access & (byte)~FileAttribs.AccessFlags.Read);
                }
            }
        }
        private bool mAccessRead;
        public bool AccessReadEnabled { get; private set; } = true;

        public bool AccessWrite {
            get { return mAccessWrite; }
            set {
                mAccessWrite = value;
                OnPropertyChanged();
                if (value) {
                    NewAttribs.Access |= (byte)FileAttribs.AccessFlags.Write;
                } else {
                    NewAttribs.Access =
                        (byte)(NewAttribs.Access & (byte)~FileAttribs.AccessFlags.Write);
                }
            }
        }
        private bool mAccessWrite;

        public bool AccessRename {
            get { return mAccessRename; }
            set {
                mAccessRename = value;
                OnPropertyChanged();
                if (value) {
                    NewAttribs.Access |= (byte)FileAttribs.AccessFlags.Rename;
                } else {
                    NewAttribs.Access =
                        (byte)(NewAttribs.Access & (byte)~FileAttribs.AccessFlags.Rename);
                }
            }
        }
        private bool mAccessRename;
        public bool AccessRenameEnabled { get; private set; } = true;

        public bool AccessDelete {
            get { return mAccessDelete; }
            set {
                mAccessDelete = value;
                OnPropertyChanged();
                if (value) {
                    NewAttribs.Access |= (byte)FileAttribs.AccessFlags.Delete;
                } else {
                    NewAttribs.Access =
                        (byte)(NewAttribs.Access & (byte)~FileAttribs.AccessFlags.Delete);
                }
            }
        }
        private bool mAccessDelete;
        public bool AccessDeleteEnabled { get; private set; } = true;


        public bool AccessBackup {
            get { return mAccessBackup; }
            set {
                mAccessBackup = value;
                OnPropertyChanged();
                if (value) {
                    NewAttribs.Access |= (byte)FileAttribs.AccessFlags.Backup;
                } else {
                    NewAttribs.Access =
                        (byte)(NewAttribs.Access & (byte)~FileAttribs.AccessFlags.Backup);
                }
            }
        }
        private bool mAccessBackup;

        public bool AccessInvisible {
            get { return mAccessInvisible; }
            set {
                mAccessInvisible = value;
                OnPropertyChanged();
                if (value) {
                    NewAttribs.Access |= (byte)FileAttribs.AccessFlags.Invisible;
                } else {
                    NewAttribs.Access =
                        (byte)(NewAttribs.Access & (byte)~FileAttribs.AccessFlags.Invisible);
                }
            }
        }
        private bool mAccessInvisible;
        public bool AccessInvisibleEnabled { get; private set; } = true;

        /// <summary>
        /// Prepares the access flag UI during construction.
        /// </summary>
        private void PrepareAccess() {
            AccessInvisibleEnabled = false;

            // No access flags in plain ZIP or gzip.  Technically they could have some
            // system-specific permissions in the "extra" data, but DiskArc doesn't currently
            // support that.
            if ((mArchiveOrFileSystem is Zip && mADFEntry == IFileEntry.NO_ENTRY) ||
                    mArchiveOrFileSystem is GZip || mArchiveOrFileSystem is Pascal) {
                AccessVisibility = Visibility.Collapsed;
                return;
            }
            if (mFileEntry.IsDirectory && mFileEntry.ContainingDir == IFileEntry.NO_ENTRY) {
                // Editing volume dir name or VTOC volume number.  ProDOS volume directory
                // headers do have access flags, but it's unclear why anybody would want to
                // edit them.
                AccessVisibility = Visibility.Collapsed;
                return;
            }

            if (mArchiveOrFileSystem is ProDOS || mArchiveOrFileSystem is NuFX ||
                    mArchiveOrFileSystem is CPM) {
                // Expose full set of flags.
                ShowAllFlagsVisibility = Visibility.Visible;
                ShowLockedOnlyVisibility = Visibility.Collapsed;
                if (mArchiveOrFileSystem is CPM) {
                    AccessReadEnabled = AccessRenameEnabled = AccessDeleteEnabled = false;
                }
                mAccessRead = (NewAttribs.Access & (byte)FileAttribs.AccessFlags.Read) != 0;
                mAccessWrite = (NewAttribs.Access & (byte)FileAttribs.AccessFlags.Write) != 0;
                mAccessRename = (NewAttribs.Access & (byte)FileAttribs.AccessFlags.Rename) != 0;
                mAccessBackup = (NewAttribs.Access & (byte)FileAttribs.AccessFlags.Backup) != 0;
                mAccessDelete = (NewAttribs.Access & (byte)FileAttribs.AccessFlags.Delete) != 0;
                mAccessInvisible =
                    (NewAttribs.Access & (byte)FileAttribs.AccessFlags.Invisible) != 0;
            } else {
                // Expose generic "locked" flag, and maybe "invisible" flag.
                ShowAllFlagsVisibility = Visibility.Collapsed;
                ShowLockedOnlyVisibility = Visibility.Visible;

                // "Locked" flag depends on value of "write" bit.
                mAccessLocked = (NewAttribs.Access & (byte)FileAttribs.AccessFlags.Write) == 0;

                // Anything with ProDOS access flags has the "hidden" bit.  HFS has an "invisible"
                // flag in the FinderInfo fdFlags, but DiskArc doesn't currently support it.
                AccessInvisibleEnabled =
                    mArchiveOrFileSystem is AppleSingle ||
                    mArchiveOrFileSystem is Binary2 ||
                    mADFEntry != IFileEntry.NO_ENTRY;
                mAccessInvisible =
                    (NewAttribs.Access & (byte)FileAttribs.AccessFlags.Invisible) != 0;
            }
        }

        #endregion Access Flags

        #region Comment

        //
        // Comment.
        //

        public Visibility CommentVisibility { get; private set; } = Visibility.Visible;

        public string CommentText {
            get { return mCommentText; }
            set {
                mCommentText = value;
                OnPropertyChanged();
                // TextBox limits the comment to 65535 chars.  Both Zip and NuFX can handle that,
                // though it's risky for Zip because UTF-8 expansion might exceed the byte limit.
                NewAttribs.Comment = value;
            }
        }
        private string mCommentText = string.Empty;

        private void PrepareComment() {
            // Only available for ZIP and NuFX.
            if ((mArchiveOrFileSystem is not Zip || mADFEntry != IFileEntry.NO_ENTRY) &&
                    mArchiveOrFileSystem is not NuFX) {
                CommentVisibility = Visibility.Collapsed;
                return;
            }

            mCommentText = NewAttribs.Comment;
        }

        #endregion Comment
    }
}
