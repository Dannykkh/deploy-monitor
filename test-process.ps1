$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = "cmd.exe"
$psi.Arguments = "/c deploy.bat auto"
$psi.WorkingDirectory = "D:\git\deploy-monitor\test-deploy\bizmanagement"
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true

$proc = [System.Diagnostics.Process]::Start($psi)
Write-Host "Started PID: $($proc.Id)"

$stdout = $proc.StandardOutput.ReadToEnd()
$stderr = $proc.StandardError.ReadToEnd()
$proc.WaitForExit()

Write-Host "Exit code: $($proc.ExitCode)"
Write-Host "STDOUT: $stdout"
Write-Host "STDERR: $stderr"
