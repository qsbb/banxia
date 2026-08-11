# Third-party notices

This repository contains or references software and assets owned by their
respective authors. The root MPL-2.0 license covers original Banxia source code
only and does not replace these terms.

## UnityMMDTools

`Packages/com.candidumgames.unitymmdtools` is an embedded copy of
CandidumGames/UnityMMDTools 0.5.0. It is distributed under the MIT License.
The package retains its own `LICENSE.md` and `Third Party Notices.md`, including
the notices for bundled dictionaries and native components.

This repository carries a local managed-code patch to `MMDPhysicsManager` that
adds tracked-hand kinematic spheres through the existing UMT native rigid-body
ABI. The bundled native binaries are unchanged.

## Unity and Meta packages

Packages resolved by Unity Package Manager remain subject to their own package
and vendor licenses. They are not relicensed by this repository.

## Avatar models, textures, and motions

User-provided PMX/PMD/VRM/GLB models, textures, and third-party VMD motions are
local development inputs. They are excluded from Git by default and are not
covered by any source-code license for this project. Add or redistribute them
only when the original author explicitly permits it.

The `Assets/StreamingAssets/MmdSamples/ForestBerry/README.md` placeholder
documents the expected local smoke-test layout without granting redistribution.
