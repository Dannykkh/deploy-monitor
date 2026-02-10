Get-ChildItem 'C:\' -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -like '*onobo*' -or $_.Name -like '*Bonobo*' } | Select-Object FullName
Get-ChildItem 'D:\' -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -like '*deploy*' } | Select-Object FullName
