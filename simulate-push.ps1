# git push 시뮬레이션: refs/heads/master 파일의 커밋 해시 변경
$refsFile = "D:\git\deploy-monitor\test-repos\bizmanagement.git\refs\heads\master"
$newHash = "b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3"

Write-Host "Simulating git push for bizmanagement..."
Write-Host "Old hash: $(Get-Content $refsFile)"
Set-Content -Path $refsFile -Value $newHash
Write-Host "New hash: $newHash"
Write-Host "Done! Check the DeployMonitor window for detection."
