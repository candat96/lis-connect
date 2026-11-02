// Package hl7: HL7 v2 qua MLLP (khung 0x0B … 0x1C 0x0D). Nhận ORU^R01 (kết quả) từ máy, trả ACK; đọc QRY/ORM
// không làm ở bản này (máy HL7 thường gửi kết quả một chiều).
package hl7

import (
	"bytes"
	"strings"
	"time"
)

const (
	SB = 0x0b // bắt đầu khối
	EB = 0x1c // kết thúc khối
	CR = 0x0d
)

// Deframer — tách bản tin MLLP từ dòng byte.
type Deframer struct{ buf []byte }

func (d *Deframer) Feed(chunk []byte) [][]byte {
	d.buf = append(d.buf, chunk...)
	var out [][]byte
	for {
		s := bytes.IndexByte(d.buf, SB)
		if s < 0 {
			d.buf = d.buf[:0]
			return out
		}
		e := bytes.Index(d.buf[s:], []byte{EB, CR})
		if e < 0 {
			d.buf = d.buf[s:]
			return out
		}
		out = append(out, append([]byte(nil), d.buf[s+1:s+e]...))
		d.buf = d.buf[s+e+2:]
	}
}

func Frame(msg string) []byte {
	b := []byte{SB}
	b = append(b, []byte(msg)...)
	return append(b, EB, CR)
}

type Item struct {
	Code, Name, Value, Unit, RefRange, Flag string
}

type SampleResult struct {
	Sid   string
	Items []Item
}

type Message struct {
	Type      string // ORU^R01…
	ControlID string
	Results   []SampleResult
}

// Parse — đọc MSH, OBR (SID: OBR-3 mã mẫu của máy, không có thì OBR-2), OBX (3 mã^tên, 5 giá trị, 6 đơn vị,
// 7 khoảng tham chiếu, 8 cờ).
func Parse(raw []byte) Message {
	text := strings.ReplaceAll(string(raw), "\n", "\r")
	var m Message
	field, comp := "|", "^"
	var cur *SampleResult
	for _, seg := range strings.Split(text, "\r") {
		if len(seg) < 3 {
			continue
		}
		if strings.HasPrefix(seg, "MSH") && len(seg) >= 8 {
			field = string(seg[3])
			comp = string(seg[4])
		}
		f := strings.Split(seg, field)
		get := func(i int) string {
			if i < len(f) {
				return strings.TrimSpace(f[i])
			}
			return ""
		}
		switch f[0] {
		case "MSH":
			// MSH-1 là dấu phân cách nên chỉ số lệch 1
			m.Type = get(8)
			m.ControlID = get(9)
		case "OBR", "SPM", "ORC":
			sid := ""
			if f[0] == "SPM" {
				sid = first(get(2), comp)
			} else {
				sid = first(get(3), comp)
				if sid == "" {
					sid = first(get(2), comp)
				}
			}
			if sid == "" {
				continue
			}
			if cur != nil && cur.Sid == sid {
				continue
			}
			if cur != nil && cur.Sid == "" {
				cur.Sid = sid
				continue
			}
			m.Results = append(m.Results, SampleResult{Sid: sid})
			cur = &m.Results[len(m.Results)-1]
		case "OBX":
			if cur == nil {
				m.Results = append(m.Results, SampleResult{})
				cur = &m.Results[len(m.Results)-1]
			}
			parts := strings.Split(get(3), comp)
			code := strings.TrimSpace(parts[0])
			name := ""
			if len(parts) > 1 {
				name = strings.TrimSpace(parts[1])
			}
			if code == "" {
				code = name
			}
			if code == "" {
				continue
			}
			cur.Items = append(cur.Items, Item{
				Code: code, Name: name, Value: first(get(5), comp), Unit: first(get(6), comp),
				RefRange: get(7), Flag: strings.ToUpper(get(8)),
			})
		}
	}
	kept := m.Results[:0]
	for _, r := range m.Results {
		if len(r.Items) > 0 {
			kept = append(kept, r)
		}
	}
	m.Results = kept
	return m
}

func first(v, comp string) string {
	for _, c := range strings.Split(v, comp) {
		if c = strings.TrimSpace(c); c != "" {
			return c
		}
	}
	return ""
}

// Ack — ACK^R01 với mã AA (nhận) hoặc AE (lỗi).
func Ack(controlID, code, text string, now time.Time) string {
	ts := now.Format("20060102150405")
	return "MSH|^~\\&|LIS-AGENT||||" + ts + "||ACK^R01|" + controlID + "-ACK|P|2.3.1\r" +
		"MSA|" + code + "|" + controlID + "|" + text + "\r"
}
