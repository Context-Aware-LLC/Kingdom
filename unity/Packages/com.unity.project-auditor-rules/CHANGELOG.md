# Changelog
All notable changes to this package will be documented in this file.

The format is based on [Keep a Changelog](http://keepachangelog.com/en/1.0.0/)
and this project adheres to [Semantic Versioning](http://semver.org/spec/v2.0.0.html).

## [3.0.0] - 2026-09-22

# Added
* Package update version database.
* Obsolete API detection for Unity 6.7.
* URP migration advice.
* CoreCLR migration advice.

## [2.0.0] - 2026-08-12

# Changed
* Raised the minimum supported Editor version to 6000.0.
* Removed RoslynAnalyzer label from the Domain_Reload_Analyzer.dll so it does not get auto-included with normal Editor compilation.
* Renamed ObsoleteDatabase.json to ObsoleteDatabase.gen.json to make it more clear that it is a generated file and should not be hand-edited.
* Add Roslyn Analyzers so we can provide code issue detection directly from the package.
* Migrate some analyzers from the built-in module out into the Rules package, so all rules can eventually be defined in this package.
* PAA3001 has been removed. It used to report dependency problems as issues, but this is now covered in the improved dependency viewer starting in Unity 6.6.
* Added PAS0036 to check Fast Enter Playmode settings in 6000.0 and newer.
* Added a new check for modified registry packages.
* Added a new check for 32-bit mesh issues created without an importer.

# Fixed
* Updated Graphics API checks to offer advice based on the preferred order of APIs, rather than simple presence.
* Fixed precision in PAS0017 check: "Time: Maximum Allowed Timestep is set to the default value".
* Fixed Audio Quick Fixers to avoid overwriting settings with defaults.
* Build size analyzers now ignore meta files.
* Fixed the managed stripping analyzer to include the Minimal level on all relevant platforms.
* Ensure all Project Settings issues report a Location.
* Audio checks weren't using the runtime size consistently.
trying to check an ios/android issue on switch.
* Ensure texture Quick Fixers preserve the file extension.
* Fix out of range texture read for 1xN textures.
* Fix transparency percentage checking.
* Ensure the Material Analyzer only reports each texture issue once, even if they have identical names.

## [1.0.3] - 2026-03-13

# Fixed
* Recognize Array.Clear as a valid reset method, and do not log delayCall issues in the Domain Reload Roslyn Analyzer.

## [1.0.2] - 2026-02-25

# Added
* Added a database of all Obsolete Unity API. Newer versions of Project Auditor will be able to use this to help with upgrades.

# Fixed
* Fixed various issues with the Domain Reload Roslyn Analyzer. It now detects more variable reset scenarios, and disallows multiple ResetInitializeOnLoad attributes.

## [1.0.1] - 2025-10-31

# Changed
* Removed MemoryIgnoreVoidReturn area, in favor of using a new returnType entry for filtering based on return type.

## [1.0.0] - 2025-09-26

### Added
* Migrated rules and Roslyn Analyzers from com-unity-project-auditor package, as we migrate the tool to be bundled with the Unity Editor as a module.

