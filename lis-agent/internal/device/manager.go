package device

import (
	"context"
	"errors"
	"fmt"
	"io"
	"log"
	"strings"
	"sync"
	"time"

	"lis-agent/internal/astm"
	"lis-agent/internal/hl7"
	"lis-agent/internal/store"
)

// Status — trạng thái một máy cho giao diện.
type Status struct {
	ID           string     `json:"id"`
	Connected    bool       `json:"connected"`
	LastActivity *time.Time `json:"lastActivity,omitempty"`
	LastError    string     `json:"lastError,omitempty"`
	Results      int        `json:"results"` // số mẫu đã nhận từ khi khởi động
	Queries      int        `json:"queries"` // số lần máy hỏi chỉ định
}

// Manager — chạy một vòng kết nối cho mỗi máy đang bật; Reload khi đổi cấu hình.
type Manager struct {
	st      *store.Store
	mu      sync.Mutex
	cancel  context.CancelFunc
	wg      sync.WaitGroup
	status  map[string]*Status
	devices []store.Device
	// OnResult — gọi sau khi lưu kết quả (đánh thức bộ gửi HIS)
	OnResult func()
	// Spool — ghi tạm kết quả ra đĩa khi CSDL mất kết nối
	Spool *store.Spool
	// Tracer — ghi bản tin thô máy ↔ Agent (nil = không ghi)
	Tracer *Tracer
}

func NewManager(st *store.Store) *Manager {
	return &Manager{st: st, status: map[string]*Status{}}
}

func (m *Manager) Reload(devices []store.Device) {
	m.Stop()
	ctx, cancel := context.WithCancel(context.Background())
	m.mu.Lock()
	m.cancel = cancel
	m.devices = devices
	m.status = map[string]*Status{}
	for _, d := range devices {
		m.status[d.ID] = &Status{ID: d.ID}
	}
	m.mu.Unlock()
	for _, d := range devices {
		if !d.Enabled {
			m.setErr(d.ID, "đang tắt")
			continue
		}
		m.wg.Add(1)
		go func(d store.Device) {
			defer m.wg.Done()
			m.run(ctx, d)
		}(d)
	}
}

func (m *Manager) Stop() {
	m.mu.Lock()
	cancel := m.cancel
	m.cancel = nil
	m.mu.Unlock()
	if cancel != nil {
		cancel()
		m.wg.Wait()
	}
}

func (m *Manager) Statuses() []Status {
	m.mu.Lock()
	defer m.mu.Unlock()
	out := make([]Status, 0, len(m.status))
	for _, d := range m.devices {
		if s, ok := m.status[d.ID]; ok {
			out = append(out, *s)
		}
	}
	return out
}

func (m *Manager) update(id string, f func(s *Status)) {
	m.mu.Lock()
	defer m.mu.Unlock()
	if s, ok := m.status[id]; ok {
		f(s)
	}
}

func (m *Manager) setErr(id, msg string) {
	m.update(id, func(s *Status) { s.LastError = msg; s.Connected = false })
}

func (m *Manager) touch(id string) {
	now := time.Now()
	m.update(id, func(s *Status) { s.LastActivity = &now })
}

// run — mở kết nối, chạy giao thức đến khi rớt, chờ rồi nối lại.
func (m *Manager) run(ctx context.Context, d store.Device) {
	src := "may:" + d.ID
	if d.Transport == "tcp-server" {
		m.runServer(ctx, d)
		return
	}
	for ctx.Err() == nil {
		conn, err := open(ctx, d)
		if err != nil {
			if ctx.Err() != nil {
				return
			}
			m.setErr(d.ID, err.Error())
			m.st.Log("warn", src, d.Name+": chưa kết nối được — "+err.Error(), "")
			sleep(ctx, 5*time.Second)
			continue
		}
		m.update(d.ID, func(s *Status) { s.Connected = true; s.LastError = "" })
		m.st.Log("info", src, d.Name+": đã kết nối", "")
		err = m.serve(ctx, d, conn)
		conn.Close()
		if ctx.Err() != nil {
			return
		}
		msg := "mất kết nối"
		if err != nil {
			msg += " — " + err.Error()
		}
		m.setErr(d.ID, msg)
		m.st.Log("warn", src, d.Name+": "+msg, "")
		sleep(ctx, 3*time.Second)
	}
}

// runServer — chế độ Agent chờ máy nối vào: giữ cổng nghe suốt thời gian chạy; máy nối lại (kể cả khi kết nối
// cũ chưa báo rớt) thì kết nối mới thay kết nối cũ ngay.
func (m *Manager) runServer(ctx context.Context, d store.Device) {
	src := "may:" + d.ID
	for ctx.Err() == nil {
		ln, err := listen(d)
		if err != nil {
			m.setErr(d.ID, err.Error())
			m.st.Log("warn", src, d.Name+": không mở được cổng nghe — "+err.Error(), "")
			sleep(ctx, 5*time.Second)
			continue
		}
		m.setErr(d.ID, "đang chờ máy nối vào cổng "+fmt.Sprint(d.TCPPort))
		go func() { <-ctx.Done(); ln.Close() }()
		var cur io.ReadWriteCloser
		var curMu sync.Mutex
		for {
			c, err := ln.Accept()
			if err != nil {
				break
			}
			curMu.Lock()
			if cur != nil {
				cur.Close() // máy nối lại: bỏ kết nối cũ
			}
			conn := &tcpConn{c}
			cur = conn
			curMu.Unlock()
			m.update(d.ID, func(s *Status) { s.Connected = true; s.LastError = "" })
			m.st.Log("info", src, d.Name+": máy đã nối vào từ "+c.RemoteAddr().String(), "")
			go func() {
				err := m.serve(ctx, d, conn)
				conn.Close()
				curMu.Lock()
				last := cur == conn
				if last {
					cur = nil
				}
				curMu.Unlock()
				if ctx.Err() == nil && last {
					msg := "máy ngắt kết nối"
					if err != nil {
						msg += " — " + err.Error()
					}
					m.setErr(d.ID, msg+"; đang chờ máy nối lại")
					m.st.Log("warn", src, d.Name+": "+msg, "")
				}
			}()
		}
		curMu.Lock()
		if cur != nil {
			cur.Close()
		}
		curMu.Unlock()
		ln.Close()
	}
}

func sleep(ctx context.Context, d time.Duration) {
	t := time.NewTimer(d)
	defer t.Stop()
	select {
	case <-ctx.Done():
	case <-t.C:
	}
}

func (m *Manager) serve(ctx context.Context, d store.Device, conn io.ReadWriteCloser) error {
	var err error
	if m.Tracer != nil && !d.NoTrace {
		ts := m.Tracer.begin(d, "Bắt đầu kết nối ("+describe(d)+")")
		conn = &traceConn{ReadWriteCloser: conn, s: ts}
		defer func() {
			msg := "Kết thúc kết nối"
			if err != nil {
				msg += ": " + err.Error()
			}
			ts.event(msg)
		}()
	}
	if d.Protocol == "hl7" {
		err = m.serveHL7(ctx, d, conn)
	} else {
		err = m.serveASTM(ctx, d, conn)
	}
	return err
}

func describe(d store.Device) string {
	switch d.Transport {
	case "serial":
		parity := "N"
		if d.Parity != "" {
			parity = strings.ToUpper(d.Parity[:1])
		}
		return fmt.Sprintf("%s, %s %d %d%s%d", d.Protocol, d.Port, d.Baud, d.DataBits, parity, d.StopBits)
	case "tcp-server":
		return fmt.Sprintf("%s, Agent nghe cổng %d", d.Protocol, d.TCPPort)
	default:
		return fmt.Sprintf("%s, Agent nối tới %s:%d", d.Protocol, d.Host, d.TCPPort)
	}
}

// ------------------------------------------------------------------ ASTM

func (m *Manager) serveASTM(ctx context.Context, d store.Device, conn io.ReadWriteCloser) error {
	src := "may:" + d.ID
	var rx astm.Receiver
	var wmu sync.Mutex
	write := func(b []byte) error {
		wmu.Lock()
		defer wmu.Unlock()
		_, err := conn.Write(b)
		return err
	}
	ackCh := make(chan byte, 4)
	var sending sync.Mutex // một phiên gửi tại một thời điểm
	buf := make([]byte, 4096)
	for ctx.Err() == nil {
		n, err := conn.Read(buf)
		if err != nil {
			if errors.Is(err, io.EOF) {
				return errors.New("máy đóng kết nối")
			}
			return err
		}
		if n == 0 {
			continue
		}
		m.touch(d.ID)
		chunk := buf[:n]
		// Đang gửi phiên trả lời: ACK/NAK/EOT của máy thuộc về bên gửi
		if !rx.InSession() && len(chunk) == 1 && (chunk[0] == astm.ACK || chunk[0] == astm.NAK || chunk[0] == astm.EOT) {
			select {
			case ackCh <- chunk[0]:
			default:
			}
			continue
		}
		for _, e := range rx.Feed(chunk) {
			if len(e.Reply) > 0 {
				if err := write(e.Reply); err != nil {
					return err
				}
			}
			if e.Err != "" {
				m.st.Log("error", src, d.Name+": "+e.Err, printable(e.Raw))
			}
			if e.Message != nil {
				raw := strings.Join(e.Message, "\n")
				m.st.Log("info", src, d.Name+": nhận "+fmt.Sprint(len(e.Message))+" bản ghi", raw)
				p := astm.Parse(e.Message)
				for _, r := range p.Results {
					items := make([]store.ResultItem, 0, len(r.Items))
					for _, it := range r.Items {
						items = append(items, store.ResultItem{DeviceCode: it.Code, TestName: it.Name, Value: it.Value, Unit: it.Unit, RefRange: it.RefRange, Flag: it.Flag})
					}
					m.saveResult(d, r.Sid, items, raw)
				}
				if len(p.Queries) > 0 {
					m.update(d.ID, func(s *Status) { s.Queries++ })
					infos := m.orderInfos(d, p.Queries)
					recs := astm.ReplyRecords(infos, time.Now())
					go func() {
						sending.Lock()
						defer sending.Unlock()
						if err := sendASTM(ctx, write, ackCh, recs); err != nil {
							m.st.Log("error", src, d.Name+": trả lời chỉ định lỗi — "+err.Error(), strings.Join(recs, "\n"))
						} else {
							m.st.Log("info", src, d.Name+": đã trả chỉ định cho "+strings.Join(p.Queries, ", "), strings.Join(recs, "\n"))
						}
					}()
				}
			}
		}
	}
	return nil
}

// sendASTM — phiên gửi: ENQ → chờ ACK → từng khung chờ ACK (NAK gửi lại tối đa 6 lần) → EOT.
func sendASTM(ctx context.Context, write func([]byte) error, ackCh chan byte, recs []string) error {
	// Bỏ ACK cũ còn sót
	for len(ackCh) > 0 {
		<-ackCh
	}
	wait := func() (byte, error) {
		t := time.NewTimer(15 * time.Second)
		defer t.Stop()
		select {
		case b := <-ackCh:
			return b, nil
		case <-t.C:
			return 0, errors.New("máy không trả lời trong 15 giây")
		case <-ctx.Done():
			return 0, ctx.Err()
		}
	}
	// Máy vừa gửi EOT kết thúc phiên hỏi; chờ một nhịp để máy sẵn sàng nhận
	time.Sleep(200 * time.Millisecond)
	if err := write([]byte{astm.ENQ}); err != nil {
		return err
	}
	if b, err := wait(); err != nil {
		return err
	} else if b != astm.ACK {
		return errors.New("máy không nhận ENQ (bận)")
	}
	for _, f := range astm.Frames(recs) {
		ok := false
		for try := 0; try < 6 && !ok; try++ {
			if err := write(f); err != nil {
				return err
			}
			b, err := wait()
			if err != nil {
				return err
			}
			ok = b == astm.ACK
			if b == astm.EOT {
				return errors.New("máy ngắt phiên giữa chừng")
			}
		}
		if !ok {
			_ = write([]byte{astm.EOT})
			return errors.New("máy từ chối khung 6 lần")
		}
	}
	return write([]byte{astm.EOT})
}

// ------------------------------------------------------------------ HL7

func (m *Manager) serveHL7(ctx context.Context, d store.Device, conn io.ReadWriteCloser) error {
	src := "may:" + d.ID
	var df hl7.Deframer
	buf := make([]byte, 8192)
	for ctx.Err() == nil {
		n, err := conn.Read(buf)
		if err != nil {
			if errors.Is(err, io.EOF) {
				return errors.New("máy đóng kết nối")
			}
			return err
		}
		if n == 0 {
			continue
		}
		m.touch(d.ID)
		for _, raw := range df.Feed(buf[:n]) {
			msg := hl7.Parse(raw)
			text := strings.ReplaceAll(string(raw), "\r", "\n")
			m.st.Log("info", src, d.Name+": nhận "+msg.Type, text)
			for _, r := range msg.Results {
				items := make([]store.ResultItem, 0, len(r.Items))
				for _, it := range r.Items {
					items = append(items, store.ResultItem{DeviceCode: it.Code, TestName: it.Name, Value: it.Value, Unit: it.Unit, RefRange: it.RefRange, Flag: it.Flag})
				}
				m.saveResult(d, r.Sid, items, text)
			}
			if _, err := conn.Write(hl7.Frame(hl7.Ack(msg.ControlID, "AA", "", time.Now()))); err != nil {
				return err
			}
		}
	}
	return nil
}

// ------------------------------------------------------------------ dùng chung

// saveResult — ánh xạ mã máy → mã HIS, lưu, đánh thức bộ gửi.
func (m *Manager) saveResult(d store.Device, sid string, items []store.ResultItem, raw string) {
	byDev := map[string]store.Mapping{}
	for _, mp := range d.Mappings {
		byDev[strings.ToUpper(strings.TrimSpace(mp.DeviceCode))] = mp
	}
	for i := range items {
		if mp, ok := byDev[strings.ToUpper(items[i].DeviceCode)]; ok {
			items[i].TestCode = mp.TestCode
			if mp.TestName != "" {
				items[i].TestName = mp.TestName
			}
		} else if len(d.Mappings) == 0 {
			// Máy chưa khai báo ánh xạ: dùng nguyên mã máy
			items[i].TestCode = items[i].DeviceCode
		}
	}
	in := store.Result{ID: store.NewID(), Sid: sid, DeviceID: d.ID, DeviceName: d.Name, Items: items, Raw: raw, ReceivedAt: time.Now()}
	r, err := m.st.AddResult(in)
	src := "may:" + d.ID
	if err != nil {
		// CSDL mất kết nối: ghi tạm ra đĩa, có kết nối lại thì đẩy lên (không mất kết quả)
		if m.Spool != nil && m.Spool.Add(in) == nil {
			log.Printf("CSDL lỗi (%v) — đã ghi tạm kết quả SID %s ra đĩa", err, sid)
			m.update(d.ID, func(s *Status) { s.Results++ })
			return
		}
		log.Printf("Không lưu được kết quả SID %s: %v", sid, err)
		return
	}
	m.update(d.ID, func(s *Status) { s.Results++ })
	if r.Matched {
		m.st.Log("info", src, d.Name+": kết quả SID "+r.Sid+" ("+fmt.Sprint(len(items))+" chỉ số)", "")
	} else {
		m.st.Log("warn", src, d.Name+": kết quả SID "+r.Sid+" không khớp chỉ định nào — chờ gán tay", "")
	}
	if m.OnResult != nil {
		m.OnResult()
	}
}

// orderInfos — chỉ định cho các SID máy hỏi, mã xét nghiệm đổi sang mã máy theo ánh xạ.
func (m *Manager) orderInfos(d store.Device, sids []string) []astm.OrderInfo {
	byTest := map[string]string{}
	for _, mp := range d.Mappings {
		byTest[strings.ToUpper(strings.TrimSpace(mp.TestCode))] = mp.DeviceCode
	}
	var out []astm.OrderInfo
	for _, sid := range sids {
		o, err := m.st.GetOrder(sid)
		if err != nil || o.Status == store.OrderCancelled {
			continue
		}
		var codes []string
		for _, t := range o.Tests {
			if dc, ok := byTest[strings.ToUpper(t.Code)]; ok {
				codes = append(codes, dc)
			} else if len(d.Mappings) == 0 {
				codes = append(codes, t.Code)
			}
		}
		if len(codes) == 0 {
			continue
		}
		if o.Status == store.OrderWaiting {
			_ = m.st.SetOrderStatus(o.Sid, store.OrderRunning)
		}
		out = append(out, astm.OrderInfo{Sid: o.Sid, PatientCode: o.Patient.Code, PatientName: o.Patient.Name, Dob: o.Patient.Dob, Gender: o.Patient.Gender, DeviceCodes: codes})
	}
	return out
}

// printable — byte điều khiển hiện dạng <STX>… để đọc nhật ký.
func printable(b []byte) string {
	names := map[byte]string{astm.ENQ: "<ENQ>", astm.ACK: "<ACK>", astm.NAK: "<NAK>", astm.EOT: "<EOT>", astm.STX: "<STX>", astm.ETX: "<ETX>", astm.ETB: "<ETB>", astm.CR: "<CR>", astm.LF: "<LF>"}
	var sb strings.Builder
	for _, c := range b {
		if n, ok := names[c]; ok {
			sb.WriteString(n)
		} else if c < 32 {
			sb.WriteString(fmt.Sprintf("<%02X>", c))
		} else {
			sb.WriteByte(c)
		}
	}
	return sb.String()
}
