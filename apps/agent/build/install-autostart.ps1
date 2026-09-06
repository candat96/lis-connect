param([Parameter(Mandatory = $true)][string]$ExePath)

# Chạy khi có người đăng nhập, tự khởi động lại nếu tiến trình chết.
# Đây là thứ giữ cho Agent sống sau khi máy khởi động lại; thiếu nó thì máy xét
# nghiệm gửi kết quả vào hư không.
$action  = New-ScheduledTaskAction -Execute $ExePath
$trigger = New-ScheduledTaskTrigger -AtLogOn
$settings = New-ScheduledTaskSettingsSet `
  -AllowStartIfOnBatteries `
  -DontStopIfGoingOnBatteries `
  -RestartCount 999 `
  -RestartInterval (New-TimeSpan -Minutes 1) `
  -ExecutionTimeLimit (New-TimeSpan -Seconds 0)

Register-ScheduledTask -TaskName 'LIS Connect Agent' `
  -Action $action -Trigger $trigger -Settings $settings -Force
