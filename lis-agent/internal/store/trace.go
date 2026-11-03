package store

import (
	"encoding/json"
	"fmt"
	"strings"
	"time"
)

// Hướng bản tin thô
const (
	TraceRx    = "rx" // máy → Agent
	TraceTx    = "tx" // Agent → máy
	TraceEvent = "ev" // sự kiện kết nối (nối, ngắt…), Data là chữ mô tả
)

// TraceFrame — một khối byte đọc/ghi trên kết nối với máy xét nghiệm (nguyên văn, kể cả ký tự điều khiển ENQ/STX/ACK…).
type TraceFrame struct {
	ID         int64     `json:"id"`
	Agent      string    `json:"agent"`
	DeviceID   string    `json:"deviceId"`
	DeviceName string    `json:"deviceName,omitempty"`
	Protocol   string    `json:"protocol,omitempty"`
	Session    string    `json:"session"` // một lần kết nối
	At         time.Time `json:"at"`
	Dir        string    `json:"dir"`
	Data       []byte    `json:"data"`
	Note       string    `json:"note,omitempty"`
	ImportID   string    `json:"importId,omitempty"`
}

type TraceFilter struct {
	DeviceID string
	Session  string
	ImportID string // trống = bản tin của chính Agent này
	From, To time.Time
	AfterID  int64 // theo dõi trực tiếp: chỉ lấy khối mới hơn
	Limit    int
}

// TraceSession — tóm tắt một lần kết nối.
type TraceSession struct {
	Session    string    `json:"session"`
	DeviceID   string    `json:"deviceId"`
	DeviceName string    `json:"deviceName"`
	Protocol   string    `json:"protocol"`
	Start      time.Time `json:"start"`
	End        time.Time `json:"end"`
	Frames     int       `json:"frames"`
	RxBytes    int       `json:"rxBytes"`
	TxBytes    int       `json:"txBytes"`
}

// TraceImport — tệp bản tin nạp từ Agent khác (xem lại, giải mã thử).
type TraceImport struct {
	ID         string    `json:"id"`
	Name       string    `json:"name"`
	FromAgent  string    `json:"fromAgent"`
	Devices    []Device  `json:"devices"`
	Frames     int       `json:"frames"`
	ImportedAt time.Time `json:"importedAt"`
	ImportedBy string    `json:"importedBy"`
}

// AddTraces — ghi một lô khối (một câu lệnh nhiều dòng).
func (s *Store) AddTraces(fs []TraceFrame) error {
	if len(fs) == 0 {
		return nil
	}
	var sb strings.Builder
	sb.WriteString(`INSERT INTO lis_trace (agent, device_id, device_name, protocol, session, at, dir, data, note, import_id) VALUES `)
	args := make([]any, 0, len(fs)*10)
	for i, f := range fs {
		if i > 0 {
			sb.WriteString(",")
		}
		b := i * 10
		fmt.Fprintf(&sb, "($%d,$%d,NULLIF($%d,''),NULLIF($%d,''),$%d,$%d,$%d,$%d,NULLIF($%d,''),NULLIF($%d,''))", b+1, b+2, b+3, b+4, b+5, b+6, b+7, b+8, b+9, b+10)
		agent := f.Agent
		if agent == "" {
			agent = s.agent
		}
		args = append(args, agent, f.DeviceID, f.DeviceName, f.Protocol, f.Session, f.At, f.Dir, f.Data, f.Note, f.ImportID)
	}
	_, err := s.db.Exec(sb.String(), args...)
	return err
}

func traceWhere(s *Store, f TraceFilter) (string, []any) {
	var conds []string
	var args []any
	add := func(c string, v any) {
		args = append(args, v)
		conds = append(conds, fmt.Sprintf(c, len(args)))
	}
	if f.ImportID != "" {
		add("import_id = $%d", f.ImportID)
	} else {
		conds = append(conds, "import_id IS NULL")
		add("agent = $%d", s.agent)
	}
	if f.DeviceID != "" {
		add("device_id = $%d", f.DeviceID)
	}
	if f.Session != "" {
		add("session = $%d", f.Session)
	}
	if !f.From.IsZero() {
		add("at >= $%d", f.From)
	}
	if !f.To.IsZero() {
		add("at < $%d", f.To)
	}
	if f.AfterID > 0 {
		add("id > $%d", f.AfterID)
	}
	return " WHERE " + strings.Join(conds, " AND "), args
}

// Traces — khối theo thời gian tăng dần.
func (s *Store) Traces(f TraceFilter) ([]TraceFrame, error) {
	where, args := traceWhere(s, f)
	limit := f.Limit
	if limit <= 0 || limit > 20000 {
		limit = 5000
	}
	rows, err := s.db.Query(`SELECT id, agent, device_id, COALESCE(device_name,''), COALESCE(protocol,''), session, at, dir, data,
		COALESCE(note,''), COALESCE(import_id,'') FROM lis_trace`+where+fmt.Sprintf(` ORDER BY at, id LIMIT %d`, limit), args...)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := []TraceFrame{}
	for rows.Next() {
		var t TraceFrame
		if err := rows.Scan(&t.ID, &t.Agent, &t.DeviceID, &t.DeviceName, &t.Protocol, &t.Session, &t.At, &t.Dir, &t.Data, &t.Note, &t.ImportID); err != nil {
			return nil, err
		}
		out = append(out, t)
	}
	return out, rows.Err()
}

// TraceSessions — các lần kết nối (mới nhất trước).
func (s *Store) TraceSessions(f TraceFilter) ([]TraceSession, error) {
	where, args := traceWhere(s, f)
	rows, err := s.db.Query(`SELECT session, max(device_id), COALESCE(max(device_name),''), COALESCE(max(protocol),''), min(at), max(at), count(*),
		COALESCE(sum(length(data)) FILTER (WHERE dir='rx'),0), COALESCE(sum(length(data)) FILTER (WHERE dir='tx'),0)
		FROM lis_trace`+where+` GROUP BY session ORDER BY min(at) DESC LIMIT 500`, args...)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := []TraceSession{}
	for rows.Next() {
		var t TraceSession
		if err := rows.Scan(&t.Session, &t.DeviceID, &t.DeviceName, &t.Protocol, &t.Start, &t.End, &t.Frames, &t.RxBytes, &t.TxBytes); err != nil {
			return nil, err
		}
		out = append(out, t)
	}
	return out, rows.Err()
}

// AddTraceImport — lưu tệp bản tin nạp từ Agent khác cùng các khối của nó.
func (s *Store) AddTraceImport(imp TraceImport, frames []TraceFrame) error {
	if _, err := s.db.Exec(`INSERT INTO lis_trace_import (id, name, from_agent, devices, frames, imported_by) VALUES ($1,$2,$3,$4,$5,$6)`,
		imp.ID, imp.Name, imp.FromAgent, mustJSON(imp.Devices), len(frames), imp.ImportedBy); err != nil {
		return err
	}
	for i := 0; i < len(frames); i += 500 {
		j := i + 500
		if j > len(frames) {
			j = len(frames)
		}
		if err := s.AddTraces(frames[i:j]); err != nil {
			_, _ = s.db.Exec(`DELETE FROM lis_trace_import WHERE id = $1`, imp.ID)
			return err
		}
	}
	return nil
}

func (s *Store) TraceImports() ([]TraceImport, error) {
	rows, err := s.db.Query(`SELECT id, name, COALESCE(from_agent,''), devices, frames, imported_at, COALESCE(imported_by,'') FROM lis_trace_import ORDER BY imported_at DESC`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := []TraceImport{}
	for rows.Next() {
		var t TraceImport
		var devs []byte
		if err := rows.Scan(&t.ID, &t.Name, &t.FromAgent, &devs, &t.Frames, &t.ImportedAt, &t.ImportedBy); err != nil {
			return nil, err
		}
		_ = json.Unmarshal(devs, &t.Devices)
		out = append(out, t)
	}
	return out, rows.Err()
}

func (s *Store) DeleteTraceImport(id string) error {
	_, err := s.db.Exec(`DELETE FROM lis_trace_import WHERE id = $1`, id)
	return err
}

// PruneTraces — xóa bản tin thô của Agent này cũ hơn số ngày giữ (tệp nạp giữ đến khi xóa tay).
func (s *Store) PruneTraces(days int) {
	if days <= 0 {
		days = 30
	}
	_, _ = s.db.Exec(fmt.Sprintf(`DELETE FROM lis_trace WHERE import_id IS NULL AND agent = $1 AND at < now() - interval '%d days'`, days), s.agent)
}

// TraceDays — số ngày giữ bản tin thô (cài đặt chung, mặc định 30).
func (s *Store) TraceDays() int {
	var d int
	_ = s.setting("traceDays", &d)
	if d <= 0 {
		return 30
	}
	return d
}

func (s *Store) SaveTraceDays(d int) error { return s.putSetting("traceDays", d) }
