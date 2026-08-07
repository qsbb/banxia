package com.lingxi.banxia.filepicker;

import android.app.Activity;
import android.content.ClipData;
import android.content.ContentResolver;
import android.content.Intent;
import android.database.Cursor;
import android.net.Uri;
import android.os.Bundle;
import android.provider.OpenableColumns;
import android.util.Base64;

import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.List;
import java.util.Set;

public final class BanxiaFilePickerActivity extends Activity {
    static final String EXTRA_RECEIVER = "receiver";
    static final String EXTRA_CALLBACK = "callback";
    private static final int PICK_REQUEST = 3107;
    private static final long MAX_SINGLE_BYTES = 512L * 1024L * 1024L;
    private static final long MAX_TOTAL_BYTES = 512L * 1024L * 1024L;

    private String receiver;
    private String callback;

    @Override
    protected void onCreate(Bundle state) {
        super.onCreate(state);
        receiver = getIntent().getStringExtra(EXTRA_RECEIVER);
        callback = getIntent().getStringExtra(EXTRA_CALLBACK);
        if (state == null) {
            launchPicker();
        }
    }

    private void launchPicker() {
        Intent picker = new Intent(Intent.ACTION_OPEN_DOCUMENT);
        picker.addCategory(Intent.CATEGORY_OPENABLE);
        picker.setType("*/*");
        picker.putExtra(Intent.EXTRA_ALLOW_MULTIPLE, true);
        picker.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION | Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION);
        startActivityForResult(picker, PICK_REQUEST);
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);
        if (requestCode != PICK_REQUEST) {
            return;
        }
        if (resultCode != RESULT_OK || data == null) {
            deliver("cancel", "用户取消了选择");
            return;
        }

        try {
            List<Uri> uris = collectUris(data);
            if (uris.isEmpty()) {
                throw new IOException("未选择文件");
            }
            File batchDirectory = createBatchDirectory();
            copyUris(uris, batchDirectory);
            deliver("ok", batchDirectory.getAbsolutePath());
        } catch (Exception exception) {
            deliver("error", exception.getMessage() == null ? "文件复制失败" : exception.getMessage());
        }
    }

    private List<Uri> collectUris(Intent data) {
        List<Uri> uris = new ArrayList<>();
        ClipData clipData = data.getClipData();
        if (clipData != null) {
            for (int index = 0; index < clipData.getItemCount(); index++) {
                Uri uri = clipData.getItemAt(index).getUri();
                if (uri != null) {
                    uris.add(uri);
                }
            }
        } else if (data.getData() != null) {
            uris.add(data.getData());
        }
        return uris;
    }

    private File createBatchDirectory() throws IOException {
        File base = getExternalFilesDir(null);
        if (base == null) {
            base = getFilesDir();
        }
        File imports = new File(base, "Imports/Batches");
        if (!imports.exists() && !imports.mkdirs()) {
            throw new IOException("无法创建导入目录");
        }
        File batch = new File(imports, "Batch_" + System.currentTimeMillis());
        if (!batch.mkdirs()) {
            throw new IOException("无法创建导入批次目录");
        }
        return batch;
    }

    private void copyUris(List<Uri> uris, File batchDirectory) throws IOException {
        long totalBytes = 0L;
        Set<String> usedNames = new HashSet<>();
        ContentResolver resolver = getContentResolver();
        for (Uri uri : uris) {
            String displayName = queryDisplayName(resolver, uri);
            String safeName = safeFileName(displayName);
            safeName = uniqueName(safeName, usedNames);
            File destination = new File(batchDirectory, safeName);
            long copied = copyUri(resolver, uri, destination);
            if (copied > MAX_SINGLE_BYTES || totalBytes > MAX_TOTAL_BYTES - copied) {
                destination.delete();
                throw new IOException("选中文件总大小超过 512 MiB");
            }
            totalBytes += copied;
        }
    }

    private long copyUri(ContentResolver resolver, Uri uri, File destination) throws IOException {
        long total = 0L;
        try (InputStream input = resolver.openInputStream(uri);
             FileOutputStream output = new FileOutputStream(destination)) {
            if (input == null) {
                throw new IOException("无法读取所选文件");
            }
            byte[] buffer = new byte[64 * 1024];
            int read;
            while ((read = input.read(buffer)) != -1) {
                total += read;
                if (total > MAX_SINGLE_BYTES) {
                    throw new IOException("单个文件超过 512 MiB");
                }
                output.write(buffer, 0, read);
            }
        }
        return total;
    }

    private String queryDisplayName(ContentResolver resolver, Uri uri) {
        Cursor cursor = resolver.query(uri, new String[]{OpenableColumns.DISPLAY_NAME}, null, null, null);
        if (cursor != null) {
            try {
                if (cursor.moveToFirst()) {
                    int index = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME);
                    if (index >= 0) {
                        String value = cursor.getString(index);
                        if (value != null && !value.trim().isEmpty()) {
                            return value;
                        }
                    }
                }
            } finally {
                cursor.close();
            }
        }
        String fallback = uri.getLastPathSegment();
        return fallback == null || fallback.isEmpty() ? "import.bin" : fallback;
    }

    private String safeFileName(String value) {
        String source = value == null ? "import.bin" : value;
        String safe = source.replaceAll("[\\\\/:*?\\\"<>|\\p{Cntrl}]", "_").trim();
        if (safe.isEmpty() || ".".equals(safe) || "..".equals(safe)) {
            safe = "import.bin";
        }
        return safe.length() > 128 ? safe.substring(0, 128) : safe;
    }

    private String uniqueName(String name, Set<String> usedNames) {
        String candidate = name;
        int suffix = 2;
        while (!usedNames.add(candidate)) {
            int dot = name.lastIndexOf('.');
            String stem = dot > 0 ? name.substring(0, dot) : name;
            String extension = dot > 0 ? name.substring(dot) : "";
            candidate = stem + "_" + suffix++ + extension;
        }
        return candidate;
    }

    private void deliver(String state, String value) {
        String target = receiver;
        String method = callback;
        if (target != null && method != null) {
            String encoded = Base64.encodeToString(
                (value == null ? "" : value).getBytes(StandardCharsets.UTF_8),
                Base64.NO_WRAP);
            try
            {
                Class<?> unityPlayer = Class.forName("com.unity3d.player.UnityPlayer");
                unityPlayer.getMethod("UnitySendMessage", String.class, String.class, String.class)
                    .invoke(null, target, method, state + ":" + encoded);
            }
            catch (Exception ignored)
            {
                // The Unity runtime may already be shutting down; finish the picker safely.
            }
        }
        finish();
    }
}