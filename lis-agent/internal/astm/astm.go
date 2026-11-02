// Package astm: giao thức ASTM E1381 (LIS1-A, tầng truyền: ENQ/ACK/STX…ETX/EOT, checksum) và E1394 (LIS2-A2,
// bản ghi H/P/O/R/Q/C/L). Máy gửi kết quả (R) hoặc hỏi chỉ định theo SID (Q); Agent trả lời bằng phiên mới gồm
// H, P, O (danh sách xét nghiệm), L.
package astm

import (
	"bytes"
	"fmt"
	"strings"
)

const (
	ENQ = 0x05
	ACK = 0x06
	NAK = 0x15
	EOT = 0x04
	STX = 0x02
	ETX = 0x03
	ETB = 0x17
	CR  = 0x0d
	LF  = 0x0a
)

// MaxFrameData — LIS1-A: tối đa 240 ký tự dữ liệu mỗi khung; dài hơn tách bằng ETB.
const MaxFrameData = 240

// Checksum — tổng các byte từ sau STX tới hết ETX/ETB, modulo 256, hex hoa 2 ký tự.
func Checksum(body []byte) string {
	var sum byte
	for _, b := range body {
		sum += b
	}
	return fmt.Sprintf("%02X", sum)
}

// Event — kết quả khi nạp byte vào Receiver.
type Event struct {
	Reply   []byte   // byte cần trả lại máy (ACK/NAK)
	Message []string // một phiên trọn vẹn: các bản ghi (dòng) đã ghép
	Raw     []byte   // nguyên văn phiên / khung lỗi
	Err     string   // lỗi khung (checksum sai, thiếu ENQ…)
}

// Receiver — máy trạng thái phía nhận (Agent là receiver).
type Receiver struct {
	buf     []byte
	inSess  bool
	partial bytes.Buffer // dữ liệu khung ETB đang ghép dở
	records []string
	raw     bytes.Buffer
}

func (r *Receiver) Reset() {
	r.buf = nil
	r.inSess = false
	r.partial.Reset()
	r.records = nil
	r.raw.Reset()
}

// InSession — đang trong phiên nhận (giữa ENQ và EOT).
func (r *Receiver) InSession() bool { return r.inSess }

func (r *Receiver) Feed(chunk []byte) []Event {
	r.buf = append(r.buf, chunk...)
	var ev []Event
	for len(r.buf) > 0 {
		switch r.buf[0] {
		case ENQ:
			r.buf = r.buf[1:]
			r.inSess = true
			r.partial.Reset()
			r.records = nil
			r.raw.Reset()
			ev = append(ev, Event{Reply: []byte{ACK}})
		case EOT:
			r.buf = r.buf[1:]
			if r.inSess && (len(r.records) > 0 || r.partial.Len() > 0) {
				if r.partial.Len() > 0 {
					r.records = append(r.records, splitRecords(r.partial.String())...)
					r.partial.Reset()
				}
				ev = append(ev, Event{Message: r.records, Raw: append([]byte(nil), r.raw.Bytes()...)})
			}
			r.inSess = false
			r.records = nil
			r.raw.Reset()
		case STX:
			end := frameEnd(r.buf)
			if end < 0 {
				return ev // khung chưa về đủ
			}
			frame := r.buf[:end]
			r.buf = r.buf[end:]
			data, last, err := verify(frame)
			if err != "" {
				ev = append(ev, Event{Err: err, Raw: append([]byte(nil), frame...)}, Event{Reply: []byte{NAK}})
				continue
			}
			if !r.inSess {
				ev = append(ev, Event{Err: "khung đến khi chưa có phiên (thiếu ENQ)", Raw: append([]byte(nil), frame...)})
				continue
			}
			r.raw.Write(frame)
			r.partial.WriteString(data)
			if last {
				r.records = append(r.records, splitRecords(r.partial.String())...)
				r.partial.Reset()
			}
			ev = append(ev, Event{Reply: []byte{ACK}})
		default:
			// Byte rác: gom liên tiếp thành một lỗi
			i := 1
			for i < len(r.buf) && r.buf[i] != ENQ && r.buf[i] != EOT && r.buf[i] != STX {
				i++
			}
			junk := r.buf[:i]
			r.buf = r.buf[i:]
			if len(bytes.Trim(junk, "\r\n")) > 0 {
				ev = append(ev, Event{Err: "byte không thuộc khung nào", Raw: append([]byte(nil), junk...)})
			}
		}
	}
	return ev
}

// frameEnd — vị trí ngay sau LF cuối khung, -1 nếu chưa đủ.
func frameEnd(b []byte) int {
	for i := 1; i < len(b); i++ {
		if b[i] != ETX && b[i] != ETB {
			continue
		}
		if i+4 >= len(b) {
			return -1
		}
		if b[i+3] == CR && b[i+4] == LF {
			return i + 5
		}
	}
	return -1
}

// verify — kiểm checksum; trả dữ liệu (bỏ số thứ tự khung và ký tự kết thúc), khung cuối (ETX) hay chưa.
func verify(frame []byte) (string, bool, string) {
	t := bytes.IndexAny(frame, string([]byte{ETX, ETB}))
	if t < 0 || t+3 > len(frame) {
		return "", false, "khung không có ETX/ETB"
	}
	body := frame[1 : t+1]
	want := Checksum(body)
	got := string(frame[t+1 : t+3])
	if !strings.EqualFold(want, got) {
		return "", false, fmt.Sprintf("checksum sai: nhận %s, tính ra %s", got, want)
	}
	if len(body) < 2 {
		return "", frame[t] == ETX, ""
	}
	return string(body[1 : len(body)-1]), frame[t] == ETX, ""
}

func splitRecords(s string) []string {
	var out []string
	for _, line := range strings.Split(s, "\r") {
		line = strings.Trim(line, "\n")
		if line != "" {
			out = append(out, line)
		}
	}
	return out
}

// Frames — chia các bản ghi thành khung gửi (đánh số 1..7,0…; tách ETB khi quá dài).
func Frames(records []string) [][]byte {
	var out [][]byte
	n := 1
	for _, rec := range records {
		data := rec + "\r"
		for len(data) > 0 {
			chunk := data
			term := byte(ETX)
			if len(chunk) > MaxFrameData {
				chunk = data[:MaxFrameData]
				term = ETB
			}
			data = data[len(chunk):]
			body := append([]byte{byte('0' + n%8)}, chunk...)
			body = append(body, term)
			f := append([]byte{STX}, body...)
			f = append(f, []byte(Checksum(body))...)
			f = append(f, CR, LF)
			out = append(out, f)
			n++
		}
	}
	return out
}
