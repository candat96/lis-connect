; Đăng ký Agent chạy khi có người đăng nhập Windows (Task Scheduler, trigger
; "At log on") và gỡ tác vụ đó khi gỡ cài đặt. Đây là phần bắt buộc để Agent
; sống sót qua một lần khởi động lại máy — không có nó, máy khởi động lại và
; dừng ở màn hình đăng nhập thì Agent không chạy, và máy xét nghiệm gửi kết
; quả vào hư không.
!macro customInstall
  ExecWait 'powershell -ExecutionPolicy Bypass -File "$INSTDIR\resources\install-autostart.ps1" -ExePath "$INSTDIR\LIS Connect Agent.exe"'
!macroend

!macro customUnInstall
  ExecWait 'schtasks /Delete /TN "LIS Connect Agent" /F'
!macroend
