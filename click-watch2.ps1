Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$proc = Get-Process DeployMonitor -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Host "Not running"; exit 1 }

$root = [System.Windows.Automation.AutomationElement]::RootElement
$condition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
$window = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)

# 모든 버튼 가져오기
$allBtns = $window.FindAll(
    [System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)))

# 3번째 버튼이 "감시 시작" (찾기2개 다음)
$idx = 0
foreach ($b in $allBtns) {
    $name = $b.Current.Name
    Write-Host "Button[$idx]: '$name'"
    # "감시" 포함하는 버튼 찾기
    if ($name -match '감시' -or $name -match '\u25CF') {
        try {
            $invokePattern = $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
            $invokePattern.Invoke()
            Write-Host "  -> Clicked!"
            break
        } catch {
            Write-Host "  -> Click failed: $_"
        }
    }
    $idx++
}

# 못 찾았으면 인덱스 2 (0:찾기, 1:찾기, 2:감시시작) 클릭
if ($idx -eq $allBtns.Count) {
    Write-Host "Falling back to button index 2..."
    $target = $allBtns[2]
    Write-Host "Clicking: '$($target.Current.Name)'"
    $invokePattern = $target.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $invokePattern.Invoke()
    Write-Host "Clicked!"
}
