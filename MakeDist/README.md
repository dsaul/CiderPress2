# MakeDist #

This is a command-line tool for creating CiderPress II distributions.  It builds the various
components for multiple platforms and packages them up.

Usage:
 - `MakeDist build [--debug|--release]`
 - `MakeDist set-exec <file.zip> <entry-in-archive...>`
 - `MakeDist clobber`

## Build ##

The build process is performed by running `dotnet build` with various arguments.  The process
is repeated for each executable target, resulting in a collection of compiled objects.  This
is repeated for each platform (Windows, Linux, Mac OS), with separate builds for runtime-dependent
and self-contained binary sets.  Documentation and support files are copied in, and then each
collection is packaged up in a ZIP file.

https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-build

Each `dotnet build` command takes a "runtime ID" or "RID" option.  This specifies which system
should be targeted when doing platform-specific things.  The RID catalog can be found in
"runtime.json" in the runtime installation.

https://learn.microsoft.com/en-us/dotnet/core/rid-catalog

The programs are currently built for win-x86, win-x64, linux-x64, and osx-x64.

Some general info: https://stackoverflow.com/q/41533592/294248

The default behavior is to build for release.  Debug builds have extra debugging info in ".pdb"
files, and are built with assertions and extended debug checks enabled.  This makes the programs
slightly larger and slower, though that won't generally be noticeable.

When the build completes, a collection of ZIP archives will be available in the DIST subdirectory.
These are ready to be shipped.

The ZIP files are named `cp2_<version-tag>_<rid>_<fd|sc>[_debug].zip`.  The `version-tag` is a
short form of the version number, obtained from AppCommon.GlobalAppVersion.  `rid` is the dotnet
runtime ID.  `fd` indicates framework-dependent, `sc` indicates self-contained.  Debug builds get
an additional `_debug`.  This naming convention allows the download files for all versions and
all RIDs to sit in the same directory, and sort nicely.

## Set-Exec ##

This marks entries in a ZIP file as executable.  It does this by changing the platform ID in
the "version made by" field from 0x00 (MS-DOS) to 0x03 (UNIX), and setting the high 16 bits
of the "external file attributes" field to UNIX permissions with "execute" set (0755).

If the command is unable to find all named entries, it will abort the operation without modifying
the archive.

## Clobber ##

The "clobber" feature recursively removes all "obj" and "bin" directories found in the same
directory as a ".csproj" file.  This is more thorough than the Visual Studio "make clean".
This does not try to remove "MakeDist/bin", since it will likely be executing.

If Visual Studio is active, it will recreate the directory structure immediately.


# Version Numbers #

The version number used for packaging comes from `GlobalAppVersion.AppVersion` in the AppCommon
library.  This is the same version object used by the GUI and CLI applications.  It's important
to do a full build in Visual Studio *before* running a MakeDist build, so that the MakeDist
binary has the updated version number.

The DiskArc library has its own version number, in DiskArc.Defs.

The [documentation publisher](../ndocs/publish.py) has a copy of the version number, primarily
for generating the download links in the installation documentation.
