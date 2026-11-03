package device

import (
	"io"
	"log"
	"sync"
	"time"

	"lis-agent/internal/store"
)

// Tracer — ghi bản tin thô máy ↔ Agent để gỡ lỗi. Ghi vào hàng đợi trong bộ nhớ, một luồng đẩy lên CSDL theo lô
// (không chặn giao tiếp với máy). Hàng đợi đầy (CSDL chậm/mất) thì bỏ khối mới và đếm số khối bị bỏ.
type Tracer struct {
	st      *store.Store
	ch      chan store.TraceFrame
	mu      sync.Mutex
	dropped int
	done    chan struct{}
}

func NewTracer(st *store.Store) *Tracer {
	t := &Tracer{st: st, ch: make(chan store.TraceFrame, 20000), done: make(chan struct{})}
	go t.loop()
	return t
}

func (t *Tracer) add(f store.TraceFrame) {
	select {
	case t.ch <- f:
	default:
		t.mu.Lock()
		t.dropped++
		t.mu.Unlock()
	}
}

// Dropped — số khối bị bỏ vì hàng đợi đầy (hiện trên giao diện).
func (t *Tracer) Dropped() int {
	t.mu.Lock()
	defer t.mu.Unlock()
	return t.dropped
}

func (t *Tracer) loop() {
	tick := time.NewTicker(400 * time.Millisecond)
	defer tick.Stop()
	batch := make([]store.TraceFrame, 0, 256)
	flush := func() {
		if len(batch) == 0 {
			return
		}
		if err := t.st.AddTraces(batch); err != nil {
			log.Printf("Không ghi được %d khối bản tin thô: %v", len(batch), err)
		}
		batch = batch[:0]
	}
	for {
		select {
		case f, ok := <-t.ch:
			if !ok {
				flush()
				close(t.done)
				return
			}
			batch = append(batch, f)
			if len(batch) >= 256 {
				flush()
			}
		case <-tick.C:
			flush()
		}
	}
}

// Close — đẩy nốt hàng đợi rồi dừng.
func (t *Tracer) Close() {
	close(t.ch)
	select {
	case <-t.done:
	case <-time.After(5 * time.Second):
	}
}

// session — một lần kết nối với một máy.
type traceSession struct {
	t   *Tracer
	d   store.Device
	sid string
}

func (t *Tracer) begin(d store.Device, note string) *traceSession {
	s := &traceSession{t: t, d: d, sid: store.NewID()}
	s.event(note)
	return s
}

func (s *traceSession) frame(dir string, b []byte, note string) {
	if s == nil || len(b) == 0 && dir != store.TraceEvent {
		return
	}
	cp := make([]byte, len(b))
	copy(cp, b)
	s.t.add(store.TraceFrame{DeviceID: s.d.ID, DeviceName: s.d.Name, Protocol: s.d.Protocol, Session: s.sid, At: time.Now(), Dir: dir, Data: cp, Note: note})
}

func (s *traceSession) event(text string) { s.frame(store.TraceEvent, []byte(text), "") }

// traceConn — bọc kết nối: mọi byte đọc (máy → Agent) và ghi (Agent → máy) đều được ghi lại nguyên văn.
type traceConn struct {
	io.ReadWriteCloser
	s *traceSession
}

func (c *traceConn) Read(p []byte) (int, error) {
	n, err := c.ReadWriteCloser.Read(p)
	if n > 0 {
		c.s.frame(store.TraceRx, p[:n], "")
	}
	return n, err
}

func (c *traceConn) Write(p []byte) (int, error) {
	n, err := c.ReadWriteCloser.Write(p)
	note := ""
	if err != nil {
		note = "lỗi ghi: " + err.Error()
	}
	c.s.frame(store.TraceTx, p, note)
	return n, err
}
