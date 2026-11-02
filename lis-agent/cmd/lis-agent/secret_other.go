//go:build !windows

package main

// Ngoài Windows (máy thử): giữ nguyên, file agent.json chỉ chủ sở hữu đọc được (0600).
func sealSecret(plain string) string { return plain }
func openSecret(v string) string     { return v }
