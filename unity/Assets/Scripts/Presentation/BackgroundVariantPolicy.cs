#nullable enable
using System;

namespace Kingdom.Presentation
{
    public enum BackgroundTier { Mobile, DesktopTablet }

    /// <summary>Shared export, address and device-selection contract for full-screen art.</summary>
    public static class BackgroundVariantPolicy
    {
        public const string AssetRoot = "Assets/Art/BackgroundVariants";
        public static readonly string[] Keys =
        {
            "CharacterRoster_Backdrop", "CollectionPlate_Vault", "PackVault_AncientCache_Backdrop",
            "Login_Backdrop", "UpdateGate_Backdrop", "MainMenu_Backdrop", "RoyalCouncilHall_Backdrop",
            "LoadingPlate_TempestBarber", "LoadingPlate_ClockwrightBreakfast",
            "LoadingPlate_ClockwrightCollection", "LoadingPlate_GravekeepDog",
            "LoadingPlate_VerdantWatering", "VersusPlate_Causeway",
        };

        // Physical device class, not framebuffer resolution: high-DPI phones remain on Mobile.
        // Android supplies its rotation-independent smallest width in dp; iOS identifies iPads.
        // Unknown handhelds use Mobile. Devices unable to upload 2560-wide textures also use it.
        public static BackgroundTier Choose(bool handheld, int smallestWidthDp, string? deviceModel,
                                            int maxTextureSize = 4096)
        {
            if (maxTextureSize < 2560) return BackgroundTier.Mobile;
            if (!handheld || smallestWidthDp >= 600
                || (deviceModel ?? "").StartsWith("iPad", StringComparison.OrdinalIgnoreCase))
                return BackgroundTier.DesktopTablet;
            return BackgroundTier.Mobile;
        }

        public static string Folder(BackgroundTier tier) => tier == BackgroundTier.DesktopTablet ? "DesktopTablet" : "Mobile";
        public static string Slug(BackgroundTier tier) => tier == BackgroundTier.DesktopTablet ? "desktop-tablet" : "mobile";
        public static int Width(BackgroundTier tier) => tier == BackgroundTier.DesktopTablet ? 2560 : 2048;
        public static int Height(BackgroundTier tier) => tier == BackgroundTier.DesktopTablet ? 1440 : 1152;
        public static string Group(BackgroundTier tier) => "Backgrounds_" + Folder(tier);
        public static string Address(BackgroundTier tier, string key) => $"backgrounds/{Slug(tier)}/{key}";
        public static string AssetPath(BackgroundTier tier, string key) => $"{AssetRoot}/{Folder(tier)}/{key}.png";
    }
}
