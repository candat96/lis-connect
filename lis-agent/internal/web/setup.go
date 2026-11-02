package web

import (
	"io/fs"
	"net"
	"net/http"
	"strings"

	"lis-agent/internal/auth"
	"lis-agent/internal/store"
)

// SetupInput — trang Cài đặt lần đầu: CSDL, tên Agent, tài khoản quản trị LIS.
type SetupInput struct {
	DB        store.DBConfig `json:"db"`
	Agent     string         `json:"agent"`
	SiteName  string         `json:"siteName"`
	AdminUser string         `json:"adminUser"`
	AdminName string         `json:"adminName"`
	AdminPass string         `json:"adminPass"`
}

// Setup — chế độ chưa cài (chưa có hoặc không vào được CSDL). Chỉ nhận yêu cầu từ chính máy cài Agent.
type Setup struct {
	DefaultAgent string
	Current      store.DBConfig
	LastError    string
	// Save — mở CSDL, ghi file cấu hình; Agent chuyển sang chạy thật.
	Save func(in SetupInput) error
}

func loopback(r *http.Request) bool {
	host, _, err := net.SplitHostPort(r.RemoteAddr)
	if err != nil {
		return false
	}
	ip := net.ParseIP(host)
	return ip != nil && ip.IsLoopback()
}

func (s *Setup) Handler() http.Handler {
	mux := http.NewServeMux()
	sub, _ := fs.Sub(static, "static")
	files := http.FileServer(http.FS(sub))
	mux.Handle("/", files)
	mux.HandleFunc("/api/v1/", func(w http.ResponseWriter, r *http.Request) {
		fail(w, 503, "setup", "LIS Agent chưa cài đặt xong (chưa kết nối CSDL)")
	})
	mux.HandleFunc("/ui/api/", func(w http.ResponseWriter, r *http.Request) {
		route := strings.Trim(strings.TrimPrefix(r.URL.Path, "/ui/api/"), "/")
		if route == "me" {
			cur := s.Current
			cur.Password = ""
			writeJSON(w, 200, map[string]any{"setup": true, "local": loopback(r), "agent": s.DefaultAgent, "db": cur, "error": s.LastError})
			return
		}
		if !loopback(r) {
			fail(w, 403, "local-only", "Cài đặt chỉ làm được trên chính máy cài LIS Agent (mở http://localhost:8090)")
			return
		}
		switch route {
		case "setup/test":
			var in SetupInput
			if err := readJSON(r, &in); err != nil {
				fail(w, 400, "bad-json", err.Error())
				return
			}
			if err := store.TestConnection(in.DB.DSN()); err != nil {
				fail(w, 400, "db", "Không kết nối được CSDL: "+err.Error())
				return
			}
			writeJSON(w, 200, map[string]bool{"ok": true})
		case "setup":
			var in SetupInput
			if err := readJSON(r, &in); err != nil {
				fail(w, 400, "bad-json", err.Error())
				return
			}
			in.Agent = strings.TrimSpace(in.Agent)
			if in.Agent == "" {
				fail(w, 400, "invalid", "Nhập tên Agent (vd XN-SINH-HOA)")
				return
			}
			if in.DB.Host == "" || in.DB.Name == "" || in.DB.User == "" {
				fail(w, 400, "invalid", "Nhập đủ máy chủ, tên CSDL, tài khoản CSDL")
				return
			}
			if err := s.Save(in); err != nil {
				fail(w, 400, "setup", err.Error())
				return
			}
			writeJSON(w, 200, map[string]bool{"ok": true})
		default:
			fail(w, 503, "setup", "LIS Agent chưa cài đặt xong")
		}
	})
	return mux
}

// EnsureAdmin — CSDL chưa có quản trị LIS nào thì bắt buộc tạo khi cài.
func EnsureAdmin(st *store.Store, in SetupInput) error {
	if st.CountLocalAdmins() > 0 && in.AdminUser == "" {
		return nil
	}
	if in.AdminUser == "" {
		return errString("CSDL mới: nhập tài khoản quản trị LIS (dùng khi cấu hình và khi HIS mất kết nối)")
	}
	if len(in.AdminPass) < 8 {
		return errString("Mật khẩu quản trị tối thiểu 8 ký tự")
	}
	hash, err := auth.HashPassword(in.AdminPass)
	if err != nil {
		return err
	}
	name := strings.TrimSpace(in.AdminName)
	if name == "" {
		name = "Quản trị LIS"
	}
	return st.SaveLocalUser(store.User{Username: strings.TrimSpace(in.AdminUser), FullName: name, Role: auth.RoleAdmin, Active: true}, hash)
}

type errString string

func (e errString) Error() string { return string(e) }
