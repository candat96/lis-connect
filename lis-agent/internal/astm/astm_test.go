package astm

import (
	"strings"
	"testing"
	"time"
)

func session(records ...string) []byte {
	b := []byte{ENQ}
	for _, f := range Frames(records) {
		b = append(b, f...)
	}
	return append(b, EOT)
}

func TestReceiveResults(t *testing.T) {
	var r Receiver
	data := session(
		"H|\\^&|||Cobas^1|||||||P|1",
		"P|1||BN001",
		"O|1|SID123||^^^GLU\\^^^URE|R",
		"R|1|^^^GLU^Glucose|5.6|mmol/L|3.9-6.4|N",
		"R|2|^^^URE|9.8|mmol/L|2.5-7.5|H",
		"L|1|N",
	)
	var msgs [][]string
	acks := 0
	// Nạp từng mẩu nhỏ như cổng COM
	for i := 0; i < len(data); i += 7 {
		end := i + 7
		if end > len(data) {
			end = len(data)
		}
		for _, e := range r.Feed(data[i:end]) {
			if e.Err != "" {
				t.Fatalf("lỗi khung: %s", e.Err)
			}
			if len(e.Reply) == 1 && e.Reply[0] == ACK {
				acks++
			}
			if e.Message != nil {
				msgs = append(msgs, e.Message)
			}
		}
	}
	if len(msgs) != 1 || acks != 7 {
		t.Fatalf("msgs=%d acks=%d", len(msgs), acks)
	}
	p := Parse(msgs[0])
	if len(p.Results) != 1 || p.Results[0].Sid != "SID123" || len(p.Results[0].Items) != 2 {
		t.Fatalf("parse sai: %+v", p)
	}
	it := p.Results[0].Items[1]
	if it.Code != "URE" || it.Value != "9.8" || it.Flag != "H" || it.Unit != "mmol/L" {
		t.Fatalf("chỉ số sai: %+v", it)
	}
	if p.Results[0].Items[0].Name != "Glucose" {
		t.Fatalf("tên sai: %+v", p.Results[0].Items[0])
	}
}

func TestChecksumWrongGetsNak(t *testing.T) {
	var r Receiver
	r.Feed([]byte{ENQ})
	f := Frames([]string{"H|\\^&"})[0]
	f[len(f)-3] = 'Z' // hỏng checksum
	ev := r.Feed(f)
	if len(ev) != 2 || ev[0].Err == "" || ev[1].Reply[0] != NAK {
		t.Fatalf("phải NAK: %+v", ev)
	}
}

func TestLongRecordSplitWithEtb(t *testing.T) {
	long := "R|1|^^^X|" + strings.Repeat("9", 300)
	frames := Frames([]string{"H|\\^&", "O|1|S1", long})
	if len(frames) != 4 {
		t.Fatalf("phải tách 4 khung, có %d", len(frames))
	}
	var r Receiver
	data := []byte{ENQ}
	for _, f := range frames {
		data = append(data, f...)
	}
	data = append(data, EOT)
	var msg []string
	for _, e := range r.Feed(data) {
		if e.Message != nil {
			msg = e.Message
		}
	}
	p := Parse(msg)
	if len(p.Results) != 1 || len(p.Results[0].Items[0].Value) != 300 {
		t.Fatalf("ghép ETB sai: %+v", p)
	}
}

func TestQueryAndReply(t *testing.T) {
	p := Parse([]string{"H|\\^&", "Q|1|^SID9||^^^ALL||||||||O", "L|1|N"})
	if len(p.Queries) != 1 || p.Queries[0] != "SID9" {
		t.Fatalf("query sai: %+v", p)
	}
	recs := ReplyRecords([]OrderInfo{{Sid: "SID9", PatientCode: "BN1", PatientName: "NGUYEN|A", Gender: "female", Dob: "1990-02-03", DeviceCodes: []string{"GLU", "URE"}}}, time.Date(2026, 11, 2, 8, 0, 0, 0, time.UTC))
	joined := strings.Join(recs, "\n")
	if !strings.Contains(joined, "O|1|SID9||^^^GLU\\^^^URE|R") || !strings.Contains(joined, "NGUYEN A") || !strings.Contains(joined, "19900203|F") {
		t.Fatalf("trả lời sai:\n%s", joined)
	}
	if none := ReplyRecords(nil, time.Now()); none[len(none)-1] != "L|1|I" {
		t.Fatalf("không có chỉ định phải L|1|I")
	}
}
