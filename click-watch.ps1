Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

$proc = Get-Process DeployMonitor -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) {
    Write-Host "DeployMonitor not running"
    exit 1
}

# UI Automation으로 "감시 시작" 버튼 찾기
$root = [System.Windows.Automation.AutomationElement]::RootElement
$condition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
    $proc.Id
)
$window = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)

if (-not $window) {
    Write-Host "Window not found"
    exit 1
}

# 버튼 찾기
$btnCondition = New-Object System.Windows.Automation.AndCondition(
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button
    )),
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        "● 감시 시작"
    ))
)

$btn = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $btnCondition)
if ($btn) {
    $invokePattern = $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $invokePattern.Invoke()
    Write-Host "Clicked: 감시 시작"
} else {
    Write-Host "Button not found, trying all buttons..."
    $allBtns = $window.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button
        ))
    )
    foreach ($b in $allBtns) {
        Write-Host "  Found button: '$($b.Current.Name)'"
    }
}
