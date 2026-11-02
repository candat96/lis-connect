package astm

import (
	"strings"
	"time"
)

// Item — một chỉ số kết quả trong bản ghi R.
type Item struct {
	Code     string // mã chỉ số trên máy
	Name     string
	Value    string
	Unit     string
	RefRange string
	Flag     string
}

// SampleResult — kết quả của một mẫu (một bản ghi O và các bản ghi R theo sau).
type SampleResult struct {
	Sid   string
	Items []Item
}

// Parsed — nội dung một phiên: kết quả và/hoặc câu hỏi chỉ định (Q, theo SID).
type Parsed struct {
	Results []SampleResult
	Queries []string
}

type delims struct{ field, repeat, comp string }

// Parse — đọc các bản ghi của một phiên. Dấu phân cách lấy từ bản ghi H (mặc định | \ ^ &).
func Parse(records []string) Parsed {
	d := delims{"|", "\\", "^"}
	var out Parsed
	var cur *SampleResult
	for _, rec := range records {
		if len(rec) < 1 {
			continue
		}
		if rec[0] == 'H' && len(rec) >= 5 {
			d = delims{string(rec[1]), string(rec[2]), string(rec[3])}
			continue
		}
		f := strings.Split(rec, d.field)
		get := func(i int) string {
			if i < len(f) {
				return strings.TrimSpace(f[i])
			}
			return ""
		}
		switch rec[0] {
		case 'O':
			sid := firstComp(get(2), d.comp)
			if sid == "" {
				sid = firstComp(get(3), d.comp)
			}
			out.Results = append(out.Results, SampleResult{Sid: sid})
			cur = &out.Results[len(out.Results)-1]
		case 'R':
			if cur == nil {
				out.Results = append(out.Results, SampleResult{})
				cur = &out.Results[len(out.Results)-1]
			}
			code, name := testID(get(2), d.comp)
			if code == "" {
				continue
			}
			cur.Items = append(cur.Items, Item{
				Code:     code,
				Name:     name,
				Value:    firstComp(get(3), d.comp),
				Unit:     get(4),
				RefRange: get(5),
				Flag:     strings.ToUpper(get(6)),
			})
		case 'Q':
			// Q|1|^SID hoặc Q|1|SID^…; có thể nhiều SID lặp bằng dấu lặp
			for _, rep := range strings.Split(get(2), d.repeat) {
				if sid := firstComp(rep, d.comp); sid != "" {
					out.Queries = append(out.Queries, sid)
				}
			}
		}
	}
	// Bỏ các mẫu không có chỉ số nào (O của câu hỏi, O rỗng)
	kept := out.Results[:0]
	for _, r := range out.Results {
		if len(r.Items) > 0 {
			kept = append(kept, r)
		}
	}
	out.Results = kept
	return out
}

func firstComp(v, comp string) string {
	for _, c := range strings.Split(v, comp) {
		if c = strings.TrimSpace(c); c != "" {
			return c
		}
	}
	return ""
}

// testID — mã chỉ số trong "Universal Test ID": chuẩn là ^^^MÃ^tên; máy khác ghi MÃ^… → lấy thành phần thứ 4
// nếu có, không thì thành phần khác rỗng đầu tiên.
func testID(v, comp string) (string, string) {
	parts := strings.Split(v, comp)
	if len(parts) >= 4 && strings.TrimSpace(parts[3]) != "" {
		name := ""
		if len(parts) >= 5 {
			name = strings.TrimSpace(parts[4])
		}
		return strings.TrimSpace(parts[3]), name
	}
	return firstComp(v, comp), ""
}

// OrderInfo — thông tin trả lời máy khi máy hỏi chỉ định một SID.
type OrderInfo struct {
	Sid         string
	PatientCode string
	PatientName string
	Dob         string // yyyy-MM-dd
	Gender      string // male | female
	DeviceCodes []string
}

// ReplyRecords — phiên trả lời câu hỏi: H, P, O (các mã xét nghiệm của máy), L. Không có chỉ định → L|1|I.
func ReplyRecords(orders []OrderInfo, now time.Time) []string {
	ts := now.Format("20060102150405")
	recs := []string{"H|\\^&|||LIS-AGENT|||||||P|1|" + ts}
	if len(orders) == 0 {
		return append(recs, "L|1|I")
	}
	for i, o := range orders {
		sex := "U"
		if o.Gender == "male" {
			sex = "M"
		} else if o.Gender == "female" {
			sex = "F"
		}
		dob := strings.ReplaceAll(o.Dob, "-", "")
		recs = append(recs, "P|"+itoa(i+1)+"||"+clean(o.PatientCode)+"||"+clean(o.PatientName)+"||"+dob+"|"+sex)
		tests := make([]string, 0, len(o.DeviceCodes))
		for _, c := range o.DeviceCodes {
			tests = append(tests, "^^^"+clean(c))
		}
		recs = append(recs, "O|1|"+clean(o.Sid)+"||"+strings.Join(tests, "\\")+"|R||"+ts+"||||N||||||||||||||O")
	}
	return append(recs, "L|1|N")
}

// clean — bỏ ký tự phân cách khỏi dữ liệu người dùng để không làm vỡ bản ghi.
func clean(s string) string {
	return strings.NewReplacer("|", " ", "\\", " ", "^", " ", "&", " ", "\r", " ", "\n", " ").Replace(s)
}

func itoa(i int) string {
	const digits = "0123456789"
	if i < 10 {
		return digits[i : i+1]
	}
	return itoa(i/10) + digits[i%10:i%10+1]
}
