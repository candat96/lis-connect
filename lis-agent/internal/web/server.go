// Package web: HTTP của Agent — API cho HIS/CIS (/api/v1, khóa X-Api-Key) và giao diện quản trị tại chỗ
// (/ui, đăng nhập bằng mật khẩu quản trị; trang tĩnh nhúng trong file chạy).
package web

import (
	"context"
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"embed"
	"encoding/hex"
	"encoding/json"
	"errors"
	"io/fs"
	"net/http"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"

	"lis-agent/internal/device"
	"lis-agent/internal/his"
	"lis-agent/internal/store"
)

//go:embed static
var static embed.FS

type Server struct {
	st       *store.Store
	devices  *device.Manager
	sender   *his.Sender
	version  string
	mu       sync.Mutex
	sessions map[string]time.Time
}

func New(st *store.Store, devices *device.Manager, sender *his.Sender, version string) *Server {
	return &Server{st: st, devices: devices, sender: sender, version: version, sessions: map[string]time.Time{}}
}

func (s *Server) Handler() http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("/api/v1/", s.api)
	mux.HandleFunc("/ui/api/", s.ui)
	sub, _ := fs.Sub(static, "static")
	files := http.FileServer(http.FS(sub))
	mux.Handle("/", http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Cache-Control", "no-cache")
		files.ServeHTTP(w, r)
	}))
	return mux
}

// ------------------------------------------------------------------ tiện ích

type apiError struct {
	Code    string `json:"code"`
	Message string `json:"message"`
}

func writeJSON(w http.ResponseWriter, status int, v any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(v)
}

func fail(w http.ResponseWriter, status int, code, msg string) {
	writeJSON(w, status, apiError{Code: code, Message: msg})
}

func readJSON(r *http.Request, v any) error {
	dec := json.NewDecoder(http.MaxBytesReader(nil, r.Body, 2<<20))
	if err := dec.Decode(v); err != nil {
		return errors.New("dữ liệu gửi lên không đúng JSON: " + err.Error())
	}
	return nil
}

func tail(path, prefix string) []string {
	p := strings.Trim(strings.TrimPrefix(path, prefix), "/")
	if p == "" {
		return nil
	}
	return strings.Split(p, "/")
}

// validateOrder — kiểm tra chỉ định (dùng chung cho HIS gửi và nhập tay).
func validateOrder(o *store.Order) string {
	o.Sid = store.NormSid(o.Sid)
	o.Patient.Name = strings.TrimSpace(o.Patient.Name)
	if o.Sid == "" {
		return "Thiếu SID (mã vạch ống mẫu)"
	}
	if len(o.Sid) > 40 || strings.ContainsAny(o.Sid, "|\\^&\r\n") {
		return "SID không hợp lệ"
	}
	if o.Patient.Name == "" {
		return "Thiếu họ tên người bệnh"
	}
	if o.Patient.Gender != "" && o.Patient.Gender != "male" && o.Patient.Gender != "female" {
		return "Giới tính là male hoặc female"
	}
	if o.Patient.Dob != "" {
		if _, err := time.Parse("2006-01-02", o.Patient.Dob); err != nil {
			return "Ngày sinh dạng yyyy-MM-dd"
		}
	}
	var tests []store.Test
	seen := map[string]bool{}
	for _, t := range o.Tests {
		t.Code = strings.TrimSpace(t.Code)
		if t.Code == "" || seen[strings.ToUpper(t.Code)] {
			continue
		}
		seen[strings.ToUpper(t.Code)] = true
		tests = append(tests, t)
	}
	if len(tests) == 0 {
		return "Chưa có xét nghiệm nào"
	}
	o.Tests = tests
	return ""
}

// ------------------------------------------------------------------ API cho HIS/CIS

func (s *Server) api(w http.ResponseWriter, r *http.Request) {
	cfg, _ := s.st.Config()
	parts := tail(r.URL.Path, "/api/v1/")
	if len(parts) == 1 && parts[0] == "health" {
		writeJSON(w, 200, map[string]any{"status": "ok", "version": s.version, "time": time.Now()})
		return
	}
	key := r.Header.Get("X-Api-Key")
	if cfg.His.APIKey == "" || subtle.ConstantTimeCompare([]byte(key), []byte(cfg.His.APIKey)) != 1 {
		fail(w, 401, "unauthorized", "Sai hoặc thiếu khóa API (header X-Api-Key)")
		return
	}
	switch {
	case len(parts) == 1 && parts[0] == "orders" && r.Method == http.MethodPost:
		var o store.Order
		if err := readJSON(r, &o); err != nil {
			fail(w, 400, "bad-json", err.Error())
			return
		}
		if msg := validateOrder(&o); msg != "" {
			fail(w, 400, "invalid", msg)
			return
		}
		o.Source = "his"
		o.Status = ""
		saved, err := s.st.UpsertOrder(o)
		if err != nil {
			fail(w, 500, "store", err.Error())
			return
		}
		s.st.Log("info", "his", "HIS gửi chỉ định SID "+saved.Sid+" ("+itoa(len(saved.Tests))+" xét nghiệm)", "")
		writeJSON(w, 200, saved)
	case len(parts) == 2 && parts[0] == "orders" && r.Method == http.MethodGet:
		o, err := s.st.GetOrder(parts[1])
		if err != nil {
			fail(w, 404, "not-found", "Không có chỉ định SID "+parts[1])
			return
		}
		writeJSON(w, 200, o)
	case len(parts) == 3 && parts[0] == "orders" && parts[2] == "cancel" && r.Method == http.MethodPost:
		if err := s.st.SetOrderStatus(parts[1], store.OrderCancelled); err != nil {
			fail(w, 404, "not-found", "Không có chỉ định SID "+parts[1])
			return
		}
		s.st.Log("info", "his", "HIS hủy chỉ định SID "+store.NormSid(parts[1]), "")
		writeJSON(w, 200, map[string]string{"status": "huy"})
	case len(parts) == 1 && parts[0] == "results" && r.Method == http.MethodGet:
		f := store.ResultFilter{Sid: r.URL.Query().Get("sid"), Limit: 500}
		if v := r.URL.Query().Get("since"); v != "" {
			t, err := time.Parse(time.RFC3339, v)
			if err != nil {
				fail(w, 400, "invalid", "since dạng RFC3339, vd 2026-11-02T08:00:00+07:00")
				return
			}
			f.Since = t
		}
		list, _ := s.st.ListResults(f)
		out := make([]his.ResultPayload, 0, len(list))
		for _, x := range list {
			if x.Matched {
				out = append(out, his.Payload(s.st, x))
			}
		}
		writeJSON(w, 200, out)
	case len(parts) == 2 && parts[0] == "results" && parts[1] == "ack" && r.Method == http.MethodPost:
		var in struct {
			IDs []string `json:"ids"`
		}
		if err := readJSON(r, &in); err != nil {
			fail(w, 400, "bad-json", err.Error())
			return
		}
		for _, id := range in.IDs {
			_ = s.st.MarkSent(id)
		}
		writeJSON(w, 200, map[string]int{"acked": len(in.IDs)})
	default:
		fail(w, 404, "not-found", "Không có API "+r.Method+" "+r.URL.Path)
	}
}

// ------------------------------------------------------------------ giao diện

const cookieName = "lis_agent_session"

func hashPassword(pw string) string {
	var salt [8]byte
	_, _ = rand.Read(salt[:])
	sh := hex.EncodeToString(salt[:])
	sum := sha256.Sum256([]byte(sh + pw))
	return sh + ":" + hex.EncodeToString(sum[:])
}

func checkPassword(stored, pw string) bool {
	i := strings.IndexByte(stored, ':')
	if i < 0 {
		return false
	}
	sum := sha256.Sum256([]byte(stored[:i] + pw))
	return subtle.ConstantTimeCompare([]byte(stored[i+1:]), []byte(hex.EncodeToString(sum[:]))) == 1
}

func (s *Server) loggedIn(r *http.Request) bool {
	c, err := r.Cookie(cookieName)
	if err != nil {
		return false
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	exp, ok := s.sessions[c.Value]
	if !ok || time.Now().After(exp) {
		delete(s.sessions, c.Value)
		return false
	}
	s.sessions[c.Value] = time.Now().Add(12 * time.Hour)
	return true
}

func (s *Server) newSession(w http.ResponseWriter) {
	var b [24]byte
	_, _ = rand.Read(b[:])
	tok := hex.EncodeToString(b[:])
	s.mu.Lock()
	s.sessions[tok] = time.Now().Add(12 * time.Hour)
	s.mu.Unlock()
	http.SetCookie(w, &http.Cookie{Name: cookieName, Value: tok, Path: "/", HttpOnly: true, SameSite: http.SameSiteStrictMode})
}

func (s *Server) ui(w http.ResponseWriter, r *http.Request) {
	parts := tail(r.URL.Path, "/ui/api/")
	cfg, _ := s.st.Config()
	route := strings.Join(parts, "/")
	// Không cần đăng nhập: trạng thái, đăng nhập, đặt mật khẩu lần đầu
	switch route {
	case "me":
		writeJSON(w, 200, map[string]any{"loggedIn": s.loggedIn(r), "needSetup": cfg.AdminPassword == "", "siteName": cfg.SiteName, "version": s.version})
		return
	case "setup":
		if cfg.AdminPassword != "" {
			fail(w, 409, "done", "Đã đặt mật khẩu")
			return
		}
		var in struct{ Password, SiteName string }
		if err := readJSON(r, &in); err != nil || len(in.Password) < 6 {
			fail(w, 400, "invalid", "Mật khẩu tối thiểu 6 ký tự")
			return
		}
		cfg.AdminPassword = hashPassword(in.Password)
		cfg.SiteName = strings.TrimSpace(in.SiteName)
		if err := s.st.SaveConfig(cfg); err != nil {
			fail(w, 500, "store", err.Error())
			return
		}
		s.newSession(w)
		writeJSON(w, 200, map[string]bool{"ok": true})
		return
	case "login":
		var in struct{ Password string }
		_ = readJSON(r, &in)
		if cfg.AdminPassword == "" || !checkPassword(cfg.AdminPassword, in.Password) {
			time.Sleep(500 * time.Millisecond)
			fail(w, 401, "wrong", "Sai mật khẩu")
			return
		}
		s.newSession(w)
		writeJSON(w, 200, map[string]bool{"ok": true})
		return
	case "logout":
		if c, err := r.Cookie(cookieName); err == nil {
			s.mu.Lock()
			delete(s.sessions, c.Value)
			s.mu.Unlock()
		}
		writeJSON(w, 200, map[string]bool{"ok": true})
		return
	}
	if !s.loggedIn(r) {
		fail(w, 401, "login", "Cần đăng nhập")
		return
	}
	q := r.URL.Query()
	switch {
	case route == "stats":
		day := startOfDay(time.Now())
		orders, _ := s.st.ListOrders(store.OrderFilter{From: day})
		counts := map[string]int{}
		for _, o := range orders {
			counts[o.Status]++
		}
		unmatched, _ := s.st.ListResults(store.ResultFilter{Unmatch: true, Since: day.AddDate(0, 0, -7)})
		writeJSON(w, 200, map[string]any{"today": len(orders), "byStatus": counts, "unmatched": len(unmatched), "outbox": s.st.OutboxCount(), "devices": s.devices.Statuses()})
	case route == "orders" && r.Method == http.MethodGet:
		f := store.OrderFilter{Status: q.Get("status"), Q: q.Get("q"), Limit: 1000}
		if d := q.Get("date"); d != "" {
			if t, err := time.ParseInLocation("2006-01-02", d, time.Local); err == nil {
				f.From, f.To = t, t.AddDate(0, 0, 1)
			}
		}
		list, _ := s.st.ListOrders(f)
		if list == nil {
			list = []store.Order{}
		}
		writeJSON(w, 200, list)
	case route == "orders" && r.Method == http.MethodPost:
		var o store.Order
		if err := readJSON(r, &o); err != nil {
			fail(w, 400, "bad-json", err.Error())
			return
		}
		if msg := validateOrder(&o); msg != "" {
			fail(w, 400, "invalid", msg)
			return
		}
		if old, err := s.st.GetOrder(o.Sid); err == nil && old.Source == "his" && old.Status != store.OrderCancelled {
			fail(w, 409, "exists", "SID "+o.Sid+" đã có chỉ định từ HIS — sửa ở HIS")
			return
		}
		o.Source = "manual"
		saved, err := s.st.UpsertOrder(o)
		if err != nil {
			fail(w, 500, "store", err.Error())
			return
		}
		s.st.Log("info", "ui", "Nhập tay chỉ định SID "+saved.Sid, "")
		writeJSON(w, 200, saved)
	case len(parts) == 3 && parts[0] == "orders" && parts[2] == "cancel" && r.Method == http.MethodPost:
		if err := s.st.SetOrderStatus(parts[1], store.OrderCancelled); err != nil {
			fail(w, 404, "not-found", "Không có chỉ định")
			return
		}
		s.st.Log("info", "ui", "Hủy chỉ định SID "+store.NormSid(parts[1]), "")
		writeJSON(w, 200, map[string]bool{"ok": true})
	case len(parts) == 3 && parts[0] == "orders" && parts[2] == "restore" && r.Method == http.MethodPost:
		_ = s.st.SetOrderStatus(parts[1], store.OrderWaiting)
		writeJSON(w, 200, map[string]bool{"ok": true})
	case route == "results" && r.Method == http.MethodGet:
		f := store.ResultFilter{Sid: q.Get("sid"), Unmatch: q.Get("unmatched") == "1", Limit: 300}
		list, _ := s.st.ListResults(f)
		if list == nil {
			list = []store.Result{}
		}
		writeJSON(w, 200, list)
	case len(parts) == 3 && parts[0] == "results" && parts[2] == "assign" && r.Method == http.MethodPost:
		var in struct{ Sid string }
		if err := readJSON(r, &in); err != nil {
			fail(w, 400, "bad-json", err.Error())
			return
		}
		res, err := s.st.AssignResult(parts[1], in.Sid)
		if err != nil {
			fail(w, 400, "invalid", err.Error())
			return
		}
		s.st.Log("info", "ui", "Gán kết quả cho SID "+res.Sid, "")
		s.sender.Wake()
		writeJSON(w, 200, res)
	case len(parts) == 3 && parts[0] == "results" && parts[2] == "resend" && r.Method == http.MethodPost:
		if err := s.st.Requeue(parts[1]); err != nil {
			fail(w, 404, "not-found", "Không có kết quả")
			return
		}
		s.sender.Wake()
		writeJSON(w, 200, map[string]bool{"ok": true})
	case route == "tests":
		writeJSON(w, 200, s.knownTests(cfg))
	case route == "devices" && r.Method == http.MethodGet:
		writeJSON(w, 200, map[string]any{"devices": nonNil(cfg.Devices), "status": s.devices.Statuses(), "serialPorts": device.SerialPorts()})
	case route == "devices" && r.Method == http.MethodPut:
		var in []store.Device
		if err := readJSON(r, &in); err != nil {
			fail(w, 400, "bad-json", err.Error())
			return
		}
		ids := map[string]bool{}
		for i := range in {
			d := &in[i]
			d.Name = strings.TrimSpace(d.Name)
			if d.ID == "" {
				d.ID = store.NewID()[20:]
			}
			if ids[d.ID] {
				fail(w, 400, "invalid", "Trùng mã máy")
				return
			}
			ids[d.ID] = true
			if d.Name == "" {
				fail(w, 400, "invalid", "Máy thứ "+itoa(i+1)+" chưa có tên")
				return
			}
			if d.Protocol != "astm" && d.Protocol != "hl7" {
				fail(w, 400, "invalid", d.Name+": chọn giao thức ASTM hoặc HL7")
				return
			}
			if d.Mappings == nil {
				d.Mappings = []store.Mapping{}
			}
		}
		cfg.Devices = in
		if err := s.st.SaveConfig(cfg); err != nil {
			fail(w, 500, "store", err.Error())
			return
		}
		s.devices.Reload(in)
		s.st.Log("info", "ui", "Lưu cấu hình "+itoa(len(in))+" máy xét nghiệm", "")
		writeJSON(w, 200, map[string]any{"devices": in})
	case route == "his" && r.Method == http.MethodGet:
		writeJSON(w, 200, map[string]any{"his": cfg.His, "listen": cfg.Listen, "siteName": cfg.SiteName})
	case route == "his" && r.Method == http.MethodPut:
		var in struct {
			His      store.HisConfig `json:"his"`
			SiteName string          `json:"siteName"`
		}
		if err := readJSON(r, &in); err != nil {
			fail(w, 400, "bad-json", err.Error())
			return
		}
		if in.His.WebhookURL != "" && !strings.HasPrefix(in.His.WebhookURL, "http://") && !strings.HasPrefix(in.His.WebhookURL, "https://") {
			fail(w, 400, "invalid", "Địa chỉ nhận kết quả phải bắt đầu bằng http:// hoặc https://")
			return
		}
		if len(strings.TrimSpace(in.His.APIKey)) < 16 {
			fail(w, 400, "invalid", "Khóa API tối thiểu 16 ký tự (bấm Tạo khóa)")
			return
		}
		cfg.His = in.His
		cfg.SiteName = strings.TrimSpace(in.SiteName)
		if err := s.st.SaveConfig(cfg); err != nil {
			fail(w, 500, "store", err.Error())
			return
		}
		s.st.Log("info", "ui", "Lưu cấu hình kết nối HIS/CIS", "")
		s.sender.Wake()
		writeJSON(w, 200, map[string]bool{"ok": true})
	case route == "his/test" && r.Method == http.MethodPost:
		var in store.HisConfig
		if err := readJSON(r, &in); err != nil {
			fail(w, 400, "bad-json", err.Error())
			return
		}
		ctx, cancel := context.WithTimeout(r.Context(), 15*time.Second)
		defer cancel()
		if err := s.sender.Ping(ctx, in); err != nil {
			fail(w, 502, "unreachable", err.Error())
			return
		}
		writeJSON(w, 200, map[string]bool{"ok": true})
	case route == "password" && r.Method == http.MethodPost:
		var in struct{ Old, New string }
		if err := readJSON(r, &in); err != nil {
			fail(w, 400, "bad-json", err.Error())
			return
		}
		if !checkPassword(cfg.AdminPassword, in.Old) {
			fail(w, 400, "wrong", "Mật khẩu cũ không đúng")
			return
		}
		if len(in.New) < 6 {
			fail(w, 400, "invalid", "Mật khẩu mới tối thiểu 6 ký tự")
			return
		}
		cfg.AdminPassword = hashPassword(in.New)
		_ = s.st.SaveConfig(cfg)
		writeJSON(w, 200, map[string]bool{"ok": true})
	case route == "logs":
		writeJSON(w, 200, nonNilLogs(s.st.Logs(500, q.Get("source"))))
	default:
		fail(w, 404, "not-found", "Không có "+r.Method+" "+r.URL.Path)
	}
}

type knownTest struct {
	Code string `json:"code"`
	Name string `json:"name"`
}

// knownTests — gợi ý mã xét nghiệm khi nhập tay: từ ánh xạ các máy và các chỉ định gần đây.
func (s *Server) knownTests(cfg store.Config) []knownTest {
	m := map[string]string{}
	for _, d := range cfg.Devices {
		for _, mp := range d.Mappings {
			if mp.TestCode != "" && m[mp.TestCode] == "" {
				m[mp.TestCode] = mp.TestName
			}
		}
	}
	recent, _ := s.st.ListOrders(store.OrderFilter{From: time.Now().AddDate(0, 0, -30), Limit: 2000})
	for _, o := range recent {
		for _, t := range o.Tests {
			if m[t.Code] == "" {
				m[t.Code] = t.Name
			}
		}
	}
	out := make([]knownTest, 0, len(m))
	for c, n := range m {
		out = append(out, knownTest{c, n})
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Code < out[j].Code })
	return out
}

func nonNil(d []store.Device) []store.Device {
	if d == nil {
		return []store.Device{}
	}
	return d
}

func nonNilLogs(l []store.LogEntry) []store.LogEntry {
	if l == nil {
		return []store.LogEntry{}
	}
	return l
}

func startOfDay(t time.Time) time.Time {
	y, m, d := t.Date()
	return time.Date(y, m, d, 0, 0, 0, 0, t.Location())
}

func itoa(i int) string { return strconv.Itoa(i) }
