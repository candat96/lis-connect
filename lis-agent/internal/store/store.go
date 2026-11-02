package store

import (
	"context"
	"crypto/rand"
	"database/sql"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"net/url"
	"strings"
	"time"

	_ "github.com/lib/pq"
)

var ErrNotFound = errors.New("không tìm thấy")

// DBConfig — địa chỉ CSDL khai báo khi cài Agent (lưu ở file cấu hình cạnh Agent).
type DBConfig struct {
	Host     string `json:"host"`
	Port     int    `json:"port"`
	Name     string `json:"name"`
	User     string `json:"user"`
	Password string `json:"password"`
	SSLMode  string `json:"sslMode"` // disable | require | verify-full
}

func (c DBConfig) DSN() string {
	port := c.Port
	if port == 0 {
		port = 5432
	}
	ssl := c.SSLMode
	if ssl == "" {
		ssl = "disable"
	}
	u := url.URL{Scheme: "postgres", User: url.UserPassword(c.User, c.Password), Host: fmt.Sprintf("%s:%d", c.Host, port), Path: c.Name}
	q := u.Query()
	q.Set("sslmode", ssl)
	q.Set("connect_timeout", "5")
	q.Set("application_name", "lis-agent")
	u.RawQuery = q.Encode()
	return u.String()
}

type Store struct {
	db    *sql.DB
	agent string // tên Agent này (máy xét nghiệm và nhật ký gắn theo Agent)
}

// Open — kết nối CSDL, tạo/nâng cấp bảng.
func Open(dsn, agent string) (*Store, error) {
	db, err := sql.Open("postgres", dsn)
	if err != nil {
		return nil, err
	}
	db.SetMaxOpenConns(10)
	db.SetConnMaxIdleTime(5 * time.Minute)
	ctx, cancel := context.WithTimeout(context.Background(), 8*time.Second)
	defer cancel()
	if err := db.PingContext(ctx); err != nil {
		db.Close()
		return nil, err
	}
	s := &Store{db: db, agent: agent}
	if err := s.migrate(); err != nil {
		db.Close()
		return nil, fmt.Errorf("tạo bảng: %w", err)
	}
	return s, nil
}

func (s *Store) Close() error  { return s.db.Close() }
func (s *Store) Agent() string { return s.agent }

// Ping — CSDL còn kết nối không (giao diện báo, gửi kết quả tạm ra đĩa).
func (s *Store) Ping() error {
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	return s.db.PingContext(ctx)
}

// TestConnection — thử kết nối (trang Cài đặt), không tạo bảng.
func TestConnection(dsn string) error {
	db, err := sql.Open("postgres", dsn)
	if err != nil {
		return err
	}
	defer db.Close()
	ctx, cancel := context.WithTimeout(context.Background(), 8*time.Second)
	defer cancel()
	return db.PingContext(ctx)
}

// Lược đồ theo phiên bản; chỉ thêm phiên bản mới, không sửa phiên bản cũ.
var migrations = []string{
	`CREATE TABLE IF NOT EXISTS lis_order (
		sid          varchar(40) PRIMARY KEY,
		his_order_id varchar(80),
		source       varchar(10) NOT NULL,
		patient      jsonb NOT NULL,
		tests        jsonb NOT NULL,
		doctor       varchar(150),
		room         varchar(150),
		note         varchar(500),
		status       varchar(15) NOT NULL,
		created_by   varchar(100),
		created_at   timestamptz NOT NULL DEFAULT now(),
		updated_at   timestamptz NOT NULL DEFAULT now()
	);
	CREATE INDEX IF NOT EXISTS ix_lis_order_created ON lis_order (created_at DESC);
	CREATE TABLE IF NOT EXISTS lis_result (
		id          varchar(60) PRIMARY KEY,
		sid         varchar(40) NOT NULL,
		device_id   varchar(60),
		device_name varchar(150),
		items       jsonb NOT NULL,
		received_at timestamptz NOT NULL,
		matched     boolean NOT NULL,
		sent_at     timestamptz,
		send_error  varchar(500),
		raw         text,
		agent       varchar(100)
	);
	CREATE INDEX IF NOT EXISTS ix_lis_result_received ON lis_result (received_at DESC);
	CREATE INDEX IF NOT EXISTS ix_lis_result_sid ON lis_result (sid);
	CREATE TABLE IF NOT EXISTS lis_outbox (
		id         varchar(60) PRIMARY KEY,
		result_id  varchar(60) NOT NULL,
		attempts   integer NOT NULL DEFAULT 0,
		next_at    timestamptz NOT NULL DEFAULT now(),
		last_error varchar(500)
	);
	CREATE TABLE IF NOT EXISTS lis_setting (
		key   varchar(60) PRIMARY KEY,
		value jsonb NOT NULL
	);
	CREATE TABLE IF NOT EXISTS lis_device (
		agent varchar(100) NOT NULL,
		id    varchar(60) NOT NULL,
		data  jsonb NOT NULL,
		PRIMARY KEY (agent, id)
	);
	CREATE TABLE IF NOT EXISTS lis_user (
		username      varchar(100) PRIMARY KEY,
		full_name     varchar(150),
		role          varchar(20) NOT NULL,
		source        varchar(10) NOT NULL,
		password_hash varchar(200),
		active        boolean NOT NULL DEFAULT true,
		last_login    timestamptz
	);
	CREATE TABLE IF NOT EXISTS lis_session (
		token      varchar(64) PRIMARY KEY,
		username   varchar(100) NOT NULL,
		expires_at timestamptz NOT NULL
	);
	CREATE TABLE IF NOT EXISTS lis_log (
		id      bigserial PRIMARY KEY,
		agent   varchar(100),
		at      timestamptz NOT NULL DEFAULT now(),
		level   varchar(10) NOT NULL,
		source  varchar(100) NOT NULL,
		message varchar(1000) NOT NULL,
		raw     text
	);
	CREATE INDEX IF NOT EXISTS ix_lis_log_at ON lis_log (at DESC);`,
}

func (s *Store) migrate() error {
	if _, err := s.db.Exec(`CREATE TABLE IF NOT EXISTS lis_schema (version integer NOT NULL)`); err != nil {
		return err
	}
	var v int
	_ = s.db.QueryRow(`SELECT COALESCE(MAX(version), 0) FROM lis_schema`).Scan(&v)
	for i := v; i < len(migrations); i++ {
		tx, err := s.db.Begin()
		if err != nil {
			return err
		}
		if _, err := tx.Exec(`SELECT pg_advisory_xact_lock(7710)`); err != nil {
			tx.Rollback()
			return err
		}
		var cur int
		_ = tx.QueryRow(`SELECT COALESCE(MAX(version), 0) FROM lis_schema`).Scan(&cur)
		if cur > i {
			tx.Rollback()
			continue
		}
		if _, err := tx.Exec(migrations[i]); err != nil {
			tx.Rollback()
			return err
		}
		if _, err := tx.Exec(`INSERT INTO lis_schema (version) VALUES ($1)`, i+1); err != nil {
			tx.Rollback()
			return err
		}
		if err := tx.Commit(); err != nil {
			return err
		}
	}
	return nil
}

// NewID — id có tiền tố thời gian.
func NewID() string {
	var b [6]byte
	_, _ = rand.Read(b[:])
	return time.Now().UTC().Format("20060102T150405.000") + "-" + hex.EncodeToString(b[:])
}

func NormSid(sid string) string { return strings.ToUpper(strings.TrimSpace(sid)) }

func mustJSON(v any) []byte {
	b, _ := json.Marshal(v)
	return b
}

// ------------------------------------------------------------------ cài đặt

func (s *Store) setting(key string, v any) error {
	var raw []byte
	err := s.db.QueryRow(`SELECT value FROM lis_setting WHERE key = $1`, key).Scan(&raw)
	if errors.Is(err, sql.ErrNoRows) {
		return nil
	}
	if err != nil {
		return err
	}
	return json.Unmarshal(raw, v)
}

func (s *Store) putSetting(key string, v any) error {
	_, err := s.db.Exec(`INSERT INTO lis_setting (key, value) VALUES ($1, $2) ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value`, key, mustJSON(v))
	return err
}

// Config — cài đặt dùng chung + máy xét nghiệm của Agent này.
func (s *Store) Config() (Config, error) {
	var c Config
	if err := s.setting("site", &c.SiteName); err != nil {
		return c, err
	}
	if err := s.setting("his", &c.His); err != nil {
		return c, err
	}
	if err := s.setting("auth", &c.Auth); err != nil {
		return c, err
	}
	devs, err := s.Devices(s.agent)
	c.Devices = devs
	return c, err
}

func (s *Store) SaveSite(name string) error  { return s.putSetting("site", name) }
func (s *Store) SaveHis(h HisConfig) error   { return s.putSetting("his", h) }
func (s *Store) SaveAuth(a AuthConfig) error { return s.putSetting("auth", a) }

func (s *Store) Devices(agent string) ([]Device, error) {
	rows, err := s.db.Query(`SELECT data FROM lis_device WHERE agent = $1 ORDER BY data->>'name'`, agent)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := []Device{}
	for rows.Next() {
		var raw []byte
		var d Device
		if rows.Scan(&raw) == nil && json.Unmarshal(raw, &d) == nil {
			out = append(out, d)
		}
	}
	return out, rows.Err()
}

// SaveDevices — thay toàn bộ máy của Agent này.
func (s *Store) SaveDevices(devs []Device) error {
	tx, err := s.db.Begin()
	if err != nil {
		return err
	}
	defer tx.Rollback()
	if _, err := tx.Exec(`DELETE FROM lis_device WHERE agent = $1`, s.agent); err != nil {
		return err
	}
	for _, d := range devs {
		if _, err := tx.Exec(`INSERT INTO lis_device (id, agent, data) VALUES ($1, $2, $3)
			ON CONFLICT (agent, id) DO UPDATE SET data = EXCLUDED.data`, d.ID, s.agent, mustJSON(d)); err != nil {
			return err
		}
	}
	return tx.Commit()
}

// ------------------------------------------------------------------ chỉ định

const orderCols = `sid, COALESCE(his_order_id,''), source, patient, tests, COALESCE(doctor,''), COALESCE(room,''), COALESCE(note,''), status, COALESCE(created_by,''), created_at, updated_at`

type scanner interface{ Scan(dest ...any) error }

func scanOrder(r scanner) (Order, error) {
	var o Order
	var p, t []byte
	err := r.Scan(&o.Sid, &o.HisOrderID, &o.Source, &p, &t, &o.Doctor, &o.Room, &o.Note, &o.Status, &o.CreatedBy, &o.CreatedAt, &o.UpdatedAt)
	if err != nil {
		return o, err
	}
	_ = json.Unmarshal(p, &o.Patient)
	_ = json.Unmarshal(t, &o.Tests)
	return o, nil
}

func (s *Store) GetOrder(sid string) (Order, error) {
	o, err := scanOrder(s.db.QueryRow(`SELECT `+orderCols+` FROM lis_order WHERE sid = $1`, NormSid(sid)))
	if errors.Is(err, sql.ErrNoRows) {
		return o, ErrNotFound
	}
	return o, err
}

// UpsertOrder — thêm hoặc thay chỉ định theo SID; giữ thời điểm tạo và trạng thái đang chạy; chỉ định đã hủy gửi
// lại thì khôi phục về chờ máy.
func (s *Store) UpsertOrder(o Order) (Order, error) {
	o.Sid = NormSid(o.Sid)
	if o.Status == "" {
		o.Status = OrderWaiting
	}
	row := s.db.QueryRow(`INSERT INTO lis_order (sid, his_order_id, source, patient, tests, doctor, room, note, status, created_by)
		VALUES ($1, NULLIF($2,''), $3, $4, $5, NULLIF($6,''), NULLIF($7,''), NULLIF($8,''), $9, NULLIF($10,''))
		ON CONFLICT (sid) DO UPDATE SET his_order_id = EXCLUDED.his_order_id, source = EXCLUDED.source,
			patient = EXCLUDED.patient, tests = EXCLUDED.tests, doctor = EXCLUDED.doctor, room = EXCLUDED.room,
			note = EXCLUDED.note, updated_at = now(),
			status = CASE WHEN lis_order.status = 'huy' OR EXCLUDED.status <> 'cho' THEN EXCLUDED.status ELSE lis_order.status END
		RETURNING `+orderCols,
		o.Sid, o.HisOrderID, o.Source, mustJSON(o.Patient), mustJSON(o.Tests), o.Doctor, o.Room, o.Note, o.Status, o.CreatedBy)
	return scanOrder(row)
}

func (s *Store) SetOrderStatus(sid, status string) error {
	q := `UPDATE lis_order SET status = $2, updated_at = now() WHERE sid = $1`
	if status != OrderWaiting && status != OrderCancelled {
		q += ` AND status <> 'huy'`
	}
	res, err := s.db.Exec(q, NormSid(sid), status)
	if err != nil {
		return err
	}
	if n, _ := res.RowsAffected(); n == 0 {
		if _, e := s.GetOrder(sid); errors.Is(e, ErrNotFound) {
			return ErrNotFound
		}
	}
	return nil
}

type OrderFilter struct {
	From, To time.Time // theo thời điểm tạo [From, To); zero = không lọc
	Status   string
	Q        string // SID, mã/tên bệnh nhân
	Limit    int
}

func (s *Store) ListOrders(f OrderFilter) ([]Order, error) {
	where := []string{"true"}
	args := []any{}
	add := func(cond string, v any) {
		args = append(args, v)
		where = append(where, strings.ReplaceAll(cond, "?", fmt.Sprintf("$%d", len(args))))
	}
	if !f.From.IsZero() {
		add("created_at >= ?", f.From)
	}
	if !f.To.IsZero() {
		add("created_at < ?", f.To)
	}
	if f.Status != "" {
		add("status = ?", f.Status)
	}
	if q := strings.TrimSpace(f.Q); q != "" {
		// cùng một tham số cho ba vế
		add("(sid ILIKE ? OR patient->>'code' ILIKE ? OR patient->>'name' ILIKE ?)", "%"+q+"%")
	}
	limit := f.Limit
	if limit <= 0 || limit > 5000 {
		limit = 5000
	}
	rows, err := s.db.Query(`SELECT `+orderCols+` FROM lis_order WHERE `+strings.Join(where, " AND ")+
		fmt.Sprintf(` ORDER BY created_at DESC LIMIT %d`, limit), args...)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := []Order{}
	for rows.Next() {
		o, err := scanOrder(rows)
		if err != nil {
			return nil, err
		}
		out = append(out, o)
	}
	return out, rows.Err()
}

// ------------------------------------------------------------------ kết quả + hàng đợi gửi

const resultCols = `id, sid, COALESCE(device_id,''), COALESCE(device_name,''), items, received_at, matched, sent_at, COALESCE(send_error,''), COALESCE(raw,''), COALESCE(agent,'')`

func scanResult(r scanner) (Result, error) {
	var x Result
	var items []byte
	var sent sql.NullTime
	err := r.Scan(&x.ID, &x.Sid, &x.DeviceID, &x.DeviceName, &items, &x.ReceivedAt, &x.Matched, &sent, &x.SendError, &x.Raw, &x.Agent)
	if err != nil {
		return x, err
	}
	_ = json.Unmarshal(items, &x.Items)
	if sent.Valid {
		t := sent.Time
		x.SentAt = &t
	}
	return x, nil
}

// AddResult — lưu kết quả; SID khớp chỉ định (chưa hủy) thì chỉ định → "có kết quả" và xếp hàng gửi HIS.
func (s *Store) AddResult(r Result) (Result, error) {
	if r.ID == "" {
		r.ID = NewID()
	}
	r.Sid = NormSid(r.Sid)
	if r.ReceivedAt.IsZero() {
		r.ReceivedAt = time.Now()
	}
	if r.Agent == "" {
		r.Agent = s.agent
	}
	tx, err := s.db.Begin()
	if err != nil {
		return r, err
	}
	defer tx.Rollback()
	res, err := tx.Exec(`UPDATE lis_order SET status = 'co-ket-qua', updated_at = now() WHERE sid = $1 AND status <> 'huy'`, r.Sid)
	if err != nil {
		return r, err
	}
	n, _ := res.RowsAffected()
	r.Matched = n > 0
	if _, err := tx.Exec(`INSERT INTO lis_result (id, sid, device_id, device_name, items, received_at, matched, raw, agent)
		VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9) ON CONFLICT (id) DO NOTHING`,
		r.ID, r.Sid, r.DeviceID, r.DeviceName, mustJSON(r.Items), r.ReceivedAt, r.Matched, r.Raw, r.Agent); err != nil {
		return r, err
	}
	if r.Matched {
		if _, err := tx.Exec(`INSERT INTO lis_outbox (id, result_id) VALUES ($1, $1) ON CONFLICT (id) DO NOTHING`, r.ID); err != nil {
			return r, err
		}
	}
	return r, tx.Commit()
}

func (s *Store) GetResult(id string) (Result, error) {
	r, err := scanResult(s.db.QueryRow(`SELECT `+resultCols+` FROM lis_result WHERE id = $1`, id))
	if errors.Is(err, sql.ErrNoRows) {
		return r, ErrNotFound
	}
	return r, err
}

type ResultFilter struct {
	Sid      string
	Since    time.Time // nhận sau thời điểm này
	From, To time.Time // khoảng nhận [From, To)
	Unmatch  bool
	Limit    int
}

func (s *Store) ListResults(f ResultFilter) ([]Result, error) {
	where := []string{"true"}
	args := []any{}
	add := func(cond string, v any) {
		args = append(args, v)
		where = append(where, strings.ReplaceAll(cond, "?", fmt.Sprintf("$%d", len(args))))
	}
	if sid := NormSid(f.Sid); sid != "" {
		add("sid = ?", sid)
	}
	if !f.Since.IsZero() {
		add("received_at > ?", f.Since)
	}
	if !f.From.IsZero() {
		add("received_at >= ?", f.From)
	}
	if !f.To.IsZero() {
		add("received_at < ?", f.To)
	}
	if f.Unmatch {
		where = append(where, "NOT matched")
	}
	limit := f.Limit
	if limit <= 0 || limit > 2000 {
		limit = 2000
	}
	rows, err := s.db.Query(`SELECT `+resultCols+` FROM lis_result WHERE `+strings.Join(where, " AND ")+
		fmt.Sprintf(` ORDER BY received_at DESC LIMIT %d`, limit), args...)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := []Result{}
	for rows.Next() {
		r, err := scanResult(rows)
		if err != nil {
			return nil, err
		}
		out = append(out, r)
	}
	return out, rows.Err()
}

// AssignResult — gán kết quả chưa khớp cho một SID có chỉ định, xếp hàng gửi HIS.
func (s *Store) AssignResult(id, sid string) (Result, error) {
	sid = NormSid(sid)
	tx, err := s.db.Begin()
	if err != nil {
		return Result{}, err
	}
	defer tx.Rollback()
	res, err := tx.Exec(`UPDATE lis_order SET status = 'co-ket-qua', updated_at = now() WHERE sid = $1 AND status <> 'huy'`, sid)
	if err != nil {
		return Result{}, err
	}
	if n, _ := res.RowsAffected(); n == 0 {
		return Result{}, errors.New("không có chỉ định SID " + sid)
	}
	res, err = tx.Exec(`UPDATE lis_result SET sid = $2, matched = true, sent_at = NULL, send_error = NULL WHERE id = $1`, id, sid)
	if err != nil {
		return Result{}, err
	}
	if n, _ := res.RowsAffected(); n == 0 {
		return Result{}, ErrNotFound
	}
	if _, err := tx.Exec(`INSERT INTO lis_outbox (id, result_id) VALUES ($1, $1) ON CONFLICT (id) DO UPDATE SET next_at = now(), attempts = 0`, id); err != nil {
		return Result{}, err
	}
	if err := tx.Commit(); err != nil {
		return Result{}, err
	}
	return s.GetResult(id)
}

// ClaimOutbox — nhận các bản tin đến hạn để gửi; nhiều Agent cùng chạy không lấy trùng (khóa hạn 2 phút).
func (s *Store) ClaimOutbox(limit int) ([]OutboxItem, error) {
	rows, err := s.db.Query(`UPDATE lis_outbox SET next_at = now() + interval '2 minutes'
		WHERE id IN (SELECT id FROM lis_outbox WHERE next_at <= now() ORDER BY next_at LIMIT $1 FOR UPDATE SKIP LOCKED)
		RETURNING id, result_id, attempts, next_at, COALESCE(last_error,'')`, limit)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []OutboxItem
	for rows.Next() {
		var o OutboxItem
		if err := rows.Scan(&o.ID, &o.ResultID, &o.Attempts, &o.NextAt, &o.LastError); err != nil {
			return nil, err
		}
		out = append(out, o)
	}
	return out, rows.Err()
}

func (s *Store) OutboxCount() int {
	n := 0
	_ = s.db.QueryRow(`SELECT count(*) FROM lis_outbox`).Scan(&n)
	return n
}

// MarkSent — HIS đã nhận: bỏ khỏi hàng đợi, ghi thời điểm gửi, chỉ định → đã trả HIS.
func (s *Store) MarkSent(id string) error {
	tx, err := s.db.Begin()
	if err != nil {
		return err
	}
	defer tx.Rollback()
	_, _ = tx.Exec(`DELETE FROM lis_outbox WHERE id = $1`, id)
	var sid string
	err = tx.QueryRow(`UPDATE lis_result SET sent_at = now(), send_error = NULL WHERE id = $1 RETURNING sid`, id).Scan(&sid)
	if err == nil {
		_, _ = tx.Exec(`UPDATE lis_order SET status = 'da-gui', updated_at = now() WHERE sid = $1 AND status <> 'huy'`, sid)
	}
	return tx.Commit()
}

// MarkFailed — gửi lỗi: lùi lịch (30 giây × 2^lần, tối đa 30 phút).
func (s *Store) MarkFailed(id, reason string) error {
	if len(reason) > 480 {
		reason = reason[:480]
	}
	_, err := s.db.Exec(`UPDATE lis_outbox SET attempts = attempts + 1, last_error = $2,
		next_at = now() + LEAST(interval '30 seconds' * power(2, LEAST(attempts, 6)), interval '30 minutes')
		WHERE id = $1`, id, reason)
	if err == nil {
		_, err = s.db.Exec(`UPDATE lis_result SET send_error = $2 WHERE id = $1`, id, reason)
	}
	return err
}

func (s *Store) Requeue(id string) error {
	var x string
	if err := s.db.QueryRow(`SELECT id FROM lis_result WHERE id = $1 AND matched`, id).Scan(&x); err != nil {
		return ErrNotFound
	}
	_, err := s.db.Exec(`INSERT INTO lis_outbox (id, result_id) VALUES ($1, $1) ON CONFLICT (id) DO UPDATE SET next_at = now(), attempts = 0`, id)
	return err
}

// ------------------------------------------------------------------ người dùng, phiên

func (s *Store) GetUser(username string) (User, string, error) {
	var u User
	var hash sql.NullString
	var last sql.NullTime
	err := s.db.QueryRow(`SELECT username, COALESCE(full_name,''), role, source, password_hash, active, last_login FROM lis_user WHERE lower(username) = lower($1)`, username).
		Scan(&u.Username, &u.FullName, &u.Role, &u.Source, &hash, &u.Active, &last)
	if errors.Is(err, sql.ErrNoRows) {
		return u, "", ErrNotFound
	}
	if last.Valid {
		t := last.Time
		u.LastLogin = &t
	}
	return u, hash.String, err
}

func (s *Store) Users() ([]User, error) {
	rows, err := s.db.Query(`SELECT username, COALESCE(full_name,''), role, source, active, last_login FROM lis_user ORDER BY source, username`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := []User{}
	for rows.Next() {
		var u User
		var last sql.NullTime
		if err := rows.Scan(&u.Username, &u.FullName, &u.Role, &u.Source, &u.Active, &last); err != nil {
			return nil, err
		}
		if last.Valid {
			t := last.Time
			u.LastLogin = &t
		}
		out = append(out, u)
	}
	return out, rows.Err()
}

func (s *Store) CountLocalAdmins() int {
	n := 0
	_ = s.db.QueryRow(`SELECT count(*) FROM lis_user WHERE source = 'local' AND role = 'quan-tri' AND active`).Scan(&n)
	return n
}

// SaveLocalUser — tạo/sửa tài khoản LIS; hash rỗng = giữ mật khẩu cũ.
func (s *Store) SaveLocalUser(u User, hash string) error {
	_, err := s.db.Exec(`INSERT INTO lis_user (username, full_name, role, source, password_hash, active)
		VALUES ($1, $2, $3, 'local', NULLIF($4,''), $5)
		ON CONFLICT (username) DO UPDATE SET full_name = EXCLUDED.full_name, role = EXCLUDED.role, active = EXCLUDED.active,
			password_hash = COALESCE(EXCLUDED.password_hash, lis_user.password_hash)`, u.Username, u.FullName, u.Role, hash, u.Active)
	return err
}

// TouchHisUser — người đăng nhập bằng tài khoản HIS: ghi lần đăng nhập, giữ vai trò quản trị đã gán ở LIS.
func (s *Store) TouchHisUser(username, fullName, role string) (User, error) {
	var u User
	var last sql.NullTime
	err := s.db.QueryRow(`INSERT INTO lis_user (username, full_name, role, source, active, last_login) VALUES ($1, $2, $3, 'his', true, now())
		ON CONFLICT (username) DO UPDATE SET full_name = EXCLUDED.full_name, last_login = now(),
			role = CASE WHEN lis_user.role = 'quan-tri' THEN lis_user.role ELSE EXCLUDED.role END
		RETURNING username, COALESCE(full_name,''), role, source, active, last_login`, username, fullName, role).
		Scan(&u.Username, &u.FullName, &u.Role, &u.Source, &u.Active, &last)
	return u, err
}

func (s *Store) SetUserRole(username, role string, active bool) error {
	_, err := s.db.Exec(`UPDATE lis_user SET role = $2, active = $3 WHERE username = $1`, username, role, active)
	return err
}

func (s *Store) TouchLogin(username string) {
	_, _ = s.db.Exec(`UPDATE lis_user SET last_login = now() WHERE username = $1`, username)
}

func (s *Store) NewSession(username string, ttl time.Duration) (string, error) {
	var b [24]byte
	_, _ = rand.Read(b[:])
	tok := hex.EncodeToString(b[:])
	_, _ = s.db.Exec(`DELETE FROM lis_session WHERE expires_at < now()`)
	_, err := s.db.Exec(`INSERT INTO lis_session (token, username, expires_at) VALUES ($1, $2, now() + $3 * interval '1 second')`, tok, username, int(ttl.Seconds()))
	return tok, err
}

// SessionUser — người dùng của phiên (còn hạn, tài khoản đang hoạt động); gia hạn 12 giờ.
func (s *Store) SessionUser(token string) (User, error) {
	var u User
	err := s.db.QueryRow(`UPDATE lis_session ss SET expires_at = now() + interval '12 hours'
		FROM lis_user u WHERE ss.token = $1 AND ss.expires_at > now() AND u.username = ss.username AND u.active
		RETURNING u.username, COALESCE(u.full_name,''), u.role, u.source, u.active`, token).
		Scan(&u.Username, &u.FullName, &u.Role, &u.Source, &u.Active)
	if errors.Is(err, sql.ErrNoRows) {
		return u, ErrNotFound
	}
	return u, err
}

func (s *Store) DeleteSession(token string) {
	_, _ = s.db.Exec(`DELETE FROM lis_session WHERE token = $1`, token)
}

// ------------------------------------------------------------------ nhật ký

func (s *Store) Log(level, source, message, raw string) {
	if len(message) > 990 {
		message = message[:990]
	}
	_, _ = s.db.Exec(`INSERT INTO lis_log (agent, level, source, message, raw) VALUES ($1,$2,$3,$4,NULLIF($5,''))`, s.agent, level, source, message, raw)
}

func (s *Store) Logs(limit int, source string) []LogEntry {
	q := `SELECT COALESCE(agent,''), at, level, source, message, COALESCE(raw,'') FROM lis_log`
	args := []any{}
	if source != "" {
		q += ` WHERE source = $1`
		args = append(args, source)
	}
	q += fmt.Sprintf(` ORDER BY id DESC LIMIT %d`, limit)
	rows, err := s.db.Query(q, args...)
	if err != nil {
		return nil
	}
	defer rows.Close()
	out := []LogEntry{}
	for rows.Next() {
		var e LogEntry
		if rows.Scan(&e.Agent, &e.At, &e.Level, &e.Source, &e.Message, &e.Raw) == nil {
			out = append(out, e)
		}
	}
	return out
}

// PruneLogs — giữ nhật ký 90 ngày.
func (s *Store) PruneLogs() {
	_, _ = s.db.Exec(`DELETE FROM lis_log WHERE at < now() - interval '90 days'`)
}
