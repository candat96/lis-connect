package store

import (
	"bufio"
	"encoding/json"
	"os"
	"sync"
)

// Spool — kết quả máy gửi lúc CSDL mất kết nối được ghi tạm ra đĩa (mỗi dòng một JSON), có kết nối lại thì đẩy lên.
// Máy xét nghiệm không chờ được CSDL: không ghi tạm là mất kết quả.
type Spool struct {
	path string
	mu   sync.Mutex
}

func NewSpool(path string) *Spool { return &Spool{path: path} }

func (s *Spool) Add(r Result) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	f, err := os.OpenFile(s.path, os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0o600)
	if err != nil {
		return err
	}
	defer f.Close()
	b, _ := json.Marshal(r)
	_, err = f.Write(append(b, '\n'))
	if err == nil {
		err = f.Sync()
	}
	return err
}

// Count — số kết quả đang ghi tạm.
func (s *Spool) Count() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	f, err := os.Open(s.path)
	if err != nil {
		return 0
	}
	defer f.Close()
	n := 0
	sc := bufio.NewScanner(f)
	sc.Buffer(make([]byte, 1<<20), 16<<20)
	for sc.Scan() {
		n++
	}
	return n
}

// Flush — đẩy các kết quả ghi tạm lên CSDL; dòng nào lỗi thì giữ lại. Trả số dòng đã đẩy.
func (s *Spool) Flush(st *Store) int {
	s.mu.Lock()
	defer s.mu.Unlock()
	data, err := os.ReadFile(s.path)
	if err != nil || len(data) == 0 {
		return 0
	}
	var keep []byte
	done := 0
	sc := bufio.NewScanner(bytesReader(data))
	sc.Buffer(make([]byte, 1<<20), 16<<20)
	for sc.Scan() {
		line := append([]byte(nil), sc.Bytes()...)
		var r Result
		if json.Unmarshal(line, &r) != nil {
			continue
		}
		if _, err := st.AddResult(r); err != nil {
			keep = append(keep, append(line, '\n')...)
			continue
		}
		done++
	}
	if len(keep) == 0 {
		_ = os.Remove(s.path)
	} else {
		_ = os.WriteFile(s.path, keep, 0o600)
	}
	return done
}
