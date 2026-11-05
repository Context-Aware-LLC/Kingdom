# Third-party notices

Kingdom of Bad Ideas includes, or is built with, the third-party components below. Each one stays
under its own licence. The licence for this repository's own code and assets is in `LICENSE`.

## Vendored in this repository

| Component | Version | Licence | Location / licence text |
| --- | --- | --- | --- |
| Google Play Games plugin for Unity | 2.3.0 | Apache-2.0 | `unity/Assets/GooglePlayGames/com.google.play.games/LICENSE` |
| External Dependency Manager for Unity | 1.2.182 | Apache-2.0 (bundled parts MIT) | `unity/Assets/ExternalDependencyManager/Editor/LICENSE` |
| TextMesh Pro essential resources | — | Unity Companion License | `unity/Assets/TextMesh Pro/` |
| Addressables Utility sample | 2.9.1 | Unity Companion License | `unity/Assets/Samples/Addressables/` |
| Unity default runtime theme | — | Unity Companion License | `unity/Assets/UI Toolkit/UnityThemes/` |

Unity Companion License: https://unity.com/legal/licenses/unity-companion-license

## Fonts (SIL Open Font License 1.1)

| Font | Files | Licence text |
| --- | --- | --- |
| Alfa Slab One | `unity/Assets/Art/Fonts/AlfaSlabOne-*`, `web/landing/public/assets/alfa-slab-one.woff2` | `unity/Assets/Art/Fonts/AlfaSlabOne-OFL.txt` |
| Google Sans Flex | `unity/Assets/Art/Fonts/GoogleSansFlex-*`, `tooling/fonts/`, `web/landing/public/assets/google-sans.woff2` | `unity/Assets/Art/Fonts/GoogleSans-OFL.txt` |
| Modern Antiqua | `unity/Assets/Art/Fonts/ModernAntiqua-*` | `unity/Assets/Art/Fonts/ModernAntiqua-OFL.txt` |
| Liberation Sans | `unity/Assets/TextMesh Pro/Fonts/LiberationSans.ttf` | `unity/Assets/TextMesh Pro/Fonts/LiberationSans - OFL.txt` |

The web fonts' licence text is also at `web/landing/public/assets/fonts-OFL.txt`.

## Fetched at build time (not redistributed here)

- **Unity packages** (`unity/Packages/manifest.json`), under the Unity Companion License or Unity's
  own terms. They need a Unity licence to use.
- **Sentry SDK for Unity** 4.12.0, MIT.
- **NuGet:** Microsoft.ML.OnnxRuntime (MIT), Npgsql (PostgreSQL License),
  Microsoft.IdentityModel.JsonWebTokens (MIT), System.Text.Json (MIT), and NUnit with its test
  adapters (MIT).
- **npm** (development tooling only: eslint, prettier, stylelint and their dependencies). All are
  under permissive licences (MIT, ISC, Apache-2.0, BSD, BlueOak, CC0).

## Generated assets

The card art, UI art, voice, music, sound effects and 3D models were made by the project with
generative tools: OpenAI and Google Gemini image models, ElevenLabs (voice, music, sound effects)
and Hyper3D Rodin (meshes). Some character animation comes from Adobe Mixamo, used under Adobe's
terms.
