using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace QuestMmdPlayer
{
    /// <summary>
    /// GitHub Releases 在线更新：查询最新版本 → 系统 DownloadManager 下载
    /// （进度轮询驱动应用内进度条）→ content:// URI 直接发起系统安装。
    /// 平台无关层：Quest/Phone 共用；全程零第三方依赖（不需要 FileProvider /
    /// androidx——DownloadManager 的 content URI 可被包安装器直接打开）。
    /// </summary>
    public sealed class BanxiaUpdateChecker : MonoBehaviour
    {
        private const string ReleasesApiUrl = "https://api.github.com/repos/qsbb/banxia/releases/latest";
        private const string ApkMimeType = "application/vnd.android.package-archive";
        private const int DownloadStatusSuccessful = 8;
        private const int DownloadStatusFailed = 16;

        public sealed class UpdateInfo
        {
            public string Version = string.Empty;
            public string ReleaseUrl = string.Empty;
            public string ApkUrl = string.Empty;
            public string ApkName = string.Empty;
            public bool HasUpdate;
        }

        /// <summary>查询 GitHub 最新 release；资产按构建形态自动挑选（Phone/Quest）。</summary>
        public async Task<UpdateInfo> CheckForUpdateAsync()
        {
            var info = new UpdateInfo();
            try
            {
                using (var request = UnityWebRequest.Get(ReleasesApiUrl))
                {
                    request.SetRequestHeader("User-Agent", "banxia-client");
                    request.timeout = 15;
                    var operation = request.SendWebRequest();
                    while (!operation.isDone)
                    {
                        await Task.Yield();
                    }
                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        Debug.LogWarning("[Update] 查询失败: " + request.error);
                        return info;
                    }
                    var json = request.downloadHandler.text;
                    info.Version = ExtractJsonString(json, "tag_name");
                    info.ReleaseUrl = ExtractJsonString(json, "html_url");
                    bool phoneForm =
#if BANXIA_PHONE
                        true;
#else
                        false;
#endif
                    info.ApkUrl = FindApkAsset(json, phoneForm);
                    info.ApkName = phoneForm ? "Phone" : "Quest";
                }
                info.HasUpdate = IsNewerVersion(info.Version, Application.version);
                return info;
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[Update] 检查异常: " + exception.Message);
                return info;
            }
        }

        /// <summary>
        /// 系统 DownloadManager 下载并在完成后发起安装。
        /// progress 回调 0~1；返回用户可读的结果描述（空串 = 正常发起安装）。
        /// </summary>
        public async Task<string> DownloadAndInstallAsync(UpdateInfo info, Action<float> progress)
        {
            if (info == null || string.IsNullOrEmpty(info.ApkUrl))
            {
                return "未找到可下载的安装包，请到 GitHub Releases 手动下载：" + (info?.ReleaseUrl ?? ReleasesApiUrl);
            }
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                long downloadId = EnqueueDownload(info);
                if (downloadId < 0)
                {
                    return "无法加入系统下载队列，请手动下载：" + info.ReleaseUrl;
                }
                float lastProgress = -1f;
                while (true)
                {
                    await Task.Delay(300);
                    var (status, downloaded, total) = QueryDownload(downloadId);
                    if (total > 0)
                    {
                        float ratio = Mathf.Clamp01((float)downloaded / total);
                        if (Mathf.Abs(ratio - lastProgress) > 0.005f)
                        {
                            lastProgress = ratio;
                            progress?.Invoke(ratio);
                        }
                    }
                    if (status == DownloadStatusSuccessful)
                    {
                        progress?.Invoke(1f);
                        break;
                    }
                    if (status == DownloadStatusFailed)
                    {
                        return "下载失败（网络中断？），请稍后重试或手动下载：" + info.ReleaseUrl;
                    }
                }
                bool started = StartInstallFromDownloadManager(downloadId);
                return started
                    ? string.Empty
                    : "无法发起安装（请在系统设置允许本应用安装未知应用），文件已在「下载」目录，可手动点开安装。";
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[Update] 下载/安装异常: " + exception.Message);
                return "更新流程异常：" + exception.Message + "；请手动下载：" + info.ReleaseUrl;
            }
#else
            await Task.CompletedTask;
            return "在线更新仅支持 Android 设备；请到 GitHub Releases 手动下载：" + info.ReleaseUrl;
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private static long EnqueueDownload(UpdateInfo info)
        {
            using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var uriClass = new AndroidJavaClass("android.net.Uri"))
            using (var uri = uriClass.CallStatic<AndroidJavaObject>("parse", info.ApkUrl))
            using (var request = new AndroidJavaObject("android.app.DownloadManager$Request", uri))
            using (var dm = activity.Call<AndroidJavaObject>("getSystemService", "download"))
            {
                request.Call<AndroidJavaObject>("setTitle", "伴夏更新 " + info.Version);
                request.Call<AndroidJavaObject>("setDescription", "正在下载新版本安装包");
                request.Call<AndroidJavaObject>("setMimeType", ApkMimeType);
                // VISIBILITY_VISIBLE_NOTIFY_COMPLETED = 2
                request.Call<AndroidJavaObject>("setNotificationVisibility", 2);
                var fileName = "banxia-" + info.Version.TrimStart('v', 'V') + "-" + info.ApkName + ".apk";
                request.Call<AndroidJavaObject>("setDestinationInExternalPublicDir", "Download", fileName);
                request.Call<AndroidJavaObject>("setAllowedOverMetered", true);
                request.Call<AndroidJavaObject>("setAllowedOverRoaming", true);
                return dm.Call<long>("enqueue", request);
            }
        }

        private static (int status, long downloaded, long total) QueryDownload(long downloadId)
        {
            using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var dm = activity.Call<AndroidJavaObject>("getSystemService", "download"))
            using (var query = new AndroidJavaObject("android.app.DownloadManager$Query"))
            {
                query.Call<AndroidJavaObject>("setFilterById", new long[] { downloadId });
                using (var cursor = dm.Call<AndroidJavaObject>("query", query))
                {
                    if (cursor == null || !cursor.Call<bool>("moveToFirst"))
                    {
                        return (DownloadStatusFailed, 0, 0);
                    }
                    int status = cursor.Call<int>("getInt", cursor.Call<int>("getColumnIndex", "status"));
                    long downloaded = cursor.Call<long>("getLong", cursor.Call<int>("getColumnIndex", "bytes_so_far"));
                    long total = cursor.Call<long>("getLong", cursor.Call<int>("getColumnIndex", "total_size"));
                    return (status, downloaded, total);
                }
            }
        }

        private static bool StartInstallFromDownloadManager(long downloadId)
        {
            try
            {
                using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var dm = activity.Call<AndroidJavaObject>("getSystemService", "download"))
                using (var uri = dm.Call<AndroidJavaObject>("getUriForDownloadedFile", downloadId))
                {
                    if (uri == null)
                    {
                        return false;
                    }
                    using (var intent = new AndroidJavaObject("android.content.Intent"))
                    {
                        intent.Call<AndroidJavaObject>("setAction", "android.intent.action.VIEW");
                        intent.Call<AndroidJavaObject>("setDataAndType", uri, ApkMimeType);
                        // FLAG_GRANT_READ_URI_PERMISSION = 1；FLAG_ACTIVITY_NEW_TASK = 0x10000000
                        intent.Call<AndroidJavaObject>("addFlags", 1);
                        intent.Call<AndroidJavaObject>("addFlags", 0x10000000);
                        activity.Call("startActivity", intent);
                        return true;
                    }
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[Update] 安装发起失败: " + exception.Message);
                return false;
            }
        }
#endif

        // ── JSON 轻量提取（避免引第三方库） ──

        private static string ExtractJsonString(string json, string key)
        {
            var marker = "\"" + key + "\"";
            var keyIndex = json.IndexOf(marker, StringComparison.Ordinal);
            if (keyIndex < 0)
            {
                return string.Empty;
            }
            var colonIndex = json.IndexOf(':', keyIndex + marker.Length);
            if (colonIndex < 0)
            {
                return string.Empty;
            }
            var quoteStart = json.IndexOf('"', colonIndex);
            if (quoteStart < 0)
            {
                return string.Empty;
            }
            var quoteEnd = json.IndexOf('"', quoteStart + 1);
            if (quoteEnd < 0)
            {
                return string.Empty;
            }
            return json.Substring(quoteStart + 1, quoteEnd - quoteStart - 1);
        }

        private static string FindApkAsset(string json, bool phoneForm)
        {
            var marker = "\"browser_download_url\"";
            int index = 0;
            string first = null;
            while ((index = json.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
            {
                var colon = json.IndexOf(':', index + marker.Length);
                if (colon < 0)
                {
                    break;
                }
                var quoteStart = json.IndexOf('"', colon);
                var quoteEnd = quoteStart >= 0 ? json.IndexOf('"', quoteStart + 1) : -1;
                if (quoteStart < 0 || quoteEnd < 0)
                {
                    break;
                }
                var url = json.Substring(quoteStart + 1, quoteEnd - quoteStart - 1);
                if (url.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                {
                    if (first == null)
                    {
                        first = url;
                    }
                    bool isPhone = url.IndexOf("Phone", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool isQuest = url.IndexOf("Quest", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (phoneForm && isPhone)
                    {
                        return url;
                    }
                    if (!phoneForm && isQuest)
                    {
                        return url;
                    }
                }
                index = quoteEnd;
            }
            return first;
        }

        private static bool IsNewerVersion(string remote, string local)
        {
            if (string.IsNullOrEmpty(remote))
            {
                return false;
            }
            remote = remote.TrimStart('v', 'V');
            local = local.TrimStart('v', 'V');
            var remoteParts = remote.Split('.');
            var localParts = local.Split('.');
            int count = Mathf.Max(remoteParts.Length, localParts.Length);
            for (int i = 0; i < count; i++)
            {
                int remoteValue = i < remoteParts.Length && int.TryParse(remoteParts[i], out var rv) ? rv : 0;
                int localValue = i < localParts.Length && int.TryParse(localParts[i], out var lv) ? lv : 0;
                if (remoteValue != localValue)
                {
                    return remoteValue > localValue;
                }
            }
            return false;
        }
    }
}
