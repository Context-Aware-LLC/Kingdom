# Changelog
All notable changes to this package will be documented in this file.

The format is based on [Keep a Changelog](http://keepachangelog.com/en/1.0.0/)
and this project adheres to [Semantic Versioning](http://semver.org/spec/v2.0.0.html).

## [1.1.0] - 2026-06-01

Full compatibility with *com.unity.addressables 3.1.0*.
Unity 6.0 or later is required.

## [1.0.10] - 2026-02-17

Fixed problem with spurious "Unable to find registered file for bundle" errors when setting "Bundle Naming Mode" to "No Hash" and building with multiple Android texture compression formats.
Unity 6.5+ specific: Handling AndroidAssetPackStatus.RequiresUserConfirmation and using AndroidAssetPacks.ShowConfirmationDialogAsync method instead of deprecated AndroidAssetPacks.RequestToUseMobileDataAsync.

## [1.0.9] - 2025-11-25

Updated documentation.

## [1.0.8] - 2025-10-31

Fixed problem with loading Addressables content when asset pack is already installed to the device, but the device is offline.

## [1.0.6] - 2025-02-12

* Loading Addressable assets synchronously can now generate an exception. For more information, refer to the documentation.
* Updated `com.unity.addressables` dependency up to v2.3.16.

## [1.0.4] - 2024-08-27

Fixed problem with building Addressables content when the empty folder is added to the addressable group.

## [1.0.3] - 2024-07-01

Fixed CI related issues.

## [1.0.2] - 2023-11-24

Fixed build problem when using custom Launcher Gradle template and/or custom Gradle Settings template.

## [1.0.1] - 2023-10-02

Full compatibility with *com.unity.addressables 2.0.3*.

## [1.0.0] - 2023-09-18

This is the first release of *Unity Package com.unity.addressables.android*.
