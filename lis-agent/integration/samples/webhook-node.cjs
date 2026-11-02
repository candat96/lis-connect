// Mẫu nhận kết quả LIS Agent bằng Node.js (không thư viện ngoài). Chạy: SECRET=<khóa ký> node webhook-node.cjs
// Địa chỉ khai báo ở Agent: http://<máy HIS>:3000/lis/results
const http = require('http');
const crypto = require('crypto');
const SECRET = process.env.SECRET || '';

http.createServer((req, res) => {
  if (req.method !== 'POST' || req.url !== '/lis/results') { res.writeHead(404); return res.end(); }
  const chunks = [];
  req.on('data', (c) => chunks.push(c));
  req.on('end', () => {
    const body = Buffer.concat(chunks); // ký trên NGUYÊN VĂN body, chưa parse
    if (SECRET) {
      const want = 'sha256=' + crypto.createHmac('sha256', SECRET).update(body).digest('hex');
      const got = req.headers['x-signature'] || '';
      if (got.length !== want.length || !crypto.timingSafeEqual(Buffer.from(got), Buffer.from(want))) {
        res.writeHead(401); return res.end('sai chữ ký');
      }
    }
    const p = JSON.parse(body.toString('utf8'));
    if (p.type === 'ping') { res.writeHead(200); return res.end('ok'); } // nút "Thử gửi tới HIS"
    // TODO: chống trùng theo p.resultId; ghi p.results vào phiếu xét nghiệm theo p.sid / p.hisOrderId,
    // trạng thái CHỜ DUYỆT (chưa trả người bệnh).
    console.log('Kết quả SID', p.sid, p.results.map((r) => `${r.testCode}=${r.value}${r.flag ? ' ' + r.flag : ''}`).join(', '));
    res.writeHead(200); res.end('ok'); // 2xx = đã nhận; khác 2xx Agent sẽ gửi lại
  });
}).listen(3000, () => console.log('Nhận kết quả tại http://0.0.0.0:3000/lis/results'));
