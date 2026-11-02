package web

import (
	"context"
	"errors"
	"net/http"
	"sort"
	"strconv"
	"strings"
	"time"

	"lis-agent/internal/auth"
	"lis-agent/internal/device"
	"lis-agent/internal/store"
)

const cookieName = "lis_agent_session"

func (s *Server) currentUser(r *http.Request) (store.User, bool) {
	c, err := r.Cookie(cookieName)
	if err != nil || c.Value == "" {
		return store.User{}, false
	}
	u, err := s.st.SessionUser(c.Value)
	return u, err == nil
}

func setCookie(w http.ResponseWriter, tok string, maxAge int) {
	http.SetCookie(w, &http.Cookie{Name: cookieName, Value: tok, Path: "/", HttpOnly: true, SameSite: http.SameSiteStrictMode, MaxAge: maxAge})
}

// dayRange — khoảng ngày [from, to] (yyyy-MM-dd, theo giờ máy) → [from 00:00, to+1 00:00).
func dayRange(from, to string) (time.Time, time.Time) {
	var f, t time.Time
	if v, err := time.ParseInLocation("2006-01-02", from, time.Local); err == nil {
		f = v
	}
	if v, err := time.ParseInLocation("2006-01-02", to, time.Local); err == nil {
		t = v.AddDate(0, 0, 1)
	}
	return f, t
}

func (s *Server) ui(w http.ResponseWriter, r *http.Request) {
	parts := tail(r.URL.Path, "/ui/api/")
	route := strings.Join(parts, "/")
	cfg, _ := s.st.Config()
	switch route {
	case "me":
		u, ok := s.currentUser(r)
		out := map[string]any{"loggedIn": ok, "siteName": cfg.SiteName, "version": s.version, "agent": s.st.Agent(),
			"hisLogin": auth.Defaults(cfg.Auth).Mode == "his" && cfg.Auth.URL != "", "hisName": cfg.His.Name}
		if ok {
			out["user"] = u
		}
		writeJSON(w, 200, out)
		return
	case "login":
		var in struct{ Username, Password string }
		_ = readJSON(r, &in)
		ctx, cancel := context.WithTimeout(r.Context(), 12*time.Second)
		defer cancel()
		u, err := auth.Login(ctx, s.st, in.Username, in.Password)
		if err != nil {
			time.Sleep(400 * time.Millisecond)
			s.st.Log("warn", "dang-nhap", "Đăng nhập không thành công: "+strings.TrimSpace(in.Username)+" — "+err.Error(), "")
			fail(w, 401, "wrong", err.Error())
			return
		}
		tok, err := s.st.NewSession(u.Username, 12*time.Hour)
		if err != nil {
			fail(w, 500, "store", err.Error())
			return
		}
		setCookie(w, tok, 0)
		s.st.Log("info", "dang-nhap", u.Username+" đăng nhập ("+map[string]string{"his": "tài khoản HIS", "local": "tài khoản LIS"}[u.Source]+")", "")
		writeJSON(w, 200, u)
		return
	case "logout":
		if c, err := r.Cookie(cookieName); err == nil {
			s.st.DeleteSession(c.Value)
		}
		setCookie(w, "", -1)
		writeJSON(w, 200, map[string]bool{"ok": true})
		return
	}
	me, ok := s.currentUser(r)
	if !ok {
		fail(w, 401, "login", "Cần đăng nhập")
		return
	}
	admin := me.Role == auth.RoleAdmin
	needAdmin := func() bool {
		if !admin {
			fail(w, 403, "forbidden", "Chỉ quản trị LIS được thao tác này")
		}
		return admin
	}
	q := r.URL.Query()
	switch {
	case route == "stats":
		from, to := dayRange(q.Get("from"), q.Get("to"))
		orders, _ := s.st.ListOrders(store.OrderFilter{From: from, To: to})
		counts := map[string]int{}
		for _, o := range orders {
			counts[o.Status]++
		}
		unmatched, _ := s.st.ListResults(store.ResultFilter{Unmatch: true, From: time.Now().AddDate(0, 0, -7)})
		dbErr := ""
		if err := s.st.Ping(); err != nil {
			dbErr = err.Error()
		}
		spooled := 0
		if s.spool != nil {
			spooled = s.spool.Count()
		}
		writeJSON(w, 200, map[string]any{"total": len(orders), "byStatus": counts, "unmatched": len(unmatched), "outbox": s.st.OutboxCount(),
			"devices": s.devices.Statuses(), "dbError": dbErr, "spooled": spooled})

	// ---------------------------------------------------------------- chỉ định
	case route == "orders" && r.Method == http.MethodGet:
		from, to := dayRange(q.Get("from"), q.Get("to"))
		list, err := s.st.ListOrders(store.OrderFilter{From: from, To: to, Status: q.Get("status"), Q: q.Get("q"), Limit: 3000})
		if err != nil {
			fail(w, 500, "store", err.Error())
			return
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
		o.CreatedBy = me.Username
		o.Status = ""
		saved, err := s.st.UpsertOrder(o)
		if err != nil {
			fail(w, 500, "store", err.Error())
			return
		}
		s.st.Log("info", "ui", me.Username+" nhập tay chỉ định SID "+saved.Sid, "")
		writeJSON(w, 200, saved)
	case len(parts) == 3 && parts[0] == "orders" && parts[2] == "cancel" && r.Method == http.MethodPost:
		if err := s.st.SetOrderStatus(parts[1], store.OrderCancelled); err != nil {
			fail(w, 404, "not-found", "Không có chỉ định")
			return
		}
		s.st.Log("info", "ui", me.Username+" hủy chỉ định SID "+store.NormSid(parts[1]), "")
		writeJSON(w, 200, map[string]bool{"ok": true})
	case len(parts) == 3 && parts[0] == "orders" && parts[2] == "restore" && r.Method == http.MethodPost:
		_ = s.st.SetOrderStatus(parts[1], store.OrderWaiting)
		s.st.Log("info", "ui", me.Username+" khôi phục chỉ định SID "+store.NormSid(parts[1]), "")
		writeJSON(w, 200, map[string]bool{"ok": true})

	// ---------------------------------------------------------------- kết quả
	case route == "results" && r.Method == http.MethodGet:
		f := store.ResultFilter{Sid: q.Get("sid"), Unmatch: q.Get("unmatched") == "1", Limit: 1000}
		if f.Sid == "" {
			f.From, f.To = dayRange(q.Get("from"), q.Get("to"))
		}
		list, err := s.st.ListResults(f)
		if err != nil {
			fail(w, 500, "store", err.Error())
			return
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
		s.st.Log("info", "ui", me.Username+" gán kết quả cho SID "+res.Sid, "")
		s.sender.Wake()
		writeJSON(w, 200, res)
	case len(parts) == 3 && parts[0] == "results" && parts[2] == "resend" && r.Method == http.MethodPost:
		if err := s.st.Requeue(parts[1]); err != nil {
			fail(w, 404, "not-found", "Không có kết quả đã khớp chỉ định")
			return
		}
		s.sender.Wake()
		writeJSON(w, 200, map[string]bool{"ok": true})
	case route == "tests":
		writeJSON(w, 200, s.knownTests(cfg))

	// ---------------------------------------------------------------- máy xét nghiệm (quản trị)
	case route == "devices" && r.Method == http.MethodGet:
		writeJSON(w, 200, map[string]any{"devices": cfg.Devices, "status": s.devices.Statuses(), "serialPorts": device.SerialPorts(), "agent": s.st.Agent(), "canEdit": admin})
	case route == "devices" && r.Method == http.MethodPut:
		if !needAdmin() {
			return
		}
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
				d.ID = strings.ToLower(store.NewID()[20:])
			}
			if ids[d.ID] {
				fail(w, 400, "invalid", "Trùng mã máy")
				return
			}
			ids[d.ID] = true
			if d.Name == "" {
				fail(w, 400, "invalid", "Máy thứ "+strconv.Itoa(i+1)+" chưa có tên")
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
		if err := s.st.SaveDevices(in); err != nil {
			fail(w, 500, "store", err.Error())
			return
		}
		s.devices.Reload(in)
		s.st.Log("info", "ui", me.Username+" lưu cấu hình "+strconv.Itoa(len(in))+" máy xét nghiệm", "")
		writeJSON(w, 200, map[string]any{"devices": in})

	// ---------------------------------------------------------------- kết nối HIS/CIS (quản trị)
	case route == "his" && r.Method == http.MethodGet:
		if !needAdmin() {
			return
		}
		writeJSON(w, 200, map[string]any{"his": cfg.His, "auth": auth.Defaults(cfg.Auth), "siteName": cfg.SiteName})
	case route == "his" && r.Method == http.MethodPut:
		if !needAdmin() {
			return
		}
		var in struct {
			His      store.HisConfig  `json:"his"`
			Auth     store.AuthConfig `json:"auth"`
			SiteName string           `json:"siteName"`
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
		if in.Auth.Mode == "his" && !strings.HasPrefix(in.Auth.URL, "http") {
			fail(w, 400, "invalid", "Nhập địa chỉ API đăng nhập của HIS")
			return
		}
		if err := s.st.SaveHis(in.His); err == nil {
			_ = s.st.SaveAuth(in.Auth)
			_ = s.st.SaveSite(strings.TrimSpace(in.SiteName))
		} else {
			fail(w, 500, "store", err.Error())
			return
		}
		s.st.Log("info", "ui", me.Username+" lưu cấu hình kết nối HIS/CIS", "")
		s.sender.Wake()
		writeJSON(w, 200, map[string]bool{"ok": true})
	case route == "his/test" && r.Method == http.MethodPost:
		if !needAdmin() {
			return
		}
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
	case route == "auth/test" && r.Method == http.MethodPost:
		if !needAdmin() {
			return
		}
		var in struct {
			Auth     store.AuthConfig `json:"auth"`
			Username string           `json:"username"`
			Password string           `json:"password"`
		}
		if err := readJSON(r, &in); err != nil {
			fail(w, 400, "bad-json", err.Error())
			return
		}
		ctx, cancel := context.WithTimeout(r.Context(), 15*time.Second)
		defer cancel()
		res, err := auth.CallHis(ctx, in.Auth, in.Username, in.Password)
		if err != nil {
			fail(w, 400, "his", err.Error())
			return
		}
		writeJSON(w, 200, res)

	// ---------------------------------------------------------------- người dùng (quản trị)
	case route == "users" && r.Method == http.MethodGet:
		if !needAdmin() {
			return
		}
		list, err := s.st.Users()
		if err != nil {
			fail(w, 500, "store", err.Error())
			return
		}
		writeJSON(w, 200, list)
	case route == "users" && r.Method == http.MethodPost:
		if !needAdmin() {
			return
		}
		var in struct {
			Username, FullName, Role, Password string
			Active                             *bool
		}
		if err := readJSON(r, &in); err != nil {
			fail(w, 400, "bad-json", err.Error())
			return
		}
		in.Username = strings.TrimSpace(in.Username)
		if in.Username == "" || strings.ContainsAny(in.Username, " \t") {
			fail(w, 400, "invalid", "Tên đăng nhập không có khoảng trắng")
			return
		}
		if in.Role != auth.RoleAdmin && in.Role != auth.RoleTech {
			fail(w, 400, "invalid", "Vai trò là quan-tri hoặc ktv")
			return
		}
		old, _, err := s.st.GetUser(in.Username)
		if err == nil && old.Source == "his" {
			fail(w, 409, "his", "Đây là tài khoản HIS — chỉ đổi được vai trò")
			return
		}
		if errors.Is(err, store.ErrNotFound) && len(in.Password) < 8 {
			fail(w, 400, "invalid", "Mật khẩu tối thiểu 8 ký tự")
			return
		}
		hash := ""
		if in.Password != "" {
			if len(in.Password) < 8 {
				fail(w, 400, "invalid", "Mật khẩu tối thiểu 8 ký tự")
				return
			}
			hash, _ = auth.HashPassword(in.Password)
		}
		active := in.Active == nil || *in.Active
		if err := s.st.SaveLocalUser(store.User{Username: in.Username, FullName: strings.TrimSpace(in.FullName), Role: in.Role, Active: active}, hash); err != nil {
			fail(w, 500, "store", err.Error())
			return
		}
		s.st.Log("info", "ui", me.Username+" lưu tài khoản LIS "+in.Username, "")
		writeJSON(w, 200, map[string]bool{"ok": true})
	case len(parts) == 2 && parts[0] == "users" && r.Method == http.MethodPut:
		if !needAdmin() {
			return
		}
		var in struct {
			Role   string
			Active bool
		}
		if err := readJSON(r, &in); err != nil {
			fail(w, 400, "bad-json", err.Error())
			return
		}
		if in.Role != auth.RoleAdmin && in.Role != auth.RoleTech {
			fail(w, 400, "invalid", "Vai trò là quan-tri hoặc ktv")
			return
		}
		if parts[1] == me.Username && (in.Role != auth.RoleAdmin || !in.Active) {
			fail(w, 400, "invalid", "Không tự hạ quyền hoặc khóa chính mình")
			return
		}
		if err := s.st.SetUserRole(parts[1], in.Role, in.Active); err != nil {
			fail(w, 500, "store", err.Error())
			return
		}
		s.st.Log("info", "ui", me.Username+" đổi vai trò "+parts[1]+" → "+in.Role, "")
		writeJSON(w, 200, map[string]bool{"ok": true})
	case route == "password" && r.Method == http.MethodPost:
		var in struct{ Old, New string }
		if err := readJSON(r, &in); err != nil {
			fail(w, 400, "bad-json", err.Error())
			return
		}
		if me.Source != "local" {
			fail(w, 400, "his", "Tài khoản HIS đổi mật khẩu trên HIS")
			return
		}
		if _, err := auth.Login(r.Context(), s.st, me.Username, in.Old); err != nil {
			fail(w, 400, "wrong", "Mật khẩu cũ không đúng")
			return
		}
		if len(in.New) < 8 {
			fail(w, 400, "invalid", "Mật khẩu mới tối thiểu 8 ký tự")
			return
		}
		hash, _ := auth.HashPassword(in.New)
		_ = s.st.SaveLocalUser(me, hash)
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

func nonNilLogs(l []store.LogEntry) []store.LogEntry {
	if l == nil {
		return []store.LogEntry{}
	}
	return l
}

func itoa(i int) string { return strconv.Itoa(i) }
