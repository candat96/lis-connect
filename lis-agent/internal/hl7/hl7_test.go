package hl7

import (
	"testing"
	"time"
)

func TestParseOru(t *testing.T) {
	msg := "MSH|^~\\&|BC-5150|Mindray|||20261102080000||ORU^R01|1234|P|2.3.1\r" +
		"PID|1||BN001\r" +
		"OBR|1||SID777|00001^Automated Count^99MRC\r" +
		"OBX|1|NM|6690-2^WBC^LN||12.3|10*9/L|4.0-10.0|H|||F\r" +
		"OBX|2|NM|789-8^RBC^LN||4.5|10*12/L|3.5-5.5|N|||F\r"
	var d Deframer
	frames := d.Feed(append(Frame(msg)[:20], Frame(msg)[20:]...))
	if len(frames) != 1 {
		t.Fatalf("tách khung sai: %d", len(frames))
	}
	m := Parse(frames[0])
	if m.Type != "ORU^R01" || m.ControlID != "1234" {
		t.Fatalf("MSH sai: %+v", m)
	}
	if len(m.Results) != 1 || m.Results[0].Sid != "SID777" || len(m.Results[0].Items) != 2 {
		t.Fatalf("kết quả sai: %+v", m)
	}
	if it := m.Results[0].Items[0]; it.Code != "6690-2" || it.Name != "WBC" || it.Value != "12.3" || it.Flag != "H" {
		t.Fatalf("OBX sai: %+v", it)
	}
	ack := Ack("1234", "AA", "", time.Now())
	if want := "MSA|AA|1234|"; !contains(ack, want) {
		t.Fatalf("ACK sai: %q", ack)
	}
}

func contains(s, sub string) bool {
	for i := 0; i+len(sub) <= len(s); i++ {
		if s[i:i+len(sub)] == sub {
			return true
		}
	}
	return false
}
