package web

import (
	"bufio"
	"bytes"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"sort"
	"strconv"
	"strings"
	"time"

	"lis-agent/internal/astm"
	"lis-agent/internal/hl7"
	"lis-agent/internal/store"
)

// Tệp bản tin thô ".listrace": JSON Lines — dòng đầu là phần đầu tệp, mỗi dòng sau một khối.
//
//	{"format":"lis-trace","version":1,"agent":"XN-SINH-HOA","exportedAt":"…","devices":[{…cấu hình máy…}]}
//	{"t":"2026-11-03T09:15:02.123+07:00","s":"<phiên>","dev":"au480","d":"rx","b":"<base64>","n":"ghi chú"}
const traceFormat = "lis-trace"

type traceHeader struct {
	Format     string         `json:"format"`
	Version    int            `json:"version"`
	Agent      string         `json:"agent"`
	ExportedAt time.Time      `json:"exportedAt"`
	Devices    []store.Device `json:"devices"`
	Note       string         `json:"note,omitempty"`
}

type traceLine struct {
	T   time.Time `json:"t"`
	S   string    `json:"s"`
	Dev string    `json:"dev"`
	D   string    `json:"d"`
	B   string    `json:"b"`
	N   string    `json:"n,omitempty"`
}

var ctlNames = map[byte]string{0x00: "NUL", 0x01: "SOH", 0x02: "STX", 0x03: "ETX", 0x04: "EOT", 0x05: "ENQ", 0x06: "ACK",
	0x07: "BEL", 0x08: "BS", 0x09: "TAB", 0x0a: "LF", 0x0b: "VT", 0x0c: "FF", 0x0d: "CR", 0x0e: "SO", 0x0f: "SI", 0x10: "DLE",
	0x15: "NAK", 0x16: "SYN", 0x17: "ETB", 0x18: "CAN", 0x1c: "FS", 0x1d: "GS", 0x1e: "RS", 0x1f: "US", 0x7f: "DEL"}

// readable — byte thô thành chữ đọc được: ký tự điều khiển thành <ENQ>, <CR>…; byte khác ngoài ASCII in <xHH>.
func readable(b []byte) string {
	var sb strings.Builder
	for _, c := range b {
		switch {
		case ctlNames[c] != "":
			sb.WriteString("<" + ctlNames[c] + ">")
			if c == 0x0d || c == 0x0a {
				// giữ xuống dòng cho dễ đọc
				if c == 0x0a {
					sb.WriteByte('\n')
				}
			}
		case c < 0x20:
			fmt.Fprintf(&sb, "<x%02X>", c)
		default:
			sb.WriteByte(c)
		}
	}
	return sb.String()
}

func dirLabel(d string) string {
	switch d {
	case store.TraceRx:
		return "MÁY → AGENT"
	case store.TraceTx:
		return "AGENT → MÁY"
	default:
		return "SỰ KIỆN"
	}
}

type traceView struct {
	store.TraceFrame
	Text string `json:"text"`
	Hex  string `json:"hex"`
}

func traceFilter(get func(string) string) store.TraceFilter {
	f := store.TraceFilter{DeviceID: get("device"), Session: get("session"), ImportID: get("import")}
	// Tệp nạp từ Agent khác: xem trọn tệp, không lọc theo ngày
	if get("session") == "" && get("import") == "" {
		f.From, f.To = dayRange(get("from"), get("to"))
	}
	f.AfterID, _ = strconv.ParseInt(get("after"), 10, 64)
	f.Limit, _ = strconv.Atoi(get("limit"))
	return f
}

// trace — /ui/api/traces/…
func (s *Server) trace(w http.ResponseWriter, r *http.Request, parts []string, me store.User, admin bool) {
	q := r.URL.Query()
	f := traceFilter(q.Get)
	sub := ""
	if len(parts) > 1 {
		sub = parts[1]
	}
	switch {
	case sub == "" && r.Method == http.MethodGet:
		list, err := s.st.Traces(f)
		if err != nil {
			fail(w, 500, "store", err.Error())
			return
		}
		out := make([]traceView, 0, len(list))
		for _, t := range list {
			out = append(out, traceView{TraceFrame: t, Text: readable(t.Data), Hex: hexDump(t.Data)})
		}
		writeJSON(w, 200, out)
	case sub == "sessions":
		list, err := s.st.TraceSessions(f)
		if err != nil {
			fail(w, 500, "store", err.Error())
			return
		}
		writeJSON(w, 200, list)
	case sub == "status":
		dropped := 0
		if s.devices.Tracer != nil {
			dropped = s.devices.Tracer.Dropped()
		}
		writeJSON(w, 200, map[string]any{"days": s.st.TraceDays(), "dropped": dropped, "agent": s.st.Agent()})
	case sub == "settings" && r.Method == http.MethodPut:
		if !admin {
			fail(w, 403, "forbidden", "Chỉ quản trị LIS được đổi cài đặt")
			return
		}
		var in struct{ Days int }
		if err := readJSON(r, &in); err != nil || in.Days < 1 || in.Days > 365 {
			fail(w, 400, "invalid", "Số ngày giữ bản tin thô từ 1 đến 365")
			return
		}
		_ = s.st.SaveTraceDays(in.Days)
		writeJSON(w, 200, map[string]int{"days": in.Days})
	case sub == "export":
		s.traceExport(w, f, q.Get("format"))
	case sub == "imports" && r.Method == http.MethodGet:
		list, err := s.st.TraceImports()
		if err != nil {
			fail(w, 500, "store", err.Error())
			return
		}
		writeJSON(w, 200, list)
	case sub == "imports" && len(parts) == 3 && r.Method == http.MethodDelete:
		if !admin {
			fail(w, 403, "forbidden", "Chỉ quản trị LIS được xóa tệp đã nạp")
			return
		}
		_ = s.st.DeleteTraceImport(parts[2])
		writeJSON(w, 200, map[string]bool{"ok": true})
	case sub == "import" && r.Method == http.MethodPost:
		s.traceImport(w, r, me)
	case sub == "decode" && r.Method == http.MethodPost:
		var in struct{ Session, ImportId, DeviceId, Protocol string }
		_ = readJSON(r, &in)
		s.traceDecode(w, store.TraceFilter{Session: in.Session, ImportID: in.ImportId, DeviceID: in.DeviceId, Limit: 20000}, in.Protocol)
	default:
		fail(w, 404, "not-found", "Không có "+r.Method+" "+r.URL.Path)
	}
}

func hexDump(b []byte) string {
	var sb strings.Builder
	for i, c := range b {
		if i > 0 {
			sb.WriteByte(' ')
		}
		fmt.Fprintf(&sb, "%02X", c)
	}
	return sb.String()
}

// traceExport — tải về: listrace (nạp được vào Agent khác / lis-sim phát lại) hoặc txt (đọc bằng mắt).
func (s *Server) traceExport(w http.ResponseWriter, f store.TraceFilter, format string) {
	f.Limit = 20000
	list, err := s.st.Traces(f)
	if err != nil {
		fail(w, 500, "store", err.Error())
		return
	}
	stamp := time.Now().Format("20060102-1504")
	name := "ban-tin-" + s.st.Agent() + "-" + stamp
	if format == "txt" {
		w.Header().Set("Content-Type", "text/plain; charset=utf-8")
		w.Header().Set("Content-Disposition", `attachment; filename="`+name+`.txt"`)
		fmt.Fprintf(w, "Bản tin thô máy xét nghiệm ↔ LIS Agent %s — xuất lúc %s — %d khối\n", s.st.Agent(), time.Now().Format("02/01/2006 15:04:05"), len(list))
		last := ""
		for _, t := range list {
			if t.Session != last {
				fmt.Fprintf(w, "\n===== Máy %s (%s) — phiên %s =====\n", t.DeviceName, t.DeviceID, t.Session)
				last = t.Session
			}
			fmt.Fprintf(w, "%s  %-12s %s\n", t.At.In(time.Local).Format("2006-01-02 15:04:05.000"), dirLabel(t.Dir), readable(t.Data))
		}
		return
	}
	cfg, _ := s.st.Config()
	devs := []store.Device{}
	seen := map[string]bool{}
	for _, t := range list {
		if seen[t.DeviceID] {
			continue
		}
		seen[t.DeviceID] = true
		d := store.Device{ID: t.DeviceID, Name: t.DeviceName, Protocol: t.Protocol}
		for _, c := range cfg.Devices {
			if c.ID == t.DeviceID {
				d = c
			}
		}
		devs = append(devs, d)
	}
	w.Header().Set("Content-Type", "application/x-ndjson; charset=utf-8")
	w.Header().Set("Content-Disposition", `attachment; filename="`+name+`.listrace"`)
	enc := json.NewEncoder(w)
	_ = enc.Encode(traceHeader{Format: traceFormat, Version: 1, Agent: s.st.Agent(), ExportedAt: time.Now(), Devices: devs})
	for _, t := range list {
		_ = enc.Encode(traceLine{T: t.At, S: t.Session, Dev: t.DeviceID, D: t.Dir, B: base64.StdEncoding.EncodeToString(t.Data), N: t.Note})
	}
}

// traceImport — nạp tệp .listrace của Agent khác (thân yêu cầu là nội dung tệp; ?name=tên tệp).
func (s *Server) traceImport(w http.ResponseWriter, r *http.Request, me store.User) {
	body := io.LimitReader(r.Body, 200<<20)
	sc := bufio.NewScanner(body)
	sc.Buffer(make([]byte, 1<<20), 64<<20)
	if !sc.Scan() {
		fail(w, 400, "invalid", "Tệp rỗng")
		return
	}
	var h traceHeader
	if err := json.Unmarshal(sc.Bytes(), &h); err != nil || h.Format != traceFormat {
		fail(w, 400, "invalid", "Không phải tệp bản tin thô của LIS Agent (.listrace)")
		return
	}
	imp := store.TraceImport{ID: store.NewID(), Name: strings.TrimSpace(r.URL.Query().Get("name")), FromAgent: h.Agent, Devices: h.Devices, ImportedBy: me.Username}
	if imp.Name == "" {
		imp.Name = "Tệp từ " + h.Agent
	}
	devName := map[string]store.Device{}
	for _, d := range h.Devices {
		devName[d.ID] = d
	}
	var frames []store.TraceFrame
	line := 1
	for sc.Scan() {
		line++
		if len(bytes.TrimSpace(sc.Bytes())) == 0 {
			continue
		}
		var l traceLine
		if err := json.Unmarshal(sc.Bytes(), &l); err != nil {
			fail(w, 400, "invalid", fmt.Sprintf("Dòng %d không đọc được: %v", line, err))
			return
		}
		data, err := base64.StdEncoding.DecodeString(l.B)
		if err != nil {
			fail(w, 400, "invalid", fmt.Sprintf("Dòng %d: dữ liệu không phải base64", line))
			return
		}
		d := devName[l.Dev]
		frames = append(frames, store.TraceFrame{Agent: h.Agent, DeviceID: l.Dev, DeviceName: d.Name, Protocol: d.Protocol, Session: imp.ID + "/" + l.S,
			At: l.T, Dir: l.D, Data: data, Note: l.N, ImportID: imp.ID})
	}
	if err := sc.Err(); err != nil {
		fail(w, 400, "invalid", "Đọc tệp lỗi: "+err.Error())
		return
	}
	if len(frames) == 0 {
		fail(w, 400, "invalid", "Tệp không có khối bản tin nào")
		return
	}
	if err := s.st.AddTraceImport(imp, frames); err != nil {
		fail(w, 500, "store", err.Error())
		return
	}
	s.st.Log("info", "ui", fmt.Sprintf("%s nạp tệp bản tin thô %q (%d khối, từ Agent %s)", me.Username, imp.Name, len(frames), h.Agent), "")
	imp.Frames = len(frames)
	writeJSON(w, 200, imp)
}

// Kết quả giải mã thử (không lưu, không gửi HIS)
type decodedMsg struct {
	At      time.Time         `json:"at"`
	Session string            `json:"session"`
	Kind    string            `json:"kind"` // ket-qua | hoi-chi-dinh | khac | loi
	Lines   []string          `json:"lines"`
	Results []decodedResult   `json:"results,omitempty"`
	Queries []string          `json:"queries,omitempty"`
	Error   string            `json:"error,omitempty"`
	Raw     string            `json:"raw,omitempty"`
	Extra   map[string]string `json:"extra,omitempty"`
}

type decodedResult struct {
	Sid   string             `json:"sid"`
	Items []store.ResultItem `json:"items"`
}

// traceDecode — chạy lại bộ đọc giao thức trên các khối máy → Agent đã ghi (giải mã thử, không lưu, không gửi HIS).
func (s *Server) traceDecode(w http.ResponseWriter, f store.TraceFilter, protocol string) {
	if f.Session == "" && f.ImportID == "" {
		fail(w, 400, "invalid", "Chọn một phiên kết nối hoặc một tệp đã nạp")
		return
	}
	list, err := s.st.Traces(f)
	if err != nil {
		fail(w, 500, "store", err.Error())
		return
	}
	sort.SliceStable(list, func(i, j int) bool { return list[i].At.Before(list[j].At) })
	type state struct {
		rx astm.Receiver
		df hl7.Deframer
	}
	states := map[string]*state{}
	out := []decodedMsg{}
	for _, t := range list {
		if t.Dir != store.TraceRx {
			continue
		}
		proto := protocol
		if proto == "" {
			proto = t.Protocol
		}
		st := states[t.Session]
		if st == nil {
			st = &state{}
			states[t.Session] = st
		}
		if proto == "hl7" {
			for _, raw := range st.df.Feed(t.Data) {
				m := hl7.Parse(raw)
				d := decodedMsg{At: t.At, Session: t.Session, Kind: "khac", Lines: strings.Split(strings.TrimRight(string(raw), "\r"), "\r"), Extra: map[string]string{"type": m.Type, "controlId": m.ControlID}}
				for _, r := range m.Results {
					d.Kind = "ket-qua"
					items := []store.ResultItem{}
					for _, it := range r.Items {
						items = append(items, store.ResultItem{DeviceCode: it.Code, TestName: it.Name, Value: it.Value, Unit: it.Unit, RefRange: it.RefRange, Flag: it.Flag})
					}
					d.Results = append(d.Results, decodedResult{Sid: r.Sid, Items: items})
				}
				out = append(out, d)
			}
			continue
		}
		// ASTM: ACK/NAK/EOT đơn lẻ ngoài phiên là trả lời của máy cho phiên Agent gửi — bỏ qua như khi chạy thật
		if !st.rx.InSession() && len(t.Data) == 1 && (t.Data[0] == astm.ACK || t.Data[0] == astm.NAK || t.Data[0] == astm.EOT) {
			continue
		}
		for _, e := range st.rx.Feed(t.Data) {
			if e.Err != "" {
				out = append(out, decodedMsg{At: t.At, Session: t.Session, Kind: "loi", Error: e.Err, Raw: readable(e.Raw)})
			}
			if e.Message == nil {
				continue
			}
			p := astm.Parse(e.Message)
			d := decodedMsg{At: t.At, Session: t.Session, Kind: "khac", Lines: e.Message, Queries: p.Queries}
			if len(p.Queries) > 0 {
				d.Kind = "hoi-chi-dinh"
			}
			for _, r := range p.Results {
				d.Kind = "ket-qua"
				items := []store.ResultItem{}
				for _, it := range r.Items {
					items = append(items, store.ResultItem{DeviceCode: it.Code, TestName: it.Name, Value: it.Value, Unit: it.Unit, RefRange: it.RefRange, Flag: it.Flag})
				}
				d.Results = append(d.Results, decodedResult{Sid: r.Sid, Items: items})
			}
			out = append(out, d)
		}
	}
	writeJSON(w, 200, out)
}
