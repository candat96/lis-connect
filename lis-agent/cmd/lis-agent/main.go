// LIS Agent — phần mềm trung gian cài trên máy Windows cạnh máy xét nghiệm: nhận chỉ định từ HIS/CIS (hoặc nhập
// tay), trả lời máy khi máy hỏi chỉ định, nhận kết quả từ máy (ASTM/HL7) và trả về HIS/CIS.
//
//	lis-agent                 chạy (cửa sổ dòng lệnh), giao diện http://localhost:8090
//	lis-agent install         cài thành Windows Service (tự chạy khi bật máy, không cần đăng nhập)
//	lis-agent uninstall|start|stop
//	lis-agent -listen :8090 -data C:\LisAgent\data
package main

import (
	"context"
	"flag"
	"fmt"
	"log"
	"net/http"
	"os"
	"path/filepath"
	"time"

	"lis-agent/internal/device"
	"lis-agent/internal/his"
	"lis-agent/internal/store"
	"lis-agent/internal/web"
)

var version = "1.0.0"

const serviceName = "LisAgent"

func main() {
	listen := flag.String("listen", "", "địa chỉ HTTP (mặc định :8090 hoặc theo cấu hình)")
	data := flag.String("data", defaultDataDir(), "thư mục dữ liệu")
	flag.Parse()

	if cmd := flag.Arg(0); cmd != "" {
		if err := serviceCommand(cmd, *listen, *data); err != nil {
			fmt.Fprintln(os.Stderr, "Lỗi:", err)
			os.Exit(1)
		}
		return
	}
	if isService() {
		runService(*listen, *data)
		return
	}
	ctx, stop := signalContext()
	defer stop()
	if err := run(ctx, *listen, *data); err != nil {
		log.Fatal(err)
	}
}

func defaultDataDir() string {
	if exe, err := os.Executable(); err == nil {
		return filepath.Join(filepath.Dir(exe), "data")
	}
	return "data"
}

// run — mở dữ liệu, khởi động kết nối máy, bộ gửi HIS và HTTP; dừng khi ctx hủy.
func run(ctx context.Context, listen, dataDir string) error {
	if err := os.MkdirAll(dataDir, 0o700); err != nil {
		return err
	}
	st, err := store.Open(filepath.Join(dataDir, "lis-agent.db"))
	if err != nil {
		return fmt.Errorf("không mở được dữ liệu (Agent khác đang chạy?): %w", err)
	}
	defer st.Close()
	cfg, err := st.Config()
	if err != nil {
		cfg = store.Config{Listen: ":8090"}
		_ = st.SaveConfig(cfg)
	}
	if listen != "" {
		cfg.Listen = listen
	}
	if cfg.Listen == "" {
		cfg.Listen = ":8090"
	}

	sender := his.NewSender(st)
	devices := device.NewManager(st)
	devices.OnResult = sender.Wake
	devices.Reload(cfg.Devices)
	defer devices.Stop()
	go sender.Run(ctx)

	srv := &http.Server{Addr: cfg.Listen, Handler: web.New(st, devices, sender, version).Handler(), ReadHeaderTimeout: 10 * time.Second}
	errCh := make(chan error, 1)
	go func() { errCh <- srv.ListenAndServe() }()
	st.Log("info", "agent", "LIS Agent "+version+" chạy tại "+cfg.Listen, "")
	log.Printf("LIS Agent %s — giao diện http://localhost%s", version, cfg.Listen)
	select {
	case <-ctx.Done():
		shut, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		_ = srv.Shutdown(shut)
		st.Log("info", "agent", "LIS Agent dừng", "")
		return nil
	case err := <-errCh:
		return fmt.Errorf("không mở được cổng %s: %w", cfg.Listen, err)
	}
}
