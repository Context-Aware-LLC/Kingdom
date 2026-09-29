using System.IO;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Core.Tests.Presentation
{
    /// <summary>
    /// The collection keeps its layout live while the loading plate is shown, because the wall
    /// needs a real viewport before it can choose its initial bake size. Hiding by inherited
    /// visibility is not sufficient: CardWall deliberately stamps visible on populated tiles,
    /// and a descendant may override an inherited hidden value. Parent opacity suppresses the
    /// composited subtree without taking it out of layout or letting a tile opt back in.
    /// </summary>
    [TestFixture]
    [Category("Presentation")]
    public sealed class CollectionLoadingTransitionContractTests
    {
        [Test]
        public void Loading_plate_suppresses_the_composited_collection_without_collapsing_layout()
        {
            string path = Path.Combine(
                RepoPaths.RepoRoot, "unity", "Emberfall", "Assets", "Scripts", "Presentation",
                "CollectionController.cs");
            string source = File.ReadAllText(path);

            Assert.Multiple(() =>
            {
                Assert.That(source, Does.Contain("_root.style.opacity = 0f;"),
                    "The loading state must make the entire composited UI subtree transparent; "
                    + "CardWall tiles set their own visibility and can override inherited hidden.");
                Assert.That(source, Does.Contain("_root.style.opacity = 1f;"),
                    "The collection must become opaque only after LoadingOverlay.OnFadedOut.");
                Assert.That(source, Does.Not.Contain("_root.style.visibility = Visibility.Hidden;"),
                    "Inherited visibility lets CardWall's inline Visibility.Visible leak tiles "
                    + "over the world-space loading plate.");
                Assert.That(source, Does.Contain("_loading.OnFadedOut += RevealScreen;"),
                    "The collection reveal must remain tied to the plate's completed fade.");
            });
        }

        [Test]
        public void Menu_renders_a_preloaded_collection_plate_before_async_scene_activation()
        {
            string presentation = Path.Combine(
                RepoPaths.RepoRoot, "unity", "Emberfall", "Assets", "Scripts", "Presentation");
            string menu = File.ReadAllText(Path.Combine(presentation, "MainMenuController.cs"));
            string nav = File.ReadAllText(Path.Combine(presentation, "ScreenNav.cs"));
            string collection = File.ReadAllText(Path.Combine(presentation, "CollectionController.cs"));
            string overlay = File.ReadAllText(Path.Combine(presentation, "LoadingOverlay.cs"));
            int transitionMethod = menu.IndexOf("private IEnumerator OpenCollection()",
                System.StringComparison.Ordinal);
            int createPlate = menu.IndexOf("CollectionController.BeginLoadingTransition(Camera.main)",
                System.StringComparison.Ordinal);
            int hideMenu = menu.IndexOf("_root.style.opacity = 0f;", System.StringComparison.Ordinal);
            int renderFrame = menu.IndexOf("yield return null;", System.StringComparison.Ordinal);
            int startLoad = menu.IndexOf("ScreenNav.GoAsync(ScreenNav.Collection)",
                System.StringComparison.Ordinal);

            Assert.Multiple(() =>
            {
                Assert.That(menu, Does.Contain("CollectionController.PrewarmNextLoadingPlate();"),
                    "The next Collection plate must be loaded while the menu boot plate still "
                    + "covers synchronous Addressables work.");
                Assert.That(menu, Does.Contain("CollectionController.BeginLoadingTransition(Camera.main)"),
                    "The menu must create the Collection transition before leaving its scene.");
                Assert.That(menu, Does.Contain("yield return null;"),
                    "The visible transition needs one rendered menu frame before scene loading starts.");
                Assert.That(menu, Does.Contain("ScreenNav.GoAsync(ScreenNav.Collection)"),
                    "Collection navigation must not block the frame that first paints the plate.");
                Assert.That(createPlate, Is.GreaterThan(transitionMethod),
                    "The source scene must create its plate inside the Collection transition.");
                Assert.That(hideMenu, Is.GreaterThan(createPlate),
                    "The menu must remain present until the preloaded plate exists behind it.");
                Assert.That(renderFrame, Is.GreaterThan(hideMenu),
                    "The hidden menu and visible plate must reach a render boundary.");
                Assert.That(startLoad, Is.GreaterThan(renderFrame),
                    "Scene loading must begin only after the plate has had a frame to paint.");
                Assert.That(nav, Does.Contain("SceneManager.LoadSceneAsync(sceneName)"),
                    "The shared navigation seam must expose Unity's asynchronous scene load.");
                Assert.That(collection, Does.Contain("TakePendingLoadingTransition(Camera.main)"),
                    "Collection must adopt the menu-owned plate rather than replacing it after load.");
                Assert.That(overlay, Does.Contain("DontDestroyOnLoad(gameObject);"),
                    "The menu-owned plate must survive destruction of the menu scene.");
            });
        }

        [Test]
        public void Initial_wall_bake_is_time_sliced_so_the_loading_plate_can_animate()
        {
            string path = Path.Combine(
                RepoPaths.RepoRoot, "unity", "Emberfall", "Assets", "Scripts", "Presentation",
                "CardWall.cs");
            string source = File.ReadAllText(path);

            Assert.Multiple(() =>
            {
                Assert.That(source, Does.Contain("MaxBakeRequestsPerTick"),
                    "A synchronous Editor art provider must not capture the complete pool in one UI tick.");
                Assert.That(source, Does.Contain("BakePoolBatch"),
                    "The eager complete-pool bake should be divided into bounded batches.");
                Assert.That(source, Does.Contain("ExecuteLater(1)"),
                    "Successive bake batches must return control to UI Toolkit so the plate can repaint.");
                Assert.That(source, Does.Not.Match(
                        @"for\s*\(int i = 0; i < _pool\.Count; i\+\+\)\s*_baker\.Bake"),
                    "The old complete-pool submission loop would still monopolize an Editor frame.");
            });
        }
    }
}
