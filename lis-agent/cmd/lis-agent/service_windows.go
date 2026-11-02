//go:build windows

package main

import (
	"context"
	"errors"
	"fmt"
	"os"
	"time"

	"golang.org/x/sys/windows/svc"
	"golang.org/x/sys/windows/svc/mgr"
)

func isService() bool {
	ok, err := svc.IsWindowsService()
	return err == nil && ok
}

type handler struct{ listen, data string }

func (h handler) Execute(_ []string, req <-chan svc.ChangeRequest, status chan<- svc.Status) (bool, uint32) {
	status <- svc.Status{State: svc.StartPending}
	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan error, 1)
	go func() { done <- run(ctx, h.listen, h.data) }()
	status <- svc.Status{State: svc.Running, Accepts: svc.AcceptStop | svc.AcceptShutdown}
	for {
		select {
		case c := <-req:
			switch c.Cmd {
			case svc.Interrogate:
				status <- c.CurrentStatus
			case svc.Stop, svc.Shutdown:
				status <- svc.Status{State: svc.StopPending}
				cancel()
				<-done
				return false, 0
			}
		case err := <-done:
			cancel()
			if err != nil {
				return true, 1
			}
			return false, 0
		}
	}
}

func runService(listen, data string) { _ = svc.Run(serviceName, handler{listen, data}) }

func serviceCommand(cmd, listen, data string) error {
	m, err := mgr.Connect()
	if err != nil {
		return fmt.Errorf("cần chạy bằng quyền Administrator: %w", err)
	}
	defer m.Disconnect()
	switch cmd {
	case "install":
		exe, err := os.Executable()
		if err != nil {
			return err
		}
		args := []string{"-data", data}
		if listen != "" {
			args = append(args, "-listen", listen)
		}
		s, err := m.CreateService(serviceName, exe, mgr.Config{
			DisplayName: "LIS Agent", Description: "Kết nối máy xét nghiệm với HIS/CIS",
			StartType: mgr.StartAutomatic,
		}, args...)
		if err != nil {
			return err
		}
		defer s.Close()
		// Tự khởi động lại khi lỗi: sau 5 giây, 3 lần liên tiếp
		_ = s.SetRecoveryActions([]mgr.RecoveryAction{{Type: mgr.ServiceRestart, Delay: 5 * time.Second}, {Type: mgr.ServiceRestart, Delay: 5 * time.Second}, {Type: mgr.ServiceRestart, Delay: 5 * time.Second}}, 86400)
		if err := s.Start(); err != nil {
			return err
		}
		fmt.Println("Đã cài và chạy dịch vụ LisAgent. Mở http://localhost:8090 để cấu hình.")
		return nil
	case "uninstall":
		s, err := m.OpenService(serviceName)
		if err != nil {
			return err
		}
		defer s.Close()
		_, _ = s.Control(svc.Stop)
		return s.Delete()
	case "start":
		s, err := m.OpenService(serviceName)
		if err != nil {
			return err
		}
		defer s.Close()
		return s.Start()
	case "stop":
		s, err := m.OpenService(serviceName)
		if err != nil {
			return err
		}
		defer s.Close()
		_, err = s.Control(svc.Stop)
		return err
	}
	return errors.New("lệnh không hợp lệ: " + cmd + " (install | uninstall | start | stop)")
}
