//go:build !windows

package main

import "errors"

func isService() bool { return false }

func runService(listen, data string) {}

func serviceCommand(cmd, listen, data string) error {
	return errors.New("lệnh " + cmd + " chỉ dùng trên Windows")
}
