// Package store: dữ liệu cục bộ của Agent (một file bbolt): chỉ định, kết quả từ máy, hàng đợi gửi HIS/CIS,
// cấu hình, nhật ký. Agent không phụ thuộc mạng ngoài: mất kết nối HIS vẫn nhận chỉ định đã có và kết quả máy.
package store

import "time"

// Trạng thái chỉ định
const (
	OrderWaiting   = "cho"        // chờ chạy máy
	OrderRunning   = "dang-chay"  // máy đã hỏi/nhận chỉ định
	OrderResulted  = "co-ket-qua" // đã có kết quả (một phần hoặc đủ)
	OrderSent      = "da-gui"     // đã trả kết quả về HIS
	OrderCancelled = "huy"
)

type Patient struct {
	Code   string `json:"code"`
	Name   string `json:"name"`
	Dob    string `json:"dob,omitempty"`    // yyyy-MM-dd
	Gender string `json:"gender,omitempty"` // male | female
}

type Test struct {
	Code string `json:"code"` // mã xét nghiệm của HIS
	Name string `json:"name,omitempty"`
}

// Order — chỉ định xét nghiệm theo SID (mã vạch ống mẫu). Nguồn: HIS/CIS gửi qua API hoặc nhập tay trên Agent.
type Order struct {
	Sid        string    `json:"sid"`
	HisOrderID string    `json:"hisOrderId,omitempty"`
	Source     string    `json:"source"` // his | manual
	Patient    Patient   `json:"patient"`
	Tests      []Test    `json:"tests"`
	Doctor     string    `json:"doctor,omitempty"`
	Room       string    `json:"room,omitempty"`
	Note       string    `json:"note,omitempty"`
	Status     string    `json:"status"`
	CreatedAt  time.Time `json:"createdAt"`
	UpdatedAt  time.Time `json:"updatedAt"`
}

type ResultItem struct {
	TestCode   string `json:"testCode"`   // mã HIS (sau ánh xạ); trống = chưa ánh xạ
	DeviceCode string `json:"deviceCode"` // mã chỉ số trên máy
	TestName   string `json:"testName,omitempty"`
	Value      string `json:"value"`
	Unit       string `json:"unit,omitempty"`
	Flag       string `json:"flag,omitempty"`     // H, L, HH, LL, N, A… như máy gửi
	RefRange   string `json:"refRange,omitempty"` // khoảng tham chiếu máy gửi
}

// Result — một lần máy gửi kết quả cho một SID.
type Result struct {
	ID         string       `json:"id"`
	Sid        string       `json:"sid"`
	DeviceID   string       `json:"deviceId"`
	DeviceName string       `json:"deviceName"`
	Items      []ResultItem `json:"items"`
	ReceivedAt time.Time    `json:"receivedAt"`
	Matched    bool         `json:"matched"` // SID khớp chỉ định đang có
	SentAt     *time.Time   `json:"sentAt,omitempty"`
	SendError  string       `json:"sendError,omitempty"`
	Raw        string       `json:"raw,omitempty"`
}

// OutboxItem — bản tin chờ gửi HIS (kết quả). Gửi lại theo lịch lùi dần đến khi HIS nhận.
type OutboxItem struct {
	ID        string    `json:"id"`
	ResultID  string    `json:"resultId"`
	Attempts  int       `json:"attempts"`
	NextAt    time.Time `json:"nextAt"`
	LastError string    `json:"lastError,omitempty"`
}

// Mapping — mã chỉ số trên máy ↔ mã xét nghiệm HIS.
type Mapping struct {
	DeviceCode string `json:"deviceCode"`
	TestCode   string `json:"testCode"`
	TestName   string `json:"testName,omitempty"`
}

// Device — một máy xét nghiệm.
type Device struct {
	ID       string    `json:"id"`
	Name     string    `json:"name"`
	Enabled  bool      `json:"enabled"`
	Protocol string    `json:"protocol"`  // astm | hl7
	Transport string   `json:"transport"` // serial | tcp-server | tcp-client
	// serial
	Port     string `json:"port,omitempty"`
	Baud     int    `json:"baud,omitempty"`
	DataBits int    `json:"dataBits,omitempty"`
	Parity   string `json:"parity,omitempty"` // none | even | odd
	StopBits int    `json:"stopBits,omitempty"`
	// tcp
	Host     string    `json:"host,omitempty"`
	TCPPort  int       `json:"tcpPort,omitempty"`
	Mappings []Mapping `json:"mappings"`
}

// HisConfig — kết nối HIS/CIS.
type HisConfig struct {
	APIKey     string `json:"apiKey"`               // HIS gửi kèm header X-Api-Key khi gọi Agent
	WebhookURL string `json:"webhookUrl,omitempty"` // Agent POST kết quả tới đây; trống = HIS tự kéo
	Secret     string `json:"secret,omitempty"`     // ký HMAC-SHA256 nội dung gửi (header X-Signature)
	Name       string `json:"name,omitempty"`       // tên hệ thống HIS/CIS (hiển thị)
}

type Config struct {
	Listen        string    `json:"listen"`        // địa chỉ HTTP, vd :8090
	AdminPassword string    `json:"adminPassword"` // băm SHA-256 (hex) mật khẩu giao diện
	SiteName      string    `json:"siteName"`
	His           HisConfig `json:"his"`
	Devices       []Device  `json:"devices"`
}

// LogEntry — nhật ký kết nối/bản tin để tra khi có sai lệch.
type LogEntry struct {
	At      time.Time `json:"at"`
	Level   string    `json:"level"` // info | warn | error
	Source  string    `json:"source"`
	Message string    `json:"message"`
	Raw     string    `json:"raw,omitempty"`
}
