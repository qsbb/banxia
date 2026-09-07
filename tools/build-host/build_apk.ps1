# build_apk.ps1 — 仅 Unity APK 增量构建（未改 Dart/Java 时用，约 4-10 分钟）
#
# 用法（容器侧）：
#   scp tools/build-host/build_apk.ps1 lx@192.168.5.55:C:/Users/lx/build_apk.ps1
#   tools/remote.sh exec build 'powershell -NoProfile -ExecutionPolicy Bypass -File C:/Users/lx/build_apk.ps1'
#
# 改了 flutter_ui/** 或 androidlib 的 Java 才需要 build_full.ps1。
# 构建失败先看日志尾部：D:\banxia_build\build_phone_apk.log
# 常见失败：脚本编译错误（容器侧改 .cs 后忘补 BOM 之外的语法问题）、
# 版本常量日期没更新（QuestMmdPlayerBuild.AndroidVersionName）。

Set-Location 'D:\banxia_build'
$unity = 'D:\Tools\2022.3.62f3c1\Editor\Unity.exe'
$arguments = @('-batchmode','-quit','-projectPath','D:\banxia_build',
    '-executeMethod','QuestMmdPlayer.Editor.QuestMmdPlayerBuild.BuildAndroidPhoneApk',
    '-questDisableDevelopmentBuild','-logFile','D:\banxia_build\build_phone_apk.log')
$p = Start-Process -FilePath $unity -ArgumentList $arguments -Wait -PassThru
Write-Output ('UNITY_EXIT=' + $p.ExitCode)
if ($p.ExitCode -ne 0) { exit $p.ExitCode }

Copy-Item D:\banxia_build\Builds\Banxia-Phone.apk C:\Users\lx\Banxia-Phone-apk.apk -Force
Get-Item C:\Users\lx\Banxia-Phone-apk.apk | Select-Object LastWriteTime,Length
