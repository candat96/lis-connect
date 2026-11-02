// Package store: dữ liệu LIS trên PostgreSQL (cloud hoặc máy chủ tại viện, khai báo khi cài): chỉ định, kết quả,
// hàng đợi gửi HIS/CIS, cài đặt chung, máy xét nghiệm theo từng Agent, người dùng, phiên đăng nhập, nhật ký.
// Nhiều Agent (mỗi phòng xét nghiệm một máy tính) dùng chung một CSDL.
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
	CreatedBy  string    `json:"createdBy,omitempty"`
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
	Agent      string       `json:"agent,omitempty"`
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
	ID        string `json:"id"`
	Name      string `json:"name"`
	Enabled   bool   `json:"enabled"`
	Protocol  string `json:"protocol"`  // astm | hl7
	Transport string `json:"transport"` // serial | tcp-server | tcp-client
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

// AuthConfig — đăng nhập dùng chung tài khoản HIS/CIS: Agent gửi tài khoản + mật khẩu tới API đăng nhập của HIS.
type AuthConfig struct {
	Mode        string `json:"mode"`        // local (chỉ tài khoản LIS) | his (tài khoản HIS, tài khoản LIS dự phòng)
	URL         string `json:"url"`         // vd http://his.benhvien.vn/api/v1/integration/auth/verify
	KeyHeader   string `json:"keyHeader"`   // vd X-Integration-Key
	Key         string `json:"key"`         // khóa hệ thống tích hợp
	Body        string `json:"body"`        // mẫu nội dung gửi, có {{username}} {{password}}
	NameField   string `json:"nameField"`   // trường họ tên trong JSON trả về, vd fullName
	RolesField  string `json:"rolesField"`  // trường vai trò (mảng), vd roles
	AdminRoles  string `json:"adminRoles"`  // vai trò HIS được làm quản trị LIS, cách nhau dấu phẩy
	DefaultRole string `json:"defaultRole"` // vai trò LIS mặc định cho người đăng nhập bằng HIS: ktv
}

// Config — cài đặt dùng chung (lưu CSDL) + máy xét nghiệm của Agent này.
type Config struct {
	SiteName string     `json:"siteName"`
	His      HisConfig  `json:"his"`
	Auth     AuthConfig `json:"auth"`
	Devices  []Device   `json:"devices"`
}

// User — người dùng LIS: tài khoản riêng (local) hoặc tài khoản HIS đã đăng nhập (his).
type User struct {
	Username  string     `json:"username"`
	FullName  string     `json:"fullName"`
	Role      string     `json:"role"`   // quan-tri | ktv
	Source    string     `json:"source"` // local | his
	Active    bool       `json:"active"`
	LastLogin *time.Time `json:"lastLogin,omitempty"`
}

// LogEntry — nhật ký kết nối/bản tin để tra khi có sai lệch.
type LogEntry struct {
	Agent   string    `json:"agent,omitempty"`
	At      time.Time `json:"at"`
	Level   string    `json:"level"` // info | warn | error
	Source  string    `json:"source"`
	Message string    `json:"message"`
	Raw     string    `json:"raw,omitempty"`
}
