Get-CimInstance Win32_Process | Where-Object { $_.Name -like '*Deploy*' } | Select-Object ProcessId, Name, ExecutablePath | Format-Table -AutoSize
