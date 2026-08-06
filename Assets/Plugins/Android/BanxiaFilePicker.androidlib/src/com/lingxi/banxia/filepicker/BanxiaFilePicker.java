package com.lingxi.banxia.filepicker;

import android.app.Activity;
import android.content.Intent;

public final class BanxiaFilePicker {
    private BanxiaFilePicker() {
    }

    public static void open(Activity host, String receiver, String callbackMethod) {
        Intent intent = new Intent(host, BanxiaFilePickerActivity.class);
        intent.putExtra(BanxiaFilePickerActivity.EXTRA_RECEIVER, receiver);
        intent.putExtra(BanxiaFilePickerActivity.EXTRA_CALLBACK, callbackMethod);
        host.startActivity(intent);
    }
}