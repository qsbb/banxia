package com.lingxi.banxia.flutter;

import android.app.Activity;
import android.content.Context;
import android.graphics.PixelFormat;
import android.graphics.SurfaceTexture;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.util.Log;
import android.view.Surface;
import android.view.View;
import android.view.ViewGroup;
import android.view.ViewParent;

import org.json.JSONArray;
import org.json.JSONObject;
import org.json.JSONTokener;

import java.lang.reflect.Constructor;
import java.lang.reflect.Field;
import java.lang.reflect.InvocationHandler;
import java.lang.reflect.Method;
import java.lang.reflect.Proxy;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Iterator;
import java.util.LinkedHashMap;
import java.util.LinkedList;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicLong;
import java.util.concurrent.atomic.AtomicReference;

/**
 * BanxiaFlutterHost — compile-safe, reflection-based host for the optional
 * Flutter 2D overlay shell.
 *
 * <p><b>Why reflection.</b> This file must compile inside Unity's Android
 * build even when no Flutter embedding AAR is present. Unity's default AGP
 * toolchain (2022.3) has no io.flutter.* classes on the classpath unless a
 * prebuilt Flutter embedding AAR is added, and there is deliberately no Flutter
 * Gradle plugin in this project. Every Flutter touch-point is therefore
 * resolved through {@code Class.forName(...)} / {@code Proxy} / {@code
 * Method.invoke(...)}. When the classes are missing the host stays compilable,
 * reports an "unavailable" state, and drops messages instead of crashing.</p>
 *
 * <p><b>Unity-side contract.</b> The C# side ({@code
 * Assets/Scripts/Flutter/BanxiaFlutterBridge.cs}) resolves this class through
 * JNI and expects exactly these members:
 * <pre>
 *   com.lingxi.banxia.flutter.BanxiaFlutterHost
 *     static BanxiaFlutterHost instance()      // singleton accessor
 *     void onUnityEvent(String json)           // engine -> Flutter event envelope
 *     void onUnityReply(long id, String json)  // engine -> Flutter reply envelope
 * </pre>
 * Commands travel the other way: Flutter invokes MethodChannel
 * {@code banxia.bridge}, this host wraps the call into the Unity envelope and
 * forwards it via {@code UnityPlayer.UnitySendMessage("BanxiaFlutterBridge",
 * "ReceiveFromNative", json)}.</p>
 *
 * <p><b>Envelope translation.</b> The Unity C# envelope keeps {@code payload}
 * as an opaque JSON <i>string</i> (JsonUtility / IL2CPP constraint), while the
 * Dart shell keeps {@code payload} as a nested map. This host is the translation
 * seam between the two representations.</p>
 *
 * <p><b>Limitations (read before assuming real rendering).</b> Even with the
 * Flutter embedding AAR present, no Dart isolate runs unless the generated AOT
 * assets ({@code libapp.so}, {@code libflutter.so}, {@code flutter_assets/},
 * {@code icudtl.dat}) are also packaged and a FlutterView/FlutterFragment is
 * attached to the Unity player (phone) or offscreen rendering is started and
 * composited (Quest). This class only ever claims to host the embedding; actual
 * rendering requires those artifacts. On Quest, {@link
 * #startOffscreenRendering(int, int)} / {@link #getSurfaceStatusJson()} are a
 * transport seam only — a true status means "surface ready", never "Quest UI
 * complete", because Unity must still attach and composite the texture. See the
 * README next to this source for the exact artifact list and placement.</p>
 */
public final class BanxiaFlutterHost {
    private static final String TAG = "BanxiaFlutterHost";

    // ------------------------------------------------------------------
    // Reflected Flutter class / interface names. Never imported directly.
    // ------------------------------------------------------------------
    private static final String CLS_ENGINE = "io.flutter.embedding.engine.FlutterEngine";
    private static final String CLS_DART_EXECUTOR =
            "io.flutter.embedding.engine.dart.DartExecutor";
    private static final String CLS_DART_ENTRYPOINT =
            "io.flutter.embedding.engine.dart.DartExecutor$DartEntrypoint";
    private static final String CLS_RENDERER =
            "io.flutter.embedding.engine.renderer.FlutterRenderer";
    private static final String CLS_VIEW = "io.flutter.embedding.android.FlutterView";
    private static final String CLS_FRAGMENT = "io.flutter.embedding.android.FlutterFragment";
    private static final String CLS_METHOD_CHANNEL = "io.flutter.plugin.common.MethodChannel";
    private static final String CLS_EVENT_CHANNEL = "io.flutter.plugin.common.EventChannel";
    private static final String CLS_METHOD_CALL_HANDLER =
            "io.flutter.plugin.common.MethodChannel$MethodCallHandler";
    private static final String CLS_STREAM_HANDLER =
            "io.flutter.plugin.common.EventChannel$StreamHandler";
    private static final String CLS_BINARY_MESSENGER =
            "io.flutter.plugin.common.BinaryMessenger";
    private static final String CLS_FRAGMENT_ACTIVITY =
            "androidx.fragment.app.FragmentActivity";
    private static final String CLS_ANDROIDX_FRAGMENT =
            "androidx.fragment.app.Fragment";

    /** MethodChannel/EventChannel names — must match the Dart shell exactly. */
    public static final String BRIDGE_CHANNEL = "banxia.bridge";
    public static final String EVENT_CHANNEL = "banxia.events";

    /** Default Unity receiver, matching BanxiaFlutterBridge.ReceiveFromNative. */
    private static final String DEFAULT_UNITY_GAME_OBJECT = "BanxiaFlutterBridge";
    private static final String DEFAULT_UNITY_CALLBACK = "ReceiveFromNative";

    /** Upper bound on outstanding, unanswered command ids (defensive). */
    private static final int MAX_PENDING_REPLIES = 256;
    private static final long MAIN_THREAD_WAIT_MS = 10000L;

    /** Upper bound on buffered Unity events while no Dart listener is attached
     *  (see the event-buffer block in the State section). */
    private static final int MAX_BUFFERED_EVENTS = 256;
    /** Soft upper bound on total buffered UTF-8 bytes of the raw accepted
     *  envelopes; a single oversized event may exceed it, keeping the event
     *  count as the hard bound. */
    private static final int MAX_BUFFERED_EVENT_BYTES = 256 * 1024;

    /** One queued Unity event: parsed bridge map plus raw envelope UTF-8 size. */
    private static final class BufferedEvent {
        final Object event;
        final int utf8Bytes;

        BufferedEvent(Object event, int utf8Bytes) {
            this.event = event;
            this.utf8Bytes = utf8Bytes;
        }
    }

    // ------------------------------------------------------------------
    // Singleton.
    // ------------------------------------------------------------------
    private static final class Holder {
        static final BanxiaFlutterHost INSTANCE = new BanxiaFlutterHost();
    }

    /** Unity contract accessor (used by BanxiaFlutterBridge via JNI). */
    public static BanxiaFlutterHost instance() {
        return Holder.INSTANCE;
    }

    /** Conventional alias for {@link #instance()}. */
    public static BanxiaFlutterHost getInstance() {
        return Holder.INSTANCE;
    }

    private BanxiaFlutterHost() {
    }

    // ------------------------------------------------------------------
    // State.
    // ------------------------------------------------------------------
    private final AtomicBoolean initialized = new AtomicBoolean(false);

    private Handler mainHandler;
    private final Object mainHandlerLock = new Object();
    private volatile String unityGameObject = DEFAULT_UNITY_GAME_OBJECT;
    private volatile String unityCallbackMethod = DEFAULT_UNITY_CALLBACK;
    private volatile String unavailableReason = "not initialized";
    private volatile Context applicationContext;
    private volatile boolean dartStarted;
    private Object phoneFlutterView; // io.flutter.embedding.android.FlutterView

    // Reflected Flutter embedding instances (all null until initialize()).
    private Object flutterEngine;   // io.flutter.embedding.engine.FlutterEngine
    private Object methodChannel;   // io.flutter.plugin.common.MethodChannel
    private Object eventChannel;    // io.flutter.plugin.common.EventChannel
    private volatile Object eventSink; // io.flutter.plugin.common.EventSink

    // ------------------------------------------------------------------
    // Bounded event buffering (limits: MAX_BUFFERED_EVENTS /
    // MAX_BUFFERED_EVENT_BYTES, defined above). While eventSink == null,
    // onUnityEvent queues events here instead of dropping them, so the Dart
    // shell receives the missed window when it subscribes. The queue is a FIFO
    // that evicts the OLDEST event when the count or the total UTF-8 byte
    // budget is exceeded, keeping the freshest events. Flushing delivers the
    // survivors in arrival order on the Android main thread (EventSink calls
    // must stay on the platform thread); onCancel/shutdown discard the queue.
    // Mutations normally happen on the main thread (onUnityEvent's posted
    // runnable, flush posted from onListen), but onListen/onCancel may arrive
    // on any thread, so every access takes bufferedEventsLock.
    // ------------------------------------------------------------------
    private final Object bufferedEventsLock = new Object();
    private final List<BufferedEvent> bufferedEvents = new LinkedList<BufferedEvent>();
    private int bufferedEventBytes;

    private final AtomicLong nextId = new AtomicLong(1L);

    /** Command id -> outstanding MethodChannel.Result, answered by onUnityReply. */
    private final Map<Long, Object> pendingResults = new LinkedHashMap<Long, Object>();
    private final AtomicLong lifecycleGeneration = new AtomicLong(1L);

    // Phone path: true once a real FlutterView is added to the Unity player
    // (not merely "engine constructed"). Reported through getStateJson().
    private volatile boolean phoneViewAttached;

    // Quest offscreen path: Flutter renders into an android.view.Surface backed
    // by a *detached* SurfaceTexture. Unity attaches a GL texture on its render
    // thread (attachOffscreenTexture) and consumes frames with
    // updateOffscreenTexture(). This is only a status/transport seam — it does
    // NOT by itself mean Quest UI is composited; Unity must consume the texture.
    private final Object offscreenLock = new Object();
    private volatile Object offscreenSurface;                // android.view.Surface
    private volatile SurfaceTexture offscreenSurfaceTexture; // android.graphics.SurfaceTexture
    private volatile boolean offscreenRendering;
    private volatile int offscreenWidth;
    private volatile int offscreenHeight;
    private volatile String surfaceStatusReason = "not started";
    // Unity owns GL attachment. Android must wait for an explicit render-thread
    // detach before releasing the SurfaceTexture during resize/shutdown.
    private boolean offscreenTextureAttached;
    private boolean offscreenDetachRequested;
    private long offscreenGeneration;
    private long detachAcknowledgedGeneration;
    private long detachRequestedGeneration;

    // ------------------------------------------------------------------
    // Lifecycle / initialization.
    // ------------------------------------------------------------------

    /**
     * Creates the Flutter engine and registers the two platform channels.
     * Returns true only when the engine is ready. Safe to call repeatedly.
     *
     * @param context any Context (Activity preferred); FlutterEngine only needs
     *                an application/activity context to construct.
     */
    public boolean initialize(final Context context) {
        if (Looper.myLooper() == Looper.getMainLooper()) {
            return initializeOnMainThread(context);
        }
        final AtomicReference<Boolean> result = new AtomicReference<Boolean>(Boolean.FALSE);
        final CountDownLatch latch = new CountDownLatch(1);
        mainHandler().post(new Runnable() {
            @Override
            public void run() {
                try {
                    result.set(initializeOnMainThread(context));
                } finally {
                    latch.countDown();
                }
            }
        });
        try {
            if (!latch.await(MAIN_THREAD_WAIT_MS, TimeUnit.MILLISECONDS)) {
                unavailableReason = "main-thread initialization timed out";
            }
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            unavailableReason = "main-thread initialization interrupted";
        }
        return result.get();
    }

    private synchronized boolean initializeOnMainThread(Context context) {
        if (initialized.get() && flutterEngine != null && dartStarted) {
            return true;
        }
        if (context == null) {
            unavailableReason = "context is null";
            return false;
        }
        if (!isFlutterEmbeddingPresent()) {
            unavailableReason = "Flutter embedding classes not found; add the prebuilt "
                    + "flutter_embedding AAR to Assets/Plugins/Android/ (see README)";
            return false;
        }
        try {
            applicationContext = context.getApplicationContext();
            Class<?> engineClass = Class.forName(CLS_ENGINE);
            Constructor<?> engineCtor = engineClass.getConstructor(Context.class);
            flutterEngine = engineCtor.newInstance(applicationContext);

            Object dartExecutor = engineClass.getMethod("getDartExecutor").invoke(flutterEngine);
            Class<?> binaryMessengerClass = Class.forName(CLS_BINARY_MESSENGER);

            Class<?> methodChannelClass = Class.forName(CLS_METHOD_CHANNEL);
            Constructor<?> methodChannelCtor =
                    methodChannelClass.getConstructor(binaryMessengerClass, String.class);
            methodChannel = methodChannelCtor.newInstance(dartExecutor, BRIDGE_CHANNEL);
            Object methodCallHandler = createMethodCallHandler();
            if (methodCallHandler != null) {
                methodChannelClass.getMethod("setMethodCallHandler",
                        Class.forName(CLS_METHOD_CALL_HANDLER)).invoke(methodChannel, methodCallHandler);
            } else {
                Log.e(TAG, "Could not build MethodCallHandler; commands will not be forwarded");
            }

            Class<?> eventChannelClass = Class.forName(CLS_EVENT_CHANNEL);
            Constructor<?> eventChannelCtor =
                    eventChannelClass.getConstructor(binaryMessengerClass, String.class);
            eventChannel = eventChannelCtor.newInstance(dartExecutor, EVENT_CHANNEL);
            Object streamHandler = createStreamHandler();
            if (streamHandler != null) {
                eventChannelClass.getMethod("setStreamHandler",
                        Class.forName(CLS_STREAM_HANDLER)).invoke(eventChannel, streamHandler);
            } else {
                Log.e(TAG, "Could not build StreamHandler; events will not be forwarded");
            }

            initialized.set(true);
            unavailableReason = null;
            if (!startDefaultDartEntrypoint(dartExecutor)) {
                unavailableReason = "FlutterEngine created but the Dart entrypoint did not start";
                releaseEngineOnMainThread();
                initialized.set(false);
                return false;
            }
            Log.i(TAG, "Flutter host initialized via reflection; Dart entrypoint started");
            return true;
        } catch (Throwable t) {
            Log.e(TAG, "initialize failed", t);
            unavailableReason = "initialize failed: " + t;
            releaseEngineOnMainThread();
            initialized.set(false);
            return false;
        }
    }

    /**
     * Lazy self-initialization using the current Unity activity
     * (com.unity3d.player.UnityPlayer.currentActivity), so the transport works
     * even when the C# side never calls {@link #initialize(Context)} explicitly.
     */
    private boolean ensureInitialized() {
        if (initialized.get() && flutterEngine != null) {
            return true;
        }
        if (!isFlutterEmbeddingPresent()) {
            unavailableReason = "Flutter embedding classes not found";
            return false;
        }
        Activity activity = resolveCurrentActivity();
        if (activity == null) {
            unavailableReason = "Unity current activity unavailable for lazy init";
            return false;
        }
        // Do not hold the host monitor while initialize() waits for the main
        // looper. initializeOnMainThread() owns the engine mutation boundary.
        return initialize(activity);
    }

    /** Starts the generated module's default Dart entrypoint exactly once. */
    private boolean startDefaultDartEntrypoint(Object dartExecutor) {
        if (dartStarted || dartExecutor == null) {
            return dartStarted;
        }
        try {
            Class<?> dartExecutorClass = Class.forName(CLS_DART_EXECUTOR);
            Class<?> entrypointClass = Class.forName(CLS_DART_ENTRYPOINT);
            Object entrypoint = entrypointClass.getMethod("createDefault").invoke(null);
            dartExecutorClass.getMethod("executeDartEntrypoint", entrypointClass)
                    .invoke(dartExecutor, entrypoint);
            dartStarted = true;
            return true;
        } catch (Throwable t) {
            Log.e(TAG, "executeDartEntrypoint failed", t);
            return false;
        }
    }

    /** Initializes the engine and attaches the FlutterView on Android's UI thread. */
    public boolean initializeAndAttach(final Activity activity) {
        if (activity == null) {
            unavailableReason = "activity is null";
            return false;
        }
        if (Looper.myLooper() == Looper.getMainLooper()) {
            return initializeAndAttachOnMainThread(activity);
        }
        final AtomicReference<Boolean> result = new AtomicReference<Boolean>(Boolean.FALSE);
        final CountDownLatch latch = new CountDownLatch(1);
        mainHandler().post(new Runnable() {
            @Override
            public void run() {
                try {
                    result.set(initializeAndAttachOnMainThread(activity));
                } finally {
                    latch.countDown();
                }
            }
        });
        try {
            if (!latch.await(MAIN_THREAD_WAIT_MS, TimeUnit.MILLISECONDS)) {
                unavailableReason = "main-thread attach timed out";
            }
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            unavailableReason = "main-thread attach interrupted";
        }
        return result.get();
    }

    private boolean initializeAndAttachOnMainThread(Activity activity) {
        if (!initializeOnMainThread(activity)) {
            return false;
        }
        if (phoneViewAttached) {
            return true;
        }
        Object view = createFlutterView(activity);
        if (!(view instanceof View)) {
            return false;
        }
        View flutterView = (View) view;
        flutterView.setBackgroundColor(android.graphics.Color.TRANSPARENT);
        boolean added = addViewToUnityPlayer(activity, flutterView);
        if (added) {
            phoneFlutterView = view;
            phoneViewAttached = true;
            return true;
        }
        try {
            view.getClass().getMethod("detachFromFlutterEngine").invoke(view);
        } catch (Throwable t) {
            Log.w(TAG, "FlutterView detach after failed attach failed", t);
        }
        unavailableReason = "FlutterView created but UnityPlayer.addViewToPlayer failed";
        return false;
    }

    /** Detaches the phone view while keeping the engine alive for focus changes. */
    public boolean detachPhoneView() {
        if (Looper.myLooper() == Looper.getMainLooper()) {
            return detachPhoneViewOnMainThread();
        }
        final AtomicReference<Boolean> result = new AtomicReference<Boolean>(Boolean.FALSE);
        final CountDownLatch latch = new CountDownLatch(1);
        mainHandler().post(new Runnable() {
            @Override
            public void run() {
                try {
                    result.set(detachPhoneViewOnMainThread());
                } finally {
                    latch.countDown();
                }
            }
        });
        try {
            if (!latch.await(MAIN_THREAD_WAIT_MS, TimeUnit.MILLISECONDS)) {
                unavailableReason = "main-thread detach timed out";
            }
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            unavailableReason = "main-thread detach interrupted";
        }
        return result.get();
    }

    private boolean detachPhoneViewOnMainThread() {
        if (phoneFlutterView == null) {
            phoneViewAttached = false;
            return true;
        }
        Object view = phoneFlutterView;
        try {
            detachAndRemoveView(view);
            phoneFlutterView = null;
            phoneViewAttached = false;
            return true;
        } catch (Throwable t) {
            Log.e(TAG, "detachPhoneView failed", t);
            return false;
        }
    }

    /** Destroys the engine and clears all bridge state. */
    public void shutdown() {
        if (Looper.myLooper() == Looper.getMainLooper()) {
            shutdownOnMainThread();
            return;
        }
        final CountDownLatch latch = new CountDownLatch(1);
        mainHandler().post(new Runnable() {
            @Override
            public void run() {
                try {
                    shutdownOnMainThread();
                } finally {
                    latch.countDown();
                }
            }
        });
        try {
            if (!latch.await(MAIN_THREAD_WAIT_MS, TimeUnit.MILLISECONDS)) {
                unavailableReason = "main-thread shutdown timed out";
            }
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            unavailableReason = "main-thread shutdown interrupted";
        }
    }

    private void shutdownOnMainThread() {
        lifecycleGeneration.incrementAndGet();
        Map<Long, Object> abandonedResults;
        synchronized (pendingResults) {
            abandonedResults = new LinkedHashMap<Long, Object>(pendingResults);
            pendingResults.clear();
        }
        for (Object result : abandonedResults.values()) {
            callResultError(result, "host_shutdown", "Flutter host shut down");
        }
        eventSink = null;
        clearBufferedEvents();
        if (!stopOffscreenRenderingOnMainThread()) {
            Log.w(TAG, "shutdown deferred until Unity detaches offscreen texture");
            unavailableReason = "shutdown waiting for Unity render-thread detach";
            return;
        }
        releaseEngineOnMainThread();
        initialized.set(false);
        unavailableReason = "shutdown";
    }

    private void releaseEngineOnMainThread() {
        if (phoneFlutterView != null) {
            detachAndRemoveView(phoneFlutterView);
            phoneFlutterView = null;
        }
        if (flutterEngine != null) {
            try {
                flutterEngine.getClass().getMethod("destroy").invoke(flutterEngine);
            } catch (Throwable t) {
                Log.w(TAG, "FlutterEngine.destroy failed", t);
            }
            flutterEngine = null;
        }
        dartStarted = false;
        phoneViewAttached = false;
        methodChannel = null;
        eventChannel = null;
        applicationContext = null;
    }

    private void detachAndRemoveView(Object view) {
        if (view == null) {
            return;
        }
        if (view instanceof View) {
            ViewParent parent = ((View) view).getParent();
            if (parent instanceof ViewGroup) {
                ((ViewGroup) parent).removeView((View) view);
            }
        }
        try {
            view.getClass().getMethod("detachFromFlutterEngine").invoke(view);
        } catch (Throwable t) {
            Log.w(TAG, "FlutterView.detachFromFlutterEngine failed", t);
        }
    }

    // ------------------------------------------------------------------
    // Capability / state reporting.
    // ------------------------------------------------------------------

    /** True when at least the core Flutter embedding classes are loadable. */
    public boolean isFlutterEmbeddingPresent() {
        try {
            Class.forName(CLS_ENGINE);
            Class.forName(CLS_METHOD_CHANNEL);
            Class.forName(CLS_EVENT_CHANNEL);
            return true;
        } catch (Throwable t) {
            return false;
        }
    }

    /** True when the engine is constructed and the channels are registered. */
    public boolean isAvailable() {
        return initialized.get() && flutterEngine != null;
    }

    /**
     * Serialized availability snapshot for diagnostics. Always returns valid
     * JSON, so a C# caller can surface a human-readable reason without parsing
     * failures.
     */
    public String getStateJson() {
        try {
            JSONObject state = new JSONObject();
            state.put("available", isAvailable());
            state.put("embeddingPresent", isFlutterEmbeddingPresent());
            state.put("engine", flutterEngine != null);
            state.put("dartStarted", dartStarted);
            state.put("bridgeChannel", methodChannel != null);
            state.put("eventChannel", eventChannel != null);
            state.put("eventListenerAttached", eventSink != null);
            state.put("bufferedEvents", bufferedEventCount());
            state.put("phoneViewAttached", phoneViewAttached);
            state.put("offscreenRendering", offscreenRendering);
            state.put("reason", unavailableReason == null ? JSONObject.NULL : unavailableReason);
            state.put("unityTarget", (unityGameObject == null ? "" : unityGameObject)
                    + "/" + (unityCallbackMethod == null ? "" : unityCallbackMethod));
            return state.toString();
        } catch (Throwable t) {
            return "{\"available\":false,\"reason\":\"state serialization failed\"}";
        }
    }

    /** Overrides the Unity receiver used by {@link #deliverToUnity(String)}. */
    public void configureUnity(String gameObject, String callbackMethod) {
        if (gameObject == null || gameObject.trim().isEmpty() ||
                callbackMethod == null || callbackMethod.trim().isEmpty()) {
            unavailableReason = "Unity receiver name is empty";
            Log.w(TAG, "Ignoring invalid Unity receiver configuration");
            return;
        }
        this.unityGameObject = gameObject;
        this.unityCallbackMethod = callbackMethod;
        Log.i(TAG, "Unity receiver configured: " + gameObject + "." + callbackMethod);
    }

    /**
     * Documents the generated Flutter AOT artifacts this host expects, as a
     * JSON array (for diagnostics / UI). Mirrors the README table.
     */
    public static String expectedArtifactsJson() {
        return "["
                + "{\"artifact\":\"flutter_embedding_release.aar\","
                + " \"role\":\"io.flutter.* embedding classes\","
                + " \"location\":\"Assets/Plugins/Android/flutter_embedding_release.aar\"},"
                + "{\"artifact\":\"libflutter.so\","
                + " \"role\":\"Flutter engine native lib (per ABI)\","
                + " \"location\":\"Assets/Plugins/Android/<abi>/libflutter.so\"},"
                + "{\"artifact\":\"libapp.so\","
                + " \"role\":\"compiled Dart AOT snapshot (per ABI)\","
                + " \"location\":\"Assets/Plugins/Android/<abi>/libapp.so\"},"
                + "{\"artifact\":\"flutter_assets/\","
                + " \"role\":\"Dart asset bundle + kernel_blob.bin\","
                + " \"location\":\"Assets/StreamingAssets/flutter_assets/\"},"
                + "{\"artifact\":\"icudtl.dat\","
                + " \"role\":\"ICU data\","
                + " \"location\":\"Assets/StreamingAssets/icudtl.dat\"}"
                + "]";
    }

    // ------------------------------------------------------------------
    // Unity -> Flutter bridge (called from C# via JNI).
    // ------------------------------------------------------------------

    /**
     * Receives an engine {@code event} envelope (Unity C# shape) and pushes it
     * to the Dart shell through EventChannel {@code banxia.events}. While no
     * Dart listener is attached (eventSink == null) the event is held in the
     * bounded buffer instead of being dropped; the buffer is flushed in order
     * on the next {@code onListen} (see MAX_BUFFERED_EVENTS /
     * MAX_BUFFERED_EVENT_BYTES).
     */
    public void onUnityEvent(String json) {
        if (!ensureInitialized()) {
            Log.w(TAG, "onUnityEvent dropped (host unavailable): " + getStateJson());
            return;
        }
        final Object event = buildBridgeEvent(json);
        if (event == null) {
            return;
        }
        mainHandler().post(new Runnable() {
            @Override
            public void run() {
                if (eventSink != null) {
                    pushEvent(event);
                } else {
                    // No Dart listener yet: keep the event for the next
                    // onListen instead of dropping it (bounded queue below).
                    bufferEvent(event, json);
                }
            }
        });
    }

    /**
     * Receives an engine {@code reply} envelope for a previously forwarded
     * command id and completes the outstanding MethodChannel result.
     */
    public void onUnityReply(final long id, final String json) {
        Object result;
        synchronized (pendingResults) {
            result = pendingResults.remove(id);
        }
        if (result == null) {
            Log.w(TAG, "onUnityReply: no pending result for id " + id);
            return;
        }
        final Object pendingResult = result;
        final Map<String, Object> reply = buildBridgeReply(id, json);
        final long generation = lifecycleGeneration.get();
        mainHandler().post(new Runnable() {
            @Override
            public void run() {
                if (generation == lifecycleGeneration.get()) {
                    callResultSuccess(pendingResult, reply);
                } else {
                    callResultError(pendingResult, "host_shutdown", "Flutter host lifecycle changed");
                }
            }
        });
    }

    // ------------------------------------------------------------------
    // Flutter -> Unity bridge (MethodChannel handler).
    // ------------------------------------------------------------------

    /**
     * Invoked by the reflected MethodCallHandler when the Dart shell calls
     * {@code invokeMethod('call', envelope)}. Wraps the call into the Unity
     * envelope and forwards it through UnitySendMessage, remembering the Result
     * so {@link #onUnityReply(long, String)} can answer it later.
     */
    private void onFlutterMethodCall(Object call, Object result) {
        long id = 0L;
        boolean pending = false;
        try {
            String methodName = (String) readField(call.getClass(), call, "method");
            Object arguments = readField(call.getClass(), call, "arguments");

            String name;
            String payloadJson;
            if (arguments instanceof Map) {
                Map<?, ?> envelope = (Map<?, ?>) arguments;
                id = toLong(envelope.get("id"));
                name = envelope.get("name") == null ? "" : String.valueOf(envelope.get("name"));
                payloadJson = toJsonString(envelope.get("payload"));
            } else {
                // Fallback: treat the invoked method name as the command name.
                id = nextId();
                name = methodName == null ? "" : methodName;
                payloadJson = toJsonString(arguments);
            }
            if (id <= 0) {
                id = nextId();
            }
            if (name.trim().isEmpty()) {
                postResultError(result, "invalid_command", "Flutter command name is empty");
                return;
            }

            synchronized (pendingResults) {
                if (pendingResults.containsKey(id)) {
                    postResultError(result, "duplicate_command_id", "Flutter command id is already pending");
                    return;
                }
                if (pendingResults.size() >= MAX_PENDING_REPLIES) {
                    postResultError(result, "too_many_pending_commands", "Flutter command queue is full");
                    return;
                }
                pendingResults.put(id, result);
                pending = true;
            }

            if (!deliverToUnity(buildCommandEnvelope(id, name, payloadJson))) {
                synchronized (pendingResults) {
                    pendingResults.remove(id);
                }
                pending = false;
                postResultError(result, "unity_unavailable", "Unity command receiver is unavailable");
            }
        } catch (Throwable t) {
            Log.e(TAG, "onFlutterMethodCall failed", t);
            if (pending) {
                synchronized (pendingResults) {
                    pendingResults.remove(id);
                }
            }
            if (result != null) {
                postResultError(result, "bridge_error", String.valueOf(t));
            }
        }
    }

    // ------------------------------------------------------------------
    // View / fragment embedding.
    // ------------------------------------------------------------------

    /**
     * Phone path. Creates a real FlutterView attached to the shared engine and
     * adds it to the Unity player via {@code UnityPlayer.addViewToPlayer(view,
     * false)}. All view operations are marshalled to Android's main looper.
     */
    public boolean attachToUnityPlayer(final Activity activity) {
        if (activity == null) {
            unavailableReason = "activity is null";
            return false;
        }
        if (Looper.myLooper() == Looper.getMainLooper()) {
            return attachToUnityPlayerOnMainThread(activity);
        }
        final AtomicReference<Boolean> result = new AtomicReference<Boolean>(Boolean.FALSE);
        final CountDownLatch latch = new CountDownLatch(1);
        mainHandler().post(new Runnable() {
            @Override
            public void run() {
                try {
                    result.set(attachToUnityPlayerOnMainThread(activity));
                } finally {
                    latch.countDown();
                }
            }
        });
        try {
            if (!latch.await(MAIN_THREAD_WAIT_MS, TimeUnit.MILLISECONDS)) {
                unavailableReason = "main-thread attach timed out";
            }
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            unavailableReason = "main-thread attach interrupted";
        }
        return result.get();
    }

    private boolean attachToUnityPlayerOnMainThread(Activity activity) {
        if (!initializeOnMainThread(activity)) {
            return false;
        }
        if (phoneViewAttached && phoneFlutterView instanceof View) {
            return true;
        }
        Object view = createFlutterViewOnMainThread(activity);
        if (!(view instanceof View)) {
            return false;
        }
        View flutterView = (View) view;
        flutterView.setBackgroundColor(android.graphics.Color.TRANSPARENT);
        boolean added = addViewToUnityPlayer(activity, flutterView);
        if (added) {
            phoneFlutterView = view;
            phoneViewAttached = true;
            return true;
        }
        detachAndRemoveView(view);
        phoneViewAttached = false;
        unavailableReason = "FlutterView created but UnityPlayer.addViewToPlayer failed";
        return false;
    }

    /** Creates a FlutterView attached to the shared engine, or null. */
    public Object createFlutterView(final Context context) {
        if (context == null) {
            unavailableReason = "context is null";
            return null;
        }
        if (Looper.myLooper() == Looper.getMainLooper()) {
            if (!initializeOnMainThread(context)) {
                return null;
            }
            return createFlutterViewOnMainThread(context);
        }
        final AtomicReference<Object> result = new AtomicReference<Object>();
        final CountDownLatch latch = new CountDownLatch(1);
        mainHandler().post(new Runnable() {
            @Override
            public void run() {
                try {
                    if (initializeOnMainThread(context)) {
                        result.set(createFlutterViewOnMainThread(context));
                    }
                } finally {
                    latch.countDown();
                }
            }
        });
        try {
            if (!latch.await(MAIN_THREAD_WAIT_MS, TimeUnit.MILLISECONDS)) {
                unavailableReason = "main-thread FlutterView creation timed out";
            }
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            unavailableReason = "main-thread FlutterView creation interrupted";
        }
        return result.get();
    }

    private Object createFlutterViewOnMainThread(Context context) {
        try {
            Class<?> viewClass = Class.forName(CLS_VIEW);
            Constructor<?> ctor = viewClass.getConstructor(Context.class);
            Object view = ctor.newInstance(context);
            viewClass.getMethod("attachToFlutterEngine", Class.forName(CLS_ENGINE))
                    .invoke(view, flutterEngine);
            return view;
        } catch (Throwable t) {
            Log.e(TAG, "createFlutterView failed", t);
            unavailableReason = "createFlutterView failed: " + t;
            return null;
        }
    }

    /**
     * Fragment embedding is deliberately gated until the exact generated Flutter
     * embedding AAR has been inspected. FlutterFragment uses cached-engine
     * builders whose signatures vary across embedding releases; the phone path
     * uses FlutterView and UnityPlayerActivity is not a FragmentActivity anyway.
     */
    public Object createFlutterFragment() {
        unavailableReason = "FlutterFragment path is not verified for the packaged embedding";
        return null;
    }

    /**
     * Fragment embedding is unavailable until the generated AAR API is verified.
     * Keep this explicit rather than constructing a fragment that cannot be
     * attached or cleaned up reliably.
     */
    public boolean attachFlutterFragment(Activity activity, int containerViewId) {
        unavailableReason = "FlutterFragment path is not verified for the packaged embedding";
        return false;
    }

    private boolean addViewToUnityPlayer(Activity activity, View view) {
        try {
            Object unityPlayer = activity.getClass().getMethod("getUnityPlayer").invoke(activity);
            Method addView = unityPlayer.getClass()
                    .getMethod("addViewToPlayer", View.class, boolean.class);
            addView.invoke(unityPlayer, view, false);
            return true;
        } catch (Throwable t) {
            Log.e(TAG, "addViewToUnityPlayer failed (host is not UnityPlayerActivity?)", t);
            return false;
        }
    }

    // ------------------------------------------------------------------
    // Quest offscreen SurfaceTexture seam.
    //
    // On Quest there is no UnityPlayerActivity.addViewToPlayer surface to write
    // into; Flutter must render offscreen and Unity composites the texture. This
    // block is the transport seam only: it starts Flutter rendering into a
    // Surface backed by a detached SurfaceTexture and exposes that SurfaceTexture
    // plus attach/update helpers for Unity's render thread. It does NOT claim
    // Quest UI is complete — Unity must attach a GL texture and composite it.
    // ------------------------------------------------------------------

    /**
     * Starts Flutter rendering to an offscreen Surface (Quest path). The backing
     * SurfaceTexture is created detached; Unity attaches a GL texture on its
     * render thread via {@link #attachOffscreenTexture(int)} and consumes frames
     * via {@link #updateOffscreenTexture()}.
     */
    public boolean startOffscreenRendering(final int width, final int height) {
        if (Looper.myLooper() == Looper.getMainLooper()) {
            return startOffscreenRenderingOnMainThread(width, height);
        }
        final AtomicReference<Boolean> result = new AtomicReference<Boolean>(Boolean.FALSE);
        final CountDownLatch latch = new CountDownLatch(1);
        mainHandler().post(new Runnable() {
            @Override
            public void run() {
                try {
                    result.set(startOffscreenRenderingOnMainThread(width, height));
                } finally {
                    latch.countDown();
                }
            }
        });
        try {
            if (!latch.await(MAIN_THREAD_WAIT_MS, TimeUnit.MILLISECONDS)) {
                surfaceStatusReason = "main-thread offscreen start timed out";
            }
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            surfaceStatusReason = "main-thread offscreen start interrupted";
        }
        return result.get();
    }

    private boolean startOffscreenRenderingOnMainThread(int width, int height) {
        if (applicationContext == null) {
            Activity activity = resolveCurrentActivity();
            if (activity != null) {
                applicationContext = activity.getApplicationContext();
            }
        }
        if (!initializeOnMainThread(applicationContext)) {
            synchronized (offscreenLock) {
                surfaceStatusReason = "engine unavailable";
            }
            return false;
        }
        if (width <= 0 || height <= 0) {
            synchronized (offscreenLock) {
                surfaceStatusReason = "invalid surface size";
            }
            return false;
        }
        synchronized (offscreenLock) {
            if (offscreenRendering && offscreenWidth == width && offscreenHeight == height) {
                return true;
            }
        }
        // Renderer lifecycle calls must stay on the Android main thread. Unity
        // must detach the old GL binding before Android releases its texture.
        if (!stopOffscreenRenderingOnMainThread()) {
            return false;
        }
        SurfaceTexture surfaceTexture = null;
        Surface surface = null;
        try {
            surfaceTexture = new SurfaceTexture(false);
            surfaceTexture.setDefaultBufferSize(width, height);
            surface = new Surface(surfaceTexture);

            Object renderer = flutterEngine.getClass().getMethod("getRenderer").invoke(flutterEngine);
            Class<?> rendererClass = Class.forName(CLS_RENDERER);
            rendererClass.getMethod("startRenderingToSurface", Surface.class)
                    .invoke(renderer, surface);
            rendererClass.getMethod("surfaceChanged", int.class, int.class)
                    .invoke(renderer, width, height);

            synchronized (offscreenLock) {
                offscreenSurface = surface;
                offscreenSurfaceTexture = surfaceTexture;
                offscreenWidth = width;
                offscreenHeight = height;
                offscreenGeneration++;
                offscreenTextureAttached = false;
                offscreenDetachRequested = false;
                offscreenRendering = true;
                surfaceStatusReason = "rendering to offscreen surface";
            }
            Log.i(TAG, "Offscreen Flutter surface started: " + width + "x" + height);
            return true;
        } catch (Throwable t) {
            Log.e(TAG, "startOffscreenRendering failed", t);
            try {
                if (flutterEngine != null) {
                    Object renderer = flutterEngine.getClass().getMethod("getRenderer")
                            .invoke(flutterEngine);
                    Class.forName(CLS_RENDERER).getMethod("stopRenderingToSurface")
                            .invoke(renderer);
                }
            } catch (Throwable stopError) {
                Log.w(TAG, "renderer cleanup after offscreen start failure failed", stopError);
            }
            if (surface != null) {
                try {
                    surface.release();
                } catch (Throwable ignored) {
                }
            }
            if (surfaceTexture != null) {
                try {
                    surfaceTexture.release();
                } catch (Throwable ignored) {
                }
            }
            synchronized (offscreenLock) {
                offscreenRendering = false;
                surfaceStatusReason = "startOffscreenRendering failed: " + t;
            }
            return false;
        }
    }

    /** Stops offscreen rendering and releases the surface and SurfaceTexture. */
    public void stopOffscreenRendering() {
        if (Looper.myLooper() == Looper.getMainLooper()) {
            stopOffscreenRenderingOnMainThread();
            return;
        }
        final CountDownLatch latch = new CountDownLatch(1);
        mainHandler().post(new Runnable() {
            @Override
            public void run() {
                try {
                    stopOffscreenRenderingOnMainThread();
                } finally {
                    latch.countDown();
                }
            }
        });
        try {
            if (!latch.await(MAIN_THREAD_WAIT_MS, TimeUnit.MILLISECONDS)) {
                surfaceStatusReason = "main-thread offscreen stop timed out";
            }
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            surfaceStatusReason = "main-thread offscreen stop interrupted";
        }
    }

    private boolean stopOffscreenRenderingOnMainThread() {
        Object surface;
        long generation;
        synchronized (offscreenLock) {
            if (offscreenTextureAttached && detachAcknowledgedGeneration != offscreenGeneration) {
                offscreenDetachRequested = true;
                detachRequestedGeneration = offscreenGeneration;
                surfaceStatusReason = "waiting for Unity render-thread detach";
                return false;
            }
        }
        SurfaceTexture surfaceTexture;
        synchronized (offscreenLock) {
            offscreenRendering = false;
            surface = offscreenSurface;
            surfaceTexture = offscreenSurfaceTexture;
            offscreenSurface = null;
            offscreenSurfaceTexture = null;
            offscreenWidth = 0;
            offscreenHeight = 0;
            surfaceStatusReason = "stopped";
        }
        try {
            if (flutterEngine != null) {
                Object renderer = flutterEngine.getClass().getMethod("getRenderer")
                        .invoke(flutterEngine);
                Class.forName(CLS_RENDERER).getMethod("stopRenderingToSurface")
                        .invoke(renderer);
            }
        } catch (Throwable t) {
            Log.w(TAG, "stopRenderingToSurface failed", t);
        }
        if (surface != null) {
            try {
                surface.getClass().getMethod("release").invoke(surface);
            } catch (Throwable ignored) {
                // surface release is best-effort
            }
        }
        if (surfaceTexture != null) {
            try {
                surfaceTexture.release();
            } catch (Throwable ignored) {
                // surface texture release is best-effort
            }
        }
        synchronized (offscreenLock) {
            offscreenTextureAttached = false;
            offscreenDetachRequested = false;
            offscreenGeneration++;
        }
        return true;
    }

    /** True once {@link #startOffscreenRendering(int, int)} succeeded. */
    public boolean isOffscreenRendering() {
        return offscreenRendering;
    }

    /** Exposes the offscreen SurfaceTexture for Unity's render thread. */
    public SurfaceTexture getOffscreenSurfaceTexture() {
        synchronized (offscreenLock) {
            return offscreenSurfaceTexture;
        }
    }

    /** Exposes the offscreen SurfaceTexture's HardwareBuffer (API 26+), or null. */
    public Object getOffscreenHardwareBuffer() {
        SurfaceTexture texture;
        synchronized (offscreenLock) {
            texture = offscreenSurfaceTexture;
            if (texture == null || Build.VERSION.SDK_INT < Build.VERSION_CODES.O) {
                return null;
            }
        }
        try {
            // SurfaceTexture#getHardwareBuffer is API-level dependent and is not
            // present in every Android SDK stub used by Unity 2022.3. Keep the
            // optional capability reflection-only so the host compiles against
            // the project SDK and still works on API 26+ implementations.
            Method method = texture.getClass().getMethod("getHardwareBuffer");
            return method.invoke(texture);
        } catch (Throwable t) {
            Log.w(TAG, "getHardwareBuffer failed", t);
            return null;
        }
    }

    /** Unity render-thread hook: binds the SurfaceTexture to a GL texture name. */
    public boolean attachOffscreenTexture(int textureName) {
        synchronized (offscreenLock) {
            SurfaceTexture texture = offscreenSurfaceTexture;
            if (texture == null || textureName <= 0 || !offscreenRendering || offscreenDetachRequested) {
                return false;
            }
            try {
                texture.attachToGLContext(textureName);
                offscreenTextureAttached = true;
                return true;
            } catch (Throwable t) {
                Log.e(TAG, "attachOffscreenTexture failed", t);
                return false;
            }
        }
    }

    /** Detaches the current SurfaceTexture from Unity's render-thread GL context. */
    public boolean detachOffscreenTexture() {
        SurfaceTexture texture;
        long generation;
        synchronized (offscreenLock) {
            texture = offscreenSurfaceTexture;
            generation = offscreenGeneration;
            if (texture == null) {
                return true;
            }
            offscreenDetachRequested = true;
            detachRequestedGeneration = generation;
        }
        try {
            texture.detachFromGLContext();
            synchronized (offscreenLock) {
                if (texture == offscreenSurfaceTexture && generation == offscreenGeneration) {
                    offscreenTextureAttached = false;
                    offscreenDetachRequested = false;
                    detachAcknowledgedGeneration = generation;
                    offscreenLock.notifyAll();
                }
            }
            return true;
        } catch (Throwable t) {
            Log.e(TAG, "detachOffscreenTexture failed", t);
            return false;
        }
    }

    /** Unity render-thread hook: consumes the latest Flutter frame into the texture. */
    public boolean updateOffscreenTexture() {
        SurfaceTexture texture;
        synchronized (offscreenLock) {
            texture = offscreenSurfaceTexture;
            if (texture == null || !offscreenRendering || offscreenDetachRequested || !offscreenTextureAttached) {
                return false;
            }
        }
        try {
            texture.updateTexImage();
            return true;
        } catch (Throwable t) {
            Log.e(TAG, "updateOffscreenTexture failed", t);
            return false;
        }
    }

    /**
     * Offscreen seam status. Reports exactly what the host knows: whether the
     * surface exists and rendering was requested. It reports nothing about Unity
     * having composited the texture, so consumers must treat a true
     * {@code offscreenRendering} as "surface ready" — not "Quest UI complete".
     */
    public String getSurfaceStatusJson() {
        try {
            JSONObject status = new JSONObject();
            status.put("offscreenRendering", offscreenRendering);
            status.put("surfaceCreated", offscreenSurface != null);
            status.put("surfaceTextureCreated", offscreenSurfaceTexture != null);
            status.put("width", offscreenWidth);
            status.put("height", offscreenHeight);
            status.put("hardwareBufferAvailable",
                    offscreenSurfaceTexture != null && Build.VERSION.SDK_INT >= Build.VERSION_CODES.O);
            status.put("reason",
                    surfaceStatusReason == null ? JSONObject.NULL : surfaceStatusReason);
            return status.toString();
        } catch (Throwable t) {
            return "{\"offscreenRendering\":false,\"reason\":\"serialization failed\"}";
        }
    }

    /**
     * Preflight report for the required AOT artifacts and runtime readiness.
     * Returns valid JSON describing whether the embedding classes, APK Flutter
     * assets, engine, phone view and offscreen surface are present/active, plus
     * the expected artifact table from {@link #expectedArtifactsJson()}.
     */
    public String preflight(Context context) {
        try {
            JSONObject report = new JSONObject();
            report.put("embeddingPresent", isFlutterEmbeddingPresent());
            report.put("engineInitialized", isAvailable());
            report.put("flutterAssetsPresent", hasFlutterAssets(context));
            report.put("phoneViewAttached", phoneViewAttached);
            report.put("offscreenRendering", offscreenRendering);
            report.put("surfaceTextureCreated", offscreenSurfaceTexture != null);
            report.put("expectedArtifacts", new JSONArray(expectedArtifactsJson()));
            return report.toString();
        } catch (Throwable t) {
            return "{\"preflight\":\"failed\"}";
        }
    }

    /** True when the APK assets contain a non-empty flutter_assets/ directory. */
    private boolean hasFlutterAssets(Context context) {
        if (context == null) {
            return false;
        }
        try {
            String[] entries = context.getAssets().list("flutter_assets");
            return entries != null && entries.length > 0;
        } catch (Throwable t) {
            return false;
        }
    }

    // ------------------------------------------------------------------
    // Envelope translation.
    // ------------------------------------------------------------------

    /** Builds the Unity C# command envelope (payload as an opaque JSON string). */
    private String buildCommandEnvelope(long id, String name, String payloadJson) {
        try {
            JSONObject envelope = new JSONObject();
            envelope.put("v", 1);
            envelope.put("id", id);
            envelope.put("type", "cmd");
            envelope.put("name", name == null ? "" : name);
            envelope.put("payload", payloadJson == null ? "" : payloadJson);
            envelope.put("error", "");
            return envelope.toString();
        } catch (Throwable t) {
            Log.e(TAG, "buildCommandEnvelope failed", t);
            return "{\"v\":1,\"id\":" + id + ",\"type\":\"cmd\",\"name\":\""
                    + escape(name) + "\",\"payload\":\"\",\"error\":\"\"}";
        }
    }

    /** Builds the Dart {@code BridgeReply} map ({id, ok, data, error}) from a Unity reply envelope. */
    private Map<String, Object> buildBridgeReply(long id, String unityReplyJson) {
        Map<String, Object> reply = new LinkedHashMap<String, Object>();
        reply.put("id", id);
        String payload = "";
        String error = "";
        if (unityReplyJson != null && !unityReplyJson.trim().isEmpty()) {
            try {
                JSONObject envelope = new JSONObject(unityReplyJson);
                payload = envelope.optString("payload", "");
                error = envelope.optString("error", "");
            } catch (Throwable t) {
                // Not an envelope; treat the whole string as a bare success payload.
                payload = unityReplyJson;
            }
        }
        boolean ok = error == null || error.isEmpty();
        reply.put("ok", ok);
        Object data = parseJson(payload);
        if (data != null) {
            reply.put("data", jsonToJava(data));
        }
        reply.put("error", error == null ? "" : error);
        return reply;
    }

    /** Builds the Dart {@code BridgeEnvelope} event map from a Unity event envelope. */
    private Map<String, Object> buildBridgeEvent(String unityEventJson) {
        if (unityEventJson == null || unityEventJson.trim().isEmpty()) {
            return null;
        }
        try {
            JSONObject envelope = new JSONObject(unityEventJson);
            if (!"event".equals(envelope.optString("type", "event"))) {
                return null;
            }
            Map<String, Object> out = new LinkedHashMap<String, Object>();
            out.put("v", 1);
            out.put("id", envelope.optLong("id", 0L));
            out.put("type", "event");
            out.put("name", envelope.optString("name", ""));
            Object payload = parseJson(envelope.optString("payload", ""));
            if (payload != null) {
                out.put("payload", jsonToJava(payload));
            }
            String error = envelope.optString("error", "");
            if (error != null && !error.isEmpty()) {
                out.put("error", error);
            }
            return out;
        } catch (Throwable t) {
            Log.e(TAG, "buildBridgeEvent failed", t);
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Reflection plumbing.
    // ------------------------------------------------------------------

    private Object createMethodCallHandler() {
        try {
            Class<?> handlerClass = Class.forName(CLS_METHOD_CALL_HANDLER);
            return Proxy.newProxyInstance(
                    handlerClass.getClassLoader(),
                    new Class<?>[]{handlerClass},
                    new InvocationHandler() {
                        @Override
                        public Object invoke(Object proxy, Method method, Object[] args) {
                            String name = method.getName();
                            if ("onMethodCall".equals(name) && args != null && args.length >= 2) {
                                onFlutterMethodCall(args[0], args[1]);
                                return null;
                            }
                            return dispatchObjectMethod(proxy, method, args);
                        }
                    });
        } catch (Throwable t) {
            Log.e(TAG, "createMethodCallHandler failed", t);
            return null;
        }
    }

    /**
     * Builds the reflected EventChannel.StreamHandler. {@code onListen} stores
     * the EventSink and flushes, on the Android main thread, the events that
     * were buffered while no listener was attached; {@code onCancel} detaches
     * the sink and discards any remaining buffer.
     */
    private Object createStreamHandler() {
        try {
            Class<?> handlerClass = Class.forName(CLS_STREAM_HANDLER);
            return Proxy.newProxyInstance(
                    handlerClass.getClassLoader(),
                    new Class<?>[]{handlerClass},
                    new InvocationHandler() {
                        @Override
                        public Object invoke(Object proxy, Method method, Object[] args) {
                            String name = method.getName();
                            if ("onListen".equals(name) && args != null && args.length >= 2) {
                                eventSink = args[1];
                                Log.i(TAG, "Flutter event listener attached");
                                // Deliver anything buffered while no listener was
                                // attached, in arrival order. EventSink calls
                                // must stay on the platform thread, so drain on
                                // the Android main looper.
                                mainHandler().post(new Runnable() {
                                    @Override
                                    public void run() {
                                        flushBufferedEvents();
                                    }
                                });
                                return null;
                            }
                            if ("onCancel".equals(name)) {
                                eventSink = null;
                                clearBufferedEvents();
                                Log.i(TAG, "Flutter event listener detached; cleared event buffer");
                                return null;
                            }
                            return dispatchObjectMethod(proxy, method, args);
                        }
                    });
        } catch (Throwable t) {
            Log.e(TAG, "createStreamHandler failed", t);
            return null;
        }
    }

    private static Object dispatchObjectMethod(Object proxy, Method method, Object[] args) {
        String name = method.getName();
        if ("hashCode".equals(name)) {
            return System.identityHashCode(proxy);
        }
        if ("equals".equals(name)) {
            return proxy == (args != null && args.length > 0 ? args[0] : null);
        }
        if ("toString".equals(name)) {
            return "BanxiaFlutterHost.ProxyHandler";
        }
        return null;
    }

    private void pushEvent(Object value) {
        Object sink = eventSink;
        if (sink == null) {
            Log.w(TAG, "No Flutter event listener attached; dropping event");
            return;
        }
        try {
            sink.getClass().getMethod("success", Object.class).invoke(sink, value);
        } catch (Throwable t) {
            Log.e(TAG, "pushEvent failed", t);
        }
    }

    /**
     * Queues an event for later delivery while no Flutter listener is attached
     * (see the bounded-buffer notes in the State section). Evicts the oldest
     * buffered events until both bounds hold; the size charged is the UTF-8
     * byte length of the raw accepted envelope, so oversized payloads are
     * accounted for (and evicted) before they inflate memory. A single event
     * larger than the byte budget is still kept, so the event count remains
     * the hard bound.
     */
    private void bufferEvent(Object event, String rawJson) {
        int bytes = rawJson == null ? 0 : rawJson.getBytes(StandardCharsets.UTF_8).length;
        int count;
        int total;
        synchronized (bufferedEventsLock) {
            while (!bufferedEvents.isEmpty()
                    && (bufferedEvents.size() >= MAX_BUFFERED_EVENTS
                        || bufferedEventBytes + bytes > MAX_BUFFERED_EVENT_BYTES)) {
                BufferedEvent oldest = bufferedEvents.remove(0);
                bufferedEventBytes -= oldest.utf8Bytes;
            }
            bufferedEvents.add(new BufferedEvent(event, bytes));
            bufferedEventBytes += bytes;
            count = bufferedEvents.size();
            total = bufferedEventBytes;
        }
        Log.w(TAG, "No Flutter event listener; buffered event (buffer: " + count
                + "/" + MAX_BUFFERED_EVENTS + " events, " + total + "/"
                + MAX_BUFFERED_EVENT_BYTES + " UTF-8 bytes)");
    }

    /** Discards the whole event buffer (listener cancelled or host shutdown). */
    private void clearBufferedEvents() {
        synchronized (bufferedEventsLock) {
            bufferedEvents.clear();
            bufferedEventBytes = 0;
        }
    }

    /**
     * Delivers everything buffered while no listener was attached, in arrival
     * order, to the current EventSink. Runs on the Android main thread
     * ({@link #createStreamHandler()} posts it there from onListen). Events
     * arriving after the listener attaches are pushed directly by
     * {@link #onUnityEvent(String)} and may interleave with this drain; the
     * buffered portion keeps its original order.
     */
    private void flushBufferedEvents() {
        final List<BufferedEvent> snapshot;
        synchronized (bufferedEventsLock) {
            if (bufferedEvents.isEmpty()) {
                return;
            }
            snapshot = new ArrayList<BufferedEvent>(bufferedEvents);
            bufferedEvents.clear();
            bufferedEventBytes = 0;
        }
        for (BufferedEvent buffered : snapshot) {
            pushEvent(buffered.event);
        }
        Log.i(TAG, "Flushed " + snapshot.size() + " buffered events in order");
    }

    /** Current number of events waiting for a Dart listener (diagnostics). */
    private int bufferedEventCount() {
        synchronized (bufferedEventsLock) {
            return bufferedEvents.size();
        }
    }

    private void postResultError(final Object result, final String code, final String message) {
        if (result == null) {
            return;
        }
        final long generation = lifecycleGeneration.get();
        mainHandler().post(new Runnable() {
            @Override
            public void run() {
                if (generation == lifecycleGeneration.get()) {
                    callResultError(result, code, message);
                }
            }
        });
    }

    private void callResultSuccess(Object result, Object value) {
        if (result == null) {
            return;
        }
        try {
            result.getClass().getMethod("success", Object.class).invoke(result, value);
        } catch (Throwable t) {
            Log.e(TAG, "Result.success failed", t);
        }
    }

    private void callResultError(Object result, String code, String message) {
        if (result == null) {
            return;
        }
        try {
            result.getClass().getMethod("error", String.class, String.class, Object.class)
                    .invoke(result, code == null ? "bridge_error" : code,
                            message == null ? "Flutter bridge error" : message,
                            null);
        } catch (Throwable t) {
            Log.e(TAG, "Result.error failed", t);
        }
    }

    private boolean deliverToUnity(String json) {
        String gameObject = unityGameObject;
        String callback = unityCallbackMethod;
        if (gameObject == null || gameObject.trim().isEmpty() ||
                callback == null || callback.trim().isEmpty()) {
            Log.w(TAG, "Unity receiver not configured; dropping command: " + json);
            return false;
        }
        try {
            Class<?> unityPlayer = Class.forName("com.unity3d.player.UnityPlayer");
            unityPlayer.getMethod("UnitySendMessage", String.class, String.class, String.class)
                    .invoke(null, gameObject, callback, json);
            return true;
        } catch (Throwable t) {
            Log.e(TAG, "UnitySendMessage failed", t);
            return false;
        }
    }

    private Activity resolveCurrentActivity() {
        try {
            Class<?> unityPlayer = Class.forName("com.unity3d.player.UnityPlayer");
            Field field = unityPlayer.getField("currentActivity");
            Object value = field.get(null);
            return value instanceof Activity ? (Activity) value : null;
        } catch (Throwable t) {
            Log.w(TAG, "resolveCurrentActivity failed", t);
            return null;
        }
    }

    private Handler mainHandler() {
        Handler handler = mainHandler;
        if (handler != null) {
            return handler;
        }
        synchronized (mainHandlerLock) {
            if (mainHandler == null) {
                mainHandler = new Handler(Looper.getMainLooper());
            }
            return mainHandler;
        }
    }

    private long nextId() {
        long id = nextId.getAndIncrement();
        if (id > 0L) {
            return id;
        }
        // Avoid ever sending a negative/zero id after AtomicLong overflow.
        nextId.compareAndSet(id + 1L, 1L);
        return nextId();
    }

    private static Object readField(Class<?> clazz, Object target, String name) throws Exception {
        return clazz.getField(name).get(target);
    }

    private static long toLong(Object value) {
        if (value instanceof Number) {
            return ((Number) value).longValue();
        }
        if (value == null) {
            return 0L;
        }
        try {
            return Long.parseLong(String.valueOf(value));
        } catch (NumberFormatException e) {
            return 0L;
        }
    }

    /** Serializes a Map/List/primitive into a JSON string (for the Unity envelope). */
    private static String toJsonString(Object value) {
        if (value == null) {
            return "";
        }
        try {
            Object wrapped = JSONObject.wrap(value);
            return wrapped == null ? "" : wrapped.toString();
        } catch (Throwable t) {
            Log.w(TAG, "toJsonString fallback", t);
            return String.valueOf(value);
        }
    }

    /** Parses a JSON string into org.json types (or returns the raw string on failure). */
    private static Object parseJson(String json) {
        if (json == null || json.trim().isEmpty()) {
            return null;
        }
        try {
            return new JSONTokener(json).nextValue();
        } catch (Throwable t) {
            Log.w(TAG, "parseJson failed; treating as plain string", t);
            return json;
        }
    }

    /**
     * Converts org.json types into plain java.util collections so they can be
     * passed through Flutter's StandardMethodCodec (which does not understand
     * org.json.JSONObject/JSONArray directly).
     */
    private static Object jsonToJava(Object value) {
        if (value instanceof JSONObject) {
            JSONObject obj = (JSONObject) value;
            Map<String, Object> map = new LinkedHashMap<String, Object>();
            Iterator<String> keys = obj.keys();
            while (keys.hasNext()) {
                String key = keys.next();
                map.put(key, jsonToJava(obj.opt(key)));
            }
            return map;
        }
        if (value instanceof JSONArray) {
            JSONArray arr = (JSONArray) value;
            List<Object> list = new ArrayList<Object>();
            for (int i = 0; i < arr.length(); i++) {
                list.add(jsonToJava(arr.opt(i)));
            }
            return list;
        }
        if (JSONObject.NULL.equals(value)) {
            return null;
        }
        return value;
    }

    private static String escape(String value) {
        if (value == null) {
            return "";
        }
        return value.replace("\\", "\\\\").replace("\"", "\\\"");
    }
}
