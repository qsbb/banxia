param(
    [ValidateSet('Flutter', 'Tests', 'Phone', 'Quest')]
    [string]$Phase = 'Tests',
    [string]$ProjectPath = (Split-Path $PSScriptRoot -Parent),
    [string]$Unity = 'D:\Tools\2022.3.62f3c1\Editor\Unity.exe',
    [string]$Flutter = 'D:\dev\flutter\bin\flutter.bat'
)
$ErrorActionPreference = 'Stop'
Set-Location $ProjectPath
$output = Join-Path $ProjectPath 'Builds/DebugValidation'
New-Item -ItemType Directory -Force $output | Out-Null

if ($Phase -eq 'Flutter') {
    Push-Location (Join-Path $ProjectPath 'flutter_ui')
    try {
        & $Flutter pub get
        if ($LASTEXITCODE -ne 0) { throw 'Flutter pub get failed' }
        # Unity's bundled SDK exposes Build Tools 34.0.0, while AGP 7.3
        # otherwise falls back to its historical 30.0.3 default. Patch the
        # generated Flutter module after pub get so the offline AAR check uses
        # a toolchain that is actually installed on the build host.
        $flutterGradle = Join-Path $ProjectPath 'flutter_ui/.android/Flutter/build.gradle'
        if (!(Test-Path $flutterGradle)) { throw 'Generated Flutter Gradle file is missing' }
        $gradleText = Get-Content -Raw $flutterGradle
        $gradleText = $gradleText.TrimStart([char]0xFEFF)
        if ($gradleText -notmatch 'buildToolsVersion\s*=') {
            $replacement = "    compileSdk = flutter.compileSdkVersion`r`n    buildToolsVersion = '34.0.0'"
            $gradleText = $gradleText.Replace('    compileSdk = flutter.compileSdkVersion', $replacement)
        }
        # Windows PowerShell's -Encoding UTF8 emits a BOM; Groovy treats that
        # generated BOM as a literal '?' on this host. Write explicitly without
        # a BOM so the offline Gradle parser sees a normal source file.
        $utf8NoBom = New-Object System.Text.UTF8Encoding -ArgumentList $false
        [System.IO.File]::WriteAllText($flutterGradle, $gradleText, $utf8NoBom)
        & $Flutter analyze
        if ($LASTEXITCODE -ne 0) { throw 'Flutter analyze failed' }
        & $Flutter test
        if ($LASTEXITCODE -ne 0) { throw 'Flutter tests failed' }
        $gradle = Get-ChildItem "$env:USERPROFILE/.gradle/wrapper/dists/gradle-8.8-bin" -Recurse -Filter gradle.bat | Select-Object -First 1
        if (!$gradle) { throw 'Gradle 8.8 is unavailable' }
        Push-Location '.android'
        try {
            & $gradle.FullName ':flutter:assembleRelease' '--offline'
            if ($LASTEXITCODE -ne 0) { throw 'Flutter AAR build failed' }
        } finally { Pop-Location }
        $aar = Join-Path $ProjectPath 'flutter_ui/.android/Flutter/build/outputs/aar/flutter-release.aar'
        $plugin = Join-Path $ProjectPath 'Assets/Plugins/Android/flutter_ui_release.aar'
        Copy-Item -Force $aar $plugin
        Get-FileHash $plugin -Algorithm SHA256 | Format-List
    } finally { Pop-Location }
    exit 0
}

$log = Join-Path $output ($Phase + '.log')
$arguments = @('-batchmode', '-projectPath', $ProjectPath, '-logFile', $log)
if ($Phase -eq 'Tests') {
    $results = Join-Path $output 'EditMode.xml'
    if (Test-Path $results) { Remove-Item $results }
    # The Test Runner exits after completion; -quit would stop it before tests run.
    $filter = 'QuestMmdPlayer.Tests.QuestDebugModeTests;QuestMmdPlayer.Tests.QuestQualitySettingsTests;QuestMmdPlayer.Tests.CallFramingSolverTests;QuestMmdPlayer.Tests.FlutterMessageProtocolTests;QuestMmdPlayer.Tests.BackendPairingTests;QuestMmdPlayer.Tests.QuestFileImportServiceTests;QuestMmdPlayer.Tests.AstrBotProtocolTests;QuestMmdPlayer.Tests.QuestMmdPlayerBuildTests;QuestMmdPlayer.Tests.QuestPassthroughConfigurationTests;QuestMmdPlayer.Tests.QuestInteractionUxTests'
    $arguments += @('-runTests', '-testPlatform', 'EditMode', '-testFilter', $filter, '-testResults', $results)
} else {
    $method = if ($Phase -eq 'Phone') { 'BuildAndroidPhoneApk' } else { 'BuildAndroidApk' }
    $apk = Join-Path $ProjectPath $(if ($Phase -eq 'Phone') { 'Builds/Banxia-Phone.apk' } else { 'Builds/Banxia.apk' })
    $arguments += @('-quit', '-executeMethod', "QuestMmdPlayer.Editor.QuestMmdPlayerBuild.$method", '-questDisableDevelopmentBuild')
}
$started = Get-Date
$process = Start-Process -FilePath $Unity -ArgumentList $arguments -Wait -PassThru
if ($process.ExitCode -ne 0) {
    Get-Content $log -Tail 65
    throw "Unity $Phase exited with $($process.ExitCode)"
}
if ($Phase -eq 'Tests') {
    if (!(Test-Path $results)) { throw 'Unity produced no test results' }
    [xml]$xml = Get-Content -Raw $results
    $run = $xml.'test-run'
    Write-Output "EDITMODE total=$($run.total) passed=$($run.passed) failed=$($run.failed) result=$($run.result)"
    if ([int]$run.total -eq 0 -or [int]$run.failed -ne 0 -or $run.result -ne 'Passed') {
        $xml.SelectNodes('//test-case[@result="Failed"]') | ForEach-Object { Write-Output $_.OuterXml }
        throw 'Unity focused tests did not pass'
    }
    foreach ($suite in @('QuestDebugModeTests', 'QuestQualitySettingsTests', 'CallFramingSolverTests', 'FlutterMessageProtocolTests')) {
        if (!$xml.SelectSingleNode("//test-suite[@name='$suite']")) { throw "Missing test suite: $suite" }
    }
} else {
    $artifact = Get-Item $apk
    if ($artifact.LastWriteTime -lt $started -or $artifact.Length -le 0) { throw 'APK is stale or empty' }
    $artifact | Select-Object FullName, Length, LastWriteTime | Format-List
    Get-FileHash $apk -Algorithm SHA256 | Format-List
}
