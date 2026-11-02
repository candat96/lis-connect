// Package web: HTTP của Agent — API cho HIS/CIS (/api/v1, khóa X-Api-Key) và giao diện quản trị tại chỗ
// (/ui, đăng nhập bằng mật khẩu quản trị; trang tĩnh nhúng trong file chạy).
package web

import (
	"crypto/subtle"
	"embed"
	"encoding/json"
	"errors"
	"io/fs"
	"net/http"
	"strings"
	"time"

	"lis-agent/internal/device"
	"lis-agent/internal/his"
	"lis-agent/internal/store"
)

//go:embed static
var static embed.FS

type Server struct {
	st      *store.Store
	devices *device.Manager
	sender  *his.Sender
	spool   *store.Spool
	version string
}

func New(st *store.Store, devices *device.Manager, sender *his.Sender, spool *store.Spool, version string) *Server {
	return &Server{st: st, devices: devices, sender: sender, spool: spool, version: version}
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
