package store

import (
	"crypto/rand"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"errors"
	"sort"
	"strings"
	"sync"
	"time"

	bolt "go.etcd.io/bbolt"
)

var (
	bOrders  = []byte("orders")
	bResults = []byte("results")
	bOutbox  = []byte("outbox")
	bConfig  = []byte("config")
	bLogs    = []byte("logs")
	keyCfg   = []byte("config")

	ErrNotFound = errors.New("không tìm thấy")
)

const maxLogs = 5000

type Store struct {
	db     *bolt.DB
	mu     sync.Mutex
	logSeq uint64
}

func Open(path string) (*Store, error) {
	db, err := bolt.Open(path, 0o600, &bolt.Options{Timeout: 2 * time.Second})
	if err != nil {
		return nil, err
	}
	err = db.Update(func(tx *bolt.Tx) error {
		for _, b := range [][]byte{bOrders, bResults, bOutbox, bConfig, bLogs} {
			if _, e := tx.CreateBucketIfNotExists(b); e != nil {
				return e
			}
		}
		return nil
	})
	if err != nil {
		db.Close()
		return nil, err
	}
	return &Store{db: db}, nil
}

func (s *Store) Close() error { return s.db.Close() }

// NewID — id ngẫu nhiên có tiền tố thời gian (sắp xếp theo thời gian khi duyệt khóa).
func NewID() string {
	var b [6]byte
	_, _ = rand.Read(b[:])
	return time.Now().UTC().Format("20060102T150405.000") + "-" + hex.EncodeToString(b[:])
}

func put(tx *bolt.Tx, bucket []byte, key string, v any) error {
	data, err := json.Marshal(v)
	if err != nil {
		return err
	}
	return tx.Bucket(bucket).Put([]byte(key), data)
}

// ------------------------------------------------------------------ cấu hình

func (s *Store) Config() (Config, error) {
	var c Config
	err := s.db.View(func(tx *bolt.Tx) error {
		data := tx.Bucket(bConfig).Get(keyCfg)
		if data == nil {
			return ErrNotFound
		}
		return json.Unmarshal(data, &c)
	})
	return c, err
}

func (s *Store) SaveConfig(c Config) error {
	return s.db.Update(func(tx *bolt.Tx) error {
		data, err := json.Marshal(c)
		if err != nil {
			return err
		}
		return tx.Bucket(bConfig).Put(keyCfg, data)
	})
}

// ------------------------------------------------------------------ chỉ định

// NormSid — SID so khớp không phân biệt hoa thường, bỏ khoảng trắng hai đầu.
func NormSid(sid string) string { return strings.ToUpper(strings.TrimSpace(sid)) }

func (s *Store) GetOrder(sid string) (Order, error) {
	var o Order
	err := s.db.View(func(tx *bolt.Tx) error {
		data := tx.Bucket(bOrders).Get([]byte(NormSid(sid)))
		if data == nil {
			return ErrNotFound
		}
		return json.Unmarshal(data, &o)
	})
	return o, err
}

// UpsertOrder — thêm hoặc thay chỉ định theo SID; giữ CreatedAt cũ, trạng thái cũ nếu đã chạy.
func (s *Store) UpsertOrder(o Order) (Order, error) {
	o.Sid = NormSid(o.Sid)
	now := time.Now()
	err := s.db.Update(func(tx *bolt.Tx) error {
		if data := tx.Bucket(bOrders).Get([]byte(o.Sid)); data != nil {
			var old Order
			if json.Unmarshal(data, &old) == nil {
				o.CreatedAt = old.CreatedAt
				if o.Status == "" || o.Status == OrderWaiting {
					if old.Status != OrderCancelled {
						o.Status = old.Status
					}
				}
			}
		}
		if o.CreatedAt.IsZero() {
			o.CreatedAt = now
		}
		if o.Status == "" {
			o.Status = OrderWaiting
		}
		o.UpdatedAt = now
		return put(tx, bOrders, o.Sid, o)
	})
	return o, err
}

func (s *Store) SetOrderStatus(sid, status string) error {
	return s.db.Update(func(tx *bolt.Tx) error {
		data := tx.Bucket(bOrders).Get([]byte(NormSid(sid)))
		if data == nil {
			return ErrNotFound
		}
		var o Order
		if err := json.Unmarshal(data, &o); err != nil {
			return err
		}
		if o.Status == OrderCancelled && status != OrderWaiting {
			return nil
		}
		o.Status = status
		o.UpdatedAt = time.Now()
		return put(tx, bOrders, o.Sid, o)
	})
}

type OrderFilter struct {
	From, To time.Time // theo ngày tạo; zero = không lọc
	Status   string
	Q        string // SID, mã/tên bệnh nhân
	Limit    int
}

func (s *Store) ListOrders(f OrderFilter) ([]Order, error) {
	var out []Order
	q := strings.ToLower(strings.TrimSpace(f.Q))
	err := s.db.View(func(tx *bolt.Tx) error {
		return tx.Bucket(bOrders).ForEach(func(_, v []byte) error {
			var o Order
			if json.Unmarshal(v, &o) != nil {
				return nil
			}
			if !f.From.IsZero() && o.CreatedAt.Before(f.From) {
				return nil
			}
			if !f.To.IsZero() && !o.CreatedAt.Before(f.To) {
				return nil
			}
			if f.Status != "" && o.Status != f.Status {
				return nil
			}
			if q != "" && !strings.Contains(strings.ToLower(o.Sid+" "+o.Patient.Code+" "+o.Patient.Name), q) {
				return nil
			}
			out = append(out, o)
			return nil
		})
	})
	sort.Slice(out, func(i, j int) bool { return out[i].CreatedAt.After(out[j].CreatedAt) })
	if f.Limit > 0 && len(out) > f.Limit {
		out = out[:f.Limit]
	}
	return out, err
}

// ------------------------------------------------------------------ kết quả + hàng đợi gửi

// AddResult — lưu kết quả; SID khớp chỉ định thì chuyển chỉ định sang "có kết quả" và xếp hàng gửi HIS.
func (s *Store) AddResult(r Result) (Result, error) {
	if r.ID == "" {
		r.ID = NewID()
	}
	r.Sid = NormSid(r.Sid)
	if r.ReceivedAt.IsZero() {
		r.ReceivedAt = time.Now()
	}
	err := s.db.Update(func(tx *bolt.Tx) error {
		if data := tx.Bucket(bOrders).Get([]byte(r.Sid)); data != nil {
			var o Order
			if json.Unmarshal(data, &o) == nil && o.Status != OrderCancelled {
				r.Matched = true
				o.Status = OrderResulted
				o.UpdatedAt = time.Now()
				if err := put(tx, bOrders, o.Sid, o); err != nil {
					return err
				}
			}
		}
		if err := put(tx, bResults, r.ID, r); err != nil {
			return err
		}
		if r.Matched {
			ob := OutboxItem{ID: r.ID, ResultID: r.ID, NextAt: time.Now()}
			return put(tx, bOutbox, ob.ID, ob)
		}
		return nil
	})
	return r, err
}

func (s *Store) GetResult(id string) (Result, error) {
	var r Result
	err := s.db.View(func(tx *bolt.Tx) error {
		data := tx.Bucket(bResults).Get([]byte(id))
		if data == nil {
			return ErrNotFound
		}
		return json.Unmarshal(data, &r)
	})
	return r, err
}

type ResultFilter struct {
	Sid      string
	Since    time.Time // nhận sau thời điểm này
	Unmatch  bool      // chỉ kết quả chưa khớp chỉ định
	Limit    int
}

func (s *Store) ListResults(f ResultFilter) ([]Result, error) {
	var out []Result
	sid := NormSid(f.Sid)
	err := s.db.View(func(tx *bolt.Tx) error {
		return tx.Bucket(bResults).ForEach(func(_, v []byte) error {
			var r Result
			if json.Unmarshal(v, &r) != nil {
				return nil
			}
			if sid != "" && r.Sid != sid {
				return nil
			}
			if !f.Since.IsZero() && !r.ReceivedAt.After(f.Since) {
				return nil
			}
			if f.Unmatch && r.Matched {
				return nil
			}
			out = append(out, r)
			return nil
		})
	})
	sort.Slice(out, func(i, j int) bool { return out[i].ReceivedAt.After(out[j].ReceivedAt) })
	if f.Limit > 0 && len(out) > f.Limit {
		out = out[:f.Limit]
	}
	return out, err
}

// AssignResult — gán kết quả chưa khớp cho một SID (người dùng chọn), rồi xếp hàng gửi HIS.
func (s *Store) AssignResult(id, sid string) (Result, error) {
	var r Result
	err := s.db.Update(func(tx *bolt.Tx) error {
		data := tx.Bucket(bResults).Get([]byte(id))
		if data == nil {
			return ErrNotFound
		}
		if err := json.Unmarshal(data, &r); err != nil {
			return err
		}
		od := tx.Bucket(bOrders).Get([]byte(NormSid(sid)))
		if od == nil {
			return errors.New("không có chỉ định SID " + sid)
		}
		var o Order
		if err := json.Unmarshal(od, &o); err != nil {
			return err
		}
		r.Sid = o.Sid
		r.Matched = true
		r.SentAt = nil
		r.SendError = ""
		o.Status = OrderResulted
		o.UpdatedAt = time.Now()
		if err := put(tx, bOrders, o.Sid, o); err != nil {
			return err
		}
		if err := put(tx, bResults, r.ID, r); err != nil {
			return err
		}
		return put(tx, bOutbox, r.ID, OutboxItem{ID: r.ID, ResultID: r.ID, NextAt: time.Now()})
	})
	return r, err
}

// DueOutbox — các bản tin đến hạn gửi.
func (s *Store) DueOutbox(now time.Time, limit int) ([]OutboxItem, error) {
	var out []OutboxItem
	err := s.db.View(func(tx *bolt.Tx) error {
		return tx.Bucket(bOutbox).ForEach(func(_, v []byte) error {
			var o OutboxItem
			if json.Unmarshal(v, &o) == nil && !o.NextAt.After(now) && len(out) < limit {
				out = append(out, o)
			}
			return nil
		})
	})
	return out, err
}

func (s *Store) OutboxCount() int {
	n := 0
	_ = s.db.View(func(tx *bolt.Tx) error {
		n = tx.Bucket(bOutbox).Stats().KeyN
		return nil
	})
	return n
}

// MarkSent — HIS đã nhận: bỏ khỏi hàng đợi, ghi thời điểm gửi, chỉ định → đã gửi.
func (s *Store) MarkSent(id string) error {
	return s.db.Update(func(tx *bolt.Tx) error {
		_ = tx.Bucket(bOutbox).Delete([]byte(id))
		data := tx.Bucket(bResults).Get([]byte(id))
		if data == nil {
			return nil
		}
		var r Result
		if err := json.Unmarshal(data, &r); err != nil {
			return err
		}
		now := time.Now()
		r.SentAt = &now
		r.SendError = ""
		if err := put(tx, bResults, r.ID, r); err != nil {
			return err
		}
		if od := tx.Bucket(bOrders).Get([]byte(r.Sid)); od != nil {
			var o Order
			if json.Unmarshal(od, &o) == nil && o.Status != OrderCancelled {
				o.Status = OrderSent
				o.UpdatedAt = now
				return put(tx, bOrders, o.Sid, o)
			}
		}
		return nil
	})
}

// MarkFailed — gửi lỗi: lùi lịch gửi lại (30 giây × 2^lần, tối đa 30 phút).
func (s *Store) MarkFailed(id, reason string) error {
	return s.db.Update(func(tx *bolt.Tx) error {
		data := tx.Bucket(bOutbox).Get([]byte(id))
		if data == nil {
			return nil
		}
		var o OutboxItem
		if err := json.Unmarshal(data, &o); err != nil {
			return err
		}
		o.Attempts++
		delay := 30 * time.Second << uint(minInt(o.Attempts-1, 6))
		if delay > 30*time.Minute {
			delay = 30 * time.Minute
		}
		o.NextAt = time.Now().Add(delay)
		o.LastError = reason
		if err := put(tx, bOutbox, o.ID, o); err != nil {
			return err
		}
		if rd := tx.Bucket(bResults).Get([]byte(o.ResultID)); rd != nil {
			var r Result
			if json.Unmarshal(rd, &r) == nil {
				r.SendError = reason
				return put(tx, bResults, r.ID, r)
			}
		}
		return nil
	})
}

// Requeue — gửi lại ngay một kết quả (nút "Gửi lại" trên giao diện).
func (s *Store) Requeue(id string) error {
	return s.db.Update(func(tx *bolt.Tx) error {
		if tx.Bucket(bResults).Get([]byte(id)) == nil {
			return ErrNotFound
		}
		return put(tx, bOutbox, id, OutboxItem{ID: id, ResultID: id, NextAt: time.Now()})
	})
}

func minInt(a, b int) int {
	if a < b {
		return a
	}
	return b
}

// ------------------------------------------------------------------ nhật ký

func (s *Store) Log(level, source, message, raw string) {
	s.mu.Lock()
	s.logSeq++
	seq := s.logSeq
	s.mu.Unlock()
	e := LogEntry{At: time.Now(), Level: level, Source: source, Message: message, Raw: raw}
	_ = s.db.Update(func(tx *bolt.Tx) error {
		b := tx.Bucket(bLogs)
		key := make([]byte, 16)
		binary.BigEndian.PutUint64(key, uint64(e.At.UnixNano()))
		binary.BigEndian.PutUint64(key[8:], seq)
		data, _ := json.Marshal(e)
		if err := b.Put(key, data); err != nil {
			return err
		}
		// Giữ tối đa maxLogs dòng gần nhất
		if n := b.Stats().KeyN; n > maxLogs+200 {
			c := b.Cursor()
			for k, _ := c.First(); k != nil && n > maxLogs; k, _ = c.Next() {
				_ = c.Delete()
				n--
			}
		}
		return nil
	})
}

func (s *Store) Logs(limit int, source string) []LogEntry {
	var out []LogEntry
	_ = s.db.View(func(tx *bolt.Tx) error {
		c := tx.Bucket(bLogs).Cursor()
		for k, v := c.Last(); k != nil && len(out) < limit; k, v = c.Prev() {
			var e LogEntry
			if json.Unmarshal(v, &e) == nil && (source == "" || e.Source == source) {
				out = append(out, e)
			}
		}
		return nil
	})
	return out
}
