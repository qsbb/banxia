# build_full.ps1 — 手机端完整构建：Flutter AAR + Unity APK（含 JDK 钉死）
#
# 用法（容器侧）：
#   scp tools/build-host/build_full.ps1 lx@192.168.5.55:C:/Users/lx/build_full.ps1
#   tools/remote.sh exec build 'powershell -NoProfile -ExecutionPolicy Bypass -File C:/Users/lx/build_full.ps1'
#
# 关键坑（2026-09-07 实踩）：
# - 必须用 Unity 自带 JDK 11：系统默认 JDK 21 会让 gradle 7.5 报
#   "Unsupported class file major version 65"。
# - flutter build aar 没有 --offline 参数（exit 64），别加。
# - 产物用 tools/remote.sh pull-bin 拉回（Windows scp 拉文件会在 204800 字节截断）。

$ErrorActionPreference = 'Continue'

# ── 1. Flutter AAR（JDK 钉死 Unity 自带 11）─────────────────────────────
Set-Location D:\banxia_build\flutter_ui
$env:JAVA_HOME = 'D:\Tools\2022.3.62f3c1\Editor\Data\PlaybackEngines\AndroidPlayer\OpenJDK'
& D:\dev\flutter\bin\flutter.bat build aar --release 2>&1 | Select-Object -Last 2
Write-Output ("AAR_EXIT=" + $LASTEXITCODE)
if ($LASTEXITCODE -ne 0) { exit 1 }

$aar = Get-ChildItem build\host\outputs\repo\com\lingxi\banxia -Recurse -Filter 'flutter_release-*.aar' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($aar -eq $null) { Write-Output 'NO_MODULE_AAR'; exit 2 }
Copy-Item $aar.FullName 'D:\banxia_build\Assets\Plugins\Android\flutter_ui_release.aar' -Force

# ── 2. Unity APK ─────────────────────────────────────────────────────────
Set-Location 'D:\banxia_build'
$unity = 'D:\Tools\2022.3.62f3c1\Editor\Unity.exe'
$arguments = @('-batchmode','-quit','-projectPath','D:\banxia_build',
    '-executeMethod','QuestMmdPlayer.Editor.QuestMmdPlayerBuild.BuildAndroidPhoneApk',
    '-questDisableDevelopmentBuild','-logFile','D:\banxia_build\build_phone_full.log')
$p = Start-Process -FilePath $unity -ArgumentList $arguments -Wait -PassThru
Write-Output ('UNITY_EXIT=' + $p.ExitCode)
if ($p.ExitCode -ne 0) { exit $p.ExitCode }

Copy-Item D:\banxia_build\Builds\Banxia-Phone.apk C:\Users\lx\Banxia-Phone-full.apk -Force
Get-Item C:\Users\lx\Banxia-Phone-full.apk | Select-Object LastWriteTime,Length
