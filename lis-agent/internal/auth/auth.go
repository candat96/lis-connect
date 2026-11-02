// Package auth: đăng nhập LIS. Tài khoản riêng của LIS (mật khẩu bcrypt) kiểm tại chỗ; tài khoản HIS/CIS kiểm bằng
// cách gọi API đăng nhập của HIS (cấu hình được: địa chỉ, header khóa, mẫu nội dung, trường họ tên/vai trò) — dùng
// được với HIS bất kỳ. HIS của dự án: POST /api/v1/integration/auth/verify, header X-Integration-Key.
package auth

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"strings"
	"time"

	"golang.org/x/crypto/bcrypt"
	"lis-agent/internal/store"
)

const (
	RoleAdmin = "quan-tri"
	RoleTech  = "ktv"
)

var (
	ErrWrong    = errors.New("Sai tên đăng nhập hoặc mật khẩu")
	ErrInactive = errors.New("Tài khoản đã bị khóa trên LIS")
)

func HashPassword(pw string) (string, error) {
	b, err := bcrypt.GenerateFromPassword([]byte(pw), 11)
	return string(b), err
}

// Defaults — cấu hình mặc định khớp HIS của dự án.
func Defaults(a store.AuthConfig) store.AuthConfig {
	if a.Mode == "" {
		a.Mode = "local"
	}
	if a.KeyHeader == "" {
		a.KeyHeader = "X-Integration-Key"
	}
	if a.Body == "" {
		a.Body = `{"username":"{{username}}","password":"{{password}}"}`
	}
	if a.NameField == "" {
		a.NameField = "fullName"
	}
	if a.RolesField == "" {
		a.RolesField = "roles"
	}
	if a.DefaultRole == "" {
		a.DefaultRole = RoleTech
	}
	return a
}

var client = &http.Client{Timeout: 10 * time.Second}

// Login — tài khoản LIS riêng có mật khẩu → kiểm tại chỗ; không thì hỏi HIS (khi bật đăng nhập dùng chung).
func Login(ctx context.Context, st *store.Store, username, password string) (store.User, error) {
	username = strings.TrimSpace(username)
	if username == "" || password == "" {
		return store.User{}, ErrWrong
	}
	u, hash, err := st.GetUser(username)
	if err == nil && u.Source == "local" && hash != "" {
		if bcrypt.CompareHashAndPassword([]byte(hash), []byte(password)) != nil {
			return store.User{}, ErrWrong
		}
		if !u.Active {
			return store.User{}, ErrInactive
		}
		st.TouchLogin(u.Username)
		return u, nil
	}
	cfg, _ := st.Config()
	a := Defaults(cfg.Auth)
	if a.Mode != "his" || a.URL == "" {
		return store.User{}, ErrWrong
	}
	if err == nil && !u.Active {
		return store.User{}, ErrInactive
	}
	r, err := CallHis(ctx, a, username, password)
	if err != nil {
		return store.User{}, err
	}
	return st.TouchHisUser(r.Username, r.FullName, r.Role)
}

// HisResult — kết quả HIS trả khi đăng nhập đúng.
type HisResult struct {
	Username string   `json:"username"`
	FullName string   `json:"fullName"`
	Roles    []string `json:"roles"`
	Role     string   `json:"role"` // vai trò LIS suy ra
}

// CallHis — gửi tài khoản sang API đăng nhập của HIS. Thành công = HTTP 2xx và (không có trường "ok" hoặc ok=true).
func CallHis(ctx context.Context, a store.AuthConfig, username, password string) (HisResult, error) {
	a = Defaults(a)
	esc := func(v string) string {
		b, _ := json.Marshal(v)
		return string(b[1 : len(b)-1])
	}
	body := strings.NewReplacer("{{username}}", esc(username), "{{password}}", esc(password)).Replace(a.Body)
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, a.URL, bytes.NewReader([]byte(body)))
	if err != nil {
		return HisResult{}, fmt.Errorf("địa chỉ đăng nhập HIS không hợp lệ: %w", err)
	}
	req.Header.Set("Content-Type", "application/json; charset=utf-8")
	if a.Key != "" {
		req.Header.Set(a.KeyHeader, a.Key)
	}
	res, err := client.Do(req)
	if err != nil {
		return HisResult{}, errors.New("Không kết nối được HIS để đăng nhập — dùng tài khoản LIS dự phòng hoặc báo quản trị")
	}
	defer res.Body.Close()
	raw, _ := io.ReadAll(io.LimitReader(res.Body, 1<<20))
	var m map[string]any
	_ = json.Unmarshal(raw, &m)
	if res.StatusCode/100 != 2 || m["ok"] == false {
		if msg, ok := m["message"].(string); ok && msg != "" && res.StatusCode != 401 && res.StatusCode != 403 {
			return HisResult{}, errors.New(msg)
		}
		if res.StatusCode == 403 || (res.StatusCode == 401 && m["result"] == "khoa-sai") {
			return HisResult{}, errors.New("HIS từ chối Agent (sai khóa tích hợp hoặc HIS chưa bật đăng nhập dùng chung) — báo quản trị")
		}
		if msg, ok := m["message"].(string); ok && msg != "" {
			return HisResult{}, errors.New(msg)
		}
		return HisResult{}, ErrWrong
	}
	r := HisResult{Username: username}
	if v, ok := field(m, "username").(string); ok && v != "" {
		r.Username = v
	}
	if v, ok := field(m, a.NameField).(string); ok {
		r.FullName = v
	}
	if arr, ok := field(m, a.RolesField).([]any); ok {
		for _, x := range arr {
			if s, ok := x.(string); ok {
				r.Roles = append(r.Roles, s)
			}
		}
	}
	r.Role = a.DefaultRole
	for _, want := range strings.Split(a.AdminRoles, ",") {
		want = strings.TrimSpace(want)
		for _, have := range r.Roles {
			if want != "" && strings.EqualFold(want, have) {
				r.Role = RoleAdmin
			}
		}
	}
	return r, nil
}

// field — đọc trường theo đường dẫn chấm (vd data.user.fullName).
func field(m map[string]any, path string) any {
	var cur any = m
	for _, p := range strings.Split(path, ".") {
		mm, ok := cur.(map[string]any)
		if !ok {
			return nil
		}
		cur = mm[p]
	}
	return cur
}
