// LIS Agent — phần mềm trung gian cài trên máy Windows cạnh máy xét nghiệm: nhận chỉ định từ HIS/CIS (hoặc nhập
// tay), trả lời máy khi máy hỏi chỉ định, nhận kết quả từ máy (ASTM/HL7) và trả về HIS/CIS.
//
//	lis-agent                 chạy (cửa sổ dòng lệnh), giao diện http://localhost:8090
//	lis-agent install         cài thành Windows Service (tự chạy khi bật máy, không cần đăng nhập)
//	lis-agent uninstall|start|stop
//	lis-agent -listen :8090 -data C:\LisAgent\data
//
// Lần đầu chạy (chưa có data\agent.json) Agent mở trang Cài đặt tại http://localhost:8090: khai báo CSDL PostgreSQL
// (cloud hoặc máy chủ tại viện), tên Agent, tài khoản quản trị LIS. Đổi CSDL: lis-agent setup rồi khởi động lại.
package main

import (
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"log"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"sync/atomic"
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

	if flag.Arg(0) == "setup" {
		if err := resetSetup(*data); err != nil {
			fmt.Fprintln(os.Stderr, "Lỗi:", err)
			os.Exit(1)
		}
		fmt.Println("Đã xóa khai báo CSDL — khởi động lại Agent rồi mở http://localhost:8090 để cài đặt lại")
		return
	}
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

// localConfig — cấu hình riêng của máy cài Agent (data\agent.json): tên Agent, cổng giao diện, địa chỉ CSDL.
// Mọi cài đặt khác (HIS, đăng nhập, máy xét nghiệm, người dùng) nằm trong CSDL dùng chung.
type localConfig struct {
	Agent  string         `json:"agent"`
	Listen string         `json:"listen"`
	DB     store.DBConfig `json:"db"`
}

func configPath(dataDir string) string { return filepath.Join(dataDir, "agent.json") }

func loadLocal(dataDir string) (localConfig, bool) {
	var c localConfig
	b, err := os.ReadFile(configPath(dataDir))
	if err != nil || json.Unmarshal(b, &c) != nil || c.DB.Host == "" {
		return c, false
	}
	c.DB.Password = openSecret(c.DB.Password)
	return c, true
}

func saveLocal(dataDir string, c localConfig) error {
	c.DB.Password = sealSecret(c.DB.Password)
	b, _ := json.MarshalIndent(c, "", "  ")
	tmp := configPath(dataDir) + ".tmp"
	if err := os.WriteFile(tmp, b, 0o600); err != nil {
		return err
	}
	return os.Rename(tmp, configPath(dataDir))
}

func resetSetup(dataDir string) error {
	c, _ := loadLocal(dataDir)
	c.DB = store.DBConfig{Host: "", Port: c.DB.Port, Name: c.DB.Name, User: c.DB.User}
	return saveLocal(dataDir, c)
}

func hostname() string {
	h, _ := os.Hostname()
	if i := strings.IndexByte(h, '.'); i > 0 {
		h = h[:i]
	}
	if h == "" {
		return "LIS-AGENT"
	}
	return strings.ToUpper(h)
}

// switcher — một cổng HTTP, đổi trang Cài đặt ↔ Agent chạy thật không cần khởi động lại.
type switcher struct{ h atomic.Value }

func (s *switcher) set(h http.Handler) { s.h.Store(&h) }
func (s *switcher) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	(*s.h.Load().(*http.Handler)).ServeHTTP(w, r)
}

// run — mở cổng giao diện; chưa cài/không vào được CSDL thì hiện trang Cài đặt (và thử kết nối lại định kỳ),
// vào được CSDL thì chạy kết nối máy, bộ gửi HIS. Dừng khi ctx hủy.
func run(ctx context.Context, listen, dataDir string) error {
	if err := os.MkdirAll(dataDir, 0o700); err != nil {
		return err
	}
	lc, ok := loadLocal(dataDir)
	if listen == "" {
		listen = lc.Listen
	}
	if listen == "" {
		listen = ":8090"
	}
	if lc.Agent == "" {
		lc.Agent = hostname()
	}

	sw := &switcher{}
	ready := make(chan *store.Store, 1)
	var setupMu sync.Mutex
	setup := &web.Setup{DefaultAgent: lc.Agent, Current: lc.DB}
	setup.Save = func(in web.SetupInput) error {
		setupMu.Lock()
		defer setupMu.Unlock()
		st, err := store.Open(in.DB.DSN(), in.Agent)
		if err != nil {
			return fmt.Errorf("không kết nối được CSDL: %w", err)
		}
		if err := web.EnsureAdmin(st, in); err != nil {
			st.Close()
			return err
		}
		if in.SiteName != "" {
			_ = st.SaveSite(in.SiteName)
		}
		nc := localConfig{Agent: in.Agent, Listen: lc.Listen, DB: in.DB}
		if err := saveLocal(dataDir, nc); err != nil {
			st.Close()
			return fmt.Errorf("không ghi được %s: %w", configPath(dataDir), err)
		}
		select {
		case ready <- st:
		default:
			st.Close()
		}
		return nil
	}
	sw.set(setup.Handler())

	srv := &http.Server{Addr: listen, Handler: sw, ReadHeaderTimeout: 10 * time.Second}
	errCh := make(chan error, 1)
	go func() { errCh <- srv.ListenAndServe() }()
	defer func() {
		shut, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		_ = srv.Shutdown(shut)
	}()

	// Đã cài: thử vào CSDL ngay, lỗi thì thử lại mỗi 15 giây (CSDL cloud có thể chưa sẵn khi bật máy).
	if ok {
		go func() {
			for {
				st, err := store.Open(lc.DB.DSN(), lc.Agent)
				if err == nil {
					select {
					case ready <- st:
					default:
						st.Close()
					}
					return
				}
				setupMu.Lock()
				setup.LastError = err.Error()
				setupMu.Unlock()
				log.Printf("Chưa vào được CSDL %s: %v — thử lại sau 15 giây", lc.DB.Host, err)
				select {
				case <-ctx.Done():
					return
				case <-time.After(15 * time.Second):
				}
			}
		}()
		log.Printf("LIS Agent %s — đang kết nối CSDL %s, giao diện http://localhost%s", version, lc.DB.Host, listen)
	} else {
		log.Printf("LIS Agent %s — chưa cài đặt, mở http://localhost%s trên máy này để khai báo CSDL", version, listen)
	}

	var st *store.Store
	select {
	case <-ctx.Done():
		return nil
	case err := <-errCh:
		return fmt.Errorf("không mở được cổng %s: %w", listen, err)
	case st = <-ready:
	}
	defer st.Close()

	cfg, err := st.Config()
	if err != nil {
		return fmt.Errorf("đọc cài đặt: %w", err)
	}
	spool := store.NewSpool(filepath.Join(dataDir, "spool.jsonl"))
	sender := his.NewSender(st)
	devices := device.NewManager(st)
	devices.Spool = spool
	devices.OnResult = sender.Wake
	devices.Reload(cfg.Devices)
	defer devices.Stop()
	go sender.Run(ctx)
	go housekeeping(ctx, st, spool, sender)

	sw.set(web.New(st, devices, sender, spool, version).Handler())
	st.Log("info", "agent", "LIS Agent "+version+" ("+st.Agent()+") chạy tại "+listen, "")
	log.Printf("LIS Agent %s (%s) — giao diện http://localhost%s", version, st.Agent(), listen)
	select {
	case <-ctx.Done():
		st.Log("info", "agent", "LIS Agent dừng", "")
		return nil
	case err := <-errCh:
		return fmt.Errorf("không mở được cổng %s: %w", listen, err)
	}
}

// housekeeping — đẩy kết quả ghi tạm khi CSDL có lại, dọn nhật ký cũ.
func housekeeping(ctx context.Context, st *store.Store, spool *store.Spool, sender *his.Sender) {
	tick := time.NewTicker(20 * time.Second)
	defer tick.Stop()
	var prune time.Time
	for {
		if spool.Count() > 0 && st.Ping() == nil {
			if n := spool.Flush(st); n > 0 {
				st.Log("info", "agent", fmt.Sprintf("CSDL kết nối lại: đã đẩy %d kết quả ghi tạm", n), "")
				sender.Wake()
			}
		}
		if time.Since(prune) > 6*time.Hour {
			st.PruneLogs()
			prune = time.Now()
		}
		select {
		case <-ctx.Done():
			return
		case <-tick.C:
		}
	}
}
