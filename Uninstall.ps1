# Removes the Memory Monitor widget for the current user.
Get-AppxPackage -Name 'MemoryMonitorWidget' | Remove-AppxPackage
Write-Host 'Memory Monitor widget removed.'
