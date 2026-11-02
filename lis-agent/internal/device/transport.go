// Package device: kết nối từng máy xét nghiệm (COM, TCP server, TCP client), chạy giao thức ASTM/HL7,
// tự nối lại khi rớt, báo trạng thái cho giao diện.
package device

import (
	"context"
	"errors"
	"fmt"
	"io"
	"net"
	"time"

	"go.bug.st/serial"
	"lis-agent/internal/store"
)

// open — mở kết nối theo cấu hình; tcp-server chờ máy kết nối vào (một kết nối tại một thời điểm).
func open(ctx context.Context, d store.Device) (io.ReadWriteCloser, error) {
	switch d.Transport {
	case "serial":
		if d.Port == "" {
			return nil, errors.New("chưa chọn cổng COM")
		}
		mode := &serial.Mode{BaudRate: or(d.Baud, 9600), DataBits: or(d.DataBits, 8)}
		switch d.Parity {
		case "even":
			mode.Parity = serial.EvenParity
		case "odd":
			mode.Parity = serial.OddParity
		default:
			mode.Parity = serial.NoParity
		}
		if d.StopBits == 2 {
			mode.StopBits = serial.TwoStopBits
		} else {
			mode.StopBits = serial.OneStopBit
		}
		p, err := serial.Open(d.Port, mode)
		if err != nil {
			return nil, err
		}
		_ = p.SetReadTimeout(500 * time.Millisecond)
		return p, nil
	case "tcp-client":
		if d.Host == "" || d.TCPPort == 0 {
			return nil, errors.New("chưa nhập IP/cổng của máy")
		}
		var dl net.Dialer
		c, err := dl.DialContext(ctx, "tcp", net.JoinHostPort(d.Host, fmt.Sprint(d.TCPPort)))
		if err != nil {
			return nil, err
		}
		return &tcpConn{c}, nil
	case "tcp-server":
		if d.TCPPort == 0 {
			return nil, errors.New("chưa nhập cổng lắng nghe")
		}
		ln, err := net.Listen("tcp", net.JoinHostPort(d.Host, fmt.Sprint(d.TCPPort)))
		if err != nil {
			return nil, err
		}
		defer ln.Close()
		done := make(chan struct{})
		defer close(done)
		go func() {
			select {
			case <-ctx.Done():
				ln.Close()
			case <-done:
			}
		}()
		c, err := ln.Accept()
		if err != nil {
			return nil, err
		}
		return &tcpConn{c}, nil
	}
	return nil, fmt.Errorf("kiểu kết nối không hỗ trợ: %s", d.Transport)
}

// tcpConn — đọc có hạn chờ để vòng đọc kiểm tra được lệnh dừng.
type tcpConn struct{ net.Conn }

func (c *tcpConn) Read(p []byte) (int, error) {
	_ = c.Conn.SetReadDeadline(time.Now().Add(500 * time.Millisecond))
	n, err := c.Conn.Read(p)
	var ne net.Error
	if errors.As(err, &ne) && ne.Timeout() {
		return n, nil
	}
	return n, err
}

func or(v, def int) int {
	if v == 0 {
		return def
	}
	return v
}

// SerialPorts — danh sách cổng COM trên máy (giao diện chọn cổng).
func SerialPorts() []string {
	ports, err := serial.GetPortsList()
	if err != nil {
		return nil
	}
	return ports
}
