﻿# CP/M Filesystem #

## Primary References ##

- cpmtools "man 5 cpm": https://manpages.ubuntu.com/manpages/lunar/en/man5/cpm.5.html
- https://www.seasip.info/Cpm/formats.html
- CP/AM 5.1 manual, https://archive.org/details/AE_Z-80_Plus_CPAM_5.1_Manual_v1.21

## General ##

CP/M, which stands for "Control Program/Monitor" or "Control Program for Microcomputers", is
an operating system created in 1974 by Gary Kildall.  There were a few official releases over the
years, as well as a number of third-party variants that extended the system in various ways.

The Apple II was first able to run CP/M in 1980, when Microsoft introduced the "Z-80 SoftCard",
based on the Zilog Z-80 CPU.  This was later renamed the "Microsoft SoftCard", and eventually
succeeded by the "Premium Softcard IIe".  The SoftCard was Microsoft's largest revenue source in
1980, and was briefly the most popular CP/M platform.  Other CP/M cards included the PCPI
Appli-card and Applied Engineering Z-80 Plus.  The latter came with a special version of CP/M,
called CP/AM, that supported 3.5" floppy drives and Sider hard drives.

The filesystem has some awkward characteristics:
 1. Disks aren't self-describing.  Most filesystems have a block at a well-known location that
	describes the structure of the disk.  CP/M has no such feature.  Systems were expected to
	"just know" the location and size of the various areas of the disk.  For example, the size of
	a "block" can vary from 1KB to 16KB, but nothing in the filesystem will tell you that.
 2. Files may or may not have an exact length.  All versions of CP/M record the length of a
	file in 128-byte records.  Some versions don't narrow it down any further, some versions
	record how many bytes are used in the last record, some versions record how many bytes are
	*unused* in the last record.
 3. On the Apple II, 5.25" disks use a CP/M-specific sector skew.  Except that some disks appear
	to use ProDOS sector skew for the boot tracks.

Supporting all possible CP/M disk layouts is a difficult task.  This document is only concerned
with the formats found on the Apple II (see [Apple II Disk Formats](#apple-ii-disk-formats)).
These were based on CP/M v2.2, though there have been modern efforts to port v3.

### Filenames and User Numbers ###

CP/M uses the classic "8+3" filename, where an 8-character filename is followed by a 3-character
extension.  These are padded with spaces to fill out the field.  The extension can be left blank,
but the filename may not.  The filename and extension may include any printable 7-bit ASCII
character except `<>.,;:=?*[]`.  (Note the list includes '.', so it can only be used as the
extension delimiter.)

Filenames may be entered in lower case, but are converted to upper case in the disk directory.

Some file attribute flags are stored in the high bits of some of the filename bytes, e.g. setting
the high bit of the first character in the extension indicates that the file is read-only.

Directory entries have a "user number" associated with them, usually 0-15, sometimes 0-31.  These
could be considered subdirectories, since you can have multiple files with the same name so long
as the user number is different.

Common extensions:

ext | purpose
--- | -------
ASM | assembly language source file
BAK | backup copy file (created by editor)
BAS | BASIC source code file
C   | C language source code
COB | COBOL source code file
COM | transient command program file
DOC | documentation file
FTN | FORTRAN source code file
HEX | hex format source code file
LIB | library file
MAC | assembly language macro file
PAS | Pascal source code file
PLI | PL/I source file
PRN | print file (assembly language listing)
REL | relocatable machine language file
SUB | command list for SUBMIT execution
TXT | text file
$$$ | temporary file

## Disk Structure ##

The CP/M filesystem is divided into three general fixed-size areas.  At the start of the disk is
the system area, which holds the boot blocks and operating system image.  It can be omitted
entirely if a disk doesn't need to be bootable.  Immediately following that is the volume
directory, the first block of which is addressed as block 0.  (I will try to refer to CP/M blocks
as "allocation blocks" to avoid confusion with the 512-byte blocks used by other systems.)  The
directory occupies one or more consecutive alloc blocks, and is immediately followed by data
storage (although a gap between directory and data is possible).  The directory cannot expand.

Files are regarded as a series of 128-byte records.  Until CP/M v3, it wasn't possible to specify
a file length with finer granularity.

The directory is a series of 32-byte "extent" records.  Each extent spans 16KB (usually), so
larger files will have multiple entries in the directory.  The part of the file that the extent
spans is specified by an extent number.  Each entry looks like this:
```
  ST F0 F1 F2 F3 F4 F5 F6 F7 E0 E1 E2 XL BC XH RC
  AL AL AL AL AL AL AL AL AL AL AL AL AL AL AL AL
```
The byte fields are:
 - `ST`: status.  Possible values:
	- 0-15: user number.
	- 16-31: could be user number, could be a password extent (CP/M v3).
	- 32: disc label (CP/M v3).
	- 33: timestamp (CP/M v3 or third-party mod to CP/M v2.2).
	- 0xe5: entry is unused
 - `F0-F7`: filename, padded with spaces.  Some third-party variants encoded attributes in the
	high bits.
 - `E0-E2`: extension, padded with spaces.  The high bits are used for attributes:
	- `E0`: file is read-only
	- `E1`: file is a system file (hidden)
	- `E2`: file has been archived (for backup software)
 - `XL`: extent number, low part.  Only values 0-31 are used; upper 3 bits are zero.  (This
	limit appears to be a holdover from CP/M v1.4, which only allowed 32 extents per file.)
	If a single extent spans 16KB, 32 extents would cover 512KB.
 - `BC`: count of bytes used in the last record (0-128).  Or possibly the count of bytes *not*
   used in the last record.  Either way, zero is understood to mean that all bytes are used.
   See https://www.seasip.info/Cpm/bytelen.html#lrbc .  (This field is sometimes called `S1`.)
 - `XH`: extent number, high part.  Only values 0-63 are used; upper 2 bits are zero.  The
	extent number is `(XH * 32) + XL` (0-2047) in v3.  In v2.2 it was capped at 512.  (This
	field is sometimes called `S2`.)
 - `RC`: number of 128-byte records used in this extent (0-128).  If it's equal to 128 then the
	extent is full, and there may be another one.  If it's zero then the extent is empty.  This
	is expected to be 128 for all extents except the last.
 - `AL`: allocation block number.  For volumes with 256 or fewer allocation blocks, each entry
   is stored in a single byte.  For larger volumes, the entries are paired to form eight 16-bit
   little-endian block numbers.

There does not appear to be an ordering requirement on extents, i.e. the extent for the second
16KB of a file may appear in the directory before the first 16KB.  It's possible for an extent
to be absent, e.g. a file could have no data stored for the area from 16KB to 32KB.  The first
extent for a file might not be #0.

The disk directory starts at allocation block zero, so that can never be used as a valid file
storage pointer.  Instead, it acts as a sparse allocation marker.  CP/M returns EOF when it
encounters a sparse area.  Sparse files are valid but rare.

The disk does not have a block usage bitmap.  The operating system generates it by scanning the
directory extents.  Extents must not share block pointers with other extents.

A file with multiple extents will have multiple copies of the access flags.  It is unclear
whether the first entry in the directory or the entry for extent #0 should take precedence.
When setting the flags, it's probably best to set the bit in all extents.

When listing files, the order in which they should be shown is not clearly defined.  Ordering
by first occurrence in the directory, regardless of extent number, is the simplest.  Listing them
in alphabetical order is also entirely reasonable.

CP/M v3 introduced "disc labels" and date stamps.  Date stamps are stored by reserving every
fourth directory entry as date storage for the three previous entries.  The date stamp format
exists in some third-party implementations of CP/M v2.2, but the format is incompatible.

Fun fact: newly-formatted disks have all sectors filled with 0xe5, not 0x00.  Because this is
used as the "empty directory entry" indicator, and there are no disk structures, the disk
initialization process doesn't have to do anything but erase all sectors.  This makes disk
format auto-detection tricky, because any disk with 0xe5 bytes in the directory area looks
like a blank CP/M disk.

### Apple II Disk Formats ###

There are two disk formats of interest for the Apple II: 5.25" disks, such as those created for
use with the Microsoft Softcard, and 3.5" disks supported by Applied Engineering's CP/AM.  The
5.25" disk format uses the same low-level sector format as DOS and ProDOS, but with a different
sector skew.

The [cpmtools](http://www.moria.de/~michael/cpmtools/) data file, `/etc/cpmtools/diskdefs`, has
two entries for Apple II disk image files:
```
# Apple II CP/M skew o Apple II DOS 3.3 skew
diskdef apple-do
  seclen 256
  tracks 35
  sectrk 16
  blocksize 1024
  maxdir 64
  skewtab 0,6,12,3,9,15,14,5,11,2,8,7,13,4,10,1
  boottrk 3
  os 2.2
end
```
The other entry is `apple-po`, and is identical except for the skew table.  The `seclen`,
`tracks`, `sectrk`, and `skewtab` entries tell the tools how to interpret the raw sectors.
`blocksize` identifies the allocation block unit size as 1024 bytes (four sectors).  `maxdir 64`
indicates the directory has 64 entries; at 32 bytes each, that's 2048 bytes (two alloc blocks).
`boottrk 3` means the first 3 tracks are reserved for system use.  `os 2.2` specifies the set
of features we can expect to find.

With 1KB alloc blocks, a 140KB floppy can be addressed in a single byte, so all allocation block
numbers in the directory entries are a single byte.

CP/AM 3.5" doesn't have a diskdefs entry, but the format can be determined with a bit of
exploration.  Allocation blocks are 2048 bytes each.  The directory starts at alloc block 8
(ProDOS block 32), and spans 4 alloc blocks (8192 bytes, ending in ProDOS block 47).  There are
a total of 392 allocation blocks in the directory and data area, so two bytes are required for
each allocation block number.

It's worth noting that a directory entry extent holds 16KB for both Apple II disk formats
(16 * 1KB or 8 * 2KB).  CP/M has a notion of "physical" and "logical" extents, where the latter
is always 16KB; having them both be 16KB makes things a little simpler.

The integrity of a 5.25" disk image can be checked with the `fsck.cpm` command from the
`cpmtools` package.  Use `fsck -f apple-do <file>` for DOS-ordered images, `apple-po` for
ProDOS-ordered images.

### Data-Only Disks ###

Some 5.25" disks have a directory record with user number 31 and a lower-case filename, such as
"cp/m.sys" or "cp/am.sys".  The extent uses allocation blocks 0x80 through 0x8b, which would be
off the end of the disk (140KB minus three 4KB boot tracks = 128KB).  If the values are treated
as wrapping around to the start of the disk, the file contains the 12KB boot track area.

This trick, which appears to have originated with the Microsoft SoftCard, is used to allow extra
storage on non-bootable disks.  Normally the first three tracks would be considered reserved and
inaccessible, and the directory must start in allocation block 0.  By wrapping around at the end
of the disk, the first three tracks become accessible as allocation blocks 128-139.  To prevent
files from overwriting the boot image, a special file is created that spans those blocks.  Giving
the file a special status prevents the file from being modified or deleted.

The filesystem implementation needs to be aware of this trick, and needs to recognize that the
special file has a valid allocation block list but should not be displayed in listings or
otherwise be available as a target of commands (especially deletion).

It's unclear whether 3.5" disks have a similar feature.  The cpmtools `fsck.cpm` command does
not recognize this arrangement, and will report an error because the allocation block pointers
are invalid.

The CP/AM "COPY /S" command, which copies the boot image from one floppy disk to another, will
create the special file if it doesn't exist.

The user=31 trick is also used on DOS+CP/M hybrid disks, such as the back side of the CP/AM 5.1
floppy.  The extent must cover a range of allocation blocks starting at 0x38, which is track 17
on a 5.25" disk.

### Text Files ###

On many disks, files will be a multiple of 128 bytes long.  For a text file, the actual file
EOF occurs when the first Ctrl-Z (0x1a) byte is read.


## Appendix: User Numbers and CiderPress ##

When using CP/M, the current user number can be selected with the "user" command (0-15).  Only
files with a matching user number are accessible, although files with user number zero are
always visible.  This allows multiple files with the same filename to exist on a single volume,
and provides a way to group files on a filesystem that lacks subdirectories.

There are a few ways to handle them in CiderPress.  Ideally we'd use an approach that doesn't
require introducing significant CP/M-specific mechanisms, so that the same set of commands used
for other filesystems will also work with minimal alterations.  Some approaches are:

 0. No special treatment.  User numbers are just another editable file attribute, albeit a
	CP/M-specific one.
 1. Define a global user number setting (similar to what CP/M does).
 2. Embed the number in the filename, as a prefix or suffix, to ensure filename uniqueness.
 3. Define "virtual" directories that hold files associated with each user number.

Approach #0 is the simplest, but it can lead to difficulties on disks where multiple files have
the same filename.  Selecting files with the command-line interface is ambiguous, and extracting
files can cause collisions.  Copying files between disks will cause the user number to be lost.

Approach #1 would add a user number setting to the filesystem object.  Newly-created files would
receive that user number, and list/extract operations would filter their results.

Approach #2 provides a simple way to view and modify the user number associated with a file.  A
suffix that involves a reserved character, such as "FILE.TXT,3", would identify the user number
in file listings, and ensure that extracted files are given unique names.  Adding the user number
as a two-digit prefix, e.g. "03,FILE.TXT", would ensure that all files with the same user number
naturally sort together.  When adding files, the prefix/suffix would be parsed, stripped, and
used to configure the file's user number.

Approach #3 requires that the filesystem implementation define a set of virtual subdirectories
internally, e.g. "3" or "user03", that cannot be created or destroyed.  All files with the same
user number would appear to live in the matching subdirectory.  File listings for a given directory
would show only the files associated with that user number, while a recursive listing would show
all files, naturally grouped by user number.

The files on most Apple II CP/M disks associate user number 0 with all files.  Omitting the
additional designator for files associated with user 0 would be useful.  For approach #2 we
simply omit the prefix/suffix, for approach #3 we treat user 0 files as living in the root
directory.

There are a number of operations to consider:

 - Adding files to a nonzero user from GUI/CLI.
 - Extracting files from a nonzero user from GUI/CLI.
 - Copying files between volumes, retaining or altering the user number.
 - Changing the user number of one or more files.

Analysis:

Approach #1 (global setting) works reasonably well for the command line, where the user number can
be specified as an additional argument, but is a bit awkward for the GUI.  It provides no
mechanism for changing a user number.  When copying files, the user number would need to be set
for both the source and destination.

Approach #2 (filename tweak) is the most direct.  User numbers are essentially part of the filename
for files in a flat filesystem, so this is a natural fit.  Suffixes feel more natural than
prefixes, but prefixes are easier to sort, and don't disrupt the file extension for extracted
files.  The user number for a file can be changed by renaming the file.  The trouble with this
approach is that there is no longer a way to get the actual filename from the API.  Anything that
wants to hide the user number will have to parse the filename and manually exclude the
prefix/suffix.  Tools that do file extraction will want to keep the user number to ensure filename
uniqueness, and also so that it's not lost if the file is added back, but it could be annoying to
have to rename files for use with tools that don't understand the prefix/suffix.

Approach #3 (virtual directories) allows files to be extracted with the correct filenames,
regardless of user number, while still allowing multiple files with the same name to coexist.
Tools for moving files between directories can be used to alter user numbers.  We don't want to
clutter up file listings with a bunch of virtual directories, so they would need to be included in
some places but omitted in others.  Unfortunately it's not possible to show the directory entries
to the pathname resolver but hide them from the file list generator, so this needs to be done at
the application level, and all of the unit tests need to be aware of the situation.  The directory
juggling and the added burden on application code makes this the most complicated approach to
implement.

None of this has an impact on the file attribute preservation technique.  User numbers aren't
preserved by AppleSingle or NAPS, so their preservation is independent of other attributes.

Conclusions:

There isn't an ideal way to handle this.  Some part of the application will need to have
CP/M-specific handling, even if we solve part of the problem in the filesystem code.

One common feature of all filesystems is that the names of files in a given directory are
expected to be unique.  To preserve that we can filter some of the files away, we can uniquify
the filename, or we can generate a partial path; but we do need to do *something* or basic
operations like CLI file selection become impossible.
