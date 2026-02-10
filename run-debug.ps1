# 콘솔 출력을 캡처하도록 실행
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = "D:\git\deploy-monitor\DeployMonitor\bin\Debug\net8.0-windows\win-x64\DeployMonitor.exe"
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true

$proc = [System.Diagnostics.Process]::Start($psi)
Write-Host "PID: $($proc.Id)"

# 비동기로 출력 읽기
$outTask = $proc.StandardOutput.ReadToEndAsync()
$errTask = $proc.StandardError.ReadToEndAsync()

# 최대 20초 대기
$proc.WaitForExit(20000)

Write-Host "Exit code: $($proc.ExitCode)"
Write-Host "STDOUT:"
Write-Host $outTask.Result
Write-Host "STDERR:"
Write-Host $errTask.Result
