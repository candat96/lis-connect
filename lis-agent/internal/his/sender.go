// Package his: trả kết quả về HIS/CIS. Có webhook → Agent POST JSON (ký HMAC-SHA256 ở header X-Signature),
// HIS trả 2xx là xong; lỗi thì gửi lại lùi dần. Không có webhook → HIS tự kéo qua API và báo đã nhận.
package his

import (
	"bytes"
	"context"
	"crypto/hmac"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"time"

	"lis-agent/internal/store"
)

// ResultPayload — bản tin kết quả gửi HIS (cũng là dạng trả về ở API kéo kết quả).
type ResultPayload struct {
	ResultID    string             `json:"resultId"`
	Sid         string             `json:"sid"`
	HisOrderID  string             `json:"hisOrderId,omitempty"`
	PatientCode string             `json:"patientCode,omitempty"`
	DeviceID    string             `json:"deviceId"`
	DeviceName  string             `json:"deviceName"`
	ReceivedAt  time.Time          `json:"receivedAt"`
	Results     []store.ResultItem `json:"results"`
}

func Payload(st *store.Store, r store.Result) ResultPayload {
	p := ResultPayload{ResultID: r.ID, Sid: r.Sid, DeviceID: r.DeviceID, DeviceName: r.DeviceName, ReceivedAt: r.ReceivedAt, Results: r.Items}
	if o, err := st.GetOrder(r.Sid); err == nil {
		p.HisOrderID = o.HisOrderID
		p.PatientCode = o.Patient.Code
	}
	return p
}

// Sign — chữ ký HMAC-SHA256 (hex) của nội dung gửi.
func Sign(secret string, body []byte) string {
	h := hmac.New(sha256.New, []byte(secret))
	h.Write(body)
	return hex.EncodeToString(h.Sum(nil))
}

type Sender struct {
	st     *store.Store
	client *http.Client
	wake   chan struct{}
}

func NewSender(st *store.Store) *Sender {
	return &Sender{st: st, client: &http.Client{Timeout: 15 * time.Second}, wake: make(chan struct{}, 1)}
}

// Wake — có kết quả mới, gửi ngay không chờ chu kỳ.
func (s *Sender) Wake() {
	select {
	case s.wake <- struct{}{}:
	default:
	}
}

func (s *Sender) Run(ctx context.Context) {
	t := time.NewTicker(5 * time.Second)
	defer t.Stop()
	for {
		s.flush(ctx)
		select {
		case <-ctx.Done():
			return
		case <-t.C:
		case <-s.wake:
		}
	}
}

func (s *Sender) flush(ctx context.Context) {
	cfg, err := s.st.Config()
	if err != nil || cfg.His.WebhookURL == "" {
		return // HIS tự kéo kết quả
	}
	items, _ := s.st.ClaimOutbox(50)
	for _, it := range items {
		if ctx.Err() != nil {
			return
		}
		r, err := s.st.GetResult(it.ResultID)
		if err != nil {
			_ = s.st.MarkSent(it.ID)
			continue
		}
		if err := s.post(ctx, cfg.His, Payload(s.st, r)); err != nil {
			_ = s.st.MarkFailed(it.ID, err.Error())
			s.st.Log("warn", "his", "Gửi kết quả SID "+r.Sid+" lỗi — "+err.Error(), "")
			continue
		}
		_ = s.st.MarkSent(it.ID)
		s.st.Log("info", "his", "Đã gửi kết quả SID "+r.Sid+" về "+name(cfg.His), "")
	}
}

func name(h store.HisConfig) string {
	if h.Name != "" {
		return h.Name
	}
	return "HIS"
}

func (s *Sender) post(ctx context.Context, h store.HisConfig, p any) error {
	body, err := json.Marshal(p)
	if err != nil {
		return err
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, h.WebhookURL, bytes.NewReader(body))
	if err != nil {
		return err
	}
	req.Header.Set("Content-Type", "application/json; charset=utf-8")
	req.Header.Set("X-Lis-Agent", "1")
	if h.Secret != "" {
		req.Header.Set("X-Signature", "sha256="+Sign(h.Secret, body))
	}
	res, err := s.client.Do(req)
	if err != nil {
		return err
	}
	defer res.Body.Close()
	if res.StatusCode/100 != 2 {
		msg, _ := io.ReadAll(io.LimitReader(res.Body, 300))
		return fmt.Errorf("HIS trả %d %s", res.StatusCode, bytes.TrimSpace(msg))
	}
	return nil
}

// Ping — gửi bản tin thử tới webhook (nút "Thử kết nối").
func (s *Sender) Ping(ctx context.Context, h store.HisConfig) error {
	if h.WebhookURL == "" {
		return fmt.Errorf("chưa nhập địa chỉ nhận kết quả")
	}
	return s.post(ctx, h, map[string]any{"type": "ping", "at": time.Now()})
}
