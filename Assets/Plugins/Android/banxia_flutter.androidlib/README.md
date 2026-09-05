# BanxiaFlutterHost — Android embedding host (build artifacts)

Reflection-based Android host that lets the Unity app embed an **optional**
Flutter 2D overlay shell without depending on the Flutter Gradle plugin and
without breaking the Unity build when no Flutter artifacts are present.

This folder is a Unity **`.androidlib`** library project. Unity compiles
`src/main/java/**` into the `unityLibrary` module and merges `AndroidManifest.xml`
into the app manifest. It is intentionally *not* wired through any Flutter
Gradle plugin — see "Gradle wiring" below.

## Files

| Path | Purpose |
|------|---------|
| `project.properties` | Marks the folder as an Android library (`android.library=true`). |
| `AndroidManifest.xml` | Empty `<application>`; adds no launcher (UnityPlayerActivity stays the only launcher). |
| `src/main/java/com/lingxi/banxia/flutter/BanxiaFlutterHost.java` | The reflection host (engine / view / fragment / JSON bridge). |
| `../../mainTemplate.gradle` | Optional custom Gradle template that conditionally links the prebuilt embedding AAR. |

## Java host contract

The C# side (`Assets/Scripts/Flutter/BanxiaFlutterBridge.cs`) resolves the host
through JNI and expects exactly:

```
com.lingxi.banxia.flutter.BanxiaFlutterHost
  static BanxiaFlutterHost instance()      // singleton accessor
  void onUnityEvent(String json)           // engine -> Flutter event envelope
  void onUnityReply(long id, String json)  // engine -> Flutter reply envelope
```

Commands travel the other way: Flutter invokes `MethodChannel('banxia.bridge')`
with method `'call'` and a map envelope; the host wraps it and forwards via
`UnityPlayer.UnitySendMessage("BanxiaFlutterBridge", "ReceiveFromNative", json)`.
Events are delivered through `EventChannel('banxia.events')`.

Because the Unity C# envelope keeps `payload` as an **opaque JSON string**
(JsonUtility / IL2CPP constraint) while the Dart shell keeps `payload` as a
**nested map**, the host translates between the two representations in both
directions.

## Rendering paths (phone vs Quest)

The host exposes **two** concrete attach/transport paths plus a preflight:

### Phone — real FlutterView

`attachToUnityPlayer(Activity)` creates a real `io.flutter.embedding.android.
FlutterView`, calls `attachToFlutterEngine(engine)`, and adds it as a tokened
`TYPE_APPLICATION_PANEL` window above the Activity's Unity window. Flutter uses
the embedding's `RenderMode.texture`, so its content is a regular `TextureView`
overlay above Unity while preserving the Unity surface. The panel disables
WindowManager's automatic system-bar fitting, retains `ADJUST_RESIZE` for the
IME, and forwards touch sequences from the lazily-created texture child into
`FlutterView`. Only after the view is actually added is `phoneViewAttached` set
true (reported in `getStateJson()`). If the view is created but the panel cannot
be added, the reason is recorded and the flag stays false — no silent
"attached" claim.

### Quest — offscreen SurfaceTexture seam (transport only, not completion)

Quest has no `addViewToPlayer` window to draw into, so Flutter must render
offscreen and Unity must composite the result. The host provides:

- `startOffscreenRendering(width, height)` — creates a **detached**
  `SurfaceTexture` + `Surface`, then calls `FlutterRenderer.startRenderingToSurface`
  and `surfaceChanged`.
- `getOffscreenSurfaceTexture()` — exposes the `SurfaceTexture` for Unity's
  render thread.
- `attachOffscreenTexture(texName)` / `updateOffscreenTexture()` — Unity
  render-thread hooks (`attachToGLContext` / `updateTexImage`).
- `getOffscreenHardwareBuffer()` — the `HardwareBuffer` (API 26+), if Unity
  prefers the hardware-buffer path.
- `getSurfaceStatusJson()` — reports `surfaceCreated`, `surfaceTextureCreated`,
  `offscreenRendering`, `width`/`height`, and `reason`.

**This is a seam, not a completed Quest UI.** A `true` `offscreenRendering`
means only "Flutter is rendering into the offscreen surface". The Quest overlay
is complete only after the Unity C# side attaches the texture and composites it
into the VR layer — that is outside this host's scope and is not claimed here.

## Required generated Flutter AOT assets

The host is compile-safe **without** any of these. It only *uses* them at
runtime. Real Flutter rendering needs all of them, placed as Unity expects:

| Generated artifact | What it is | Where it goes in the Unity project |
|--------------------|------------|-----------------------------------|
| `flutter_embedding_release.aar` (or `_debug`/`_profile`) | `io.flutter.*` embedding Java classes | `Assets/Plugins/Android/flutter_embedding_release.aar` |
| `flutter_engine_arm64_release.aar` | ARM64 Flutter engine (`libflutter.so`) | `Assets/Plugins/Android/flutter_engine_arm64_release.aar` |
| `flutter_ui_release.aar` | Compiled Dart AOT (`libapp.so`) and `flutter_assets/` | `Assets/Plugins/Android/flutter_ui_release.aar` |
| `libflutter.so` | Flutter engine native library (per ABI), when unpacked manually | `Assets/Plugins/Android/<abi>/libflutter.so` (e.g. `arm64-v8a/`) |
| `libapp.so` | Compiled Dart AOT snapshot (per ABI), when unpacked manually | `Assets/Plugins/Android/<abi>/libapp.so` |
| `flutter_assets/` | Asset bundle: `kernel_blob.bin`, `AssetManifest.json`, `FontManifest.json`, fonts, `vm_snapshot_data`, etc. | `Assets/StreamingAssets/flutter_assets/` |
| `icudtl.dat` | ICU data (the Android engine artifact normally supplies this in the APK asset pipeline) | `Assets/StreamingAssets/icudtl.dat` |

### How to generate them (Flutter SDK)

```bash
cd flutter_ui                  # the Dart module at the repo root
flutter build apk --release    # produces libapp.so / libflutter.so / flutter_assets/
# or, for the embedding classes only:
flutter build aar              # produces build/host/outputs/repo/io/flutter/flutter_embedding_release/<ver>/flutter_embedding_release-<ver>.aar
```

The AOT outputs come from the Flutter engine cache
(`<flutter-sdk>/bin/cache/artifacts/engine/android-arm64-release/`) and the
APK build intermediates. Place the AAR, `.so` files, `flutter_assets/`, and
`icudtl.dat` into the Unity paths in the table above, then enable the Gradle
wiring below.

## Gradle wiring (no Flutter Gradle plugin)

Unity 2022.3 compiles the `.androidlib` Java source without the embedding AAR
(the host is reflection-only, so it compiles clean). To make the `io.flutter.*`
classes available at *runtime*, the embedding AAR must be on the app classpath.

The optional `Assets/Plugins/Android/mainTemplate.gradle` does this with a
**conditional** dependency: if `libs/flutter_embedding_release.aar` exists it is
added via `implementation(name: 'flutter_embedding_release', ext: 'aar')`;
otherwise the build is untouched. There is **no** `apply plugin: 'com.flutter'`
and no Flutter Gradle plugin, so the standard Unity 2022.3 AGP toolchain applies.

Enable it in Unity: **Player Settings → Publishing Settings → Custom Main Gradle
Template** (Unity then uses `mainTemplate.gradle`). If you prefer Unity's stock
template, delete `mainTemplate.gradle`; the host still compiles, it simply never
sees the Flutter classes at runtime.

> Transitive note: the flatDir link resolves the AAR but does **not** pull the
> AAR's transitive `androidx.*` dependencies from its POM. Unity 2022.3 already
> bundles the androidx fragment/annotation/lifecycle classes the embedding
> needs; if you hit `NoClassDefFoundError` at runtime, add the missing androidx
> artifacts explicitly or switch to the full Flutter Gradle plugin.

## Limitations (do not claim rendering without the artifacts)

- **No Dart isolate without the AOT assets.** With only the AAR, the engine is
  constructed and the channels register, but `main()` never runs — the view
  stays blank. The host reports `available:false` / a missing-artifact reason.
- **UnityPlayerActivity is a plain Activity.** The primary attach path is
  `attachToUnityPlayer(Activity)` (FlutterView + `UnityPlayer.addViewToPlayer`).
  `attachFlutterFragment` requires an `androidx.fragment.app.FragmentActivity`
  and will report "host is not a FragmentActivity" against UnityPlayerActivity.
- **Reply `data` shape.** The host faithfully converts the Unity reply envelope
  into the Dart `{id, ok, data, error}` map. The current Dart
  `BridgeReply.tryParse` only materialises `data` when it is a JSON object;
  array/scalar payloads are passed through but dropped by the Dart parser.
- **This folder adds no launcher.** UnityPlayerActivity remains the only
  `MAIN`/`LAUNCHER` activity.
- **Quest is a seam, not a finish line.** `offscreenRendering == true` means the
  offscreen Surface/SurfaceTexture is live; compositing it into the VR layer is
  Unity C#'s job. This host never reports "Quest UI complete".

## Preflight

`BanxiaFlutterHost.preflight(Context)` returns one JSON report that answers the
"am I ready to render?" question honestly:

```json
{
  "embeddingPresent": false,
  "engineInitialized": false,
  "flutterAssetsPresent": false,
  "phoneViewAttached": false,
  "offscreenRendering": false,
  "surfaceTextureCreated": false,
  "expectedArtifacts": [ /* table above, as JSON */ ]
}
```

- `embeddingPresent` — the `io.flutter.*` classes are on the classpath (AAR wired).
- `engineInitialized` — the engine and both channels are constructed.
- `flutterAssetsPresent` — APK `assets/flutter_assets/` is non-empty (detected
  via `context.getAssets().list("flutter_assets")`).
- `phoneViewAttached` / `offscreenRendering` / `surfaceTextureCreated` — the
  phone / Quest seams.

## State reporting

`BanxiaFlutterHost.getStateJson()` always returns valid JSON, e.g.:

```json
{
  "available": false,
  "embeddingPresent": false,
  "engine": false,
  "bridgeChannel": false,
  "eventChannel": false,
  "eventListenerAttached": false,
  "phoneViewAttached": false,
  "offscreenRendering": false,
  "reason": "Flutter embedding classes not found; add the prebuilt flutter_embedding AAR to Assets/Plugins/Android/ (see README)",
  "unityTarget": "BanxiaFlutterBridge/ReceiveFromNative"
}
```

`BanxiaFlutterHost.expectedArtifactsJson()` lists the artifact table above as
JSON, and `getSurfaceStatusJson()` reports the offscreen seam independently.
