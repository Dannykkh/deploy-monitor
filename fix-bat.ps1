$content = "@echo off`r`necho deploy started mode=%1`r`necho Build complete.`r`necho Deploy finished.`r`nexit /b 0`r`n"
[System.IO.File]::WriteAllText("D:\git\deploy-monitor\test-deploy\bizmanagement\deploy.bat", $content, [System.Text.Encoding]::ASCII)
[System.IO.File]::WriteAllText("D:\git\deploy-monitor\test-deploy\website\deploy.bat", $content, [System.Text.Encoding]::ASCII)
Write-Host "deploy.bat files fixed"
