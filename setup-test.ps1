# 테스트용 bare repo 와 deploy 폴더 생성
$testRepoRoot = "D:\git\deploy-monitor\test-repos"
$testDeployRoot = "D:\git\deploy-monitor\test-deploy"

# 1. bare repo 시뮬레이션 (bizmanagement.git)
$bareRepo = "$testRepoRoot\bizmanagement.git"
New-Item -ItemType Directory -Path "$bareRepo\refs\heads" -Force | Out-Null
Set-Content -Path "$bareRepo\HEAD" -Value "ref: refs/heads/master"
Set-Content -Path "$bareRepo\refs\heads\master" -Value "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2"
Write-Host "Created: $bareRepo"

# 2. bare repo 시뮬레이션 (website.git)
$bareRepo2 = "$testRepoRoot\website.git"
New-Item -ItemType Directory -Path "$bareRepo2\refs\heads" -Force | Out-Null
Set-Content -Path "$bareRepo2\HEAD" -Value "ref: refs/heads/master"
Set-Content -Path "$bareRepo2\refs\heads\master" -Value "d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5"
Write-Host "Created: $bareRepo2"

# 3. bare repo 시뮬레이션 (internal-tool.git) - deploy.bat 없음
$bareRepo3 = "$testRepoRoot\internal-tool.git"
New-Item -ItemType Directory -Path "$bareRepo3\refs\heads" -Force | Out-Null
Set-Content -Path "$bareRepo3\HEAD" -Value "ref: refs/heads/master"
Set-Content -Path "$bareRepo3\refs\heads\master" -Value "f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1"
Write-Host "Created: $bareRepo3"

# 4. deploy 폴더 + deploy.bat
New-Item -ItemType Directory -Path "$testDeployRoot\bizmanagement" -Force | Out-Null
Set-Content -Path "$testDeployRoot\bizmanagement\deploy.bat" -Value @"
@echo off
echo [%date% %time%] bizmanagement deploy started (mode: %1)
echo Building...
timeout /t 2 /nobreak >nul
echo Build complete.
echo Deploy finished successfully.
exit /b 0
"@
Write-Host "Created: $testDeployRoot\bizmanagement\deploy.bat"

New-Item -ItemType Directory -Path "$testDeployRoot\website" -Force | Out-Null
Set-Content -Path "$testDeployRoot\website\deploy.bat" -Value @"
@echo off
echo [%date% %time%] website deploy started (mode: %1)
echo Building...
timeout /t 1 /nobreak >nul
echo Build complete.
echo Deploy finished successfully.
exit /b 0
"@
Write-Host "Created: $testDeployRoot\website\deploy.bat"

# internal-tool은 deploy 폴더 없음 (미설정 상태)

Write-Host ""
Write-Host "=== Test setup complete ==="
Write-Host "Repo folder:   $testRepoRoot"
Write-Host "Deploy folder:  $testDeployRoot"
