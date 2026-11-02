//go:build windows

package main

import (
	"encoding/base64"
	"unsafe"

	"golang.org/x/sys/windows"
)

// Mật khẩu CSDL trong agent.json mã hóa bằng DPAPI theo máy (CRYPTPROTECT_LOCAL_MACHINE): chép file sang máy khác
// không giải được; Windows Service (LocalSystem) và chạy tay trên cùng máy đều đọc được.
const secretPrefix = "dpapi:"

func blob(b []byte) *windows.DataBlob {
	if len(b) == 0 {
		return &windows.DataBlob{}
	}
	return &windows.DataBlob{Size: uint32(len(b)), Data: &b[0]}
}

func sealSecret(plain string) string {
	if plain == "" {
		return ""
	}
	var out windows.DataBlob
	if err := windows.CryptProtectData(blob([]byte(plain)), nil, nil, 0, nil, windows.CRYPTPROTECT_LOCAL_MACHINE|windows.CRYPTPROTECT_UI_FORBIDDEN, &out); err != nil {
		return plain
	}
	defer windows.LocalFree(windows.Handle(unsafe.Pointer(out.Data)))
	return secretPrefix + base64.StdEncoding.EncodeToString(unsafe.Slice(out.Data, out.Size))
}

func openSecret(v string) string {
	if len(v) < len(secretPrefix) || v[:len(secretPrefix)] != secretPrefix {
		return v
	}
	raw, err := base64.StdEncoding.DecodeString(v[len(secretPrefix):])
	if err != nil {
		return ""
	}
	var out windows.DataBlob
	if err := windows.CryptUnprotectData(blob(raw), nil, nil, 0, nil, windows.CRYPTPROTECT_UI_FORBIDDEN, &out); err != nil {
		return ""
	}
	defer windows.LocalFree(windows.Handle(unsafe.Pointer(out.Data)))
	return string(unsafe.Slice(out.Data, out.Size))
}
