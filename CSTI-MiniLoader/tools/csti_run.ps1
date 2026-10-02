param(
  [string]$Dll = "D:\RiderProjects\ml-installer-06\mods-06\CSTI-MiniLoader-06\bin\Release\CSTI-MiniLoader.dll",
  [int]$Wait = 60,
  [int]$ExtraWait = 0
)
$ErrorActionPreference = 'Continue'
$adb = 'D:\devtools\HBuilderX\plugins\launcher-tools\tools\adbs\adb.exe'
$S = 'NZNBUC456PJN7HR4'
$pkg = 'com.winterspringgames.survivaljourney'
$act = 'com.x.shell.JPolicyActivity'
$mods = "/sdcard/MelonLoader/$pkg/Mods"
$log  = "/sdcard/MelonLoader/$pkg/MelonLoader/Latest.log"
$out  = 'D:\RiderProjects\ml-installer-06\mods-06\CSTI-MiniLoader-06\tools\_run'
New-Item -ItemType Directory -Force -Path $out | Out-Null
Write-Host "== push $(Split-Path $Dll -Leaf) =="
& $adb -s $S push $Dll "$mods/"
& $adb -s $S shell am force-stop $pkg
& $adb -s $S logcat -c
Write-Host "== start =="
& $adb -s $S shell am start -n "$pkg/$act"
Start-Sleep -Seconds $Wait
$procId = (& $adb -s $S shell pidof $pkg | Out-String).Trim()
Write-Host "PID=[$procId]"
if ($ExtraWait -gt 0) { Start-Sleep -Seconds $ExtraWait; $procId = (& $adb -s $S shell pidof $pkg | Out-String).Trim(); Write-Host "PID(after extra)=[$procId]" }
& $adb -s $S shell "cat $log" > "$out\latest.log"
& $adb -s $S logcat -d > "$out\logcat.txt"
Write-Host "== log lines: $((Get-Content "$out\latest.log" | Measure-Object -Line).Lines) =="
Write-Host "== crash markers =="
Select-String -Path "$out\latest.log" -Pattern 'SIGABRT|SIGSEGV|FATAL|mono_class_from_mono_type_internal' | Select-Object -Last 5 | ForEach-Object { $_.Line }
Write-Host "== threw an exception count =="
(Select-String -Path "$out\latest.log" -Pattern 'threw an exception' | Measure-Object).Count
Write-Host "== 类型初始化失败 count =="
(Select-String -Path "$out\latest.log" -Pattern '类型初始化失败|ScriptableObject 类型初始化' | Measure-Object).Count
