param(
  [string]$Dll = "D:\RiderProjects\ml-installer-06\mods-06\CSTI-MiniLoader-06\bin\Release\CSTI-MiniLoader.dll",
  [int]$MaxPolls = 14,
  [int]$PollSec = 5
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
& $adb -s $S push $Dll "$mods/" 2>&1 | Out-Null
& $adb -s $S shell am force-stop $pkg
& $adb -s $S shell am start -n "$pkg/$act" 2>&1 | Out-Null

$found = $false
for ($i = 1; $i -le $MaxPolls; $i++) {
  Start-Sleep -Seconds $PollSec
  & $adb -s $S shell "cat $log" > "$out\latest.log" 2>$null
  $txt = Get-Content -Raw "$out\latest.log"
  $procId = (& $adb -s $S shell pidof $pkg | Out-String).Trim()
  if ($txt -match '\[STEP\] 9 done' -or $txt -match '\[自检\] 结束') {
    Write-Host "poll#$i 命中关键日志 (pid=$procId)"
    $found = $true
    break
  }
  if ([string]::IsNullOrWhiteSpace($procId)) { Write-Host "poll#$i pid 为空（可能崩溃）"; break }
  Write-Host "poll#$i 等待中 (pid=$procId, lines=$((Get-Content "$out\latest.log" | Measure-Object -Line).Lines))"
}
if (-not $found) { Write-Host "== 未命中关键日志，仍取当前日志 ==" }
Start-Sleep -Seconds 3
& $adb -s $S shell "cat $log" > "$out\latest.log"
& $adb -s $S logcat -d > "$out\logcat.txt"
$procId = (& $adb -s $S shell pidof $pkg | Out-String).Trim()
Write-Host "PID=[$procId]  lines=$((Get-Content "$out\latest.log" | Measure-Object -Line).Lines)"
Write-Host "== crash markers =="
Select-String -Path "$out\latest.log" -Pattern 'SIGABRT|SIGSEGV|FATAL|mono_class_from_mono_type_internal' | Select-Object -Last 3 | ForEach-Object { $_.Line }
Write-Host "== threw an exception = $((Select-String -Path "$out\latest.log" -Pattern 'threw an exception' | Measure-Object).Count) ; 类型初始化失败 = $((Select-String -Path "$out\latest.log" -Pattern '类型初始化失败|ScriptableObject 类型初始化' | Measure-Object).Count) =="
Write-Host "== 基线 =="
Select-String -Path "$out\latest.log" -Pattern '加载 Windy 成功|增量 ' | ForEach-Object { $_.Line }
