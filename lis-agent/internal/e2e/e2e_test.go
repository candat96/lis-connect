// Kiểm thử đầu–cuối: HIS gửi chỉ định qua API → máy (giả lập TCP) hỏi chỉ định → Agent trả lời đúng mã máy →
// máy gửi kết quả → Agent đẩy kết quả về webhook HIS có chữ ký HMAC.
package e2e

import (
	"bytes"
	"context"
	"database/sql"
	"encoding/json"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/http/httptest"
	"os"
	"strconv"
	"strings"
	"testing"
	"time"

	"lis-agent/internal/astm"
	"lis-agent/internal/device"
	"lis-agent/internal/his"
	"lis-agent/internal/hl7"
	"lis-agent/internal/store"
	"lis-agent/internal/web"
)

func freePort(t *testing.T) int {
	l, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer l.Close()
	return l.Addr().(*net.TCPAddr).Port
}

type env struct {
	st      *store.Store
	api     *httptest.Server
	hisHook chan []byte
	sigs    chan string
	stop    func()
}

func setup(t *testing.T, devices []store.Device) *env {
	st := openTestStore(t)
	e := &env{st: st, hisHook: make(chan []byte, 10), sigs: make(chan string, 10)}
	hook := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		b, _ := io.ReadAll(r.Body)
		e.sigs <- r.Header.Get("X-Signature")
		e.hisHook <- b
		w.WriteHeader(200)
	}))
	if err := st.SaveHis(store.HisConfig{APIKey: "khoa-api-thu-nghiem-123", WebhookURL: hook.URL, Secret: "bi-mat"}); err != nil {
		t.Fatal(err)
	}
	if err := st.SaveDevices(devices); err != nil {
		t.Fatal(err)
	}
	spool := store.NewSpool(t.TempDir() + "/spool.jsonl")
	sender := his.NewSender(st)
	mgr := device.NewManager(st)
	mgr.OnResult = sender.Wake
	mgr.Spool = spool
	mgr.Reload(devices)
	ctx, cancel := context.WithCancel(context.Background())
	go sender.Run(ctx)
	e.api = httptest.NewServer(web.New(st, mgr, sender, spool, "test").Handler())
	e.stop = func() { cancel(); mgr.Stop(); e.api.Close(); hook.Close(); st.Close() }
	return e
}

// openTestStore — mỗi phép thử một schema riêng trên CSDL thử (LIS_TEST_DSN, vd
// postgres://lis:...@localhost:5441/lis?sslmode=disable); không khai báo thì bỏ qua.
func openTestStore(t *testing.T) *store.Store {
	dsn := os.Getenv("LIS_TEST_DSN")
	if dsn == "" {
		t.Skip("chưa đặt LIS_TEST_DSN")
	}
	schema := fmt.Sprintf("e2e_%d", time.Now().UnixNano())
	db, err := sql.Open("postgres", dsn)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := db.Exec("CREATE SCHEMA " + schema); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _, _ = db.Exec("DROP SCHEMA " + schema + " CASCADE"); db.Close() })
	sep := "?"
	if strings.Contains(dsn, "?") {
		sep = "&"
	}
	st, err := store.Open(dsn+sep+"search_path="+schema, "AGENT-THU")
	if err != nil {
		t.Fatal(err)
	}
	return st
}

func (e *env) post(t *testing.T, path, key string, body any) *http.Response {
	b, _ := json.Marshal(body)
	req, _ := http.NewRequest(http.MethodPost, e.api.URL+path, bytes.NewReader(b))
	req.Header.Set("X-Api-Key", key)
	req.Header.Set("Content-Type", "application/json")
	res, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	return res
}

func dial(t *testing.T, port int) net.Conn {
	var c net.Conn
	var err error
	for i := 0; i < 50; i++ {
		c, err = net.Dial("tcp", "127.0.0.1:"+itoa(port))
		if err == nil {
			return c
		}
		time.Sleep(100 * time.Millisecond)
	}
	t.Fatal(err)
	return nil
}

func itoa(i int) string { return strconv.Itoa(i) }

func readByte(t *testing.T, c net.Conn) byte {
	_ = c.SetReadDeadline(time.Now().Add(5 * time.Second))
	b := make([]byte, 1)
	if _, err := io.ReadFull(c, b); err != nil {
		t.Fatalf("không đọc được byte: %v", err)
	}
	return b[0]
}

// sendSession — máy gửi một phiên ASTM, chờ ACK từng bước.
func sendSession(t *testing.T, c net.Conn, recs ...string) {
	c.Write([]byte{astm.ENQ})
	if b := readByte(t, c); b != astm.ACK {
		t.Fatalf("ENQ không được ACK: %x", b)
	}
	for _, f := range astm.Frames(recs) {
		c.Write(f)
		if b := readByte(t, c); b != astm.ACK {
			t.Fatalf("khung không được ACK: %x", b)
		}
	}
	c.Write([]byte{astm.EOT})
}

func TestAstmQueryAndResultToHis(t *testing.T) {
	port := freePort(t)
	e := setup(t, []store.Device{{ID: "au480", Name: "Sinh hóa thử", Enabled: true, Protocol: "astm", Transport: "tcp-server", Host: "127.0.0.1", TCPPort: port,
		Mappings: []store.Mapping{{DeviceCode: "GLU-P", TestCode: "GLU", TestName: "Glucose"}, {DeviceCode: "UREA", TestCode: "URE"}}}})
	defer e.stop()

	// Sai khóa API
	if res := e.post(t, "/api/v1/orders", "sai", map[string]any{}); res.StatusCode != 401 {
		t.Fatalf("sai khóa phải 401, được %d", res.StatusCode)
	}
	// HIS gửi chỉ định
	res := e.post(t, "/api/v1/orders", "khoa-api-thu-nghiem-123", map[string]any{
		"sid": "2611020001", "hisOrderId": "CD-15",
		"patient": map[string]any{"code": "BN1", "name": "NGUYEN VAN THU", "dob": "1985-04-12", "gender": "male"},
		"tests":   []map[string]string{{"code": "GLU"}, {"code": "URE"}},
	})
	if res.StatusCode != 200 {
		b, _ := io.ReadAll(res.Body)
		t.Fatalf("gửi chỉ định lỗi %d %s", res.StatusCode, b)
	}

	c := dial(t, port)
	defer c.Close()
	// Máy hỏi chỉ định
	sendSession(t, c, "H|\\^&|||AU480", "Q|1|^2611020001||^^^ALL", "L|1|N")
	// Agent mở phiên trả lời
	if b := readByte(t, c); b != astm.ENQ {
		t.Fatalf("Agent phải gửi ENQ, được %x", b)
	}
	c.Write([]byte{astm.ACK})
	var rx astm.Receiver
	rx.Feed([]byte{astm.ENQ})
	var reply []string
	buf := make([]byte, 1024)
	for reply == nil {
		_ = c.SetReadDeadline(time.Now().Add(5 * time.Second))
		n, err := c.Read(buf)
		if err != nil {
			t.Fatal(err)
		}
		for _, ev := range rx.Feed(buf[:n]) {
			if len(ev.Reply) > 0 && ev.Reply[0] == astm.ACK {
				c.Write([]byte{astm.ACK})
			}
			if ev.Message != nil {
				reply = ev.Message
			}
		}
	}
	joined := strings.Join(reply, "\n")
	if !strings.Contains(joined, "O|1|2611020001||^^^GLU-P\\^^^UREA") {
		t.Fatalf("trả lời chỉ định sai:\n%s", joined)
	}
	if o, _ := e.st.GetOrder("2611020001"); o.Status != store.OrderRunning {
		t.Fatalf("chỉ định phải chuyển đang chạy, là %s", o.Status)
	}

	// Máy gửi kết quả
	time.Sleep(300 * time.Millisecond)
	sendSession(t, c, "H|\\^&|||AU480", "P|1", "O|1|2611020001||^^^GLU-P", "R|1|^^^GLU-P|5.6|mmol/L|3.9-6.4|N", "R|2|^^^UREA|9.8|mmol/L|2.5-7.5|H", "L|1|N")

	select {
	case body := <-e.hisHook:
		sig := <-e.sigs
		if sig != "sha256="+his.Sign("bi-mat", body) {
			t.Fatalf("chữ ký sai: %s", sig)
		}
		var p his.ResultPayload
		_ = json.Unmarshal(body, &p)
		if p.Sid != "2611020001" || p.HisOrderID != "CD-15" || len(p.Results) != 2 || p.Results[0].TestCode != "GLU" || p.Results[1].Flag != "H" {
			t.Fatalf("bản tin HIS sai: %s", body)
		}
	case <-time.After(8 * time.Second):
		t.Fatal("HIS không nhận được kết quả")
	}
	time.Sleep(200 * time.Millisecond)
	if o, _ := e.st.GetOrder("2611020001"); o.Status != store.OrderSent {
		t.Fatalf("chỉ định phải chuyển đã trả HIS, là %s", o.Status)
	}
}

func TestHl7ResultUnmatchedThenAssigned(t *testing.T) {
	port := freePort(t)
	e := setup(t, []store.Device{{ID: "bc", Name: "Huyết học thử", Enabled: true, Protocol: "hl7", Transport: "tcp-server", Host: "127.0.0.1", TCPPort: port}})
	defer e.stop()
	c := dial(t, port)
	defer c.Close()
	msg := "MSH|^~\\&|BC5150||||20261102||ORU^R01|77|P|2.3.1\rOBR|1||SAI-SID\rOBX|1|NM|WBC^WBC||12.3|10*9/L|4-10|H\r"
	c.Write(hl7.Frame(msg))
	_ = c.SetReadDeadline(time.Now().Add(5 * time.Second))
	buf := make([]byte, 512)
	n, err := c.Read(buf)
	if err != nil || !strings.Contains(string(buf[:n]), "MSA|AA|77") {
		t.Fatalf("phải nhận ACK AA: %q %v", buf[:n], err)
	}
	time.Sleep(200 * time.Millisecond)
	list, _ := e.st.ListResults(store.ResultFilter{Unmatch: true})
	if len(list) != 1 || list[0].Items[0].TestCode != "WBC" {
		t.Fatalf("phải có 1 kết quả chưa khớp: %+v", list)
	}
	// Có chỉ định nhập tay rồi gán kết quả → gửi HIS
	_, _ = e.st.UpsertOrder(store.Order{Sid: "2611020009", Source: "manual", Patient: store.Patient{Name: "TRAN THI THU"}, Tests: []store.Test{{Code: "WBC"}}})
	if _, err := e.st.AssignResult(list[0].ID, "2611020009"); err != nil {
		t.Fatal(err)
	}
	select {
	case body := <-e.hisHook:
		if !strings.Contains(string(body), "2611020009") {
			t.Fatalf("bản tin sai: %s", body)
		}
	case <-time.After(8 * time.Second):
		t.Fatal("HIS không nhận được kết quả sau khi gán")
	}
}
