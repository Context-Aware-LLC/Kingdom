---
uid: addressables-for-android-load-assets
---

# Load Addressable assets from asset packs

[Asynchronous loading](https://docs.unity3d.com/Packages/com.unity.addressables@latest?subfolder=/manual/load-manage-asynchronous-loads.html) is the recommended way to load Addressable assets from asset packs.

You can load the assets synchronously by calling the `WaitForCompletion()` method on the `AsyncOperationHandle`. Before doing this, ensure that you [initialize the Addressables system](https://docs.unity3d.com/Packages/com.unity.addressables@latest?subfolder=/manual/InitializeAsync.html). This initialization step isn't required when loading assets asynchronously, because the Addressables system automatically initializes during the first asynchronous load call.

The Addressables for Android package doesn't support synchronous loading if `WaitForCompletion()` method needs to wait for the asset pack to download. In this case, the package generates an exception indicating that synchronous loading isn't supported for Play Asset Delivery.

If the asset pack is already downloaded and configured for use with the package, `WaitForCompletion()` method exits immediately and no exceptions are generated. Although synchronous loading works in this case, it is highly recommended to always load assets from asset packs asynchronously.

## Additional resources

* [WaitForCompletion()](xref:UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle.WaitForCompletion*)
* [AsyncOperationHandle](xref:addressables-async-operation-handling)
