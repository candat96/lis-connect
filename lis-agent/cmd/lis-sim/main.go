// lis-sim — máy xét nghiệm giả để thử tích hợp khi chưa có máy thật. Nối TCP tới Agent (Agent cấu hình máy kiểu
// "TCP – Agent chờ máy nối vào"), hỏi chỉ định một SID (ASTM) rồi gửi kết quả.
//
//	lis-sim -addr 127.0.0.1:5000 -sid 2611020001 -query -results "GLU-P=5.6,UREA=9.8:H"
//	lis-sim -addr 127.0.0.1:5001 -proto hl7 -sid 2611020001 -results "WBC=12.3:H,RBC=4.5"
package main

import (
	"errors"
	"flag"
	"fmt"
	"io"
	"log"
	"net"
	"strings"
	"time"

	"lis-agent/internal/astm"
	"lis-agent/internal/hl7"
)

type item struct{ code, value, flag string }

func parseItems(s string) []item {
	var out []item
	for _, p := range strings.Split(s, ",") {
		p = strings.TrimSpace(p)
		if p == "" {
			continue
		}
		kv := strings.SplitN(p, "=", 2)
		it := item{code: kv[0], value: "1", flag: "N"}
		if len(kv) == 2 {
			vf := strings.SplitN(kv[1], ":", 2)
			it.value = vf[0]
			if len(vf) == 2 {
				it.flag = vf[1]
			}
		}
		out = append(out, it)
	}
	return out
}

func main() {
	addr := flag.String("addr", "127.0.0.1:5000", "địa chỉ cổng TCP của Agent cho máy này")
	proto := flag.String("proto", "astm", "astm | hl7")
	sid := flag.String("sid", "", "SID (mã vạch ống)")
	query := flag.Bool("query", false, "ASTM: hỏi chỉ định trước khi gửi kết quả (in danh sách xét nghiệm Agent trả)")
	results := flag.String("results", "", "kết quả: MA=giá trị[:cờ],… (mã máy)")
	flag.Parse()
	if *sid == "" {
		log.Fatal("thiếu -sid")
	}
	c, err := net.DialTimeout("tcp", *addr, 5*time.Second)
	if err != nil {
		log.Fatal("không nối được Agent: ", err)
	}
	defer c.Close()
	items := parseItems(*results)
	if *proto == "hl7" {
		if err := sendHL7(c, *sid, items); err != nil {
			log.Fatal(err)
		}
		return
	}
	if *query {
		tests, err := askOrder(c, *sid)
		if err != nil {
			log.Fatal(err)
		}
		fmt.Printf("Agent trả chỉ định SID %s: %s\n", *sid, strings.Join(tests, ", "))
		if len(items) == 0 {
			for _, t := range tests {
				items = append(items, item{code: t, value: "5.0", flag: "N"})
			}
		}
		time.Sleep(300 * time.Millisecond)
	}
	if len(items) == 0 {
		return
	}
	recs := []string{"H|\\^&|||LIS-SIM", "P|1", "O|1|" + *sid + "||^^^ALL|R"}
	for i, it := range items {
		recs = append(recs, fmt.Sprintf("R|%d|^^^%s|%s|||%s", i+1, it.code, it.value, it.flag))
	}
	recs = append(recs, "L|1|N")
	if err := session(c, recs); err != nil {
		log.Fatal(err)
	}
	fmt.Printf("Đã gửi %d kết quả cho SID %s\n", len(items), *sid)
}

func readByte(c net.Conn) (byte, error) {
	_ = c.SetReadDeadline(time.Now().Add(10 * time.Second))
	b := make([]byte, 1)
	_, err := io.ReadFull(c, b)
	return b[0], err
}

// session — gửi một phiên ASTM, chờ ACK từng khung.
func session(c net.Conn, recs []string) error {
	if _, err := c.Write([]byte{astm.ENQ}); err != nil {
		return err
	}
	if b, err := readByte(c); err != nil || b != astm.ACK {
		return fmt.Errorf("Agent không ACK ENQ (%v)", err)
	}
	for _, f := range astm.Frames(recs) {
		c.Write(f)
		if b, err := readByte(c); err != nil || b != astm.ACK {
			return fmt.Errorf("Agent không ACK khung (%v)", err)
		}
	}
	_, err := c.Write([]byte{astm.EOT})
	return err
}

// askOrder — gửi Q, nhận phiên trả lời của Agent, lấy danh sách mã xét nghiệm trong bản ghi O.
func askOrder(c net.Conn, sid string) ([]string, error) {
	if err := session(c, []string{"H|\\^&|||LIS-SIM", "Q|1|^" + sid + "||^^^ALL||||||||O", "L|1|N"}); err != nil {
		return nil, err
	}
	var rx astm.Receiver
	buf := make([]byte, 2048)
	deadline := time.Now().Add(20 * time.Second)
	for time.Now().Before(deadline) {
		_ = c.SetReadDeadline(time.Now().Add(2 * time.Second))
		n, err := c.Read(buf)
		var ne net.Error
		if errors.As(err, &ne) && ne.Timeout() {
			continue
		}
		if err != nil {
			return nil, err
		}
		for _, ev := range rx.Feed(buf[:n]) {
			if len(ev.Reply) > 0 {
				c.Write(ev.Reply)
			}
			if ev.Message != nil {
				var tests []string
				for _, r := range ev.Message {
					if strings.HasPrefix(r, "O|") {
						f := strings.Split(r, "|")
						if len(f) > 4 {
							for _, t := range strings.Split(f[4], "\\") {
								if code := strings.Trim(t, "^"); code != "" {
									tests = append(tests, code)
								}
							}
						}
					}
				}
				return tests, nil
			}
		}
	}
	return nil, errors.New("Agent không trả lời chỉ định")
}

func sendHL7(c net.Conn, sid string, items []item) error {
	now := time.Now().Format("20060102150405")
	msg := "MSH|^~\\&|LIS-SIM||||" + now + "||ORU^R01|" + now + "|P|2.3.1\r" + "OBR|1||" + sid + "\r"
	for i, it := range items {
		msg += fmt.Sprintf("OBX|%d|NM|%s^%s||%s|||%s|||F\r", i+1, it.code, it.code, it.value, it.flag)
	}
	if _, err := c.Write(hl7.Frame(msg)); err != nil {
		return err
	}
	_ = c.SetReadDeadline(time.Now().Add(10 * time.Second))
	buf := make([]byte, 1024)
	n, err := c.Read(buf)
	if err != nil {
		return fmt.Errorf("không nhận được ACK: %w", err)
	}
	fmt.Printf("Agent trả: %s\n", strings.ReplaceAll(string(buf[:n]), "\r", " | "))
	return nil
}
